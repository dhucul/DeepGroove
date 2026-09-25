using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WaveLab.Audio.Transcription;

public sealed record LyricsOptions(bool IsolateVocals, bool Speech, bool CompareOriginal,
    string Model, string? Language, string Hints, bool CpuOnly);
public sealed record LyricsProgress(string Message, double? Fraction = null);

/// <summary>A cancellable, isolated inference process. No audio leaves this computer.</summary>
public sealed class LyricsEngine
{
    private const string UvUrl = "https://github.com/astral-sh/uv/releases/download/0.12.3/uv-x86_64-pc-windows-msvc.zip";
    private const string UvSha256 = "B23350C79E8AD0192B8124AF13A0F17E8D4E4549524785E1AEF389AE5A06990E";
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private readonly string _root;
    private readonly string _assets;
    private string Python => Path.Combine(_root, "environment-v1", "Scripts", "python.exe");
    private string ReadyFile => Path.Combine(_root, "ready-v1.txt");

    public LyricsEngine(string? root = null, string? assets = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveLab", "Lyrics");
        _assets = assets ?? Path.Combine(AppContext.BaseDirectory, "Transcription");
    }

    private string RequirementsFingerprint => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_assets, "requirements.txt"))));
    public bool IsReady
    {
        get
        {
            try { return File.Exists(Python) && File.Exists(ReadyFile) && File.ReadAllText(ReadyFile) == RequirementsFingerprint; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public async Task SetupAsync(bool cpuOnly, IProgress<LyricsProgress> progress, CancellationToken token)
    {
        if (!Environment.Is64BitProcess || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new NotSupportedException("The local lyrics engine currently requires 64-bit Windows on an Intel or AMD processor.");
        await SetupGate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_root);
            File.Delete(ReadyFile);
            string uv = await EnsureUvAsync(progress, token);
            progress.Report(new("Preparing a private Python environment…"));
            await RunProcessAsync(uv, ["venv", "--allow-existing", "--python", "3.11", Path.GetDirectoryName(Path.GetDirectoryName(Python)!)!],
                EnvironmentVariables(), progress, token);
            bool gpu = !cpuOnly && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"));
            progress.Report(new(gpu ? "Downloading the NVIDIA engine (several GB)…" : "Downloading the CPU engine…"));
            await RunProcessAsync(uv, ["pip", "install", "--reinstall-package", "torch", "--python", Python, "--index-url",
                gpu ? "https://download.pytorch.org/whl/cu128" : "https://download.pytorch.org/whl/cpu", "torch==2.8.0"],
                EnvironmentVariables(), progress, token);
            progress.Report(new("Installing the lyrics and vocal isolation models' software…"));
            await RunProcessAsync(uv, ["pip", "install", "--python", Python, "--requirement", Path.Combine(_assets, "requirements.txt")],
                EnvironmentVariables(), progress, token);
            await RunProcessAsync(Python, ["-u", Path.Combine(_assets, "lyrics_worker.py"), "--check"],
                EnvironmentVariables(), progress, token, jsonProgress: true);
            await File.WriteAllTextAsync(ReadyFile, RequirementsFingerprint, token);
            progress.Report(new("Local engine ready. Model weights download the first time you transcribe.", 1));
        }
        finally { SetupGate.Release(); }
    }

    private async Task<string> EnsureUvAsync(IProgress<LyricsProgress> progress, CancellationToken token)
    {
        string executable = Path.Combine(_root, "uv-0.12.3.exe");
        if (File.Exists(executable)) return executable;
        progress.Report(new("Downloading and verifying the local engine installer…"));
        string archive = Path.Combine(_root, "uv.download");
        string temporary = executable + ".partial";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await client.GetAsync(UvUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var stream = File.Create(archive))
                await response.Content.CopyToAsync(stream, token);
            await using (var stream = File.OpenRead(archive))
                if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != UvSha256)
                    throw new InvalidDataException("The installer download failed its integrity check. Please try setup again.");
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.Entries.Single(e => e.Name == "uv.exe");
                await using var input = entry.Open();
                await using var output = File.Create(temporary);
                await input.CopyToAsync(output, token);
            }
            File.Move(temporary, executable, overwrite: true);
            return executable;
        }
        finally { TryDeleteFile(archive); TryDeleteFile(temporary); }
    }

    public async Task<LyricsTranscript> TranscribeAsync(float[][] channels, int rate, int start, int count,
        string title, int editVersion, LyricsOptions options, IProgress<LyricsProgress> progress, CancellationToken token)
    {
        if (!IsReady) throw new InvalidOperationException("Set up the local engine first.");
        if (options.Model is not ("large-v3" or "turbo")) throw new ArgumentException("Choose one of the supported speech models.");
        if (options.Hints.Length > 500) throw new ArgumentException("Keep spelling hints under 500 characters.");
        string working = Path.Combine(_root, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            string input = Path.Combine(working, "input.wav"), output = Path.Combine(working, "result.json");
            progress.Report(new("Preparing the selected audio…", 0));
            await Task.Run(() => LyricsAudio.Write(channels, rate, start, count, input, token), token);
            var arguments = new List<string> { "-u", Path.Combine(_assets, "lyrics_worker.py"), "--input", input,
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

    private Dictionary<string, string> EnvironmentVariables() => new()
    {
        ["UV_CACHE_DIR"] = Path.Combine(_root, "package-cache"),
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_root, "python"),
        ["HF_HOME"] = Path.Combine(_root, "models"),
        ["TORCH_HOME"] = Path.Combine(_root, "torch"),
        ["HF_HUB_DISABLE_TELEMETRY"] = "1", ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1", ["UV_NO_PROGRESS"] = "1",
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

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class LyricsProcessException(int exitCode, string detail)
    : Exception($"The local engine stopped (code {exitCode}). {detail}")
{
    public bool IsGpuFailure => exitCode == unchecked((int)0xC0000409) || exitCode == unchecked((int)0xC0000005)
        || new[] { "CUDA", "cuDNN", "cublas", "out of memory", "cudnn", "cudart" }.Any(s => detail.Contains(s, StringComparison.OrdinalIgnoreCase));
}
