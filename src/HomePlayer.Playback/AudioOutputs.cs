using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace HomePlayer.Playback;

public sealed record AudioOutput(string Id, string Name, bool SystemDefault);

public static class AudioOutputs
{
    public const string SystemDefaultId = "";

    public static IReadOnlyList<AudioOutput> List(string? savedId = null)
    {
        var items = new List<AudioOutput> { new(SystemDefaultId, "系统默认", true) };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? currentDefault = null;
            try
            {
                using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                currentDefault = endpoint.ID;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var name = device.FriendlyName;
                    if (!string.IsNullOrWhiteSpace(currentDefault) && device.ID == currentDefault)
                        name += "（当前系统默认）";
                    items.Add(new(device.ID, name, false));
                }
            }
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrWhiteSpace(savedId) && items.All(item => item.Id != savedId))
            items.Insert(1, new(savedId, "已保存的设备当前不可用", false));
        return items;
    }

    public static string Describe(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "系统默认";
        var match = List(id).FirstOrDefault(item => item.Id == id);
        return string.IsNullOrWhiteSpace(match?.Name) ? "系统默认" : match.Name;
    }

    public static bool IsAvailable(string? id) =>
        string.IsNullOrWhiteSpace(id) || List().Any(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public static MMDevice? OpenWasapi(string? id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (string.IsNullOrWhiteSpace(id))
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            try
            {
                return enumerator.GetDevice(id);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static object? CreateRenderer(string? id, out string name)
    {
        name = Describe(id);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var wanted = NameWithoutSuffix(name);
        object? fallback = null;
        string? fallbackName = null;
        foreach (var candidate in EnumerateRenderers())
        {
            if (IsDefaultRenderer(candidate.Name))
            {
                Release(candidate.Filter);
                continue;
            }

            if (NamesMatch(candidate.Name, wanted, strict: true))
            {
                Release(fallback);
                name = candidate.Name;
                return candidate.Filter;
            }

            if (fallback == null && NamesMatch(candidate.Name, wanted, strict: false))
            {
                fallback = candidate.Filter;
                fallbackName = candidate.Name;
                continue;
            }

            Release(candidate.Filter);
        }

        if (fallback != null)
        {
            name = fallbackName ?? name;
            return fallback;
        }

        return null;
    }

    private static IEnumerable<(object Filter, string Name)> EnumerateRenderers()
    {
        var type = Type.GetTypeFromCLSID(DsGuid.SystemDeviceEnum, throwOnError: false);
        if (type == null)
            yield break;

        object? enumeratorObject = null;
        IEnumMoniker? enumerator = null;
        var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        var fetched = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is not ICreateDevEnum create)
                yield break;
            var category = DsGuid.AudioRendererCategory;
            if (create.CreateClassEnumerator(in category, out enumerator, 0) != Hr.Ok || enumerator == null)
                yield break;

            while (enumerator.Next(1, slot, fetched) == Hr.Ok && Marshal.ReadInt32(fetched) == 1)
            {
                var pointer = Marshal.ReadIntPtr(slot);
                var unknown = Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                if (unknown is not IMonikerDs moniker)
                {
                    Release(unknown);
                    continue;
                }

                var bound = BindRenderer(moniker);
                Release(moniker);
                if (bound != null)
                    yield return bound.Value;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            Marshal.FreeCoTaskMem(fetched);
            if (enumerator != null)
                Release(enumerator);
            if (enumeratorObject != null)
                Release(enumeratorObject);
        }
    }

    private static (object Filter, string Name)? BindRenderer(IMonikerDs moniker)
    {
        var bagId = typeof(IPropertyBag).GUID;
        if (moniker.BindToStorage(0, 0, in bagId, out var bagObject) < 0)
            return null;
        var name = "音频渲染器";
        try
        {
            if (bagObject is IPropertyBag bag)
            {
                object value = "";
                if (bag.Read("FriendlyName", ref value, 0) >= 0 && value is string text && !string.IsNullOrWhiteSpace(text))
                    name = text;
            }
        }
        finally
        {
            Release(bagObject);
        }

        var filterId = typeof(IBaseFilter).GUID;
        if (moniker.BindToObject(0, 0, in filterId, out var filter) < 0 || filter == null)
            return null;
        return (filter, name);
    }

    private static bool IsDefaultRenderer(string name)
        => name.Contains("Default DirectSound", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Default WaveOut", StringComparison.OrdinalIgnoreCase);

    private static bool NamesMatch(string renderer, string device, bool strict)
    {
        var left = Normalize(renderer);
        var right = Normalize(device);
        if (string.IsNullOrWhiteSpace(right))
            return false;
        if (left == right || left == "directsound " + right)
            return true;
        return !strict && left.Contains(right, StringComparison.Ordinal);
    }

    private static string NameWithoutSuffix(string name)
    {
        var cut = name.IndexOf('（');
        return cut > 0 ? name[..cut].Trim() : name;
    }

    private static string Normalize(string name)
        => name.Replace("DirectSound:", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();

    private static void Release(object? com)
    {
        if (com == null)
            return;
        try
        {
            Marshal.ReleaseComObject(com);
        }
        catch
        {
        }
    }
}
