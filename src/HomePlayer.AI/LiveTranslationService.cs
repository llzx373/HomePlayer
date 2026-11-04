using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed class LiveTranslationService : IAsyncDisposable
{
    private readonly AiModelManager _models;
    private readonly string _modelId;
    private readonly string? _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly AiComputeDevice _device;
    private readonly Func<string> _mirror;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _reader;
    private Task? _errorReader;
    private Task? _start;
    private int _nextId;
    private int _disposed;

    public LiveTranslationService(
        AiModelManager models,
        string modelId,
        string? sourceLanguage,
        string targetLanguage,
        AiComputeDevice device,
        Func<string> mirror)
    {
        _models = models;
        _modelId = modelId;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _device = device;
        _mirror = mirror;
    }

    public Task StartAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken) =>
        _start ??= StartCoreAsync(progress, cancellationToken);

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (_start == null)
            throw new InvalidOperationException("翻译服务尚未启动。");
        await _start.WaitAsync(cancellationToken);
        var translated = await TranslateOnceAsync(text, false, cancellationToken);
        if (!NeedsRetry(translated, text))
            return translated;
        translated = await TranslateOnceAsync(text, true, cancellationToken);
        if (NeedsRetry(translated, text))
            throw new InvalidOperationException("翻译结果仍保留原文。");
        return translated;
    }

    private async Task<string> TranslateOnceAsync(string text, bool strict, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var prompt = AiTranslationText.Prompt(
                [new SubtitleTranslationInput(0, text.Replace("\r", ""))],
                _sourceLanguage ?? "",
                _targetLanguage,
                strict);
            var request = JsonSerializer.Serialize(new Request(id, prompt));
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
            var output = await completion.Task.WaitAsync(cancellationToken);
            var parsed = AiTranslationText.Parse(output, [0]);
            if (parsed.TryGetValue(0, out var translated) && !string.IsNullOrWhiteSpace(translated))
                return translated.Trim();
            var cleaned = AiTranslationText.CleanModelOutput(output);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
            throw new InvalidOperationException("翻译结果无法解析。");
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private bool NeedsRetry(string translated, string source)
    {
        var target = _targetLanguage.Split('-', '_')[0];
        if (target != "ja" && ContainsForeignScript(translated, '\u3040', '\u30ff'))
            return true;
        if (target != "ko" && ContainsForeignScript(translated, '\uac00', '\ud7af'))
            return true;
        return !string.IsNullOrWhiteSpace(source)
            && !LooksLikeTarget(source, target)
            && Normalized(translated) == Normalized(source);
    }

    private static bool LooksLikeTarget(string text, string target)
    {
        var kana = CountRange(text, '\u3040', '\u30ff');
        var hangul = CountRange(text, '\uac00', '\ud7af');
        var han = CountRange(text, '\u3400', '\u9fff');
        var latin = text.Count(character => character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z');
        var letters = text.Count(char.IsLetterOrDigit);
        if (letters == 0)
            return false;
        return target switch
        {
            "zh" => kana == 0 && hangul == 0 && han * 2 >= letters,
            "ja" => kana > 0,
            "ko" => hangul * 2 >= letters,
            "en" => latin * 2 >= letters,
            _ => false
        };
    }

    private static int CountRange(string text, char first, char last)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character >= first && character <= last)
                count++;
        }
        return count;
    }

    private static bool ContainsForeignScript(string text, char first, char last)
    {
        var foreign = 0;
        var letters = 0;
        foreach (var character in text)
        {
            if (character >= first && character <= last)
                foreign++;
            if (char.IsLetterOrDigit(character))
                letters++;
        }
        return foreign >= 3 && letters > 0 && foreign * 10 >= letters * 3;
    }

    private static string Normalized(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
        var model = AiSubtitleCatalog.Model(_modelId);
        if (model.Kind != AiModelKind.Translation)
            throw new InvalidOperationException("所选模型不是翻译模型。");
        Report(progress, "检查翻译模型", 0.02);
        var modelPath = await _models.EnsureAsync(model, _mirror(),
            new Progress<double>(value => Report(progress, "下载翻译模型", value)), cancellationToken);
        var useGpu = _device != AiComputeDevice.Cpu;
        if (useGpu)
        {
            try
            {
                var gpuReady = StartWorker(modelPath, true);
                await gpuReady.Task.WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);
                return;
            }
            catch (Exception) when (_device == AiComputeDevice.Auto)
            {
                StopWorker();
            }
        }
        var ready = StartWorker(modelPath, false);
        await ready.Task.WaitAsync(cancellationToken);
    }

    private TaskCompletionSource StartWorker(string modelPath, bool useGpu)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new OperationCanceledException();
        var info = AiProcess.StartInfo(AiRuntime.WorkerExecutable, AiRuntime.WorkerDirectory);
        info.ArgumentList.Add("--serve");
        info.ArgumentList.Add(modelPath);
        if (useGpu)
            info.ArgumentList.Add("--gpu");
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动本地翻译进程。");
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
                    response = JsonSerializer.Deserialize<Response>(line);
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
                if (!_pending.TryRemove(response.Id, out var completion))
                    continue;
                if (string.IsNullOrWhiteSpace(response.Text))
                    completion.TrySetException(new InvalidOperationException("翻译模型没有返回结果。"));
                else
                    completion.TrySetResult(response.Text);
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
        finally
        {
            var message = "翻译进程已退出。" + ErrorTail();
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

    private sealed record Request(int Id, string Prompt);
    private sealed record Response(int Id, string? Text, bool Ready);
}
