using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WaveLab.Audio.Transcription;

public sealed record LyricsOptions(bool IsolateVocals, bool Speech, bool CompareOriginal,
    string Model, string? Language, string Hints, bool CpuOnly, bool RetryUnclear = true);
public sealed record LyricsProgress(string Message, double? Fraction = null);

/// <summary>A cancellable, isolated inference process. No audio leaves this computer.</summary>
public sealed class LyricsEngine
{
    private readonly string _root;
    private readonly string _assets;
    private readonly string _bundle;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task>? _runWorker;
    private string Python => Path.Combine(_bundle, "python", "python.exe");

    public LyricsEngine(string? root = null, string? assets = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveLab", "Lyrics");
        _assets = assets ?? Path.Combine(AppContext.BaseDirectory, "Transcription");
        _bundle = Path.Combine(_assets, "Engine");
    }

    internal LyricsEngine(string root, string assets, Func<IReadOnlyList<string>, CancellationToken, Task> runWorker)
        : this(root, assets) => _runWorker = runWorker;

    public bool IsReady => LyricsBundle.IsReady(_assets);
    private string CacheIdentity => $"{Path.GetFullPath(_bundle)}|{File.GetLastWriteTimeUtc(Path.Combine(_bundle, "bundle.json")).Ticks}|htdemucs_ft-2-0.5-7-v1";

    public async Task<LyricsTranscript> TranscribeAsync(float[][] channels, int rate, int start, int count,
        string title, int editVersion, LyricsOptions options, IProgress<LyricsProgress> progress, CancellationToken token,
        LyricsVocalCache? vocalCache = null)
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
            var cached = options.IsolateVocals
                ? vocalCache?.Find(channels, rate, editVersion, start, count, options.CpuOnly, CacheIdentity) : null;
            if (options.IsolateVocals) arguments.Add("--isolate");
            if (cached != null)
            {
                progress.Report(new("Reusing the extracted vocals…"));
                string cachedInput = Path.Combine(working, "cached-vocals.wav");
                await Task.Run(() => LyricsAudio.Write(cached.Channels, LyricsAudio.AnalysisRate,
                    cached.Start, cached.Count, cachedInput, token), token);
                arguments.AddRange(["--vocals-input", cachedInput]);
            }
            if (options.Speech) arguments.Add("--speech");
            if (options.CompareOriginal && options.IsolateVocals && !options.Speech) arguments.Add("--compare");
            arguments.Add(options.RetryUnclear && !options.Speech ? "--retry-unclear" : "--no-retry-unclear");
            if (!string.IsNullOrWhiteSpace(options.Language)) { arguments.Add("--language"); arguments.Add(options.Language); }
            if (!string.IsNullOrWhiteSpace(options.Hints)) { arguments.Add("--hints"); arguments.Add(options.Hints); }
            await RunWorkerAsync(arguments, options.CpuOnly, progress, token);
            token.ThrowIfCancellationRequested();
            var result = JsonSerializer.Deserialize<LyricsTranscript>(await File.ReadAllTextAsync(output, token), LyricsTranscript.JsonOptions)
                ?? throw new InvalidDataException("No transcript was returned.");
            result.MapToSource((double)start / rate, (double)count / rate);
            result.SourceTitle = title;
            result.SourceEditVersion = editVersion;
            if (vocalCache != null && cached == null && result.IsolatedVocals)
            {
                var vocals = await Task.Run(() => ReadVocals(Path.Combine(working, "vocals.wav"), title,
                    (double)count / rate, start != 0 || count != channels[0].Length, token), token);
                token.ThrowIfCancellationRequested();
                vocalCache.Store(channels, rate, editVersion, start, count, options.CpuOnly, CacheIdentity, vocals);
            }
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

    /// <summary>Extract a new float audio document, without running speech recognition or changing the source.</summary>
    public async Task<AudioDocument> IsolateVocalsAsync(float[][] channels, int rate, int start, int count,
        string title, bool cpuOnly, IProgress<LyricsProgress> progress, CancellationToken token,
        LyricsVocalCache? vocalCache = null, int editVersion = 0)
    {
        if (!IsReady) throw new InvalidOperationException("This installation is missing its built-in transcription files. Reinstall Deep Groove or rebuild the complete Release application.");
        var cached = vocalCache?.Find(channels, rate, editVersion, start, count, cpuOnly, CacheIdentity);
        if (cached != null)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new("Reusing the extracted vocals…", 1));
            var copy = await Task.Run(() => cached.ToDocument(title, start != 0 || count != channels[0].Length), token);
            token.ThrowIfCancellationRequested();
            return copy;
        }
        string working = Path.Combine(_root, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            string input = Path.Combine(working, "input.wav"), output = Path.Combine(working, "result.json");
            progress.Report(new("Preparing audio for vocal isolation…", 0));
            await Task.Run(() => LyricsAudio.Write(channels, rate, start, count, input, token), token);
            var arguments = new List<string> { "-I", "-B", "-X", "utf8", "-u", Path.Combine(_assets, "lyrics_worker.py"),
                "--input", input, "--output", output, "--vocals-only", "--device", cpuOnly ? "cpu" : "auto" };
            await RunWorkerAsync(arguments, cpuOnly, progress, token);
            var vocals = await Task.Run(() => ReadVocals(Path.Combine(working, "vocals.wav"), title,
                (double)count / rate, start != 0 || count != channels[0].Length, token), token);
            token.ThrowIfCancellationRequested();
            vocalCache?.Store(channels, rate, editVersion, start, count, cpuOnly, CacheIdentity, vocals);
            return vocals;
        }
        finally
        {
            try { Directory.Delete(working, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static AudioDocument ReadVocals(string path, string title, double duration, bool selection,
        CancellationToken token)
    {
        var vocals = WavCodec.Load(path, token);
        if (vocals.SampleRate != LyricsAudio.AnalysisRate || vocals.ChannelCount != 2
            || vocals.SourceBitDepth != 32 || vocals.Length == 0 || Math.Abs(vocals.Duration - duration) > .05)
            throw new InvalidDataException("The vocal separator returned an unexpected audio format or duration.");
        foreach (var channel in vocals.Channels)
        {
            for (int i = 0; i < channel.Length; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                if (!float.IsFinite(channel[i])) throw new InvalidDataException("The vocal separator returned invalid samples.");
            }
        }
        // The worker file is temporary. Save must prompt for a new destination, never
        // point to the job directory or the original recording.
        vocals.FilePath = null;
        vocals.RequiresSaveAs = true;
        vocals.Title = Path.GetFileNameWithoutExtension(title) + (selection ? " - vocals (selection).wav" : " - vocals.wav");
        vocals.MarkUnsaved();
        return vocals;
    }

    private async Task RunWorkerAsync(List<string> arguments, bool cpuOnly, IProgress<LyricsProgress> progress,
        CancellationToken token)
    {
        if (_runWorker != null)
        {
            token.ThrowIfCancellationRequested();
            await _runWorker(arguments, token);
            return;
        }
        try
        {
            await RunProcessAsync(Python, arguments, EnvironmentVariables(), progress, token, jsonProgress: true);
        }
        catch (LyricsProcessException ex) when (!cpuOnly && ex.IsGpuFailure)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new("The GPU could not finish. Retrying on the CPU; this will take longer…"));
            arguments[arguments.IndexOf("--device") + 1] = "cpu";
            await RunProcessAsync(Python, arguments, EnvironmentVariables(), progress, token, jsonProgress: true);
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
