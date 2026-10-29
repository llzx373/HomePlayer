namespace HomePlayer.Core;

public interface IPlaybackAudioEnhancements
{
    void SetAudioDelay(int milliseconds);
    void SetNormalization(bool enabled);
    void SetNightMode(bool enabled);
}

public interface IPlaybackAudioOutputEvents
{
    event Action<Exception>? AudioOutputFailed;
}
