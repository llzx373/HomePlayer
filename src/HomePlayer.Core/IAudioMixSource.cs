namespace HomePlayer.Core;

public enum DubbingMode
{
    Off = 0,
    Mixed = 1,
    DubOnly = 2
}

/// <summary>
/// 在音频时间轴上就地处理一段 PCM。实现方负责把配音混进来，
/// 并在需要时压低原声，播放器不关心具体策略。
/// </summary>
public interface IAudioMixSource
{
    bool Enabled { get; }

    void Process(double mediaSeconds, byte[] pcm, int count, int sampleRate, int channels);
}
