using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class MatroskaTimelineInspectorTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public void Internal_ordered_editions_remain_playable_with_observable_default()
    {
        var uid = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var path = Write("internal.mkv",
            Element(0x1549A966, Element(0x73A4, uid)),
            Element(0x1043A770,
                Element(0x45B9, Element(0x45DD, [1]), Element(0xB6, Element(0x6E67, uid))),
                Element(0x45B9, Element(0xB6, []))));

        var info = MatroskaTimelineInspector.Inspect(path);

        Assert.Equal(2, info.EditionCount);
        Assert.Equal(2, info.ChapterCount);
        Assert.True(info.HasOrderedChapters);
        Assert.False(info.HasExternalChapterReferences);
        Assert.False(info.RequiresExplicitDegradation);
        Assert.Contains("默认选择", info.Describe());
    }

    [Fact]
    public void External_chapter_reference_requires_explicit_degradation()
    {
        var path = Write("external.mkv",
            Element(0x1549A966, Element(0x73A4, new byte[16])),
            Element(0x1043A770, Element(0x45B9,
                Element(0x45DD, [1]),
                Element(0xB6, Element(0x6E67, Enumerable.Repeat((byte)1, 16).ToArray())))));

        var info = MatroskaTimelineInspector.Inspect(path);

        Assert.True(info.HasExternalChapterReferences);
        Assert.True(info.RequiresExplicitDegradation);
        Assert.Contains("跨文件引用", info.Describe());
    }

    [Fact]
    public void Track_join_is_detected_and_rejected_by_native_open()
    {
        var path = Write("join.mkv", Element(0x1654AE6B, Element(0xAE, Element(0xE2, Element(0xE9, [])))));

        var info = MatroskaTimelineInspector.Inspect(path);
        Assert.True(info.HasTrackJoins);
        Assert.True(info.RequiresExplicitDegradation);
        using var player = new FfmpegPlayer();
        Assert.Throws<NotSupportedException>(() => player.Open(path));
        Assert.False(player.IsOpen);
    }

    [Fact]
    public void Truncated_ebml_is_rejected()
    {
        var path = Write("truncated.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0xFF]);
        Assert.Throws<InvalidDataException>(() => MatroskaTimelineInspector.Inspect(path));
    }

    [Fact]
    public void Non_matroska_files_do_not_pay_for_inspection()
    {
        var path = Write("video.mp4", [0x1A, 0x45, 0xDF, 0xA3]);
        Assert.Equal(MatroskaTimelineInfo.Empty, MatroskaTimelineInspector.Inspect(path));
    }

    private string Write(string name, params byte[][] parts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeplayer-ebml-{Guid.NewGuid():N}-{name}");
        var bytes = parts.SelectMany(part => part).ToArray();
        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }

    private static byte[] Element(uint id, byte[] payload) => WriteElement(id, payload);

    private static byte[] Element(uint id, byte[] first, byte[] second) =>
        WriteElement(id, [.. first, .. second]);

    private static byte[] WriteElement(uint id, byte[] payload)
    {
        var idBytes = ToBigEndian(id);
        var size = new[] { (byte)(0x80 | payload.Length) };
        return [.. idBytes, .. size, .. payload];
    }

    private static byte[] ToBigEndian(uint value)
    {
        if (value <= 0xFF)
            return [(byte)value];
        if (value <= 0xFFFF)
            return [(byte)(value >> 8), (byte)value];
        if (value <= 0xFFFFFF)
            return [(byte)(value >> 16), (byte)(value >> 8), (byte)value];
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }
}
