using System.Numerics;
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
    private int[] _mapX = [];
    private int _width;
    private int _height;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private bool _drawing;
    private bool _again;
    private bool _resizePending;

    public VideoPresenter(SwapChainPanel panel)
    {
        _panel = panel;
        panel.Loaded += (_, _) => Refresh();
        panel.SizeChanged += (_, _) => Refresh();
        panel.CompositionScaleChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Resize();
        Draw();
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
        if (_drawing)
        {
            _resizePending = true;
            return;
        }

        var scaleX = ScaleOf(_panel.CompositionScaleX);
        var scaleY = ScaleOf(_panel.CompositionScaleY);
        var width = PixelWidth(scaleX);
        var height = PixelHeight(scaleY);
        if (width < 2 || height < 2)
            return;
        if (_swapChain != null && width == _width && height == _height && scaleX == _scaleX && scaleY == _scaleY)
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
        _scaleX = scaleX;
        _scaleY = scaleY;
        ApplyPanelTransform();
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
        if (_drawing)
        {
            _again = true;
            return;
        }

        var scaleX = ScaleOf(_panel.CompositionScaleX);
        var scaleY = ScaleOf(_panel.CompositionScaleY);
        var width = PixelWidth(scaleX);
        var height = PixelHeight(scaleY);
        if (width >= 2 && height >= 2 && (_swapChain == null || width != _width || height != _height || scaleX != _scaleX || scaleY != _scaleY))
            Resize();
        if (_context == null || _swapChain == null || _upload == null || _width < 2 || _height < 2)
            return;
        VideoFrame? frame;
        lock (_gate)
            frame = _pending;
        if (frame == null)
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
            if (_resizePending)
            {
                _resizePending = false;
                Resize();
                _again = true;
            }

            if (_again)
            {
                _again = false;
                _panel.DispatcherQueue.TryEnqueue(Draw);
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
            return;
        _device = device;
        _context = context;
    }

    private int PixelWidth(float scale) => Math.Max(0, (int)Math.Round(_panel.ActualWidth * scale));
    private int PixelHeight(float scale) => Math.Max(0, (int)Math.Round(_panel.ActualHeight * scale));
    private static float ScaleOf(double value) => value < 0.01 ? 1f : (float)value;

    private unsafe void Blit(VideoFrame frame, byte* destination, int destinationPitch, int viewWidth, int viewHeight)
    {
        if (frame.Width < 1 || frame.Height < 1 || frame.Stride < 4 || frame.Data.Length < 4)
            return;
        var scale = Math.Min(viewWidth / (double)frame.Width, viewHeight / (double)frame.Height);
        if (scale <= 0)
            return;
        var drawWidth = Math.Clamp((int)Math.Round(frame.Width * scale), 1, viewWidth);
        var drawHeight = Math.Clamp((int)Math.Round(frame.Height * scale), 1, viewHeight);
        var originX = (viewWidth - drawWidth) / 2;
        var originY = (viewHeight - drawHeight) / 2;
        if (_mapX.Length < drawWidth)
            _mapX = new int[drawWidth];
        for (var i = 0; i < drawWidth; i++)
        {
            var sourceX = (int)((long)i * frame.Width / drawWidth);
            if (sourceX >= frame.Width)
                sourceX = frame.Width - 1;
            _mapX[i] = sourceX * 4;
        }

        for (var y = 0; y < viewHeight; y++)
        {
            var row = destination + y * destinationPitch;
            if (y < originY || y >= originY + drawHeight)
            {
                ClearRow(row, viewWidth);
                continue;
            }

            var sourceY = (int)((long)(y - originY) * frame.Height / drawHeight);
            if (sourceY >= frame.Height)
                sourceY = frame.Height - 1;
            if (frame.BottomUp)
                sourceY = frame.Height - 1 - sourceY;
            var sourceRow = sourceY * frame.Stride;
            if (originX > 0)
                ClearRow(row, originX);
            var pixel = row + originX * 4;
            for (var i = 0; i < drawWidth; i++)
            {
                var source = sourceRow + _mapX[i];
                if ((uint)(source + 3) < (uint)frame.Data.Length)
                {
                    pixel[0] = frame.Data[source];
                    pixel[1] = frame.Data[source + 1];
                    pixel[2] = frame.Data[source + 2];
                }

                pixel[3] = 255;
                pixel += 4;
            }

            var tail = viewWidth - originX - drawWidth;
            if (tail > 0)
                ClearRow(row + (originX + drawWidth) * 4, tail);
        }
    }

    private static unsafe void ClearRow(byte* row, int pixels)
    {
        var count = pixels * 4;
        var index = 0;
        for (; index + 8 <= count; index += 8)
            *(ulong*)(row + index) = 0xFF000000FF000000UL;
        for (; index < count; index += 4)
        {
            row[index] = row[index + 1] = row[index + 2] = 0;
            row[index + 3] = 255;
        }
    }
}

[ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}
