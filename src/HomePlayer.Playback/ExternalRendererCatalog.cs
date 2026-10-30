using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HomePlayer.Playback;

public enum ExternalRendererKind
{
    MadVR,
    MpcVR
}

public enum ExternalRendererStatus
{
    NotInstalled,
    FilesOnly,
    Unavailable,
    Available
}

public sealed record ExternalRendererInfo(
    ExternalRendererKind Kind,
    string DisplayName,
    Guid Clsid,
    ExternalRendererStatus Status,
    string? InstallPath,
    string? RegisteredPath,
    string? Version,
    string ReasonCode,
    string Reason)
{
    public bool Available => Status == ExternalRendererStatus.Available;
}

public static class ExternalRendererCatalog
{
    public static readonly Guid MadVRClsid = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");
    public static readonly Guid MpcVRClsid = new("71F080AA-8661-4093-B15E-4F6903E77D0A");

    public static ExternalRendererInfo Detect(ExternalRendererKind kind) =>
        Detect(kind, new ExternalRendererPlatform());

    internal static ExternalRendererInfo Detect(ExternalRendererKind kind, IExternalRendererPlatform platform)
    {
        var clsid = kind == ExternalRendererKind.MadVR ? MadVRClsid : MpcVRClsid;
        var name = kind == ExternalRendererKind.MadVR ? "madVR" : "MPC Video Renderer";
        string? registeredPath = null;
        try
        {
            registeredPath = platform.RegisteredPath(clsid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        var path = new[] { registeredPath }.Concat(platform.CandidatePaths(kind))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && platform.FileExists(candidate));
        var version = path == null ? null : platform.Version(path);
        if (path == null)
        {
            return new ExternalRendererInfo(kind, name, clsid, ExternalRendererStatus.NotInstalled,
                null, registeredPath, null, "not-installed", "未检测到用户安装。");
        }
        if (string.IsNullOrWhiteSpace(registeredPath))
        {
            return new ExternalRendererInfo(kind, name, clsid, ExternalRendererStatus.FilesOnly,
                path, null, version, "not-registered", "检测到安装文件，但 x64 DirectShow 滤镜未注册；不会自动修改系统注册。");
        }
        if (!string.Equals(path, registeredPath, StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalRendererInfo(kind, name, clsid, ExternalRendererStatus.Unavailable,
                path, registeredPath, version, "registration-missing-file", "COM 注册路径不存在或不可读。");
        }
        object? instance = null;
        try
        {
            instance = platform.CreateInstance(clsid);
            return new ExternalRendererInfo(kind, name, clsid, ExternalRendererStatus.Available,
                path, registeredPath, version, "available", "x64 COM 滤镜可创建。");
        }
        catch (Exception ex)
        {
            return new ExternalRendererInfo(kind, name, clsid, ExternalRendererStatus.Unavailable,
                path, registeredPath, version, "activation-failed", $"x64 COM 滤镜创建失败：{ex.Message}");
        }
        finally
        {
            if (instance != null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }
}

internal interface IExternalRendererPlatform
{
    string? RegisteredPath(Guid clsid);
    IEnumerable<string> CandidatePaths(ExternalRendererKind kind);
    bool FileExists(string path);
    string? Version(string path);
    object CreateInstance(Guid clsid);
}

internal sealed class ExternalRendererPlatform : IExternalRendererPlatform
{
    public string? RegisteredPath(Guid clsid)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
        var path = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
    }

    public IEnumerable<string> CandidatePaths(ExternalRendererKind kind)
    {
        if (kind != ExternalRendererKind.MadVR)
            yield break;
        var configured = Environment.GetEnvironmentVariable("HOMEPLAYER_MADVR_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return Path.Combine(configured, "madVR64.ax");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "madVR09217", "madVR64.ax");
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? Version(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public object CreateInstance(Guid clsid) =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!)
            ?? throw new InvalidOperationException("COM 激活返回空对象。");
}
