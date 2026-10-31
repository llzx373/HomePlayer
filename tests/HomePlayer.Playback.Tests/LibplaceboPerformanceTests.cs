using System.Diagnostics;
using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Playback;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed unsafe class LibplaceboPerformanceTests
{
    [Fact]
    public void Presets_record_actual_gpu_timestamps()
    {
        if (!LibplaceboRenderer.IsAvailable)
            Assert.Fail(LibplaceboRenderer.Version);
        var output = Environment.GetEnvironmentVariable("HOMEPLAYER_LIBPLACEBO_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
        if (!result.Success)
            return;
        using var _ = device;
        using var __ = context;
        using var source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1920,
            Height = 1080,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        using var target = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 3840,
            Height = 2160,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        using var startTimestamp = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using var endTimestamp = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        var mapped = context.Map(source, 0, MapMode.WriteDiscard);
        var pixel = (uint*)mapped.DataPointer;
        for (var y = 0; y < 1080; y++)
        {
            var row = (uint*)((byte*)mapped.DataPointer + y * mapped.RowPitch);
            for (var x = 0; x < 1920; x++)
                row[x] = 0xFF202020;
        }
        context.Unmap(source, 0);
        using var renderer = new LibplaceboRenderer(device, context);
        var color = new VideoColorMetadata(VideoColorMatrix.Bt709, VideoColorRange.Full,
            VideoColorPrimaries.Bt709, VideoTransferFunction.Sdr);
        var geometry = VideoAspect.Quad(3840, 2160, 1920, 1080, AspectMode.Stretch, false);
        context.End(disjoint);
        context.Flush();
        var frequencies = ReadUInt<QueryDataTimestampDisjoint>(context, disjoint);
        Assert.False(frequencies.Disjoint);
        var results = new List<object>();
        if (frequencies.Frequency > 0)
        {
            foreach (var quality in new[] { LibplaceboQuality.Fast, LibplaceboQuality.Balanced, LibplaceboQuality.HighQuality })
            {
                var samples = new List<double>();
                for (var frame = 0; frame < 20; frame++)
                {
                    context.End(startTimestamp);
                    Assert.True(renderer.Render(source, null, 0, 0, VideoPixelFormat.Bgra32, false, color,
                        target, 1920, 1080, 3840, 2160, geometry, 1, 1, false, quality));
                    context.End(endTimestamp);
                    context.Flush();
                    var start = ReadUInt<ulong>(context, startTimestamp);
                    var end = ReadUInt<ulong>(context, endTimestamp);
                    samples.Add((end - start) * 1000.0 / frequencies.Frequency);
                }
                samples.Sort();
                results.Add(new
                {
                    quality = quality.ToString(),
                    frames = samples.Count,
                    gpuMeanMilliseconds = samples.Average(),
                    gpuP95Milliseconds = samples[^1],
                    gpuMaxMilliseconds = samples[^1]
                });
            }
        }
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var document = new
        {
            schema = "homeplayer.libplacebo.gpu-performance.v1",
            recordedUtc = DateTime.UtcNow,
            gpu = adapter.Description.Description,
            timestampAvailable = frequencies.Frequency > 0,
            note = frequencies.Frequency > 0 ? null : "D3D11 timestamp frequency is 0; GPU time is Unknown.",
            frequencyHz = frequencies.Frequency,
            source = "1920x1080 BGRA",
            target = "3840x2160 BGRA",
            results
        };
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T ReadUInt<T>(ID3D11DeviceContext context, ID3D11Query query) where T : unmanaged
    {
        while (true)
        {
            try
            {
                return context.GetData<T>(query, AsyncGetDataFlags.None);
            }
            catch (Exception ex) when (ex.HResult == 1)
            {
                Thread.Sleep(1);
            }
        }
    }
}
