using System.Drawing;
using System.Drawing.Imaging;
using HomePlayer.Core;
using HomePlayer.Library;

namespace HomePlayer.Library.Tests;

public class FolderCoversTests
{
    [Fact]
    public void Cache_path_is_stable_and_library_scoped()
    {
        var first = FolderCovers.CacheDirectory("lib-a", "剧集/第 1 季");
        var second = FolderCovers.CacheDirectory("lib-a", @"剧集\第 1 季\");
        var other = FolderCovers.CacheDirectory("lib-b", "剧集/第 1 季");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.EndsWith("cover.jpg", FolderCovers.CoverPath("lib-a", "剧集"));
    }

    [Fact]
    public void Stamp_changes_when_a_source_changes()
    {
        var first = new MediaItem { Id = "a", MtimeUtcTicks = 1 };
        var second = new MediaItem { Id = "b", MtimeUtcTicks = 2 };
        var same = new MediaItem { Id = "b", MtimeUtcTicks = 2 };
        var changed = new MediaItem { Id = "b", MtimeUtcTicks = 3 };

        Assert.Equal(FolderCovers.Stamp([first, second]), FolderCovers.Stamp([first, same]));
        Assert.NotEqual(FolderCovers.Stamp([first, second]), FolderCovers.Stamp([first, changed]));
        Assert.Equal("", FolderCovers.Stamp([]));
    }

    [Fact]
    public void Raw_items_prefer_the_middle_frame_and_catalog_items_use_the_poster()
    {
        var frames = new MediaItem { FramePaths = ["f0", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8"] };
        Assert.Equal("f4", FolderCovers.ImagePath(frames, LibraryKind.Raw));

        var poster = new MediaItem { PosterPath = @"C:\poster.jpg", FramePaths = ["f0"] };
        Assert.Equal(@"C:\poster.jpg", FolderCovers.ImagePath(poster, LibraryKind.Catalog));
        Assert.Null(FolderCovers.ImagePath(new MediaItem(), LibraryKind.Raw));
    }

    [Fact]
    public void Compose_grid_writes_a_fixed_size_cover()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Covers." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sources = Enumerable.Range(0, 4)
                .Select(index =>
                {
                    var path = Path.Combine(directory, $"src{index}.jpg");
                    using var bitmap = new Bitmap(64, 36);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.FromArgb(255, 40 + index * 40, 60, 90));
                    bitmap.Save(path, ImageFormat.Jpeg);
                    return path;
                })
                .ToList();
            var target = Path.Combine(directory, "cover.jpg");

            var result = ImageFiles.ComposeGrid(sources, target, FolderCovers.CoverWidth, FolderCovers.CoverHeight);

            Assert.Equal(target, result);
            using var cover = new Bitmap(target);
            Assert.Equal(FolderCovers.CoverWidth, cover.Width);
            Assert.Equal(FolderCovers.CoverHeight, cover.Height);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
