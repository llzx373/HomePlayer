namespace HomePlayer.Ffmpeg;

public sealed record MatroskaTimelineInfo(
    int EditionCount,
    int ChapterCount,
    bool HasOrderedChapters,
    bool HasExternalChapterReferences,
    bool HasTrackJoins)
{
    public static MatroskaTimelineInfo Empty { get; } = new(0, 0, false, false, false);
    public bool RequiresExplicitDegradation => HasExternalChapterReferences || HasTrackJoins;

    public string Describe()
    {
        if (EditionCount == 0 && ChapterCount == 0 && !HasTrackJoins)
            return "Matroska：无 edition、ordered chapter 或 TrackJoin 扩展。";
        var parts = new List<string>
        {
            $"Matroska：{EditionCount} 个 edition，{ChapterCount} 个章节原子",
            HasOrderedChapters ? "含 ordered chapters" : "普通章节",
            HasExternalChapterReferences ? "含跨文件引用（当前拒绝播放）" : "无跨文件章节引用",
            HasTrackJoins ? "含 TrackJoin（当前拒绝播放）" : "无 TrackJoin"
        };
        if (EditionCount > 1)
            parts.Add("多 edition 使用 FFmpeg 默认选择，尚未提供切换");
        return string.Join("；", parts) + "。";
    }
}

public static class MatroskaTimelineInspector
{
    private const uint Segment = 0x18538067;
    private const uint Info = 0x1549A966;
    private const uint SegmentUid = 0x73A4;
    private const uint Tracks = 0x1654AE6B;
    private const uint TrackEntry = 0xAE;
    private const uint TrackOperation = 0xE2;
    private const uint TrackJoinBlocks = 0xE9;
    private const uint Chapters = 0x1043A770;
    private const uint EditionEntry = 0x45B9;
    private const uint EditionFlagOrdered = 0x45DD;
    private const uint ChapterAtom = 0xB6;
    private const uint ChapterSegmentUid = 0x6E67;
    private const uint ChapterDisplay = 0x80;

    public static MatroskaTimelineInfo Inspect(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase))
            return MatroskaTimelineInfo.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var state = new InspectionState();
        Scan(stream, stream.Length, 0, state);
        var external = state.ReferenceUids.Count > 0 && (state.SegmentUid == null || state.ReferenceUids.Any(uid => !uid.SequenceEqual(state.SegmentUid)));
        return new MatroskaTimelineInfo(state.Editions, state.Chapters, state.Ordered, external, state.TrackJoins);
    }

    private static void Scan(FileStream stream, long end, int depth, InspectionState state)
    {
        if (depth > 12)
            throw new InvalidDataException("Matroska 章节嵌套过深。");
        while (stream.Position < end)
        {
            var element = ReadElement(stream, end);
            var available = element.UnknownSize ? end - stream.Position : Math.Min(element.Size, end - stream.Position);
            if (available < 0)
                throw new InvalidDataException("Matroska 元素越界。");
            var elementEnd = stream.Position + available;
            switch (element.Id)
            {
                case Segment:
                case Info:
                case Tracks:
                case TrackEntry:
                case TrackOperation:
                case Chapters:
                case EditionEntry:
                case ChapterAtom:
                case ChapterDisplay:
                    if (element.Id == EditionEntry)
                        state.Editions++;
                    if (element.Id == ChapterAtom)
                        state.Chapters++;
                    if (element.Id == TrackJoinBlocks)
                        state.TrackJoins = true;
                    Scan(stream, elementEnd, depth + 1, state);
                    break;
                case TrackJoinBlocks:
                    state.TrackJoins = true;
                    break;
                case SegmentUid:
                    state.SegmentUid = ReadBytes(stream, available, 64);
                    break;
                case ChapterSegmentUid:
                    state.ReferenceUids.Add(ReadBytes(stream, available, 64));
                    break;
                case EditionFlagOrdered:
                    state.Ordered |= ReadUnsigned(stream, available) != 0;
                    break;
            }
            stream.Position = elementEnd;
        }
    }

    private static (uint Id, long Size, bool UnknownSize) ReadElement(FileStream stream, long end)
    {
        var id = ReadVint(stream, end, 4, false);
        var size = ReadVint(stream, end, 8, true);
        return ((uint)id.Value, size.Unknown ? -1 : checked((long)size.Value), size.Unknown);
    }

    private static (ulong Value, bool Unknown) ReadVint(FileStream stream, long end, int maxBytes, bool size)
    {
        if (stream.Position >= end)
            throw new EndOfStreamException();
        var first = stream.ReadByte();
        if (first <= 0)
            throw new InvalidDataException("Matroska 变长整数无效。");
        var mask = 0x80;
        var length = 1;
        while ((first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > maxBytes || stream.Position + length - 1 > end)
            throw new InvalidDataException("Matroska 变长整数越界。");
        ulong value = size ? (uint)(first & (0xFF >> length)) : (uint)first;
        for (var index = 1; index < length; index++)
        {
            var next = stream.ReadByte();
            if (next < 0)
                throw new EndOfStreamException();
            value = (value << 8) | (uint)next;
        }
        var unknown = size && value == (1UL << (7 * length)) - 1;
        return (value, unknown);
    }

    private static byte[] ReadBytes(FileStream stream, long length, int maxLength)
    {
        if (length <= 0 || length > maxLength)
            throw new InvalidDataException($"Matroska UID 长度无效：{length}，偏移 {stream.Position}。");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ulong ReadUnsigned(FileStream stream, long length)
    {
        if (length is < 0 or > 8)
            throw new InvalidDataException("Matroska 整数长度无效。");
        ulong value = 0;
        for (var index = 0; index < length; index++)
        {
            var next = stream.ReadByte();
            if (next < 0)
                throw new EndOfStreamException();
            value = (value << 8) | (uint)next;
        }
        return value;
    }

    private sealed class InspectionState
    {
        public int Editions;
        public int Chapters;
        public bool Ordered;
        public bool TrackJoins;
        public byte[]? SegmentUid;
        public List<byte[]> ReferenceUids { get; } = [];
    }
}
