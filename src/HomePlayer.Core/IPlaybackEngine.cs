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
    PipelineInfo GetPipeline();
    event Action<VideoFrame>? FrameArrived;
    event Action? Ended;
}
