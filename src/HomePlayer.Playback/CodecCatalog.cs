using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HomePlayer.Core;
using Microsoft.Win32;

namespace HomePlayer.Playback;

public enum CodecKind
{
    Splitter,
    VideoDecoder,
    AudioDecoder
}

public enum CodecBitness
{
    X64,
    X86
}

public sealed class InstalledCodec
{
    public required string Id { get; init; }
    public required Guid Clsid { get; init; }
    public required CodecKind Kind { get; init; }
    public required CodecBitness Bitness { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? Version { get; init; }
    public int Merit { get; init; }
    public bool Usable { get; init; }
    public bool Enabled { get; set; }

    public string BitnessText => Bitness == CodecBitness.X64 ? "x64" : "x86";
}

public sealed class CodecPlan
{
    public static CodecPlan Empty { get; } = new([], [], []);

    public CodecPlan(
        IReadOnlyList<InstalledCodec> splitters,
        IReadOnlyList<InstalledCodec> videoDecoders,
        IReadOnlyList<InstalledCodec> audioDecoders)
    {
        Splitters = splitters;
        VideoDecoders = videoDecoders;
        AudioDecoders = audioDecoders;
    }

    public IReadOnlyList<InstalledCodec> Splitters { get; }
    public IReadOnlyList<InstalledCodec> VideoDecoders { get; }
    public IReadOnlyList<InstalledCodec> AudioDecoders { get; }
}

public static class CodecCatalog
{
    private static readonly Guid FilterCategory = new("083863F1-70DE-11d0-BD40-00A0C911CE86");

    public static CodecPlan Plan(AppSettings settings)
    {
        var all = Scan();
        return new CodecPlan(
            Enabled(Display(all, CodecKind.Splitter, settings.SplitterOrder)),
            Enabled(Display(all, CodecKind.VideoDecoder, settings.VideoDecoderOrder)),
            Enabled(Display(all, CodecKind.AudioDecoder, settings.AudioDecoderOrder)));
    }

    public static IReadOnlyList<InstalledCodec> Display(IReadOnlyList<InstalledCodec> all, CodecKind kind, IReadOnlyList<CodecPreference>? saved)
        => Merge(Defaults(all, kind), saved);

    public static IReadOnlyList<InstalledCodec> Scan()
    {
        var found = new List<InstalledCodec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(RegistryView.Registry64, CodecBitness.X64, found, seen);
        Collect(RegistryView.Registry32, CodecBitness.X86, found, seen);
        return found;
    }

    public static string? LoadedModule(string? registeredPath)
    {
        if (string.IsNullOrWhiteSpace(registeredPath))
            return null;
        var file = Path.GetFileName(registeredPath);
        var handle = string.IsNullOrWhiteSpace(file) ? 0 : GetModuleHandleW(file);
        if (handle == 0)
            handle = GetModuleHandleW(registeredPath);
        if (handle == 0)
            return registeredPath;
        var buffer = new char[1024];
        var length = GetModuleFileNameW(handle, buffer, buffer.Length);
        return length <= 0 ? registeredPath : new string(buffer, 0, length);
    }

    private static List<InstalledCodec> Defaults(IReadOnlyList<InstalledCodec> all, CodecKind kind)
    {
        return all.Where(item => item.Kind == kind)
            .OrderBy(item => item.Usable ? 0 : 1)
            .ThenBy(Priority)
            .ThenByDescending(item => item.Merit)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();
    }

    private static List<InstalledCodec> Merge(List<InstalledCodec> defaults, IReadOnlyList<CodecPreference>? saved)
    {
        foreach (var item in defaults)
            item.Enabled = item.Usable;
        if (saved == null || saved.Count == 0)
            return defaults;

        var map = defaults.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledCodec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in saved)
        {
            if (string.IsNullOrWhiteSpace(preference.Id) || !map.TryGetValue(preference.Id, out var codec) || !seen.Add(codec.Id))
                continue;
            codec.Enabled = preference.Enabled && codec.Usable;
            result.Add(codec);
        }

        foreach (var codec in defaults)
        {
            if (seen.Add(codec.Id))
                result.Add(codec);
        }

        return result;
    }

    private static IReadOnlyList<InstalledCodec> Enabled(IReadOnlyList<InstalledCodec> items)
        => items.Where(item => item.Enabled && item.Usable).ToList();

    private static InstalledCodec Clone(InstalledCodec source) => new()
    {
        Id = source.Id,
        Clsid = source.Clsid,
        Kind = source.Kind,
        Bitness = source.Bitness,
        Name = source.Name,
        Path = source.Path,
        Version = source.Version,
        Merit = source.Merit,
        Usable = source.Usable,
        Enabled = source.Enabled
    };

    private static int Priority(InstalledCodec codec)
    {
        if (codec.Kind == CodecKind.Splitter && codec.Clsid == DsGuid.LavSplitterSource)
            return 0;
        if (codec.Kind == CodecKind.Splitter && codec.Clsid == DsGuid.LavSplitter)
            return 1;
        if (codec.Kind == CodecKind.VideoDecoder && codec.Clsid == DsGuid.LavVideo)
            return 0;
        if (codec.Kind == CodecKind.AudioDecoder && codec.Clsid == DsGuid.LavAudio)
            return 0;
        return 10;
    }

    private static void Collect(RegistryView view, CodecBitness bitness, List<InstalledCodec> found, HashSet<string> seen)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var category = root.OpenSubKey($@"CLSID\{{{FilterCategory}}}\Instance");
            if (category == null)
                return;
            foreach (var name in category.GetSubKeyNames())
            {
                using var instance = category.OpenSubKey(name);
                if (instance == null)
                    continue;
                var friendly = instance.GetValue("FriendlyName") as string;
                var clsidText = instance.GetValue("CLSID") as string;
                if (string.IsNullOrWhiteSpace(friendly) || !Guid.TryParse(clsidText, out var clsid))
                    continue;
                var parsed = Parse(instance.GetValue("FilterData") as byte[]);
                if (!Classify(friendly, parsed.Pins, out var kind))
                    continue;
                var id = $"{(bitness == CodecBitness.X64 ? "x64" : "x86")}:{clsid:D}";
                if (!seen.Add(id))
                    continue;
                var path = ServerPath(root, clsid);
                string? version = null;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    try
                    {
                        version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
                    }
                    catch (IOException)
                    {
                    }
                }

                var wanted = Environment.Is64BitProcess ? CodecBitness.X64 : CodecBitness.X86;
                found.Add(new InstalledCodec
                {
                    Id = id,
                    Clsid = clsid,
                    Kind = kind,
                    Bitness = bitness,
                    Name = friendly.Trim(),
                    Path = path,
                    Version = string.IsNullOrWhiteSpace(version) ? null : version,
                    Merit = parsed.Merit,
                    Usable = bitness == wanted && !string.IsNullOrWhiteSpace(path) && File.Exists(path),
                    Enabled = false
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static string? ServerPath(RegistryKey root, Guid clsid)
    {
        using var key = root.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
        var path = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool Classify(string name, List<ParsedPin> pins, out CodecKind kind)
    {
        if (IsSplitter(name, pins))
        {
            kind = CodecKind.Splitter;
            return true;
        }

        if (IsDecoder(name, pins, DsGuid.Video))
        {
            kind = CodecKind.VideoDecoder;
            return true;
        }

        if (IsDecoder(name, pins, DsGuid.Audio))
        {
            kind = CodecKind.AudioDecoder;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsSplitter(string name, List<ParsedPin> pins)
    {
        if (Blocked(name) || name.Contains("Information", StringComparison.OrdinalIgnoreCase) || name.Contains("Sections", StringComparison.OrdinalIgnoreCase))
            return false;
        var named = Regex.IsMatch(name, "Splitter|Demultiplexer|Demux", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return named && pins.Any(pin => pin.Output);
    }

    private static bool IsDecoder(string name, List<ParsedPin> pins, Guid major)
    {
        if (Blocked(name) || !name.Contains("Decoder", StringComparison.OrdinalIgnoreCase))
            return false;
        return pins.Any(pin => !pin.Output && pin.Majors.Contains(major))
            && pins.Any(pin => pin.Output && pin.Majors.Contains(major));
    }

    private static bool Blocked(string name)
        => name.Contains("Encoder", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Renderer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Analyzer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Line 21", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VPS", StringComparison.OrdinalIgnoreCase);

    private static ParsedFilter Parse(byte[]? data)
    {
        var parsed = new ParsedFilter();
        if (data == null || data.Length < 16 || ReadInt(data, 0) != 2)
            return parsed;
        parsed.Merit = ReadInt(data, 4);
        var pins = ReadInt(data, 8);
        if (pins < 0 || pins > 32)
            return parsed;
        var offset = 16;
        for (var i = 0; i < pins && offset + 24 <= data.Length; i++)
        {
            var flags = ReadInt(data, offset + 4);
            var count = ReadInt(data, offset + 12);
            var mediums = ReadInt(data, offset + 16);
            offset += 24;
            var majors = new List<Guid>();
            if (count is > 0 and <= 512)
            {
                for (var type = 0; type < count && offset + 16 <= data.Length; type++)
                {
                    if (data[offset + 2] != (byte)'y' || data[offset + 3] != (byte)'3')
                        break;
                    var majorAt = ReadInt(data, offset + 8);
                    offset += 16;
                    if (majorAt > 0 && majorAt + 16 <= data.Length)
                        majors.Add(new Guid(data.AsSpan(majorAt, 16)));
                }
            }

            if (mediums is > 0 and <= 32)
            {
                for (var medium = 0; medium < mediums && offset + 16 <= data.Length; medium++)
                {
                    if (data[offset + 3] != (byte)'3')
                        break;
                    offset += 16;
                }
            }

            parsed.Pins.Add(new ParsedPin((flags & 0x8) != 0, majors));
        }

        return parsed;
    }

    private static int ReadInt(byte[] data, int offset)
        => offset < 0 || offset + 4 > data.Length ? 0 : BitConverter.ToInt32(data, offset);

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetModuleFileNameW(IntPtr hModule, char[] lpFilename, int nSize);

    private sealed class ParsedFilter
    {
        public int Merit { get; set; }
        public List<ParsedPin> Pins { get; } = [];
    }

    private sealed record ParsedPin(bool Output, List<Guid> Majors);
}
