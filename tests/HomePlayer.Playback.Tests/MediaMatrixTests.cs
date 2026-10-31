using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class MediaMatrixTests
{
    [Fact]
    public void Multiple_audio_tracks_have_stable_distinct_identity()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_MULTI_AUDIO_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        using var player = new FfmpegPlayer();
        player.Open(media);
        var tracks = player.GetAudioTracks();
        Assert.True(tracks.Count >= 2, $"音轨数量 {tracks.Count}");
        Assert.Equal(2, tracks.Select(track => track.StableId).Distinct().Count());
        player.SetAudioTrack(1);
        Assert.Equal(1, player.AudioTrackIndex);
        player.Play();
    }
}
