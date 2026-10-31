namespace HomePlayer.Ffmpeg;

public sealed class MediaDecodeRecoveryPolicy(int limit = 32)
{
    private int _consecutive;

    public int Limit { get; } = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    public int Total { get; private set; }
    public int? LastError { get; private set; }

    public bool TryRecover(int error)
    {
        Total++;
        LastError = error;
        _consecutive++;
        return _consecutive <= Limit;
    }

    public void Reset() => _consecutive = 0;

    public void ResetAll()
    {
        _consecutive = 0;
        Total = 0;
        LastError = null;
    }
}
