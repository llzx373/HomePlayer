using System.Runtime.InteropServices;

namespace HomePlayer;

internal static class FolderDialog
{
    public static Task<string?> PickAsync()
    {
        // The WinUI thread cannot host this modal loop; doing so removes the content island.
        var source = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                source.SetResult(PickCore());
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return source.Task;
    }

    private static string? PickCore()
    {
        var owner = CreateWindowEx(0, "Static", "", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        try
        {
            var dialog = (IFileDialog)new FileOpenDialog();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | 0x8 | 0x20 | 0x40 | 0x800);
            dialog.SetTitle("选择媒体库文件夹");
            var result = dialog.Show(owner);
            if (result == unchecked((int)0x800704C7))
                return null;
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            item.GetDisplayName(0x80058000, out var path);
            return path;
        }
        finally
        {
            if (owner != IntPtr.Zero)
                DestroyWindow(owner);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IntPtr item);
        void SetFolder(IntPtr item);
        void GetFolder(out IntPtr item);
        void GetCurrentSelection(out IntPtr item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IntPtr item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindCtx, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint nameType, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
