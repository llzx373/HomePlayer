#pragma warning disable CS8500
using System.Runtime.InteropServices;
using HomePlayer.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback;

public enum LibplaceboQuality
{
    Fast,
    Balanced,
    HighQuality
}

public static unsafe class LibplaceboNative
{
    private static readonly object Gate = new();
    private static nint _library;
    private static string? _error;

    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
                return Ensure();
        }
    }

    public static string Version
    {
        get
        {
            lock (Gate)
            {
                if (!Ensure())
                    return _error ?? "libplacebo 不可用";
                return Marshal.PtrToStringUTF8(pl_version()) ?? "unknown";
            }
        }
    }

    public static nint CreateDevice(ID3D11Device device)
    {
        lock (Gate)
        {
            if (!Ensure())
                throw new InvalidOperationException(_error);
            var parameters = new PlD3D11Params
            {
                Device = device.NativePointer,
                AllowSoftware = true
            };
            if (_log == 0)
            {
                var logParameters = new PlLogParams
                {
                    LogCallback = Marshal.GetFunctionPointerForDelegate(LogCallback),
                    LogLevel = 2
                };
                var createLog = Marshal.GetDelegateForFunctionPointer<PlLogCreateDelegate>(GetExport("pl_log_create_360"));
                _log = createLog(360, &logParameters);
            }
            return pl_d3d11_create(_log, &parameters);
        }
    }

    public static nint CreateRenderer(nint gpu)
    {
        lock (Gate)
            return pl_renderer_create(_log, gpu);
    }

    public static nint Wrap(nint gpu, ID3D11Texture2D texture, Format format, int width, int height, int arraySlice)
    {
        var parameters = new PlD3D11WrapParams
        {
            Texture = texture.NativePointer,
            ArraySlice = arraySlice,
            Format = format,
            Width = width,
            Height = height
        };
        var wrap = Marshal.GetDelegateForFunctionPointer<PlD3D11WrapDelegate>(GetExport("pl_d3d11_wrap"));
        return wrap(gpu, &parameters);
    }

    public static bool Render(nint renderer, PlFrame image, PlFrame target, LibplaceboQuality quality)
    {
        var preset = quality switch
        {
            LibplaceboQuality.Fast => GetExport("pl_render_fast_params"),
            LibplaceboQuality.Balanced => GetExport("pl_render_default_params"),
            _ => GetExport("pl_render_high_quality_params")
        };
        return pl_render_image(renderer, image, target, preset);
    }

    public static void DestroyTexture(nint gpu, ref nint texture)
    {
        fixed (nint* pointer = &texture)
            pl_tex_destroy(gpu, pointer);
    }

    public static void DestroyRenderer(ref nint renderer)
    {
        fixed (nint* pointer = &renderer)
            pl_renderer_destroy(pointer);
    }

    public static void DestroyDevice(ref nint device)
    {
        fixed (nint* pointer = &device)
            pl_d3d11_destroy(pointer);
    }

    public static void SetChromaLocation(PlFrame frame, VideoChromaLocation location) =>
        pl_frame_set_chroma_location(frame, location switch
        {
            VideoChromaLocation.Center => 2,
            VideoChromaLocation.TopLeft => 3,
            _ => 1
        });

    private static bool Ensure()
    {
        if (_library != 0)
            return true;
        try
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("HOMEPLAYER_LIBPLACEBO_DIR"),
                Path.Combine(AppContext.BaseDirectory, "libplacebo")
            };
            var directory = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "libplacebo-360.dll")));
            if (directory == null)
            {
                _error = "未找到 libplacebo-360.dll 及其私有依赖目录。";
                return false;
            }
            _library = NativeLibrary.Load(Path.Combine(directory, "libplacebo-360.dll"));
            NativeLibrary.SetDllImportResolver(typeof(LibplaceboNative).Assembly, (name, _, _) =>
                string.Equals(name, "libplacebo-360.dll", StringComparison.OrdinalIgnoreCase) ? _library : 0);
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return false;
        }
    }

    private static nint GetExport(string name) => NativeLibrary.GetExport(_library, name);

    [StructLayout(LayoutKind.Sequential)]
    public struct PlD3D11Params
    {
        public nint Device;
        public nint Adapter;
        public long AdapterLuid;
        [MarshalAs(UnmanagedType.I1)] public bool AllowSoftware;
        [MarshalAs(UnmanagedType.I1)] public bool ForceSoftware;
        [MarshalAs(UnmanagedType.I1)] public bool Debug;
        [MarshalAs(UnmanagedType.I1)] public bool NoCompute;
        public uint Flags;
        public int MinFeatureLevel;
        public int MaxFeatureLevel;
        public int MaxFrameLatency;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlD3D11WrapParams
    {
        public nint Texture;
        public int ArraySlice;
        public Format Format;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlBitEncoding
    {
        public int SampleDepth;
        public int ColorDepth;
        public int BitShift;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlColorRepr
    {
        public int System;
        public int Levels;
        public int Alpha;
        public PlBitEncoding Bits;
        public nint Dovi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlCieXy
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlRawPrimaries
    {
        public PlCieXy Red;
        public PlCieXy Green;
        public PlCieXy Blue;
        public PlCieXy White;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlHdrBezier
    {
        public float TargetLuma;
        public float KneeX;
        public float KneeY;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)] public float[] Anchors;
        public byte NumAnchors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlHdrMetadata
    {
        public PlRawPrimaries Primaries;
        public float MinLuma;
        public float MaxLuma;
        public float MaxCll;
        public float MaxFall;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] SceneMax;
        public float SceneAvg;
        public PlHdrBezier Ootf;
        public float MaxPqY;
        public float AvgPqY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlColorSpace
    {
        public int Primaries;
        public int Transfer;
        public PlHdrMetadata Hdr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlPlane
    {
        public nint Texture;
        public int AddressMode;
        [MarshalAs(UnmanagedType.I1)] public bool Flipped;
        public int Components;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] ComponentMapping;
        public float ShiftX;
        public float ShiftY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlIccProfile
    {
        public nint Data;
        public nuint Length;
        public ulong Signature;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlRect2Df
    {
        public float X0;
        public float Y0;
        public float X1;
        public float Y1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public class PlFilmGrainData
    {
        public int Type;
        public ulong Seed;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 192)] public byte[] Parameters = new byte[192];
    }

    [StructLayout(LayoutKind.Sequential)]
    public class PlFrame
    {
        public int NumPlanes;
        private int _planesPadding;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public PlPlane[] Planes = new PlPlane[4];
        public int Field;
        public int FirstField;
        public nint Previous;
        public nint Next;
        public nint Acquire;
        public nint Release;
        public PlColorRepr Repr;
        public PlColorSpace Color;
        public nint Icc;
        public PlIccProfile Profile;
        public nint Lut;
        public int LutType;
        public PlRect2Df Crop;
        public int Rotation;
        public nint Overlays;
        public int NumOverlays;
        public PlFilmGrainData FilmGrain = new();
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlLogParams
    {
        public nint LogCallback;
        public nint LogPrivate;
        public int LogLevel;
    }

    private static nint _log;
    private static readonly PlLogCallbackDelegate LogCallback = (_, level, message) =>
        Console.Error.WriteLine($"LP {level}: {Marshal.PtrToStringUTF8(message)}");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PlLogCallbackDelegate(nint priv, int level, nint message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PlLogCreateDelegate(int apiVersion, PlLogParams* parameters);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint pl_version();

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint pl_d3d11_create(nint log, PlD3D11Params* parameters);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint pl_renderer_create(nint log, nint gpu);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint pl_d3d11_wrap(nint gpu, PlD3D11WrapParams* parameters);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool pl_render_image(nint renderer, [In] PlFrame image, [In] PlFrame target, nint parameters);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_tex_destroy(nint gpu, nint* texture);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_renderer_destroy(nint* renderer);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_d3d11_destroy(nint* device);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_frame_set_chroma_location([In, Out] PlFrame frame, int location);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PlD3D11WrapDelegate(nint gpu, PlD3D11WrapParams* parameters);
}
