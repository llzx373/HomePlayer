using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer.Playback;

public static unsafe partial class LibplaceboNative
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed class PlRenderParams
    {
        public const int Size360 = 288;
        public const int HooksOffset360 = 136;
        public const int NumHooksOffset360 = 144;

        public nint Upscaler;
        public nint Downscaler;
        public nint PlaneUpscaler;
        public nint PlaneDownscaler;
        public float AntiringingStrength;
        public nint FrameMixer;
        public nint DebandParams;
        public nint SigmoidParams;
        public nint ColorAdjustment;
        public nint PeakDetectParams;
        public nint ColorMapParams;
        public nint DitherParams;
        public nint ErrorDiffusion;
        public nint ConeParams;
        public nint BlendParams;
        public nint DeinterlaceParams;
        public nint DistortParams;
        public nint Hooks;
        public int NumHooks;
        public nint Lut;
        public int LutType;
        public int Background;
        public int Border;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] BackgroundColor = new float[3];
        public float BackgroundTransparency;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public float[] TileColors = new float[6];
        public int TileSize;
        public float BlurRadius;
        public float CornerRounding;
        [MarshalAs(UnmanagedType.I1)] public bool SkipAntiAliasing;
        [MarshalAs(UnmanagedType.I1)] public bool PreserveMixingCache;
        [MarshalAs(UnmanagedType.I1)] public bool SkipCachingSingleFrame;
        [MarshalAs(UnmanagedType.I1)] public bool DisableLinearScaling;
        [MarshalAs(UnmanagedType.I1)] public bool DisableBuiltinScalers;
        [MarshalAs(UnmanagedType.I1)] public bool CorrectSubpixelOffsets;
        [MarshalAs(UnmanagedType.I1)] public bool ForceDither;
        [MarshalAs(UnmanagedType.I1)] public bool DisableDitherGammaCorrection;
        [MarshalAs(UnmanagedType.I1)] public bool DisableFbos;
        [MarshalAs(UnmanagedType.I1)] public bool ForceLowBitDepthFbos;
        [MarshalAs(UnmanagedType.I1)] public bool DynamicConstants;
        public nint InfoCallback;
        public nint InfoPriv;
        [MarshalAs(UnmanagedType.I1)] public bool AllowDelayedPeakDetect;
        public nint IccParams;
        [MarshalAs(UnmanagedType.I1)] public bool IgnoreIccProfiles;
        public int LutEntries;
        public float PolarCutoff;
        [MarshalAs(UnmanagedType.I1)] public bool SkipTargetClearing;
        [MarshalAs(UnmanagedType.I1)] public bool BlendAgainstTiles;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlHook
    {
        public int Stages;
        public int Input;
        public nint Priv;
        public nint Parameters;
        public int NumParameters;
        public nint Reset;
        public nint Hook;
        public ulong Signature;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PlVarData
    {
        [FieldOffset(0)] public int Int;
        [FieldOffset(0)] public uint UInt;
        [FieldOffset(0)] public float Float;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlHookPar
    {
        public nint Name;
        public int Type;
        public int Mode;
        public nint Description;
        public nint Data;
        public PlVarData Initial;
        public PlVarData Minimum;
        public PlVarData Maximum;
        public nint Names;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlRenderErrors
    {
        public int Errors;
        public nint DisabledHooks;
        public int NumDisabledHooks;
    }

    public static PlRenderErrors GetRendererErrors(nint renderer) => pl_renderer_get_errors(renderer);

    public static void ResetRendererErrors(nint renderer) => pl_renderer_reset_errors(renderer, 0);

    public static void FlushRenderer(nint renderer) => pl_renderer_flush_cache(renderer);

    internal static nint ParseShader(nint gpu, byte[] utf8)
    {
        var hook = pl_mpv_user_shader_parse(gpu, utf8, (nuint)utf8.Length);
        if (hook == 0)
            throw new InvalidDataException("libplacebo 无法解析 Shader。");
        return hook;
    }

    internal static void DestroyShader(ref nint hook)
    {
        fixed (nint* pointer = &hook)
            pl_mpv_user_shader_destroy(pointer);
    }

    internal static IReadOnlyList<LibplaceboShaderParameter> ReadParameters(nint hook)
    {
        var definition = Marshal.PtrToStructure<PlHook>(hook);
        var result = new List<LibplaceboShaderParameter>(definition.NumParameters);
        for (var index = 0; index < definition.NumParameters; index++)
        {
            var address = definition.Parameters + index * Marshal.SizeOf<PlHookPar>();
            var parameter = Marshal.PtrToStructure<PlHookPar>(address);
            var name = Marshal.PtrToStringUTF8(parameter.Name) ?? "";
            var description = Marshal.PtrToStringUTF8(parameter.Description) ?? "";
            var names = ReadParameterNames(parameter);
            result.Add(new LibplaceboShaderParameter(name, description, parameter.Type, parameter.Mode,
                ReadValue(parameter.Type, parameter.Initial), ReadValue(parameter.Type, parameter.Minimum),
                ReadValue(parameter.Type, parameter.Maximum), names));
        }
        return result;
    }

    internal static void ApplyParameters(nint hook, string file, IReadOnlyDictionary<string, double> values)
    {
        foreach (var requested in values)
        {
            var definition = Marshal.PtrToStructure<PlHook>(hook);
            var found = false;
            for (var index = 0; index < definition.NumParameters; index++)
            {
                var address = definition.Parameters + index * Marshal.SizeOf<PlHookPar>();
                var parameter = Marshal.PtrToStructure<PlHookPar>(address);
                var name = Marshal.PtrToStringUTF8(parameter.Name) ?? "";
                if (!string.Equals(name, requested.Key, StringComparison.Ordinal))
                    continue;
                if (parameter.Data == 0)
                    throw new InvalidDataException($"Shader {file} 的参数 {name} 不可写。");
                WriteValue(parameter, requested.Value, file, name);
                found = true;
                break;
            }
            if (!found)
                throw new InvalidDataException($"Shader {file} 没有名为 {requested.Key} 的参数。");
        }
    }

    private static IReadOnlyList<string> ReadParameterNames(PlHookPar parameter)
    {
        if (parameter.Names == 0)
            return [];
        var minimum = parameter.Minimum.Int;
        var maximum = parameter.Maximum.Int;
        if (minimum < 0 || maximum < minimum || maximum - minimum >= 256)
            return [];
        var names = new string[maximum - minimum + 1];
        for (var index = 0; index < names.Length; index++)
            names[index] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(parameter.Names, index * nint.Size)) ?? "";
        return names;
    }

    private static double ReadValue(int type, PlVarData value) => type switch
    {
        1 => value.Int,
        2 => value.UInt,
        3 => value.Float,
        _ => 0
    };

    private static void WriteValue(PlHookPar parameter, double value, string file, string name)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException($"Shader {file} 的参数 {name} 不是有限数值。");
        if (parameter.Type is 1 or 2 && value != Math.Truncate(value))
            throw new InvalidDataException($"Shader {file} 的参数 {name} 必须是整数。");
        var minimum = ReadValue(parameter.Type, parameter.Minimum);
        var maximum = ReadValue(parameter.Type, parameter.Maximum);
        if ((parameter.Names != 0 || minimum != 0 || maximum != 0) && (value < minimum || value > maximum))
            throw new InvalidDataException($"Shader {file} 的参数 {name} 必须在 {minimum} 到 {maximum} 之间。");
        switch (parameter.Type)
        {
            case 1:
                Marshal.WriteInt32(parameter.Data, checked((int)value));
                break;
            case 2:
                if (value < 0)
                    throw new InvalidDataException($"Shader {file} 的参数 {name} 不能为负数。");
                Marshal.WriteInt32(parameter.Data, unchecked((int)checked((uint)value)));
                break;
            case 3:
                var single = (float)value;
                if (!float.IsFinite(single))
                    throw new InvalidDataException($"Shader {file} 的参数 {name} 超出浮点范围。");
                Marshal.WriteInt32(parameter.Data, BitConverter.SingleToInt32Bits(single));
                break;
            default:
                throw new InvalidDataException($"Shader {file} 的参数 {name} 类型不受支持。");
        }
    }

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern PlRenderErrors pl_renderer_get_errors(nint renderer);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_renderer_reset_errors(nint renderer, nint errors);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_renderer_flush_cache(nint renderer);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint pl_mpv_user_shader_parse(nint gpu, [In] byte[] shaderText, nuint shaderLength);

    [DllImport("libplacebo-360.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pl_mpv_user_shader_destroy(nint* hook);
}

public sealed record LibplaceboShaderParameter(
    string Name,
    string Description,
    int Type,
    int Mode,
    double Initial,
    double Minimum,
    double Maximum,
    IReadOnlyList<string> Names);

public sealed record LibplaceboLoadedShader(
    string File,
    IReadOnlyList<LibplaceboShaderParameter> Parameters);

public sealed record LibplaceboShaderSource(
    string File,
    string Text,
    IReadOnlyDictionary<string, double> Parameters);

public sealed unsafe class LibplaceboShaderSet : IDisposable
{
    private readonly List<nint> _hooks;
    private nint _array;

    private LibplaceboShaderSet(List<nint> hooks, IReadOnlyList<LibplaceboLoadedShader> shaders)
    {
        _hooks = hooks;
        Shaders = shaders;
        Count = hooks.Count;
        if (hooks.Count > 0)
        {
            _array = Marshal.AllocHGlobal(hooks.Count * nint.Size);
            for (var index = 0; index < hooks.Count; index++)
                Marshal.WriteIntPtr(_array, index * nint.Size, hooks[index]);
        }
    }

    public nint Pointer => _array;
    public int Count { get; }
    public IReadOnlyList<LibplaceboLoadedShader> Shaders { get; }

    public static LibplaceboShaderSet Create(nint gpu, IEnumerable<LibplaceboShaderSource> sources)
    {
        var hooks = new List<nint>();
        var loaded = new List<LibplaceboLoadedShader>();
        try
        {
            foreach (var source in sources)
            {
                var text = System.Text.Encoding.UTF8.GetBytes(source.Text);
                if (text.Length == 0)
                    throw new InvalidDataException($"Shader {source.File} 为空。");
                var hook = LibplaceboNative.ParseShader(gpu, text);
                hooks.Add(hook);
                try
                {
                    LibplaceboNative.ApplyParameters(hook, source.File, source.Parameters);
                    loaded.Add(new LibplaceboLoadedShader(source.File, LibplaceboNative.ReadParameters(hook)));
                }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                {
                    throw new InvalidDataException($"Shader {source.File} 参数无效：{ex.Message}", ex);
                }
            }
            return new LibplaceboShaderSet(hooks, loaded);
        }
        catch
        {
            for (var index = hooks.Count - 1; index >= 0; index--)
            {
                var hook = hooks[index];
                LibplaceboNative.DestroyShader(ref hook);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_array != 0)
        {
            Marshal.FreeHGlobal(_array);
            _array = 0;
        }
        for (var index = _hooks.Count - 1; index >= 0; index--)
        {
            var hook = _hooks[index];
            if (hook != 0)
                LibplaceboNative.DestroyShader(ref hook);
            _hooks[index] = 0;
        }
    }
}
