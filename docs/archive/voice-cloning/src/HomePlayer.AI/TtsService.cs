using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.AI;

/// <summary>
/// 常驻语音合成进程。模型只加载一次，之后按行收发 JSON 请求。
/// </summary>
public sealed class TtsService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly AiModelManager _models;
    private readonly AiComputeDevice _device;
    private readonly Func<string> _mirror;
    private readonly string _voicesDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _reader;
    private Task? _errorReader;
    private Task? _start;
    private int _nextId;
    private int _disposed;

    public TtsService(AiModelManager models, AiComputeDevice device, Func<string> mirror, string voicesDirectory)
    {
        _models = models;
        _device = device;
        _mirror = mirror;
        _voicesDirectory = voicesDirectory;
    }

    public Task StartAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken) =>
        _start ??= StartCoreAsync(progress, cancellationToken);

    public async Task<(int SampleRate, long Samples)> SynthesizeAsync(
        string text,
        string voiceId,
        string outputPath,
        int steps,
        CancellationToken cancellationToken)
    {
        if (_start == null)
            throw new InvalidOperationException("语音合成服务尚未启动。");
        await _start.WaitAsync(cancellationToken);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var request = JsonSerializer.Serialize(new Request(id, text, voiceId, 1f, steps, outputPath), Json);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _input!.WriteLineAsync(request);
                await _input.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            using var registration = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending))
                    pending.TrySetCanceled(cancellationToken);
            });
            var response = await completion.Task.WaitAsync(cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error) ? "语音合成失败。" : response.Error);
            return (response.SampleRate, response.Samples);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        StopWorker();
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var pending))
                pending.TrySetCanceled();
        }

        foreach (var reader in new[] { _reader, _errorReader })
        {
            if (reader == null)
                continue;
            try
            {
                await reader;
            }
            catch
            {
            }
        }
    }

    private async Task StartCoreAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken)
    {
        var model = AiSubtitleCatalog.Model("zipvoice-model");
        var vocoder = AiSubtitleCatalog.Model("zipvoice-vocoder");
        Report(progress, "检查语音合成模型", 0.02);
        var modelDirectory = await _models.EnsureArchiveAsync(model, _mirror(),
            new Progress<double>(value => Report(progress, "下载语音合成模型", value * 0.85)), cancellationToken);
        var vocoderPath = await _models.EnsureAsync(vocoder, _mirror(),
            new Progress<double>(value => Report(progress, "下载声码器", 0.85 + value * 0.15)), cancellationToken);

        if (_device != AiComputeDevice.Cpu)
        {
            try
            {
                var gpuReady = StartWorker(modelDirectory, vocoderPath, true);
                await gpuReady.Task.WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);
                return;
            }
            catch (Exception) when (_device == AiComputeDevice.Auto)
            {
                StopWorker();
            }
        }

        var ready = StartWorker(modelDirectory, vocoderPath, false);
        await ready.Task.WaitAsync(cancellationToken);
    }

    private TaskCompletionSource StartWorker(string modelDirectory, string vocoderPath, bool useGpu)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new OperationCanceledException();
        var info = AiProcess.StartInfo(AiRuntime.TtsWorkerExecutable, AiRuntime.TtsWorkerDirectory);
        info.ArgumentList.Add("--serve");
        info.ArgumentList.Add(modelDirectory);
        info.ArgumentList.Add(vocoderPath);
        info.ArgumentList.Add(_voicesDirectory);
        if (useGpu)
            info.ArgumentList.Add("--gpu");
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动本地语音合成进程。");
        AiProcess.Track(process);
        if (Volatile.Read(ref _disposed) != 0)
        {
            AiProcess.TryKill(process);
            process.Dispose();
            throw new OperationCanceledException();
        }

        _process = process;
        _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader = Task.Run(() => ReadLoopAsync(process, ready));
        _errorReader = Task.Run(() => DrainErrorsAsync(process));
        return ready;
    }

    private async Task DrainErrorsAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                lock (_errors)
                {
                    _errors.AppendLine(line);
                    if (_errors.Length > 16_000)
                        _errors.Remove(0, _errors.Length - 16_000);
                }
            }
        }
        catch
        {
        }
    }

    private async Task ReadLoopAsync(Process process, TaskCompletionSource ready)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                Response? response;
                try
                {
                    response = JsonSerializer.Deserialize<Response>(line, Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response == null)
                    continue;
                if (response.Ready)
                {
                    ready.TrySetResult();
                    continue;
                }

                if (_pending.TryRemove(response.Id, out var completion))
                    completion.TrySetResult(response);
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
        finally
        {
            var message = "语音合成进程已退出。" + ErrorTail();
            ready.TrySetException(new InvalidOperationException(message));
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var completion))
                    completion.TrySetException(new InvalidOperationException(message));
            }
        }
    }

    private string ErrorTail()
    {
        lock (_errors)
        {
            if (_errors.Length == 0)
                return "";
            var text = _errors.ToString().Trim();
            return text.Length <= 600 ? text : text[^600..];
        }
    }

    private void StopWorker()
    {
        var process = _process;
        _process = null;
        try
        {
            _input?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _input = null;
        if (process == null)
            return;
        AiProcess.TryKill(process);
        process.Dispose();
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record Request(int Id, string Text, string Voice, float Speed, int Steps, string Output);

    private sealed record Response(
        int Id,
        bool Ok,
        string? Path,
        string? Error,
        int SampleRate,
        long Samples,
        long ElapsedMs,
        bool Ready);
}
