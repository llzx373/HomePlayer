namespace HomePlayer.Core;

public interface IPlaybackEngine : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    void Open(string path);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double linear);
    void SetMuted(bool muted);
    double Rate { get; }
    void SetRate(double rate);
    void StepFrame();
    IReadOnlyList<MediaTrack> GetAudioTracks();
    int AudioTrackIndex { get; }
    void SetAudioTrack(int index);
    IReadOnlyList<MediaTrack> GetSubtitleTracks();
    int SubtitleTrackIndex { get; }
    void SetSubtitleTrack(int index);
    PipelineInfo GetPipeline();
    event Action<VideoFrame>? FrameArrived;
    event Action? Ended;
}

public interface IPlaybackFaultSource
{
    event Action<Exception>? Faulted;
}
