using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using HomePlayer.Core;
using WinRT;

namespace HomePlayer;

public sealed class VideoPresenter : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _upload;
    private VideoFrame? _pending;
    private int _width;
    private int _height;
    private bool _drawing;

    public VideoPresenter(SwapChainPanel panel)
    {
        _panel = panel;
        panel.SizeChanged += (_, _) => Resize();
        panel.CompositionScaleChanged += (_, _) => Resize();
    }

    public void Submit(VideoFrame frame)
    {
        var copy = new byte[frame.Data.Length];
        Buffer.BlockCopy(frame.Data, 0, copy, 0, copy.Length);
        lock (_gate)
        {
            _pending = new VideoFrame
            {
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                Data = copy,
                BottomUp = frame.BottomUp
            };
        }

        _panel.DispatcherQueue.TryEnqueue(Draw);
    }

    public void Dispose()
    {
        _upload?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    private void Resize()
    {
        var width = PixelWidth();
        var height = PixelHeight();
        if (width < 2 || height < 2)
            return;
        if (_swapChain != null && width == _width && height == _height)
            return;
        EnsureDevice();
        if (_device == null)
            return;
        _upload?.Dispose();
        _upload = null;
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
        _upload = _device.CreateTexture2D(new Texture2DDescription
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
        Draw();
    }

    private unsafe void Draw()
    {
        if (_drawing || _context == null || _swapChain == null || _upload == null)
            return;
        VideoFrame? frame;
        lock (_gate)
            frame = _pending;
        if (frame == null || _width < 2 || _height < 2)
            return;
        _drawing = true;
        try
        {
            var mapped = _context.Map(_upload, 0, MapMode.WriteDiscard);
            Blit(frame, (byte*)mapped.DataPointer, (int)mapped.RowPitch, _width, _height);
            _context.Unmap(_upload, 0);
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(back, _upload);
            _swapChain.Present(1, PresentFlags.None);
        }
        catch
        {
        }
        finally
        {
            _drawing = false;
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
            return;
        _device = device;
        _context = context;
    }

    private int PixelWidth() => Math.Max(0, (int)(_panel.ActualWidth * _panel.CompositionScaleX));
    private int PixelHeight() => Math.Max(0, (int)(_panel.ActualHeight * _panel.CompositionScaleY));

    private static unsafe void Blit(VideoFrame frame, byte* destination, int destinationPitch, int viewWidth, int viewHeight)
    {
        var scale = Math.Min(viewWidth / (double)frame.Width, viewHeight / (double)frame.Height);
        if (scale <= 0)
            return;
        var drawWidth = Math.Max(1, (int)(frame.Width * scale));
        var drawHeight = Math.Max(1, (int)(frame.Height * scale));
        var originX = (viewWidth - drawWidth) / 2;
        var originY = (viewHeight - drawHeight) / 2;
        for (var y = 0; y < viewHeight; y++)
        {
            var row = destination + y * destinationPitch;
            for (var x = 0; x < viewWidth; x++)
            {
                var pixel = row + x * 4;
                if (x < originX || y < originY || x >= originX + drawWidth || y >= originY + drawHeight)
                {
                    pixel[0] = pixel[1] = pixel[2] = 0;
                    pixel[3] = 255;
                    continue;
                }

                var sourceX = Math.Min(frame.Width - 1, (int)((x - originX) / scale));
                var sourceY = Math.Min(frame.Height - 1, (int)((y - originY) / scale));
                if (frame.BottomUp)
                    sourceY = frame.Height - 1 - sourceY;
                var source = sourceY * frame.Stride + sourceX * 4;
                if (source + 3 >= frame.Data.Length)
                    continue;
                pixel[0] = frame.Data[source];
                pixel[1] = frame.Data[source + 1];
                pixel[2] = frame.Data[source + 2];
                pixel[3] = 255;
            }
        }
    }
}

[ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}
