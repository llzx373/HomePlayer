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

    [Fact]
    public void Pgs_subtitles_decode_to_real_bitmaps()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_PGS_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        var streams = FfmpegSubtitles.List(media);
        var pgs = Assert.Single(streams);
        Assert.Equal("hdmv_pgs_subtitle", pgs.Codec);
        Assert.False(pgs.IsText);
        var cues = FfmpegSubtitles.ReadCues(media, pgs.Index);
        var bitmaps = cues.SelectMany(cue => cue.Bitmaps).ToArray();
        Assert.NotEmpty(bitmaps);
        var bitmap = bitmaps[0];
        Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
        Assert.Contains(bitmap.Bgra, value => value != 0);
    }
}
