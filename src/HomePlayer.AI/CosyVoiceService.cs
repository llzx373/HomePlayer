using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using HomePlayer.Core;

namespace HomePlayer.AI;

/// <summary>
/// CosyVoice.cpp 原生语音克隆引擎。参考音频先在前端（ONNX）里编码成 prompt speech，
/// 再由常驻的 OpenAI 兼容服务逐句合成；跨语言时不需要参考音频的逐字稿。
/// 服务器只能在启动时加载音色，换角色音色时采用“先起新、再停旧”的无停机切换。
/// </summary>
public sealed class CosyVoiceService : IAsyncDisposable
{
    public const string ModelName = "cosyvoice";

    private readonly string _modelPath;
    private readonly string _tokenizerPath;
    private readonly string _campplusPath;
    private readonly bool _useGpu;
    private readonly string _workDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StringBuilder _errors = new();
    private Process? _process;
    private HttpClient? _http;
    private Task? _errorReader;

    public CosyVoiceService(string modelPath, string tokenizerPath, string campplusPath, bool useGpu, string workDirectory)
    {
        _modelPath = modelPath;
        _tokenizerPath = tokenizerPath;
        _campplusPath = campplusPath;
        _useGpu = useGpu;
        _workDirectory = workDirectory;
    }

    /// <summary>
    /// 把参考音频和逐字稿编码成 prompt speech 文件，返回文件路径。
    /// </summary>
    public async Task<string> PrepareVoiceAsync(string id, string referenceAudio, string transcript, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workDirectory);
        var output = Path.Combine(_workDirectory, id + ".gguf");
        if (File.Exists(output))
            File.Delete(output);

        var info = new ProcessStartInfo(AiRuntime.CosyVoiceCliExecutable)
        {
            WorkingDirectory = AiRuntime.CosyVoiceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "--frontend-only",
                     "--speech-tokenizer", _tokenizerPath,
                     "--campplus", _campplusPath,
                     "--prompt-audio", referenceAudio,
                     "--prompt-text", transcript,
                     "--prompt-speech-output", output
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 CosyVoice 前端编码。");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                errors.AppendLine(args.Data);
        };
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("音色参考编码失败：" + Tail(errors.ToString()));
        return output;
    }

    public async Task StartAsync(
        IReadOnlyDictionary<string, string> promptSpeech,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process != null)
                return;
            Assign(await LaunchAsync(promptSpeech, progress, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 用新的音色集合替换当前服务：先启动新服务器，健康后再切换并停掉旧服务器。
    /// 新服务器失败时保留旧服务器。
    /// </summary>
    public async Task SwapVoicesAsync(
        IReadOnlyDictionary<string, string> promptSpeech,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process == null)
            {
                Assign(await LaunchAsync(promptSpeech, null, cancellationToken));
                return;
            }

            var session = await LaunchAsync(promptSpeech, null, cancellationToken);
            var oldProcess = _process;
            var oldHttp = _http;
            Assign(session);
            oldHttp?.Dispose();
            if (oldProcess != null)
            {
                AiProcess.TryKill(oldProcess);
                oldProcess.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(int SampleRate, long Samples)> SynthesizeAsync(
        string text,
        string voiceId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var http = _http ?? throw new InvalidOperationException("CosyVoice 语音引擎尚未启动。");
            var payload = JsonSerializer.Serialize(new
            {
                model = ModelName,
                input = text,
                voice = voiceId,
                response_format = "wav",
                speed = 1.0
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/v1/audio/speech", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("语音合成失败：" + await TailAsync(response, cancellationToken));

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);

            var info = WavFile.ReadInfo(outputPath);
            return (info.SampleRate, info.Frames);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _http?.Dispose();
            _http = null;
            var process = _process;
            _process = null;
            if (process != null)
            {
                AiProcess.TryKill(process);
                process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_errorReader != null)
        {
            try
            {
                await _errorReader;
            }
            catch
            {
            }
        }
    }

    private async Task<ServerSession> LaunchAsync(
        IReadOnlyDictionary<string, string> promptSpeech,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workDirectory);
        var port = FreePort();
        var errors = new StringBuilder();

        var info = new ProcessStartInfo(AiRuntime.CosyVoiceServerExecutable)
        {
            WorkingDirectory = AiRuntime.CosyVoiceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "--api",
                     "--model", _modelPath,
                     "--served-model-name", ModelName,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--backend", _useGpu ? "auto" : "cpu",
                     "--speech-tokenizer", _tokenizerPath,
                     "--campplus", _campplusPath
                 })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, path) in promptSpeech)
        {
            info.ArgumentList.Add("--voice-prompt");
            info.ArgumentList.Add(name + "=" + path);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 CosyVoice 语音引擎。");
        AiProcess.Track(process);
        var reader = Task.Run(() => DrainAsync(process, errors));
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            Report(progress, "等待语音引擎就绪", 0.5);
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("CosyVoice 语音引擎启动失败。" + ErrorTail(errors));
                try
                {
                    using var response = await http.GetAsync("/healthz", cancellationToken);
                    if (response.IsSuccessStatusCode)
                        return new ServerSession(process, http, port, errors, reader);
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(300, cancellationToken);
            }

            throw new InvalidOperationException("CosyVoice 语音引擎启动超时。" + ErrorTail(errors));
        }
        catch
        {
            http.Dispose();
            AiProcess.TryKill(process);
            process.Dispose();
            throw;
        }
    }

    private void Assign(ServerSession session)
    {
        _process = session.Process;
        _http = session.Http;
        _errors = session.Errors;
        _errorReader = session.Reader;
    }

    private static async Task DrainAsync(Process process, StringBuilder errors)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line == null)
                    break;
                lock (errors)
                {
                    errors.AppendLine(line);
                    if (errors.Length > 16_000)
                        errors.Remove(0, errors.Length - 16_000);
                }
            }
        }
        catch
        {
        }
    }

    private async Task<string> TailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return Tail(text);
        }
        catch
        {
            return response.StatusCode.ToString();
        }
    }

    private static string ErrorTail(StringBuilder errors)
    {
        lock (errors)
            return Tail(errors.ToString());
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 600 ? text : text[^600..];
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record ServerSession(
        Process Process,
        HttpClient Http,
        int Port,
        StringBuilder Errors,
        Task Reader);
}
