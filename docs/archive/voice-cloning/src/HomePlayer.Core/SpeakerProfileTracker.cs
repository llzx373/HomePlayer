namespace HomePlayer.Core;

/// <summary>
/// 增量说话人声纹档案：块内聚类得到的 embedding 映射到全局说话人 ID。
/// 短样本优先归到最近档案，避免噪声或短句不断生成新角色。
/// </summary>
public sealed class SpeakerProfileTracker(int maxSpeakers = 6)
{
    public const double MatchThreshold = 0.65;
    public const double MinimumNewSpeakerSeconds = 1.5;

    private readonly List<float[]> _profiles = [];

    public int Count
    {
        get
        {
            lock (_profiles)
                return _profiles.Count;
        }
    }

    public int MaxSpeakers { get; } = Math.Max(1, maxSpeakers);

    public int Match(float[] embedding, double durationSeconds)
    {
        lock (_profiles)
        {
            if (embedding.Length == 0)
                return _profiles.Count == 0 ? -1 : 0;

            var best = -1;
            var bestScore = MatchThreshold;
            for (var index = 0; index < _profiles.Count; index++)
            {
                var score = Cosine(_profiles[index], embedding);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = index;
                }
            }

            if (best >= 0)
            {
                _profiles[best] = Average(_profiles[best], embedding);
                return best;
            }

            var nearest = -1;
            var nearestScore = -2.0;
            for (var index = 0; index < _profiles.Count; index++)
            {
                var score = Cosine(_profiles[index], embedding);
                if (score > nearestScore)
                {
                    nearestScore = score;
                    nearest = index;
                }
            }

            if (durationSeconds < MinimumNewSpeakerSeconds && nearest >= 0)
                return nearest;
            if (_profiles.Count >= MaxSpeakers)
                return nearest >= 0 ? nearest : 0;

            _profiles.Add((float[])embedding.Clone());
            return _profiles.Count - 1;
        }
    }

    public IReadOnlyList<float[]> Profiles()
    {
        lock (_profiles)
            return _profiles.ToArray();
    }

    private static float[] Average(float[] first, float[] second)
    {
        if (first.Length != second.Length)
            return second;
        var result = new float[first.Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = (first[index] + second[index]) / 2;
        return result;
    }

    private static double Cosine(float[] first, float[] second)
    {
        if (first.Length != second.Length || first.Length == 0)
            return -1;
        double dot = 0, left = 0, right = 0;
        for (var index = 0; index < first.Length; index++)
        {
            dot += first[index] * second[index];
            left += first[index] * first[index];
            right += second[index] * second[index];
        }

        return dot / (Math.Sqrt(left) * Math.Sqrt(right) + 1e-9);
    }
}
