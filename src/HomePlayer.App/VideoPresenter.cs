using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using HomePlayer.Core;
using WinRT;

namespace HomePlayer;

/// <summary>
/// 把解码线程送来的帧上传成纹理并呈现到 <see cref="SwapChainPanel"/>。
/// 所有 D3D 调用都在专用渲染线程上串行执行，UI 线程只负责尺寸/挂载这类轻量工作。
/// 最近一次上传的帧会保留在 <see cref="_lastFrame"/> 里，截图时直接读内存，
/// 因此热路径上不再有额外的一整帧拷贝。
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

    private const string PixelShaderCode = """
        Texture2D frameTexture : register(t0);
        SamplerState frameSampler : register(s0);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {
            return float4(frameTexture.Sample(frameSampler, uv).rgb, 1);
        }
        """;

    private static readonly ID3D11RenderTargetView[] NoTargets = [];
    private static readonly ID3D11ShaderResourceView[] NoFrameViews = new ID3D11ShaderResourceView[1];

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueue _ui;
    private readonly RenderThread _render;
    private readonly object _gate = new();
    private ISwapChainPanelNative? _native;

    private VideoFrame? _pending;
    private VideoFrame? _lastFrame;

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
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _target;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameView;
    private ID3D11Buffer? _vertices;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _layout;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;
    private byte[]? _vertexBytecode;
    private int _width;
    private int _height;
    private int _frameWidth;
    private int _frameHeight;
    private bool _frameBottomUp;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private long _lastRecoveryMs;
    private long _lastPipelineTryMs;
    private readonly uint[] _vertexStrides = [16];
    private readonly uint[] _vertexOffsets = [0];
    private readonly ID3D11Buffer[] _vertexBuffers = new ID3D11Buffer[1];
    private readonly ID3D11ShaderResourceView[] _frameViews = new ID3D11ShaderResourceView[1];
    private readonly ID3D11SamplerState[] _samplers = new ID3D11SamplerState[1];

    public Func<bool>? CanRender { get; set; }

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

    public void Submit(VideoFrame frame)
    {
        VideoFrame? replaced;
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
        lock (_gate)
        {
            var frame = _lastFrame;
            if (frame == null)
            {
                width = 0;
                height = 0;
                stride = 0;
                bottomUp = false;
                data = [];
                return false;
            }

            var length = Math.Min(frame.Stride * frame.Height, frame.Data.Length);
            if (length <= 0)
            {
                width = 0;
                height = 0;
                stride = 0;
                bottomUp = false;
                data = [];
                return false;
            }

            width = frame.Width;
            height = frame.Height;
            stride = frame.Stride;
            bottomUp = frame.BottomUp;
            data = new byte[length];
            Buffer.BlockCopy(frame.Data, 0, data, 0, length);
            return true;
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

        VideoFrame? pending;
        VideoFrame? last;
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
        UnbindTarget();
        var chain = _swapChain;
        _swapChain = null;
        _width = 0;
        _height = 0;
        if (chain == null)
            return;
        DetachSwapChainBeforeRelease();
        chain.Dispose();
    }

    private unsafe void Draw()
    {
        Interlocked.Exchange(ref _posted, 0);
        if (_suspended || !_allowed)
            return;

        if (Interlocked.Exchange(ref _resizeRequested, 0) == 1)
            TryResize();
        else if (_swapChain != null && Volatile.Read(ref _attached) == 0)
            AttachSwapChainToPanel(_swapChain.NativePointer);

        if (_context == null || _swapChain == null || _target == null || _vertexShader == null || _pixelShader == null
            || _vertices == null || _layout == null || _sampler == null || _rasterizer == null)
        {
            if (_device != null && _context != null && _vertexShader == null)
                TryEnsurePipeline();
            return;
        }

        VideoFrame? frame;
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

        if (!upload && (_frameTexture == null || _frameView == null || _frameWidth < 1 || _frameHeight < 1))
            return;

        try
        {
            if (upload && frame != null)
            {
                EnsureFrameTexture(frame.Width, frame.Height);
                if (_frameTexture == null || _frameView == null)
                {
                    frame.ReturnBuffer();
                    frame = null;
                }
                else
                {
                    var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard);
                    CopyFrame(frame, (byte*)mapped.DataPointer, (int)mapped.RowPitch);
                    _context.Unmap(_frameTexture, 0);
                    _frameBottomUp = frame.BottomUp;
                }
            }

            var frameView = _frameView;
            if (frameView == null)
                return;

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
            _context.PSSetShader(_pixelShader, null!, 0);
            _frameViews[0] = frameView;
            _context.PSSetShaderResources(0, 1, _frameViews);
            _samplers[0] = _sampler!;
            _context.PSSetSamplers(0, 1, _samplers);
            _context.Draw(4, 0);
            _context.OMSetRenderTargets(NoTargets, null);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (Exception ex)
        {
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

    private void SwapLastFrame(VideoFrame frame)
    {
        VideoFrame? previous;
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
        if (_swapChain == null)
        {
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Scaling = Scaling.Stretch
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, description);
            AttachSwapChainToPanel(_swapChain.NativePointer);
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        }

        _width = width;
        _height = height;
        _scaleX = scaleX;
        _scaleY = scaleY;
        ApplyPanelTransform();
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(back);
        try
        {
            EnsurePipeline();
        }
        catch (Exception ex)
        {
            Trace("pipeline error " + ex);
        }
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

    private void RecoverDevice()
    {
        var now = Environment.TickCount64;
        if (now - _lastRecoveryMs < 2000)
            return;
        _lastRecoveryMs = now;
        try
        {
            ReleaseDeviceResources();
            Interlocked.Exchange(ref _resizeRequested, 1);
            PostDraw();
        }
        catch
        {
        }
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_device == null || (_frameTexture != null && width == _frameWidth && height == _frameHeight))
            return;
        _context?.PSSetShaderResources(0, 1, NoFrameViews);

        _frameView?.Dispose();
        _frameTexture?.Dispose();
        _frameTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        _frameView = _device.CreateShaderResourceView(_frameTexture);
        _frameWidth = width;
        _frameHeight = height;
    }

    private unsafe void WriteQuad()
    {
        if (_context == null || _vertices == null || _width < 2 || _height < 2 || _frameWidth < 1 || _frameHeight < 1)
            return;
        var q = VideoAspect.Quad(_width, _height, _frameWidth, _frameHeight, (AspectMode)Volatile.Read(ref _aspectValue), _frameBottomUp);
        var quad = stackalloc float[16]
        {
            q.Left, q.Top, q.UvLeft, q.UvTop,
            q.Right, q.Top, q.UvRight, q.UvTop,
            q.Left, q.Bottom, q.UvLeft, q.UvBottom,
            q.Right, q.Bottom, q.UvRight, q.UvBottom
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
    }

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
        if (_vertexBytecode == null || pixelBytecode == null)
            return;
        fixed (byte* vertex = _vertexBytecode)
        fixed (byte* pixel = pixelBytecode)
        {
            _vertexShader = _device.CreateVertexShader(vertex, (nuint)_vertexBytecode.Length);
            _pixelShader = _device.CreatePixelShader(pixel, (nuint)pixelBytecode.Length);
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
        ReleaseSwapChain();
        _frameView?.Dispose();
        _frameView = null;
        _frameTexture?.Dispose();
        _frameTexture = null;
        _vertices?.Dispose();
        _vertices = null;
        _layout?.Dispose();
        _layout = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
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
