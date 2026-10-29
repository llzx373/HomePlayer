using System.Text;

namespace HomePlayer.Playback.Tests;

internal static class TestMedia
{
    private const int Width = 32;
    private const int Height = 24;
    private const int FrameCount = 12;
    private const int SampleRate = 8000;

    public static string CreateAvi(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "two-audio-tracks.avi");
        var header = List("hdrl",
            Chunk("avih", VideoHeader()),
            List("strl", Chunk("strh", VideoStreamHeader()), Chunk("strf", BitmapHeader())),
            List("strl", Chunk("strh", AudioStreamHeader()), Chunk("strf", WaveHeader())),
            List("strl", Chunk("strh", AudioStreamHeader()), Chunk("strf", WaveHeader())));

        var chunks = new List<(string Id, byte[] Data)>();
        for (var frame = 0; frame < FrameCount; frame++)
        {
            chunks.Add(("00db", VideoFrame(frame)));
            chunks.Add(("01wb", AudioFrame(frame, 440)));
            chunks.Add(("02wb", AudioFrame(frame, 660)));
        }

        var moviPayload = new MemoryStream();
        var index = new MemoryStream();
        using (var indexWriter = new BinaryWriter(index, Encoding.ASCII, true))
        {
            var offset = 4;
            foreach (var (id, data) in chunks)
            {
                var chunk = Chunk(id, data);
                moviPayload.Write(chunk);
                indexWriter.Write(Encoding.ASCII.GetBytes(id));
                indexWriter.Write(0x10);
                indexWriter.Write(offset);
                indexWriter.Write(data.Length);
                offset += chunk.Length;
            }
        }

        using var output = new MemoryStream();
        output.Write(header);
        output.Write(List("movi", moviPayload.ToArray()));
        output.Write(Chunk("idx1", index.ToArray()));
        var body = output.ToArray();
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(body.Length + 4);
            writer.Write(Encoding.ASCII.GetBytes("AVI "));
            writer.Write(body);
        }
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    private static byte[] VideoHeader()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1_000_000 / 6);
        writer.Write(Width * Height * 3 * 6);
        writer.Write(0);
        writer.Write(0x10);
        writer.Write(FrameCount);
        writer.Write(0);
        writer.Write(3);
        writer.Write(Width * Height * 3);
        writer.Write(Width);
        writer.Write(Height);
        for (var i = 0; i < 4; i++)
            writer.Write(0);
        return stream.ToArray();
    }

    private static byte[] VideoStreamHeader()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("vids"));
        writer.Write(Encoding.ASCII.GetBytes("DIB "));
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write(1);
        writer.Write(6);
        writer.Write(0);
        writer.Write(FrameCount);
        writer.Write(Width * Height * 3);
        writer.Write(-1);
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)Width);
        writer.Write((short)Height);
        return stream.ToArray();
    }

    private static byte[] BitmapHeader()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(Width * Height * 3);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        return stream.ToArray();
    }

    private static byte[] AudioStreamHeader()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("auds"));
        writer.Write(0);
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write(1);
        writer.Write(SampleRate);
        writer.Write(0);
        writer.Write(SampleRate * 2);
        writer.Write(SampleRate * 2);
        writer.Write(-1);
        writer.Write(2);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        return stream.ToArray();
    }

    private static byte[] WaveHeader()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        return stream.ToArray();
    }

    private static byte[] VideoFrame(int frame)
    {
        var data = new byte[Width * Height * 3];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = (byte)(frame * 15);
            data[i + 1] = (byte)(255 - frame * 15);
            data[i + 2] = (byte)(i % 251);
        }
        return data;
    }

    private static byte[] AudioFrame(int frame, int frequency)
    {
        var samples = SampleRate / 6;
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin((frame * samples + i) * frequency * Math.PI * 2 / SampleRate) * 1000);
            data[i * 2] = (byte)value;
            data[i * 2 + 1] = (byte)(value >> 8);
        }
        return data;
    }

    private static byte[] List(string type, params byte[][] content) => Container("LIST", type, content);

    private static byte[] Chunk(string id, byte[] content) => Container(id, null, [content]);

    private static byte[] Container(string id, string? type, byte[][] content)
    {
        var payloadLength = content.Sum(item => item.Length) + (type == null ? 0 : 4);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write(payloadLength);
        if (type != null)
            writer.Write(Encoding.ASCII.GetBytes(type));
        foreach (var item in content)
            writer.Write(item);
        if (payloadLength % 2 != 0)
            writer.Write((byte)0);
        return stream.ToArray();
    }
}
