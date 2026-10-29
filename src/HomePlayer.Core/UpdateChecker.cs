using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace HomePlayer.Core;

public sealed record UpdateInfo(string Version, string Url, bool Prerelease);
public sealed record UpdateCheckResult(bool HasUpdate, string CurrentVersion, UpdateInfo? Latest)
{
    public string Message => Latest == null
        ? "没有收到版本信息。"
        : HasUpdate
            ? $"发现新版本 {Latest.Version}（当前 {CurrentVersion}）。"
            : $"当前已是最新版本（{CurrentVersion}）。";
}

public static class UpdateChecker
{
    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, string channel, HttpClient? client = null)
    {
        var owned = client == null;
        client ??= new HttpClient();
        try
        {
            if (!client.DefaultRequestHeaders.UserAgent.Any())
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HomePlayer", "1"));
            var beta = channel.Equals("beta", StringComparison.OrdinalIgnoreCase);
            var url = beta
                ? "https://api.github.com/repos/llzx373/HomePlayer/releases?per_page=10"
                : "https://api.github.com/repos/llzx373/HomePlayer/releases/latest";
            var json = await client.GetStringAsync(url);
            return Parse(json, currentVersion, beta);
        }
        finally
        {
            if (owned)
                client.Dispose();
        }
    }

    public static UpdateCheckResult Parse(string json, string currentVersion, bool beta)
    {
        using var document = JsonDocument.Parse(json);
        var releases = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        foreach (var release in releases)
        {
            if (!release.TryGetProperty("tag_name", out var tag))
                continue;
            var version = tag.GetString() ?? "";
            var candidate = new UpdateInfo(
                version,
                release.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean());
            if (!beta && candidate.Prerelease)
                continue;
            return new UpdateCheckResult(Compare(version, currentVersion) > 0, currentVersion, candidate);
        }
        return new UpdateCheckResult(false, currentVersion, null);
    }

    public static string CurrentVersion()
    {
        var version = typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        return version.Split('+')[0];
    }

    public static int Compare(string left, string right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        for (var i = 0; i < 3; i++)
        {
            var comparison = a.Numbers[i].CompareTo(b.Numbers[i]);
            if (comparison != 0)
                return comparison;
        }
        if (string.IsNullOrEmpty(a.Suffix) && !string.IsNullOrEmpty(b.Suffix))
            return 1;
        if (!string.IsNullOrEmpty(a.Suffix) && string.IsNullOrEmpty(b.Suffix))
            return -1;
        return NumericSuffix(a.Suffix).CompareTo(NumericSuffix(b.Suffix));
    }

    private static (int[] Numbers, string Suffix) ParseVersion(string version)
    {
        var value = version.TrimStart('v', 'V').Split('+')[0];
        var parts = value.Split('-', 2);
        var numbers = parts[0].Split('.');
        return ([
            numbers.Length > 0 && int.TryParse(numbers[0], out var major) ? major : 0,
            numbers.Length > 1 && int.TryParse(numbers[1], out var minor) ? minor : 0,
            numbers.Length > 2 && int.TryParse(numbers[2], out var patch) ? patch : 0
        ], parts.Length > 1 ? parts[1] : "");
    }

    private static int NumericSuffix(string suffix) =>
        int.TryParse(new string(suffix.TakeWhile(char.IsDigit).ToArray()), out var value) ? value : 0;
}
