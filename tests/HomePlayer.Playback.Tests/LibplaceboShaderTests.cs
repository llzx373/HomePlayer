using HomePlayer.Core;
using HomePlayer.Playback;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed unsafe class LibplaceboShaderTests
{
    private const string InvertShader = """
        //!PARAM strength
        //!DESC Inversion strength
        //!TYPE DYNAMIC float
        //!MINIMUM 0.0
        //!MAXIMUM 2.0
        1.0
        //!HOOK MAIN
        //!BIND HOOKED
        vec4 hook()
        {
            vec4 color = HOOKED_texOff(0);
            color.rgb = mix(color.rgb, vec3(1.0) - color.rgb, strength);
            return color;
        }
        """;

    [Fact]
    public void Mpv_hook_changes_pixels_and_exposes_parameters()
    {
        using var fixture = new Fixture();
        fixture.Renderer.UpdateProfile(Profile("invert", InvertShader, 1), _ => InvertShader);

        var captured = fixture.RenderRed();

        Assert.Equal("invert", fixture.Renderer.Status.ProfileId);
        var parameter = Assert.Single(fixture.Renderer.Status.Shaders);
        Assert.Equal("shaders/invert.glsl", parameter.File);
        Assert.Equal("strength", Assert.Single(parameter.Parameters).Name);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.True(captured.Data[pixel * 4] >= 220);
            Assert.True(captured.Data[pixel * 4 + 1] >= 220);
            Assert.True(captured.Data[pixel * 4 + 2] <= 30);
        }
    }

    [Fact]
    public void Zero_strength_and_disabled_when_leave_pixels_unchanged()
    {
        using var fixture = new Fixture();
        fixture.Renderer.UpdateProfile(Profile("zero", InvertShader, 0), _ => InvertShader);
        var zero = fixture.RenderRed();
        var disabledShader = InvertShader.Replace("//!HOOK MAIN", "//!HOOK MAIN\n//!WHEN 0");
        fixture.Renderer.UpdateProfile(Profile("disabled", disabledShader, 1, "v2"), _ => disabledShader);
        var disabled = fixture.RenderRed();

        AssertRed(zero);
        AssertRed(disabled);
    }

    [Fact]
    public void Invalid_parameters_keep_the_last_known_good_profile()
    {
        using var fixture = new Fixture();
        fixture.Renderer.UpdateProfile(Profile("good", InvertShader, 1), _ => InvertShader);
        _ = fixture.RenderRed();
        var bad = Profile("bad", InvertShader, 3, "v2");

        Assert.Throws<LibplaceboShaderException>(() => fixture.Renderer.UpdateProfile(bad, _ => InvertShader));
        var captured = fixture.RenderRed();

        Assert.Equal("good", fixture.Renderer.Status.ProfileId);
        for (var pixel = 0; pixel < 16; pixel++)
            Assert.True(captured.Data[pixel * 4 + 2] <= 30);
    }

    [Fact]
    public void Multiple_hooks_execute_in_profile_order()
    {
        using var fixture = new Fixture();
        var profile = Profile("twice", InvertShader, 1) with
        {
            Shaders =
            [
                new ResolvedRenderShader("shaders/first.glsl", new Dictionary<string, double> { ["strength"] = 1 }),
                new ResolvedRenderShader("shaders/second.glsl", new Dictionary<string, double> { ["strength"] = 1 })
            ]
        };
        fixture.Renderer.UpdateProfile(profile, _ => InvertShader);

        AssertRed(fixture.RenderRed());
    }

    [Fact]
    public void Gpu_compile_failure_preserves_the_last_known_good_profile()
    {
        using var fixture = new Fixture();
        fixture.Renderer.UpdateProfile(Profile("good", InvertShader, 1), _ => InvertShader);
        _ = fixture.RenderRed();
        const string broken = """
            //!HOOK MAIN
            //!BIND HOOKED
            vec4 hook()
            {
                return definitely_not_a_valid_function();
            }
            """;
        var accepted = true;
        try
        {
            fixture.Renderer.UpdateProfile(Profile("broken", broken, 1, "v2"), _ => broken);
        }
        catch (LibplaceboShaderException)
        {
            accepted = false;
        }

        var captured = fixture.RenderRed(!accepted);

        Assert.Equal("good", fixture.Renderer.Status.ProfileId);
        for (var pixel = 0; pixel < 16; pixel++)
            Assert.True(captured.Data[pixel * 4 + 2] <= 30);
    }

    [Fact]
    public void Gpu_timing_reports_samples_or_unknown_from_actual_device_capability()
    {
        using var fixture = new Fixture();
        for (var frame = 0; frame < 65; frame++)
            _ = fixture.RenderRed();

        if (fixture.TimestampAvailable())
        {
            Assert.True(fixture.Renderer.Status.GpuSampleCount > 0);
            Assert.True(fixture.Renderer.Status.GpuP95Milliseconds >= 0);
        }
        else
        {
            Assert.Equal(0, fixture.Renderer.Status.GpuSampleCount);
            Assert.Null(fixture.Renderer.Status.GpuMilliseconds);
            Assert.Null(fixture.Renderer.Status.GpuP95Milliseconds);
        }
    }

    [Fact]
    public void Every_bundled_shader_compiles_and_executes_on_d3d11()
    {
        using var fixture = new Fixture();
        var root = Path.Combine(AppContext.BaseDirectory, "render");
        var files = Directory.EnumerateFiles(Path.Combine(root, "shaders"), "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".glsl" or ".hook")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(17, files.Length);
        foreach (var path in files)
        {
            var relative = "shaders/" + Path.GetRelativePath(Path.Combine(root, "shaders"), path).Replace('\\', '/');
            var file = BuiltInRenderPresets.Prefix + relative;
            var profile = new ResolvedRenderProfile("test-" + Path.GetFileName(path), null, VideoRenderQuality.HighQuality,
                [new ResolvedRenderShader(file, new Dictionary<string, double>())], path);
            fixture.Renderer.UpdateProfile(profile, ReadBuiltInShader);

            _ = fixture.RenderRed();

            Assert.Equal(profile.ProfileId, fixture.Renderer.Status.ProfileId);
            Assert.Null(fixture.Renderer.Status.Warning);
        }
    }

    [Fact]
    public void Built_in_quick_presets_execute_their_complete_chains()
    {
        using var fixture = new Fixture();
        foreach (var definition in BuiltInRenderPresets.Profiles)
        {
            var profile = new ResolvedRenderProfile(definition.Id, null, VideoRenderQuality.HighQuality,
                definition.Shaders.Select(shader => new ResolvedRenderShader(shader.File, shader.Parameters)).ToArray(),
                definition.Id);
            fixture.Renderer.UpdateProfile(profile, ReadBuiltInShader);

            var captured = fixture.RenderRed();

            Assert.True(captured.Data.Chunk(4).Any(pixel => pixel[0] > 0 || pixel[1] > 0 || pixel[2] > 0),
                definition.Id + " produced a black frame");
            Assert.Equal(definition.Id, fixture.Renderer.Status.ProfileId);
            Assert.Equal(definition.Shaders.Count, fixture.Renderer.Status.Shaders.Count);
            Assert.Null(fixture.Renderer.Status.Warning);
        }
    }

    private static string ReadBuiltInShader(string file)
    {
        var relative = BuiltInRenderPresets.ShaderPath(file).Replace('/', Path.DirectorySeparatorChar);
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "render", relative));
    }

    private static ResolvedRenderProfile Profile(string id, string shader, double strength, string revision = "v1") =>
        new(id, null, VideoRenderQuality.Balanced,
            [new ResolvedRenderShader("shaders/invert.glsl", new Dictionary<string, double> { ["strength"] = strength })], revision);

    private static void AssertRed(D3D11CapturedFrame captured)
    {
        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.True(captured.Data[pixel * 4] <= 30, $"pixel {pixel} B={captured.Data[pixel * 4]}");
            Assert.True(captured.Data[pixel * 4 + 1] <= 30, $"pixel {pixel} G={captured.Data[pixel * 4 + 1]}");
            Assert.True(captured.Data[pixel * 4 + 2] >= 220, $"pixel {pixel} R={captured.Data[pixel * 4 + 2]}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Texture2D _source;
        private readonly ID3D11Texture2D _target;

        public Fixture()
        {
            if (!LibplaceboRenderer.IsAvailable)
                Assert.Fail(LibplaceboRenderer.Version);
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out _device, out _context);
            if (!result.Success)
                throw new InvalidOperationException("测试需要可用的 D3D11 设备。");
            Renderer = new LibplaceboRenderer(_device, _context);
            _source = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = 2,
                Height = 2,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write
            });
            _target = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });
            var mapped = _context.Map(_source, 0, MapMode.WriteDiscard);
            for (var row = 0; row < 2; row++)
            {
                var destination = (byte*)mapped.DataPointer + row * mapped.RowPitch;
                for (var pixel = 0; pixel < 2; pixel++)
                {
                    destination[pixel * 4] = 0;
                    destination[pixel * 4 + 1] = 0;
                    destination[pixel * 4 + 2] = 255;
                    destination[pixel * 4 + 3] = 255;
                }
            }
            _context.Unmap(_source, 0);
        }

        public LibplaceboRenderer Renderer { get; }

        public D3D11CapturedFrame RenderRed(bool expectSuccess = true)
        {
            var color = new VideoColorMetadata(VideoColorMatrix.Bt709, VideoColorRange.Full,
                VideoColorPrimaries.Bt709, VideoTransferFunction.Sdr);
            var geometry = VideoAspect.Quad(4, 4, 2, 2, AspectMode.Stretch, false);
            var rendered = Renderer.Render(_source, null, 0, 0, VideoPixelFormat.Bgra32, false, color, _target,
                2, 2, 4, 4, geometry, 1, 1, false, LibplaceboQuality.Balanced);
            Assert.Equal(expectSuccess, rendered);
            return D3D11FrameCapture.CopyBgra(_device, _context, _target, false);
        }

        public bool TimestampAvailable()
        {
            using var query = _device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _context.End(query);
            _context.Flush();
            while (true)
            {
                try
                {
                    var value = _context.GetData<QueryDataTimestampDisjoint>(query, AsyncGetDataFlags.None);
                    return !value.Disjoint && value.Frequency > 0;
                }
                catch (Exception ex) when (ex.HResult == 1)
                {
                    Thread.Sleep(1);
                }
            }
        }

        public void Dispose()
        {
            Renderer.Dispose();
            _source.Dispose();
            _target.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
