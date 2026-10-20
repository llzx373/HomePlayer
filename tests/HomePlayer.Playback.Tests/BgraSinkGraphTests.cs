using System.Runtime.InteropServices;
using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public class BgraSinkGraphTests
{
    [Fact]
    public void Graph_unwrap_keeps_managed_sink_as_IBaseFilterSink()
    {
        var sink = new BgraSinkFilter();
        var unknown = Marshal.GetIUnknownForObject(sink);
        object obj;
        try
        {
            obj = Marshal.GetObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        Assert.Same(sink, obj);
        var filter = Assert.IsAssignableFrom<IBaseFilterSink>(obj);
        Assert.Equal(0, filter.QueryFilterInfo(out var info));
        Assert.Equal("HomePlayer Video Sink", info.Name);
        if (info.Graph != 0)
            Marshal.Release(info.Graph);
    }
}
