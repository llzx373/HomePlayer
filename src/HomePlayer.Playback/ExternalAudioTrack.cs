namespace HomePlayer.Playback;

public interface IExternalAudioTrack
{
    string? ExternalAudioPath { get; }
    void SetExternalAudio(string? path);
}
