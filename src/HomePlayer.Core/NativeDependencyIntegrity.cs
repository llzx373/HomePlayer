using System.Security.Cryptography;
using System.Text.Json;

namespace HomePlayer.Core;

public static class NativeDependencyIntegrity
{
    public static void Verify(string directory, string relativeDirectory, IReadOnlyList<string> requiredFiles)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "sbom.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("缺少原生依赖清单 sbom.json。");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("files", out var files))
            throw new InvalidOperationException("原生依赖清单缺少 files。");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            var name = file.GetProperty("fileName").GetString();
            var checksum = file.GetProperty("checksums")[0].GetProperty("checksumValue").GetString();
            if (name != null && checksum != null)
                expected[name] = checksum;
        }
        foreach (var file in requiredFiles)
        {
            var key = $"third_party/{relativeDirectory}/{file}";
            if (!expected.TryGetValue(key, out var hash))
                throw new InvalidOperationException($"原生依赖清单没有 {key}。");
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
                throw new InvalidOperationException($"缺少原生依赖 {key}。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(hash)))
            {
                throw new InvalidOperationException($"原生依赖哈希不一致：{key}。");
            }
        }
    }
}
