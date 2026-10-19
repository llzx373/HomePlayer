using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using HomePlayer.Core;
using WinRT;

namespace HomePlayer;

public sealed class VideoPresenter : IDisposable
{
    private const string VertexShaderCode = """
        struct Input {
            float2 pos : POSITION;
            float2 uv : TEXCOORD;
        };
        struct Output {
            float4 pos : SV_Position;
            float2 uv : TEXCOORD;
        };
        Output main(Input input) {
            Output output;
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

    private readonly SwapChainPanel _panel;
    private readonly object _gate = new();
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
    private VideoFrame? _pending;
    private int _width;
    private int _height;
    private int _frameWidth;
    private int _frameHeight;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private readonly uint[] _vertexStrides = [16];
    private readonly uint[] _vertexOffsets = [0];
    private readonly ID3D11Buffer[] _vertexBuffers = new ID3D11Buffer[1];
    private readonly ID3D11ShaderResourceView[] _frameViews = new ID3D11ShaderResourceView[1];
    private readonly ID3D11SamplerState[] _samplers = new ID3D11SamplerState[1];
    private int _posted;
    private bool _drawing;
    private bool _resizing;
    private long _lastRecoveryMs;

    public VideoPresenter(SwapChainPanel panel)
    {
        _panel = panel;
        panel.Loaded += (_, _) => Refresh();
        panel.SizeChanged += (_, _) => Refresh();
        panel.CompositionScaleChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        TryResize();
        PostDraw();
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
        PostDraw();
    }

    public void Dispose()
    {
        VideoFrame? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.ReturnBuffer();
        ReleaseDeviceResources();
    }

    private void ReleaseDeviceResources()
    {
        _target?.Dispose();
        _target = null;
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
        _swapChain?.Dispose();
        _swapChain = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _vertexBytecode = null;
        _width = 0;
        _height = 0;
        _frameWidth = 0;
        _frameHeight = 0;
    }

    private void PostDraw()
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0)
            _panel.DispatcherQueue.TryEnqueue(Draw);
    }

    private void Resize()
    {
        if (_resizing)
            return;
        var scaleX = ScaleOf(_panel.CompositionScaleX);
        var scaleY = ScaleOf(_panel.CompositionScaleY);
        var width = PixelSize(_panel.ActualWidth, scaleX);
        var height = PixelSize(_panel.ActualHeight, scaleY);
        if (width < 2 || height < 2)
            return;
        if (_swapChain != null
            && Math.Abs(width - _width) < 2
            && Math.Abs(height - _height) < 2
            && Math.Abs(scaleX - _scaleX) < 0.01f
            && Math.Abs(scaleY - _scaleY) < 0.01f)
            return;

        _resizing = true;
        try
        {
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
                var native = _panel.As<ISwapChainPanelNative>();
                native.SetSwapChain(_swapChain.NativePointer);
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
            catch
            {
            }
        }
        finally
        {
            _resizing = false;
        }
    }

    private void UnbindTarget()
    {
        _target?.Dispose();
        _target = null;
        if (_context == null)
            return;
        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
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

    private unsafe void Draw()
    {
        Interlocked.Exchange(ref _posted, 0);
        if (_drawing)
        {
            PostDraw();
            return;
        }

        TryResize();
        if (_context == null || _swapChain == null || _target == null || _vertexShader == null || _vertices == null)
            return;
        VideoFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }

        if (frame == null || frame.Width < 1 || frame.Height < 1)
        {
            frame?.ReturnBuffer();
            return;
        }

        _drawing = true;
        try
        {
            EnsureFrameTexture(frame.Width, frame.Height);
            if (_frameTexture == null || _frameView == null)
                return;
            var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard);
            CopyFrame(frame, (byte*)mapped.DataPointer, (int)mapped.RowPitch);
            _context.Unmap(_frameTexture, 0);
            WriteQuad(frame);
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
            _frameViews[0] = _frameView;
            _context.PSSetShaderResources(0, 1, _frameViews);
            _samplers[0] = _sampler!;
            _context.PSSetSamplers(0, 1, _samplers);
            _context.Draw(4, 0);
            _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (Exception ex)
        {
            if (IsDeviceLost(ex) || DeviceIsLost())
                RecoverDevice();
        }
        finally
        {
            frame.ReturnBuffer();
            _drawing = false;
            var newer = false;
            lock (_gate)
                newer = _pending != null;
            if (newer)
                PostDraw();
        }
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
            Resize();
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
        _context?.PSSetShaderResources(0, 1, new ID3D11ShaderResourceView[1]);

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

    private unsafe void WriteQuad(VideoFrame frame)
    {
        if (_context == null || _vertices == null || _width < 2 || _height < 2)
            return;
        var scale = Math.Min(_width / (double)frame.Width, _height / (double)frame.Height);
        if (scale <= 0)
            return;
        var drawWidth = Math.Clamp(frame.Width * scale, 1, _width);
        var drawHeight = Math.Clamp(frame.Height * scale, 1, _height);
        var originX = (_width - drawWidth) / 2;
        var originY = (_height - drawHeight) / 2;
        var left = (float)(originX / _width * 2 - 1);
        var right = (float)((originX + drawWidth) / _width * 2 - 1);
        var top = (float)(1 - originY / _height * 2);
        var bottom = (float)(1 - (originY + drawHeight) / _height * 2);
        var vTop = frame.BottomUp ? 1f : 0f;
        var vBottom = frame.BottomUp ? 0f : 1f;
        var quad = stackalloc float[16]
        {
            left, top, 0, vTop,
            right, top, 1, vTop,
            left, bottom, 0, vBottom,
            right, bottom, 1, vBottom
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
                        Marshal.Release(errors);
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
