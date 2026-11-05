using System.Diagnostics;
using System.Security.Cryptography;

namespace HomePlayer.AI;

public sealed class AiModelManager
{
    private static readonly string[] ArchiveSuffixes = [".tar.bz2", ".tar.gz", ".tgz"];

    private string _modelDirectory;
    private readonly Func<string> _proxy;

    public AiModelManager(string directory, Func<string> proxy)
    {
        _modelDirectory = directory;
        _proxy = proxy;
        Directory.CreateDirectory(_modelDirectory);
    }

    public string ModelDirectory => _modelDirectory;

    public void SetModelDirectory(string directory)
    {
        _modelDirectory = directory;
        Directory.CreateDirectory(_modelDirectory);
    }

    public string PathFor(AiModelInfo model) => Path.Combine(_modelDirectory, model.LocalFileName);

    public bool IsInstalled(AiModelInfo model)
    {
        var path = PathFor(model);
        if (!File.Exists(path) || new FileInfo(path).Length != model.Size)
            return false;
        var marker = path + ".verified";
        if (!File.Exists(marker))
            return false;
        try
        {
            return string.Equals(File.ReadAllText(marker).Trim(), model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<string> EnsureAsync(
        AiModelInfo model,
        string mirror,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var path = PathFor(model);
        if (IsInstalled(model))
            return path;
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
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.DownloadUrl))
        {
            if (!Uri.TryCreate(model.DownloadUrl, UriKind.Absolute, out var direct) || direct.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("模型下载地址无效。");
            urls.Add(model.DownloadUrl);
        }
        else
        {
            var bases = new[] { mirror.TrimEnd('/'), "https://huggingface.co" }
                .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (bases.Length == 0)
                throw new InvalidOperationException("模型镜像地址无效。");
            foreach (var baseUrl in bases)
                urls.Add($"{baseUrl}/{model.Repository}/resolve/{model.Revision}/{model.FileName}");
        }

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
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
        var directory = ArchiveDirectoryFor(model);
        if (directory != null && Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    /// <summary>
    /// 归档模型解压后的目录；不是归档则返回 null。
    /// </summary>
    public string? ArchiveDirectoryFor(AiModelInfo model)
    {
        var name = model.FileName;
        foreach (var suffix in ArchiveSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(_modelDirectory, name[..^suffix.Length]);
        }

        return null;
    }

    public bool IsArchiveInstalled(AiModelInfo model)
    {
        var directory = ArchiveDirectoryFor(model);
        if (directory == null || !Directory.Exists(directory))
            return false;
        var marker = Path.Combine(directory, ".verified");
        try
        {
            return File.Exists(marker)
                && string.Equals(File.ReadAllText(marker).Trim(), model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<string> EnsureArchiveAsync(
        AiModelInfo model,
        string mirror,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var directory = ArchiveDirectoryFor(model)
            ?? throw new InvalidOperationException("模型 " + model.Id + " 不是归档文件。");
        if (IsArchiveInstalled(model))
            return directory;

        var archive = await EnsureAsync(model, mirror, progress, cancellationToken);
        var extract = directory + ".extract";
        if (Directory.Exists(extract))
            Directory.Delete(extract, true);
        Directory.CreateDirectory(extract);
        try
        {
            ExtractArchive(archive, extract);
            var entries = Directory.GetFileSystemEntries(extract);
            var root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : extract;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            Directory.Move(root, directory);
            File.WriteAllText(Path.Combine(directory, ".verified"), model.Sha256);
            return directory;
        }
        finally
        {
            if (Directory.Exists(extract))
                Directory.Delete(extract, true);
        }
    }

    private static void ExtractArchive(string archive, string directory)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(executable))
            executable = "tar.exe";
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-xf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(directory);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动解压程序。");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException("模型解压失败：" + errors.Trim());
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
