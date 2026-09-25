using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WaveLab.Audio.Transcription;

public sealed record LyricsOptions(bool IsolateVocals, bool Speech, bool CompareOriginal,
    string Model, string? Language, string Hints, bool CpuOnly);
public sealed record LyricsProgress(string Message, double? Fraction = null);

/// <summary>A cancellable, isolated inference process. No audio leaves this computer.</summary>
public sealed class LyricsEngine
{
    private readonly string _root;
    private readonly string _assets;
    private readonly string _bundle;
    private string Python => Path.Combine(_bundle, "python", "python.exe");

    public LyricsEngine(string? root = null, string? assets = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveLab", "Lyrics");
        _assets = assets ?? Path.Combine(AppContext.BaseDirectory, "Transcription");
        _bundle = Path.Combine(_assets, "Engine");
    }

    public bool IsReady => LyricsBundle.IsReady(_assets);

    public async Task<LyricsTranscript> TranscribeAsync(float[][] channels, int rate, int start, int count,
        string title, int editVersion, LyricsOptions options, IProgress<LyricsProgress> progress, CancellationToken token)
    {
        if (!IsReady) throw new InvalidOperationException("This installation is missing its built-in transcription files. Reinstall Deep Groove or rebuild the complete Release application.");
        if (options.Model is not ("large-v3" or "turbo")) throw new ArgumentException("Choose one of the supported speech models.");
        if (options.Hints.Length > 500) throw new ArgumentException("Keep spelling hints under 500 characters.");
        string working = Path.Combine(_root, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            string input = Path.Combine(working, "input.wav"), output = Path.Combine(working, "result.json");
            progress.Report(new("Preparing the selected audio…", 0));
            await Task.Run(() => LyricsAudio.Write(channels, rate, start, count, input, token), token);
            var arguments = new List<string> { "-I", "-B", "-X", "utf8", "-u", Path.Combine(_assets, "lyrics_worker.py"), "--input", input,
                "--output", output, "--model", options.Model, "--device", options.CpuOnly ? "cpu" : "auto" };
            if (options.IsolateVocals) arguments.Add("--isolate");
            if (options.Speech) arguments.Add("--speech");
            if (options.CompareOriginal) arguments.Add("--compare");
            if (!string.IsNullOrWhiteSpace(options.Language)) { arguments.Add("--language"); arguments.Add(options.Language); }
            if (!string.IsNullOrWhiteSpace(options.Hints)) { arguments.Add("--hints"); arguments.Add(options.Hints); }
            try
            {
                await RunProcessAsync(Python, arguments, EnvironmentVariables(), progress, token, jsonProgress: true);
            }
            catch (LyricsProcessException ex) when (!options.CpuOnly && ex.IsGpuFailure)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(new("The GPU could not finish. Retrying on the CPU; this will take longer…"));
                arguments[arguments.IndexOf("--device") + 1] = "cpu";
                await RunProcessAsync(Python, arguments, EnvironmentVariables(), progress, token, jsonProgress: true);
            }
            token.ThrowIfCancellationRequested();
            var result = JsonSerializer.Deserialize<LyricsTranscript>(await File.ReadAllTextAsync(output, token), LyricsTranscript.JsonOptions)
                ?? throw new InvalidDataException("No transcript was returned.");
            result.MapToSource((double)start / rate, (double)count / rate);
            result.SourceTitle = title;
            result.SourceEditVersion = editVersion;
            return result;
        }
        finally
        {
            // This is a GUID directory created by this call. Never remove models or the user's source.
            try { Directory.Delete(working, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal Dictionary<string, string> EnvironmentVariables() => new()
    {
        ["HF_HOME"] = Path.Combine(_bundle, "models"),
        ["TORCH_HOME"] = Path.Combine(_root, "torch"),
        ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1", ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1",
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
    };

    internal static async Task RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, IProgress<LyricsProgress> progress,
        CancellationToken token, bool jsonProgress = false)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = new Queue<string>();
        async Task DrainAsync(StreamReader reader, bool stderr)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 2000) line = line[..2000];
                if (stderr)
                {
                    if (errors.Count == 12) errors.Dequeue();
                    errors.Enqueue(line);
                    // Model download progress is intentionally not mixed with the structured protocol.
                    if (!jsonProgress && !string.IsNullOrWhiteSpace(line)) progress.Report(new(line));
                }
                else if (jsonProgress)
                {
                    try
                    {
                        using var data = JsonDocument.Parse(line);
                        string message = data.RootElement.GetProperty("message").GetString() ?? "Working…";
                        double? fraction = data.RootElement.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetDouble(out var n)
                            && double.IsFinite(n) ? Math.Clamp(n, 0, 1) : null;
                        progress.Report(new(message, fraction));
                    }
                    catch (JsonException) { }
                    catch (InvalidOperationException) { }
                    catch (KeyNotFoundException) { }
                }
            }
        }
        await Task.WhenAll(DrainAsync(process.StandardOutput, false), DrainAsync(process.StandardError, true), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new LyricsProcessException(process.ExitCode, string.Join(Environment.NewLine, errors));
    }


}

internal sealed class LyricsProcessException(int exitCode, string detail)
    : Exception($"The local engine stopped (code {exitCode}). {detail}")
{
    public bool IsGpuFailure => exitCode == unchecked((int)0xC0000409) || exitCode == unchecked((int)0xC0000005)
        || new[] { "CUDA", "cuDNN", "cublas", "out of memory", "cudnn", "cudart" }.Any(s => detail.Contains(s, StringComparison.OrdinalIgnoreCase));
}
