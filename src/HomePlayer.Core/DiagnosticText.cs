using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public static partial class DiagnosticText
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var sanitized = UrlCredentials().Replace(text, "$1***@");
        sanitized = UncCredentials().Replace(sanitized, @"\\***@");
        return SensitiveParameter().Replace(sanitized, "$1***");
    }

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.-]*://)(?<credentials>[^/\s:@]+(?::[^/\s@]*)?@)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\\\\[^\\\s:@]+(?::[^\\\s@]+)?@", RegexOptions.CultureInvariant)]
    private static partial Regex UncCredentials();

    [GeneratedRegex(@"(?<prefix>[?&](?:token|sig|signature|key|api_key|apikey|password|secret|auth)=)[^&#\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveParameter();
}
