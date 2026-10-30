using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using HomePlayer.Core;
using HomePlayer.Playback;
using WinRT;

namespace HomePlayer;

/// <summary>
/// 把 CPU 平面或 D3D11 纹理租约呈现到 <see cref="SwapChainPanel"/>。
/// 所有 D3D 调用都在专用渲染线程上串行执行，UI 线程只负责尺寸/挂载这类轻量工作。
/// BGRA 直接复制、YUV 仅在截图时转换，不在热路径上保留额外 RGB 整帧副本。
/// </summary>
public sealed class VideoPresenter : IDisposable
{
    private const string VertexShaderCode = """
        struct VSInput {
            float2 pos : POSITION;
            float2 uv : TEXCOORD;
        };
        struct VSOutput {
            float4 pos : SV_Position;
            float2 uv : TEXCOORD;
        };
        VSOutput main(VSInput input) {
            VSOutput output;
            output.pos = float4(input.pos, 0, 1);
            output.uv = input.uv;
            return output;
        }
        """;

    private const string ColorTransformCode = """
        cbuffer ColorConversion : register(b0) {
            float yOffset;
            float yScale;
            float chromaOffset;
            float outputTransform;
            float rV;
            float gU;
            float gV;
            float bU;
        };
        float3 hp_to2020(float3 rgb) {
            return float3(0.627403 * rgb.r + 0.329283 * rgb.g + 0.0433136 * rgb.b,
                0.069097 * rgb.r + 0.91954 * rgb.g + 0.0113612 * rgb.b,
                0.0163916 * rgb.r + 0.0880132 * rgb.g + 0.895595 * rgb.b);
        }
        float3 hp_to709(float3 rgb) {
            return float3(1.660491 * rgb.r - 0.587641 * rgb.g - 0.07285 * rgb.b,
                -0.12455 * rgb.r + 1.1329 * rgb.g - 0.008349 * rgb.b,
                -0.018151 * rgb.r - 0.100579 * rgb.g + 1.11873 * rgb.b);
        }
        float3 hp_sdr_linear(float3 rgb) { return pow(max(rgb, 0), 2.2); }
        float3 hp_sdr_encode(float3 rgb) { return pow(max(rgb, 0), 1.0 / 2.2); }
        float3 hp_pq_linear(float3 rgb) {
            float3 p = pow(max(rgb, 0), 1.0 / 78.84375);
            return pow(max(p - 0.8359375, 0) / (18.8515625 - 18.6875 * p), 1.0 / 0.1593017578125);
        }
        float3 hp_pq_encode(float3 rgb) {
            float3 p = pow(max(rgb, 0), 0.1593017578125);
            return pow((0.8359375 + 18.8515625 * p) / (1 + 18.6875 * p), 78.84375);
        }
        float3 hp_hlg_linear(float3 rgb) {
            float3 low = rgb * rgb / 3;
            float3 high = (exp((rgb - 0.55991073) / 0.17883277) + 0.28466892) / 12;
            return rgb <= 0.5 ? low : high;
        }
        float3 hp_transform(float3 rgb) {
            if (outputTransform == 1)
                return hp_pq_encode(hp_to2020(hp_sdr_linear(rgb)));
            if (outputTransform == 2)
                return hp_sdr_encode(hp_to709(rgb / (1 + rgb)));
            if (outputTransform == 3)
                return hp_pq_encode(rgb);
            if (outputTransform == 4)
                return hp_sdr_encode(hp_to709(rgb / (1 + rgb)));
            return rgb;
        }
        """;

    private const string PixelShaderCode = ColorTransformCode + """
        Texture2D frameTexture : register(t0);
        SamplerState frameSampler : register(s0);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {
            float3 rgb = frameTexture.Sample(frameSampler, uv).rgb;
            if (outputTransform == 2 || outputTransform == 4)
                rgb = hp_pq_linear(rgb);
            else if (outputTransform == 3)
                rgb = hp_hlg_linear(rgb);
            return float4(hp_transform(rgb), 1);
        }
        """;

    private const string YuvPixelShaderCode = ColorTransformCode + """
        Texture2D lumaTexture : register(t0);
        Texture2D chromaTexture : register(t1);
        SamplerState frameSampler : register(s0);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {
            float y = (lumaTexture.Sample(frameSampler, uv).r - yOffset) * yScale;
            float2 chroma = chromaTexture.Sample(frameSampler, uv).rg - chromaOffset;
            float3 rgb = float3(y + rV * chroma.y, y - gU * chroma.x - gV * chroma.y, y + bU * chroma.x);
            if (outputTransform == 2 || outputTransform == 4)
                rgb = hp_pq_linear(rgb);
            else if (outputTransform == 3)
                rgb = hp_hlg_linear(rgb);
            return float4(hp_transform(rgb), 1);
        }
        """;

    private static readonly ID3D11RenderTargetView[] NoTargets = [];
    private static readonly ID3D11ShaderResourceView[] NoFrameViews = new ID3D11ShaderResourceView[2];

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueue _ui;
    private readonly RenderThread _render;
    private readonly object _gate = new();
    private readonly object _deviceSync = new();
    private ISwapChainPanelNative? _native;

    private FrameLease? _pending;
    private FrameLease? _lastFrame;

    private int _desiredWidth;
    private int _desiredHeight;
    private float _desiredScaleX = 1;
    private float _desiredScaleY = 1;
    private int _resizeRequested;
    private int _posted;
    private int _aspectValue = (int)AspectMode.Fit;
    private int _attached;
    private int _disposed;
    private volatile bool _suspended = true;
    private volatile bool _allowed;

    private static readonly object TraceGate = new();

    private static void Trace(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomePlayer");
            Directory.CreateDirectory(directory);
            lock (TraceGate)
                File.AppendAllText(Path.Combine(directory, "render.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    // 以下字段只在渲染线程上读写。
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private D3D11SharedDevice? _sharedDevice;
    private IDXGISwapChain1? _swapChain;
    private IntPtr _frameLatencyWaitable;
    private ID3D11RenderTargetView? _target;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11Texture2D? _chromaTexture;
    private ID3D11ShaderResourceView? _frameView;
    private ID3D11ShaderResourceView? _chromaView;
    private ID3D11Buffer? _vertices;
    private ID3D11Buffer? _colorConstants;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _yuvPixelShader;
    private ID3D11InputLayout? _layout;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;
    private byte[]? _vertexBytecode;
    private int _width;
    private int _height;
    private int _frameWidth;
    private int _frameHeight;
    private VideoPixelFormat _framePixelFormat;
    private bool _frameBottomUp;
    private float _frameUvScaleX = 1;
    private float _frameUvScaleY = 1;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private long _lastRecoveryMs;
    private long _lastPipelineTryMs;
    private long _deviceGeneration;
    private long _adapterLuid;
    private bool _hdrOutput;
    private bool _hdrOutputBlocked;
    private bool _displaySupportsHdr;
    private string _displayName = "";
    private long _displayGeneration;
    private long _lastDisplayCheck;
    private DisplayOutputInfo _displayInfo = new("", DisplayColorMode.Unknown, "Unknown", "Unknown", 0);
    private nint _gpuTexture;
    private int _gpuSubresource = -1;
    private readonly uint[] _vertexStrides = [16];
    private readonly uint[] _vertexOffsets = [0];
    private readonly ID3D11Buffer[] _vertexBuffers = new ID3D11Buffer[1];
    private readonly ID3D11Buffer[] _colorBuffers = new ID3D11Buffer[1];
    private readonly ID3D11ShaderResourceView[] _frameViews = new ID3D11ShaderResourceView[2];
    private readonly ID3D11SamplerState[] _samplers = new ID3D11SamplerState[1];

    public Func<bool>? CanRender { get; set; }

    public DisplayOutputInfo CurrentDisplayOutput
    {
        get
        {
            lock (_gate)
                return _displayInfo;
        }
    }

    public VideoPresenter(SwapChainPanel panel)
    {
        _panel = panel;
        _ui = panel.DispatcherQueue;
        _render = new RenderThread();
        panel.Loaded += (_, _) => Resume();
        panel.Unloaded += (_, _) => Suspend();
        panel.SizeChanged += (_, _) => Refresh();
        panel.CompositionScaleChanged += (_, _) => Refresh();
    }

    public D3D11SharedDevice GetSharedDevice() => _render.Invoke(() =>
    {
        lock (_deviceSync)
        {
            EnsureDevice();
            if (_device == null || _context == null)
                throw new InvalidOperationException("无法创建 D3D11 播放设备。");
            if (_sharedDevice == null)
                _sharedDevice = new D3D11SharedDevice(
                    _device.NativePointer,
                    _context.NativePointer,
                    _adapterLuid,
                    _deviceGeneration,
                    _deviceSync);
            else
                _sharedDevice.Update(_device.NativePointer, _context.NativePointer, _deviceGeneration);
            return _sharedDevice;
        }
    });

    /// <summary>
    /// 面板的 WinRT 接口必须在 UI 线程上取，第一次用到时再拿，避免构造阶段过早访问。
    /// </summary>
    private ISwapChainPanelNative? Native
    {
        get
        {
            if (_native != null)
                return _native;
            try
            {
                _native = _panel.As<ISwapChainPanelNative>();
            }
            catch
            {
                _native = null;
            }

            return _native;
        }
    }

    public AspectMode AspectMode
    {
        get => (AspectMode)Volatile.Read(ref _aspectValue);
        set
        {
            var next = (int)value;
            if (Interlocked.Exchange(ref _aspectValue, next) == next)
                return;
            Refresh();
        }
    }

    public void Refresh()
    {
        UpdateAllowed();
        if (!_allowed || _suspended)
            return;
        CaptureDesiredSize();
        Interlocked.Exchange(ref _resizeRequested, 1);
        PostDraw();
    }

    public void Suspend()
    {
        _suspended = true;
        _allowed = false;
        DetachSwapChainOnUiThread();
        _render.Post(ReleaseSwapChain);
    }

    public void Resume()
    {
        if (!_panel.IsLoaded || CanRender?.Invoke() == false)
            return;
        _suspended = false;
        _allowed = true;
        CaptureDesiredSize();
        Interlocked.Exchange(ref _resizeRequested, 1);
        PostDraw();
    }

    public bool HasLastFrame
    {
        get
        {
            lock (_gate)
                return _lastFrame != null;
        }
    }

    public void Submit(FrameLease frame)
    {
        FrameLease? replaced;
        lock (_gate)
        {
            replaced = _pending;
            _pending = frame;
        }

        replaced?.ReturnBuffer();
        if (!_suspended && _allowed)
            PostDraw();
    }

    public bool TryCopyLastFrame(out int width, out int height, out int stride, out bool bottomUp, out byte[] data)
    {
        width = 0;
        height = 0;
        stride = 0;
        bottomUp = false;
        data = [];
        lock (_gate)
        {
            if (_lastFrame is VideoFrame bgra)
            {
                var length = Math.Min(bgra.Stride * bgra.Height, bgra.Data.Length);
                if (length <= 0)
                    return false;
                width = bgra.Width;
                height = bgra.Height;
                stride = bgra.Stride;
                bottomUp = bgra.BottomUp;
                data = new byte[length];
                Buffer.BlockCopy(bgra.Data, 0, data, 0, length);
                return true;
            }

            if (_lastFrame is CpuFrameLease cpu && YuvFrameConverter.TryCopyBgra(cpu, out stride, out data))
            {
                width = cpu.Width;
                height = cpu.Height;
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        FrameLease? pending;
        FrameLease? last;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            last = _lastFrame;
            _lastFrame = null;
        }

        pending?.ReturnBuffer();
        last?.ReturnBuffer();
        _render.Post(ClearFrame);
    }

    private void ClearFrame()
    {
        lock (_deviceSync)
            ClearFrameCore();
    }

    private void ClearFrameCore()
    {
        ReleaseFrameResources();
        _frameWidth = 0;
        _frameHeight = 0;
        if (_context == null || _target == null || _swapChain == null)
            return;
        try
        {
            _context.OMSetRenderTargets(_target, null);
            _context.ClearRenderTargetView(_target, new Color4(0, 0, 0, 1));
            _context.OMSetRenderTargets(NoTargets, null);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (Exception ex)
        {
            if (IsDeviceLost(ex) || DeviceIsLost())
                RecoverDevice();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _suspended = true;
        _allowed = false;
        DetachSwapChainOnUiThread();
        _render.Post(ReleaseDeviceResources);
        _render.Dispose();

        FrameLease? pending;
        FrameLease? last;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            last = _lastFrame;
            _lastFrame = null;
        }

        pending?.ReturnBuffer();
        last?.ReturnBuffer();
    }

    private void UpdateAllowed() =>
        _allowed = _panel.IsLoaded && CanRender?.Invoke() != false;

    private void CaptureDesiredSize()
    {
        var scaleX = ScaleOf(_panel.CompositionScaleX);
        var scaleY = ScaleOf(_panel.CompositionScaleY);
        Volatile.Write(ref _desiredWidth, PixelSize(_panel.ActualWidth, scaleX));
        Volatile.Write(ref _desiredHeight, PixelSize(_panel.ActualHeight, scaleY));
        Volatile.Write(ref _desiredScaleX, scaleX);
        Volatile.Write(ref _desiredScaleY, scaleY);
    }

    private void PostDraw()
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0)
            _render.Post(Draw);
    }

    private void DetachSwapChainOnUiThread()
    {
        Interlocked.Exchange(ref _attached, 0);
        var native = Native;
        if (native == null)
            return;
        try
        {
            native.SetSwapChain(IntPtr.Zero);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 渲染线程创建交换链后，必须回到 UI 线程把它挂到面板上。
    /// </summary>
    private void AttachSwapChainToPanel(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero || _suspended)
            return;

        Interlocked.Exchange(ref _attached, 1);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    var native = Native;
                    if (!_suspended && native != null)
                        native.SetSwapChain(pointer);
                }
                catch (Exception ex)
                {
                    Trace("attach error " + ex.Message);
                }
                finally
                {
                    done.TrySetResult();
                }
            }))
        {
            Trace("attach enqueue failed");
            return;
        }

        if (!done.Task.Wait(1000))
            Trace("attach wait timed out");
    }

    /// <summary>
    /// 释放交换链前必须先解绑面板，否则 XAML 合成器可能访问已释放的对象。
    /// Suspend/Dispose 已经在 UI 线程同步解绑过了，这里只处理设备丢失的情况。
    /// </summary>
    private void DetachSwapChainBeforeRelease()
    {
        Interlocked.Exchange(ref _attached, 0);
        if (_suspended)
            return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    Native?.SetSwapChain(IntPtr.Zero);
                }
                catch
                {
                }
                finally
                {
                    done.TrySetResult();
                }
            }))
        {
            return;
        }

        done.Task.Wait(1000);
    }

    private void ReleaseSwapChain()
    {
        lock (_deviceSync)
            ReleaseSwapChainCore();
    }

    private void ReleaseSwapChainCore()
    {
        UnbindTarget();
        var chain = _swapChain;
        _swapChain = null;
        _frameLatencyWaitable = IntPtr.Zero;
        _width = 0;
        _height = 0;
        if (chain == null)
            return;
        DetachSwapChainBeforeRelease();
        chain.Dispose();
    }

    private void Draw()
    {
        lock (_deviceSync)
            DrawCore();
    }

    private void DrawCore()
    {
        Interlocked.Exchange(ref _posted, 0);
        if (_suspended || !_allowed)
            return;

        if (Interlocked.Exchange(ref _resizeRequested, 0) == 1)
            TryResize();
        else if (_swapChain != null && Volatile.Read(ref _attached) == 0)
            AttachSwapChainToPanel(_swapChain.NativePointer);

        if (_context == null || _swapChain == null || _target == null || _vertexShader == null || _pixelShader == null
            || _yuvPixelShader == null || _vertices == null || _colorConstants == null || _layout == null || _sampler == null || _rasterizer == null)
        {
            if (_device != null && _context != null && _vertexShader == null)
                TryEnsurePipeline();
            return;
        }

        FrameLease? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }

        var upload = frame != null && frame.Width >= 1 && frame.Height >= 1;
        if (frame != null && !upload)
        {
            frame.ReturnBuffer();
            frame = null;
        }

        if (!upload && (_frameView == null || _frameWidth < 1 || _frameHeight < 1))
            return;

        var outputTransform = EnsureDisplayOutput(frame?.Color.Transfer ?? _lastFrame?.Color.Transfer ?? VideoTransferFunction.Unknown);
        try
        {
            if (upload && frame != null)
            {
                if (!PrepareFrame(frame))
                {
                    frame.ReturnBuffer();
                    frame = null;
                }
                else
                {
                    UpdateColorConversion(frame.Color, frame.Height, outputTransform);
                }
            }

            var frameView = _frameView;
            if (frameView == null)
                return;

            var yuv = _framePixelFormat is VideoPixelFormat.Nv12 or VideoPixelFormat.P010;
            WriteQuad();
            _context.OMSetRenderTargets(_target, null);
            _context.RSSetViewport(0, 0, _width, _height);
            _context.RSSetState(_rasterizer);
            _context.ClearRenderTargetView(_target, new Color4(0, 0, 0, 1));
            _context.IASetInputLayout(_layout);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _vertexBuffers[0] = _vertices;
            _context.IASetVertexBuffers(0, 1, _vertexBuffers, _vertexStrides, _vertexOffsets);
            _context.VSSetShader(_vertexShader, null!, 0);
            _context.PSSetShader(yuv ? _yuvPixelShader : _pixelShader, null!, 0);
            _frameViews[0] = frameView;
            _frameViews[1] = _chromaView!;
            _context.PSSetShaderResources(0, 2, _frameViews);
            _colorBuffers[0] = _colorConstants;
            _context.PSSetConstantBuffers(0, 1, _colorBuffers);
            _samplers[0] = _sampler!;
            _context.PSSetSamplers(0, 1, _samplers);
            _context.Draw(4, 0);
            _context.OMSetRenderTargets(NoTargets, null);
            if (frame != null)
                PlaybackTelemetry.Observe(frame, PresentationObservation.Submitted);
            WaitForFrameLatency();
            _swapChain.Present(0, PresentFlags.None);
            if (frame != null)
            {
                PlaybackTelemetry.Observe(frame, PresentationObservation.PresentReturned);
                if (frame.FrameSequence % PlaybackTelemetry.FrameSampling == 0)
                    TracePresentStatistics(frame);
            }
        }
        catch (Exception ex)
        {
            Trace("draw error " + ex);
            if (frame != null)
            {
                frame.ReturnBuffer();
                frame = null;
            }

            if (IsDeviceLost(ex) || DeviceIsLost())
                RecoverDevice();
        }
        finally
        {
            if (frame != null)
                SwapLastFrame(frame);
            var newer = false;
            lock (_gate)
                newer = _pending != null;
            if (newer)
                PostDraw();
        }
    }

    private void SwapLastFrame(FrameLease frame)
    {
        FrameLease? previous;
        lock (_gate)
        {
            previous = _lastFrame;
            _lastFrame = frame;
        }

        previous?.ReturnBuffer();
    }

    private void TryResize()
    {
        try
        {
            Resize();
        }
        catch (Exception ex)
        {
            if (IsDeviceLost(ex) || DeviceIsLost())
                RecoverDevice();
        }
    }

    private void Resize()
    {
        var width = Volatile.Read(ref _desiredWidth);
        var height = Volatile.Read(ref _desiredHeight);
        var scaleX = Volatile.Read(ref _desiredScaleX);
        var scaleY = Volatile.Read(ref _desiredScaleY);
        if (width < 2 || height < 2)
            return;
        if (_swapChain != null
            && Math.Abs(width - _width) < 2
            && Math.Abs(height - _height) < 2
            && Math.Abs(scaleX - _scaleX) < 0.01f
            && Math.Abs(scaleY - _scaleY) < 0.01f)
            return;

        EnsureDevice();
        if (_device == null || _context == null)
            return;
        UnbindTarget();
        var swapChainFormat = _hdrOutput ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        if (_swapChain == null)
        {
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = swapChainFormat,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Scaling = Scaling.Stretch,
                Flags = SwapChainFlags.FrameLatencyWaitableObject
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, description);
            using (var chain = _swapChain.QueryInterface<IDXGISwapChain2>())
            {
                chain.MaximumFrameLatency = 1;
                _frameLatencyWaitable = chain.FrameLatencyWaitableObject;
            }
            AttachSwapChainToPanel(_swapChain.NativePointer);
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, swapChainFormat, SwapChainFlags.FrameLatencyWaitableObject);
        }

        _width = width;
        _height = height;
        _scaleX = scaleX;
        _scaleY = scaleY;
        ApplyPanelTransform();
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(back);
        ConfigureSwapChainColorSpace();
        UpdateDisplayCapabilities(true);
        try
        {
            EnsurePipeline();
        }
        catch (Exception ex)
        {
            Trace("pipeline error " + ex);
        }
    }

    private VideoOutputTransform EnsureDisplayOutput(VideoTransferFunction input)
    {
        UpdateDisplayCapabilities();
        var selection = DisplayOutputPolicy.Select(_displaySupportsHdr && !_hdrOutputBlocked, input);
        if (selection.HdrOutput != _hdrOutput)
        {
            _hdrOutput = selection.HdrOutput;
            ReleaseSwapChain();
            Interlocked.Exchange(ref _resizeRequested, 1);
            TryResize();
        }
        return selection.Transform;
    }

    private unsafe void ConfigureSwapChainColorSpace()
    {
        if (_swapChain == null)
            return;
        using var chain = _swapChain.QueryInterface<IDXGISwapChain3>();
        var colorSpace = _hdrOutput ? ColorSpaceType.RgbFullG2084NoneP2020 : ColorSpaceType.RgbFullG22NoneP709;
        var support = chain.CheckColorSpaceSupport(colorSpace);
        if (!support.HasFlag(SwapChainColorSpaceSupportFlags.Present))
        {
            if (_hdrOutput)
            {
                _hdrOutputBlocked = true;
                _hdrOutput = false;
                ReleaseSwapChain();
                Interlocked.Exchange(ref _resizeRequested, 1);
                PostDraw();
            }
            return;
        }
        chain.SetColorSpace1(colorSpace);
        if (!_hdrOutput)
            return;
        try
        {
            var metadata = new HdrMetadataHdr10
            {
                MaxMasteringLuminance = 1000,
                MinMasteringLuminance = 50,
                MaxContentLightLevel = 1000,
                MaxFrameAverageLightLevel = 400
            };
            metadata.RedPrimary[0] = 13200;
            metadata.RedPrimary[1] = 34500;
            metadata.GreenPrimary[0] = 7500;
            metadata.GreenPrimary[1] = 30000;
            metadata.BluePrimary[0] = 30000;
            metadata.BluePrimary[1] = 7500;
            metadata.WhitePoint[0] = 15635;
            metadata.WhitePoint[1] = 16450;
            using var chain4 = _swapChain.QueryInterface<IDXGISwapChain4>();
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<HdrMetadataHdr10>());
            try
            {
                Marshal.StructureToPtr(metadata, pointer, false);
                chain4.SetHDRMetaData(HdrMetadataType.Hdr10, (uint)Marshal.SizeOf<HdrMetadataHdr10>(), pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception ex)
        {
            Trace("HDR metadata error " + ex.Message);
        }
    }

    private void UpdateDisplayCapabilities(bool force = false)
    {
        var now = Environment.TickCount64;
        if (_swapChain == null || (!force && now - _lastDisplayCheck < 1000))
            return;
        _lastDisplayCheck = now;
        var previousName = _displayName;
        var previousHdr = _displaySupportsHdr;
        try
        {
            using var output = _swapChain.GetContainingOutput();
            using var output6 = output.QueryInterface<IDXGIOutput6>();
            var description = output6.Description1;
            _displayName = description.DeviceName;
            _displaySupportsHdr = description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            if (previousName != _displayName || previousHdr != _displaySupportsHdr)
            {
                _displayGeneration++;
                _hdrOutputBlocked = false;
            }
            PublishDisplayOutput();
        }
        catch (Exception ex)
        {
            _displaySupportsHdr = false;
            PublishDisplayOutput("当前显示能力不可观测：" + ex.Message);
        }
    }

    private void PublishDisplayOutput(string? reason = null)
    {
        var mode = _displaySupportsHdr ? DisplayColorMode.Hdr : DisplayColorMode.Sdr;
        var info = new DisplayOutputInfo(
            _displayName,
            mode,
            _hdrOutput ? "R16G16B16A16_Float" : "B8G8R8A8_UNorm",
            _hdrOutput ? "RGB_FULL_G2084_NONE_P2020" : "RGB_FULL_G22_NONE_P709",
            _displayGeneration,
            null,
            reason ?? (_hdrOutputBlocked ? "交换链不支持 HDR，已回退 SDR。" : null));
        lock (_gate)
            _displayInfo = info;
    }

    private void UnbindTarget()
    {
        _target?.Dispose();
        _target = null;
        _context?.OMSetRenderTargets(NoTargets, null);
    }

    private void ApplyPanelTransform()
    {
        if (_swapChain == null)
            return;
        try
        {
            using var chain = _swapChain.QueryInterface<IDXGISwapChain2>();
            chain.MatrixTransform = new Matrix3x2(1f / _scaleX, 0, 0, 1f / _scaleY, 0, 0);
        }
        catch
        {
        }
    }

    private static bool IsDeviceLost(Exception ex)
    {
        var code = unchecked((uint)ex.HResult);
        return code is 0x887A0005 or 0x887A0006 or 0x887A0007;
    }

    private bool DeviceIsLost()
    {
        try
        {
            return _device != null && _device.DeviceRemovedReason.Failure;
        }
        catch
        {
            return false;
        }
    }

    private void DiscardFrameLeases()
    {
        FrameLease? pending;
        FrameLease? last;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            last = _lastFrame;
            _lastFrame = null;
        }
        pending?.ReturnBuffer();
        last?.ReturnBuffer();
    }

    private void RecoverDevice()
    {
        var now = Environment.TickCount64;
        if (now - _lastRecoveryMs < 2000)
            return;
        _lastRecoveryMs = now;
        try
        {
            DiscardFrameLeases();
            ReleaseDeviceResources();
            Interlocked.Exchange(ref _resizeRequested, 1);
            PostDraw();
        }
        catch
        {
        }
    }

    private unsafe bool PrepareFrame(FrameLease frame)
    {
        if (_device == null || _context == null)
            return false;
        switch (frame)
        {
            case VideoFrame bgra when bgra.PixelFormat == VideoPixelFormat.Bgra32:
                EnsureFrameTexture(bgra.Width, bgra.Height);
                if (_frameTexture == null)
                    return false;
                var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard);
                CopyFrame(bgra, (byte*)mapped.DataPointer, (int)mapped.RowPitch);
                _context.Unmap(_frameTexture, 0);
                _frameBottomUp = bgra.BottomUp;
                _framePixelFormat = VideoPixelFormat.Bgra32;
                return true;
            case CpuFrameLease cpu when cpu.PixelFormat is VideoPixelFormat.Nv12 or VideoPixelFormat.P010:
                return UploadYuv(cpu);
            case D3D11FrameLease gpu:
                return PrepareGpuFrame(gpu);
            default:
                Trace($"unsupported frame storage={frame.Storage} format={frame.PixelFormat}");
                return false;
        }
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_device == null || (_frameTexture != null && _framePixelFormat == VideoPixelFormat.Bgra32
            && width == _frameWidth && height == _frameHeight))
            return;
        ReleaseFrameResources();
        _frameTexture = CreateDynamicTexture(width, height, Format.B8G8R8A8_UNorm);
        _frameView = _device!.CreateShaderResourceView(_frameTexture);
        _frameWidth = width;
        _frameHeight = height;
        _framePixelFormat = VideoPixelFormat.Bgra32;
    }

    private unsafe bool UploadYuv(CpuFrameLease frame)
    {
        if (frame.Width < 2 || frame.Height < 2 || frame.Width % 2 != 0 || frame.Height % 2 != 0)
            return false;
        var bytesPerSample = frame.PixelFormat == VideoPixelFormat.P010 ? 2 : 1;
        var lumaRowBytes = frame.Width * bytesPerSample;
        var chromaRowBytes = frame.Width * bytesPerSample;
        if (!VideoFrameLayout.TryGetPlane(frame, 0, lumaRowBytes, frame.Height, out var luma)
            || !VideoFrameLayout.TryGetPlane(frame, 1, chromaRowBytes, frame.Height / 2, out var chroma))
            return false;

        EnsureYuvTextures(frame.Width, frame.Height, frame.PixelFormat);
        if (_frameTexture == null || _chromaTexture == null)
            return false;
        UploadPlane(frame.Data, luma, lumaRowBytes, frame.Height, _frameTexture);
        UploadPlane(frame.Data, chroma, chromaRowBytes, frame.Height / 2, _chromaTexture);
        _frameWidth = frame.Width;
        _frameHeight = frame.Height;
        _framePixelFormat = frame.PixelFormat;
        _frameBottomUp = false;
        return true;
    }

    private void EnsureYuvTextures(int width, int height, VideoPixelFormat format)
    {
        if (_frameTexture != null && _chromaTexture != null && _framePixelFormat == format
            && width == _frameWidth && height == _frameHeight)
            return;
        ReleaseFrameResources();
        var tenBit = format == VideoPixelFormat.P010;
        _frameTexture = CreateDynamicTexture(width, height, tenBit ? Format.R16_UNorm : Format.R8_UNorm);
        _chromaTexture = CreateDynamicTexture(width / 2, height / 2, tenBit ? Format.R16G16_UNorm : Format.R8G8_UNorm);
        _frameView = _device!.CreateShaderResourceView(_frameTexture);
        _chromaView = _device.CreateShaderResourceView(_chromaTexture);
    }

    private ID3D11Texture2D CreateDynamicTexture(int width, int height, Format format) =>
        _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });

    private unsafe void UploadPlane(byte[] data, VideoFramePlane plane, int rowBytes, int rows, ID3D11Texture2D texture)
    {
        var mapped = _context!.Map(texture, 0, MapMode.WriteDiscard);
        fixed (byte* source = data)
            CopyPlane(source + plane.Offset, plane.Stride, rowBytes, rows, (byte*)mapped.DataPointer, (int)mapped.RowPitch);
        _context.Unmap(texture, 0);
    }

    private bool PrepareGpuFrame(D3D11FrameLease frame)
    {
        var resource = frame.Resource;
        if (_device == null || resource.Texture == 0 || frame.DeviceGeneration != _deviceGeneration
            || resource.AdapterLuid != _adapterLuid)
            return false;
        if (_gpuTexture == resource.Texture && _gpuSubresource == resource.Subresource && _framePixelFormat == frame.PixelFormat
            && _frameWidth == frame.Width && _frameHeight == frame.Height)
            return true;

        using var texture = new ID3D11Texture2D(resource.Texture);
        texture.AddRef();
        var description = texture.Description;
        if (description.Width < (uint)frame.Width || description.Height < (uint)frame.Height
            || resource.Subresource < 0 || resource.Subresource >= description.ArraySize)
            return false;
        var (expectedFormat, lumaFormat, chromaFormat) = frame.PixelFormat switch
        {
            VideoPixelFormat.Bgra32 => (Format.B8G8R8A8_UNorm, Format.B8G8R8A8_UNorm, Format.Unknown),
            VideoPixelFormat.Nv12 => (Format.NV12, Format.R8_UNorm, Format.R8G8_UNorm),
            VideoPixelFormat.P010 => (Format.P010, Format.R16_UNorm, Format.R16G16_UNorm),
            _ => (Format.Unknown, Format.Unknown, Format.Unknown)
        };
        if (description.Format != expectedFormat)
            return false;

        ReleaseFrameResources();
        _frameView = CreateGpuView(texture, resource.Subresource, description.ArraySize, lumaFormat);
        if (chromaFormat != Format.Unknown)
            _chromaView = CreateGpuView(texture, resource.Subresource, description.ArraySize, chromaFormat);
        _gpuTexture = resource.Texture;
        _gpuSubresource = resource.Subresource;
        _frameWidth = frame.Width;
        _frameHeight = frame.Height;
        _framePixelFormat = frame.PixelFormat;
        _frameBottomUp = false;
        _frameUvScaleX = (float)frame.Width / description.Width;
        _frameUvScaleY = (float)frame.Height / description.Height;
        return true;
    }

    private ID3D11ShaderResourceView CreateGpuView(ID3D11Texture2D texture, int subresource, uint arraySize, Format format)
    {
        if (arraySize > 1 || subresource > 0)
        {
            return _device!.CreateShaderResourceView(texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    FirstArraySlice = (uint)subresource,
                    ArraySize = 1
                }
            });
        }

        return _device!.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
        });
    }

    private void ReleaseFrameResources()
    {
        _context?.PSSetShaderResources(0, 2, NoFrameViews);
        _frameView?.Dispose();
        _frameView = null;
        _chromaView?.Dispose();
        _chromaView = null;
        _frameTexture?.Dispose();
        _frameTexture = null;
        _chromaTexture?.Dispose();
        _chromaTexture = null;
        _gpuTexture = 0;
        _gpuSubresource = -1;
        _framePixelFormat = VideoPixelFormat.Unknown;
        _frameUvScaleX = 1;
        _frameUvScaleY = 1;
    }

    private unsafe void UpdateColorConversion(VideoColorMetadata color, int frameHeight, VideoOutputTransform transform)
    {
        if (_context == null || _colorConstants == null)
            return;
        var value = YuvColorConversion.For(color, frameHeight);
        var constants = stackalloc float[8]
        {
            value.YOffset, value.YScale, value.ChromaOffset, (float)transform,
            value.RedV, value.GreenU, value.GreenV, value.BlueU
        };
        var mapped = _context.Map(_colorConstants, 0, MapMode.WriteDiscard);
        Buffer.MemoryCopy(constants, (void*)mapped.DataPointer, 32, 32);
        _context.Unmap(_colorConstants, 0);
    }

    private unsafe void WriteQuad()
    {
        if (_context == null || _vertices == null || _width < 2 || _height < 2 || _frameWidth < 1 || _frameHeight < 1)
            return;
        var q = VideoAspect.Quad(_width, _height, _frameWidth, _frameHeight, (AspectMode)Volatile.Read(ref _aspectValue), _frameBottomUp);
        var quad = stackalloc float[16]
        {
            q.Left, q.Top, q.UvLeft * _frameUvScaleX, q.UvTop * _frameUvScaleY,
            q.Right, q.Top, q.UvRight * _frameUvScaleX, q.UvTop * _frameUvScaleY,
            q.Left, q.Bottom, q.UvLeft * _frameUvScaleX, q.UvBottom * _frameUvScaleY,
            q.Right, q.Bottom, q.UvRight * _frameUvScaleX, q.UvBottom * _frameUvScaleY
        };
        var mapped = _context.Map(_vertices, 0, MapMode.WriteDiscard);
        Buffer.MemoryCopy(quad, (void*)mapped.DataPointer, 64, 64);
        _context.Unmap(_vertices, 0);
    }

    private static unsafe void CopyFrame(VideoFrame frame, byte* destination, int destinationPitch)
    {
        var rowBytes = frame.Width * 4;
        if (rowBytes <= 0 || frame.Stride < rowBytes)
            return;
        fixed (byte* source = frame.Data)
        {
            if (destinationPitch == frame.Stride && frame.Stride == rowBytes)
            {
                var bytes = rowBytes * frame.Height;
                if (bytes <= frame.Data.Length)
                    Buffer.MemoryCopy(source, destination, bytes, bytes);
                return;
            }

            for (var y = 0; y < frame.Height; y++)
            {
                var offset = y * frame.Stride;
                if (offset + rowBytes > frame.Data.Length)
                    return;
                Buffer.MemoryCopy(source + offset, destination + y * destinationPitch, rowBytes, rowBytes);
            }
        }
    }

    private static unsafe void CopyPlane(
        byte* source,
        int sourceStride,
        int rowBytes,
        int rows,
        byte* destination,
        int destinationStride)
    {
        for (var y = 0; y < rows; y++)
            Buffer.MemoryCopy(source + (long)y * sourceStride, destination + (long)y * destinationStride, rowBytes, rowBytes);
    }

    private void WaitForFrameLatency()
    {
        if (_frameLatencyWaitable != IntPtr.Zero)
            WaitForSingleObject(_frameLatencyWaitable, 100);
    }

    private void TracePresentStatistics(FrameLease frame)
    {
        if (_swapChain == null || _swapChain.GetFrameStatistics(out var statistics).Failure)
            return;
        PlaybackTelemetry.Trace(
            "PresentStatistics",
            frame.SessionId,
            frame.Epoch,
            frame.SeekCommandId,
            PresentationObservation.PresentReturned,
            new Dictionary<string, object?>
            {
                ["presentCount"] = statistics.PresentCount,
                ["presentRefreshCount"] = statistics.PresentRefreshCount,
                ["syncRefreshCount"] = statistics.SyncRefreshCount,
                ["syncQpc"] = statistics.SyncQPCTime,
                ["source"] = "DXGIGetFrameStatistics"
            });
    }

    private void EnsureDevice()
    {
        if (_device != null)
            return;
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 },
                out device,
                out context);
            if (result.Failure)
                return;
        }

        _device = device;
        _context = context;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        _adapterLuid = ToInt64(adapter.Description.Luid);
    }

    private static long ToInt64(Vortice.Luid value) => (uint)value.LowPart | ((long)value.HighPart << 32);

    private void TryEnsurePipeline()
    {
        var now = Environment.TickCount64;
        if (now - _lastPipelineTryMs < 1000)
            return;
        _lastPipelineTryMs = now;
        try
        {
            EnsurePipeline();
        }
        catch (Exception ex)
        {
            Trace("pipeline retry error " + ex);
        }
    }

    private unsafe void EnsurePipeline()
    {
        if (_device == null || _vertexShader != null)
            return;
        _vertexBytecode = CompileShader(VertexShaderCode, "vs_4_0");
        var pixelBytecode = CompileShader(PixelShaderCode, "ps_4_0");
        var yuvBytecode = CompileShader(YuvPixelShaderCode, "ps_4_0");
        if (_vertexBytecode == null || pixelBytecode == null || yuvBytecode == null)
            return;
        fixed (byte* vertex = _vertexBytecode)
        fixed (byte* pixel = pixelBytecode)
        fixed (byte* yuv = yuvBytecode)
        {
            _vertexShader = _device.CreateVertexShader(vertex, (nuint)_vertexBytecode.Length);
            _pixelShader = _device.CreatePixelShader(pixel, (nuint)pixelBytecode.Length);
            _yuvPixelShader = _device.CreatePixelShader(yuv, (nuint)yuvBytecode.Length);
        }

        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
        ],
        _vertexBytecode);

        _vertices = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 64,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        _colorConstants = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 32,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });
        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true
        });
    }

    private void ReleaseDeviceResources()
    {
        lock (_deviceSync)
            ReleaseDeviceResourcesCore();
    }

    private void ReleaseDeviceResourcesCore()
    {
        _deviceGeneration++;
        _sharedDevice?.Update(0, 0, _deviceGeneration);
        ReleaseSwapChain();
        ReleaseFrameResources();
        _vertices?.Dispose();
        _vertices = null;
        _colorConstants?.Dispose();
        _colorConstants = null;
        _layout?.Dispose();
        _layout = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _yuvPixelShader?.Dispose();
        _yuvPixelShader = null;
        _sampler?.Dispose();
        _sampler = null;
        _rasterizer?.Dispose();
        _rasterizer = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _vertexBytecode = null;
        _frameWidth = 0;
        _frameHeight = 0;
        _frameBottomUp = false;
    }

    private static int PixelSize(double dips, float scale) => Math.Max(0, (int)Math.Round(dips * scale));
    private static float ScaleOf(double value) => value < 0.01 ? 1f : (float)value;

    private static byte[]? CompileShader(string source, string target)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        var entry = Marshal.StringToHGlobalAnsi("main");
        var profile = Marshal.StringToHGlobalAnsi(target);
        try
        {
            unsafe
            {
                fixed (byte* pointer = bytes)
                {
                    var result = D3DCompile((IntPtr)pointer, (nuint)bytes.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, entry, profile, 0, 0, out var code, out var errors);
                    if (errors != IntPtr.Zero)
                    {
                        if (result < 0)
                            Trace($"d3dcompile {target} hr=0x{result:X8} {ReadBlobText(errors)}");
                        Marshal.Release(errors);
                    }

                    if (result < 0 || code == IntPtr.Zero)
                        return null;
                    return ReadBlob(code);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            Marshal.FreeHGlobal(profile);
        }
    }

    private static string ReadBlobText(IntPtr blob)
    {
        try
        {
            var table = Marshal.ReadIntPtr(blob);
            var getPointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
            var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
            var pointer = getPointer(blob);
            var size = (int)getSize(blob).ToUInt64();
            return size <= 0 ? "" : Marshal.PtrToStringAnsi(pointer, size) ?? "";
        }
        catch (Exception ex)
        {
            return "<" + ex.Message + ">";
        }
    }

    private static byte[] ReadBlob(IntPtr blob)
    {
        var table = Marshal.ReadIntPtr(blob);
        var getPointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
        var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
        var pointer = getPointer(blob);
        var size = (int)getSize(blob).ToUInt64();
        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, size);
        Marshal.Release(blob);
        return bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        IntPtr source,
        nuint sourceSize,
        IntPtr sourceName,
        IntPtr defines,
        IntPtr include,
        IntPtr entryPoint,
        IntPtr target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errors);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBufferSizeDelegate(IntPtr self);
}

[ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}
