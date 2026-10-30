using System.Buffers.Binary;

namespace HomePlayer.Core;

public sealed class PcmTimeStretcher
{
    private const int FrameSize = 1024;
    private const int Overlap = 128;
    private const int OutputHop = FrameSize - Overlap;
    private const int SearchRadius = 64;
    private readonly List<short> _samples = [];
    private double _position;
    private short[] _tail = new short[Overlap];

    public byte[] Process(byte[] data, int count, int channels, double rate)
    {
        if (count <= 0)
            return [];
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (Math.Abs(rate - 1) < 0.0001)
            return data[..count];
        if (_tail.Length != Overlap * channels)
            Reset();
        _tail = _tail.Length == Overlap * channels ? _tail : new short[Overlap * channels];
        for (var i = 0; i < count - 1; i += 2)
            _samples.Add(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2)));
        var analysisHop = Math.Max(1, (int)Math.Round(OutputHop * rate));
        var output = new List<short>((int)(_samples.Count / channels / rate * channels + FrameSize * channels));
        while (_position + FrameSize + SearchRadius < _samples.Count / channels)
        {
            var start = (int)_position;
            var best = FindBest(start, channels);
            for (var frame = 0; frame < OutputHop; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var source = _samples[(best + frame) * channels + channel];
                    if (frame < Overlap)
                    {
                        var previous = _tail[frame * channels + channel];
                        var mix = frame / (double)Overlap;
                        source = (short)Math.Clamp(Math.Round(previous * (1 - mix) + source * mix), short.MinValue, short.MaxValue);
                    }
                    output.Add(source);
                }
            }
            _tail = new short[Overlap * channels];
            for (var frame = 0; frame < Overlap; frame++)
            for (var channel = 0; channel < channels; channel++)
                _tail[frame * channels + channel] = _samples[(best + OutputHop + frame) * channels + channel];
            _position += analysisHop;
        }
        var discard = Math.Max(0, (int)_position - FrameSize);
        if (discard > 0)
        {
            _samples.RemoveRange(0, discard * channels);
            _position -= discard;
        }
        var bytes = new byte[output.Count * 2];
        for (var i = 0; i < output.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), output[i]);
        return bytes;
    }

    public byte[] Flush(int channels, double rate)
    {
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (Math.Abs(rate - 1) < 0.0001)
        {
            Reset();
            return [];
        }
        var available = Math.Max(0, _samples.Count / channels - (int)_position);
        var output = new List<short>(Math.Max(0, (int)(available / rate) * channels));
        while (_position < _samples.Count / channels)
        {
            var source = Math.Min(_samples.Count / channels - 1, (int)_position);
            for (var channel = 0; channel < channels; channel++)
                output.Add(_samples[source * channels + channel]);
            _position += Math.Max(0.01, rate);
        }
        Reset();
        var bytes = new byte[output.Count * 2];
        for (var i = 0; i < output.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), output[i]);
        return bytes;
    }

    public void Reset()
    {
        _samples.Clear();
        _position = 0;
        _tail = new short[Overlap];
    }

    private int FindBest(int expected, int channels)
    {
        if (_tail.All(value => value == 0))
            return expected;
        var best = expected;
        var bestScore = double.MinValue;
        for (var candidate = Math.Max(0, expected - SearchRadius); candidate <= expected + SearchRadius; candidate += 4)
        {
            double score = 0;
            for (var frame = 0; frame < Overlap; frame += 4)
            {
                var index = frame * channels;
                score += (double)_tail[index] * _samples[(candidate + frame) * channels];
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }
}

public sealed class PcmAudioEffects
{
    private double _gain = 1;

    public void Apply(byte[] data, int count, bool normalize, bool nightMode)
    {
        if (!normalize && !nightMode)
        {
            _gain = 1;
            return;
        }
        var peak = 0;
        for (var i = 0; i + 1 < count; i += 2)
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2))));
        var target = normalize && peak > 0 ? Math.Min(2.5, 22000d / peak) : 1;
        _gain = _gain * 0.85 + target * 0.15;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2)) / 32768d;
            sample *= _gain;
            if (nightMode)
            {
                const double threshold = 0.35;
                var magnitude = Math.Abs(sample);
                if (magnitude > threshold)
                    sample = Math.Sign(sample) * (threshold + (magnitude - threshold) / 4);
                sample *= 1.35;
            }
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i, 2), (short)Math.Clamp(Math.Round(sample * 32768), short.MinValue, short.MaxValue));
        }
    }
}
