using SherpaOnnx;

internal static class SpeakerProbe
{
    public const double ProbeSeconds = 8;

    public static float[] Extract(
        float[] samples,
        int rate,
        OfflineSpeakerDiarizationSegment[] segments,
        int speaker,
        SpeakerEmbeddingExtractor extractor)
    {
        var parts = new List<float>(rate * 8);
        var wanted = (int)(ProbeSeconds * rate);
        foreach (var segment in segments)
        {
            if (segment.Speaker != speaker)
                continue;
            var start = Math.Max(0, (int)(segment.Start * rate));
            var end = Math.Min(samples.Length, (int)(segment.End * rate));
            for (var index = start; index < end && parts.Count < wanted; index++)
                parts.Add(samples[index]);
            if (parts.Count >= wanted)
                break;
        }

        if (parts.Count < rate / 2)
            return [];
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(rate, parts.ToArray());
        return extractor.IsReady(stream) ? extractor.Compute(stream) : [];
    }

    public static double TotalSeconds(OfflineSpeakerDiarizationSegment[] segments, int speaker)
    {
        var total = 0.0;
        foreach (var segment in segments)
        {
            if (segment.Speaker == speaker)
                total += segment.End - segment.Start;
        }

        return total;
    }
}
