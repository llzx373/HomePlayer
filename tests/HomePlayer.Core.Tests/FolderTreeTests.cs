using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class FolderTreeTests
{
    private static readonly MediaLibrary Library = new()
    {
        Id = "lib",
        Name = "测试库",
        RootPath = @"D:\视频",
        Kind = LibraryKind.Raw
    };

    [Fact]
    public void Build_splits_direct_and_recursive_counts()
    {
        var folders = FolderTree.Build(Library, ["root.mkv", "a/direct.mkv", "a/b/nested.mkv"]);

        Assert.Collection(
            folders,
            root =>
            {
                Assert.Equal("", root.RelativePath);
                Assert.Null(root.ParentPath);
                Assert.Equal("测试库", root.Name);
                Assert.Equal(1, root.DirectCount);
                Assert.Equal(3, root.RecursiveCount);
            },
            a =>
            {
                Assert.Equal("a", a.RelativePath);
                Assert.Equal("", a.ParentPath);
                Assert.Equal("a", a.Name);
                Assert.Equal(1, a.DirectCount);
                Assert.Equal(2, a.RecursiveCount);
            },
            ab =>
            {
                Assert.Equal("a/b", ab.RelativePath);
                Assert.Equal("a", ab.ParentPath);
                Assert.Equal("b", ab.Name);
                Assert.Equal(1, ab.DirectCount);
                Assert.Equal(1, ab.RecursiveCount);
            });
    }

    [Fact]
    public void Build_uses_only_videos_that_were_enumerated()
    {
        var folders = FolderTree.Build(Library, ["a/one.mkv", "a/two.mkv"]);

        var root = folders[0];
        var a = folders[1];
        Assert.Equal(0, root.DirectCount);
        Assert.Equal(2, root.RecursiveCount);
        Assert.Equal(2, a.DirectCount);
        Assert.Equal(2, a.RecursiveCount);
    }

    [Fact]
    public void Ancestors_walk_up_to_the_root()
    {
        Assert.Equal(["a/b/c", "a/b", "a", ""], FolderTree.Ancestors("a/b/c"));
        Assert.Equal([""], FolderTree.Ancestors(""));
    }

    [Fact]
    public void Parent_maps_each_level()
    {
        Assert.Null(FolderTree.Parent(""));
        Assert.Equal("", FolderTree.Parent("a"));
        Assert.Equal("a", FolderTree.Parent("a/b"));
    }
}
