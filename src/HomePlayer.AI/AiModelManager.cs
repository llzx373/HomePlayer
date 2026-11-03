using System.Security.Cryptography;

namespace HomePlayer.AI;

public sealed class AiModelManager
{
    private readonly string _directory;
    private readonly Func<string> _proxy;

    public AiModelManager(string directory, Func<string> proxy)
    {
        _directory = directory;
        _proxy = proxy;
        Directory.CreateDirectory(_directory);
    }

    public string PathFor(AiModelInfo model) => Path.Combine(_directory, model.LocalFileName);

    public bool IsInstalled(AiModelInfo model)
    {
        var path = PathFor(model);
        return File.Exists(path) && new FileInfo(path).Length == model.Size && File.Exists(path + ".verified");
    }

    public async Task<string> EnsureAsync(
        AiModelInfo model,
        string mirror,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var path = PathFor(model);
        if (File.Exists(path))
        {
            try
            {
                await VerifyAsync(model, path, cancellationToken);
                File.WriteAllText(path + ".verified", model.Sha256);
                return path;
            }
            catch (InvalidDataException)
            {
                File.Delete(path);
                if (File.Exists(path + ".verified"))
                    File.Delete(path + ".verified");
            }
        }

        var temporary = path + ".download";
        if (string.IsNullOrWhiteSpace(mirror))
            mirror = AiSubtitleCatalog.DefaultMirror;
        var bases = new[] { mirror.TrimEnd('/'), "https://huggingface.co" }
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bases.Length == 0)
            throw new InvalidOperationException("模型镜像地址无效。");

        Exception? last = null;
        foreach (var baseUrl in bases)
        {
            try
            {
                var url = $"{baseUrl}/{model.Repository}/resolve/{model.Revision}/{model.FileName}";
                await DownloadAsync(url, temporary, model.Size, progress, cancellationToken);
                await VerifyAsync(model, temporary, cancellationToken);
                File.Move(temporary, path, true);
                File.WriteAllText(path + ".verified", model.Sha256);
                progress?.Report(1);
                return path;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                last = ex;
                if (ex is InvalidDataException && File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        throw new InvalidOperationException($"模型 {model.DisplayName} 下载或校验失败：{last?.Message ?? "未知错误"}", last);
    }

    public void Delete(AiModelInfo model)
    {
        var path = PathFor(model);
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path + ".verified"))
            File.Delete(path + ".verified");
        if (File.Exists(path + ".download"))
            File.Delete(path + ".download");
    }

    private async Task DownloadAsync(
        string url,
        string path,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (existing > expectedSize)
            throw new InvalidDataException("模型临时文件超过清单大小。");
        if (existing == expectedSize)
            return;
        using var client = CreateClient(_proxy());
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing == expectedSize)
            return;
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var total = append ? existing : 0;
        var expectedResponse = expectedSize - total;
        if (response.Content.Headers.ContentLength is long length && length != expectedResponse)
            throw new InvalidDataException("模型响应大小与清单不一致。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        var lastProgressAt = 0L;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > expectedSize)
                throw new InvalidDataException("模型下载超过清单大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var now = Environment.TickCount64;
            if (progress != null && (now - lastProgressAt >= 200 || total == expectedSize))
            {
                lastProgressAt = now;
                progress.Report((double)total / expectedSize);
            }
        }

        if (total != expectedSize)
            throw new IOException("模型下载不完整。");
    }

    private static HttpClient CreateClient(string proxyAddress)
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(proxyAddress))
            handler.UseProxy = false;
        else if (Uri.TryCreate(proxyAddress, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            handler.Proxy = new System.Net.WebProxy(uri);
            handler.UseProxy = true;
        }
        else
            throw new InvalidOperationException("下载代理地址无效。");
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task VerifyAsync(AiModelInfo model, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(model.Sha256)))
        {
            throw new InvalidDataException("模型 SHA-256 校验失败。");
        }
    }
}
