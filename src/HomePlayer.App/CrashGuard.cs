using System.Runtime.InteropServices;
using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;

namespace HomePlayer;

internal static class CrashGuard
{
    private static UnhandledExceptionFilter? _filter;
    private static IntPtr _previousFilter;

    public static void Attach(Application app)
    {
        AppPaths.Ensure();
        app.UnhandledException += OnXamlException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
        InstallNativeFilter();
    }

    public static void StartSession()
    {
        AppPaths.Ensure();
        CrashLog.ImportLegacy();
        CrashLog.ConsumeAbandonedSession();
        CrashLog.BeginSession();
    }

    public static void StopSession() => CrashLog.EndSession();

    private static void OnXamlException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var details = e.Exception?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(e.Message) && !details.Contains(e.Message, StringComparison.Ordinal))
            details = string.IsNullOrWhiteSpace(details) ? e.Message : e.Message + Environment.NewLine + details;
        CrashLog.Write(CrashLog.UnhandledKind, details);
        e.Handled = true;
    }

    private static void OnDomainException(object sender, System.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write(CrashLog.DomainKind, e.ExceptionObject?.ToString() ?? "未知运行时异常");
        if (e.IsTerminating)
            CrashLog.EndSession();
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write(CrashLog.TaskKind, e.Exception?.ToString() ?? "未知后台任务异常");
        e.SetObserved();
    }

    private static void InstallNativeFilter()
    {
        try
        {
            _filter = OnNativeException;
            _previousFilter = SetUnhandledExceptionFilter(_filter);
        }
        catch
        {
        }
    }

    private static int OnNativeException(IntPtr exceptionPointers)
    {
        try
        {
            var code = ReadExceptionCode(exceptionPointers);
            if (code == ClrException)
                return ContinueSearch(exceptionPointers);

            var log = CrashLog.Write(CrashLog.NativeKind, NativeDetails(code, exceptionPointers));
            if (!string.IsNullOrWhiteSpace(log) && code != StackOverflow)
                WriteDump(Path.ChangeExtension(log, ".dmp"), exceptionPointers);
            CrashLog.EndSession();
        }
        catch
        {
        }

        return ContinueSearch(exceptionPointers);
    }

    private static int ContinueSearch(IntPtr exceptionPointers)
    {
        if (_previousFilter == IntPtr.Zero)
            return ExceptionContinueSearch;
        try
        {
            var previous = Marshal.GetDelegateForFunctionPointer<UnhandledExceptionFilter>(_previousFilter);
            return previous(exceptionPointers);
        }
        catch
        {
            return ExceptionContinueSearch;
        }
    }

    private static uint ReadExceptionCode(IntPtr exceptionPointers)
    {
        if (exceptionPointers == IntPtr.Zero)
            return 0;
        var pointers = Marshal.PtrToStructure<ExceptionPointers>(exceptionPointers);
        if (pointers.ExceptionRecord == IntPtr.Zero)
            return 0;
        return Marshal.PtrToStructure<ExceptionRecord>(pointers.ExceptionRecord).ExceptionCode;
    }

    private static string NativeDetails(uint code, IntPtr exceptionPointers)
    {
        var address = IntPtr.Zero;
        if (exceptionPointers != IntPtr.Zero)
        {
            var pointers = Marshal.PtrToStructure<ExceptionPointers>(exceptionPointers);
            if (pointers.ExceptionRecord != IntPtr.Zero)
                address = Marshal.PtrToStructure<ExceptionRecord>(pointers.ExceptionRecord).ExceptionAddress;
        }

        return string.Join(Environment.NewLine, [
            "ExceptionCode: 0x" + code.ToString("X8"),
            "ExceptionName: " + NativeName(code),
            "ExceptionAddress: 0x" + address.ToString("X"),
            "Thread: " + GetCurrentThreadId()
        ]);
    }

    private static string NativeName(uint code) => code switch
    {
        0xC0000005 => "ACCESS_VIOLATION",
        0xC00000FD => "STACK_OVERFLOW",
        0xC000001D => "ILLEGAL_INSTRUCTION",
        0xC0000094 => "INTEGER_DIVIDE_BY_ZERO",
        0xC0000409 => "STACK_BUFFER_OVERRUN",
        0x80000003 => "BREAKPOINT",
        0xC0000374 => "HEAP_CORRUPTION",
        _ => "UNKNOWN"
    };

    private static void WriteDump(string path, IntPtr exceptionPointers)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var info = new MiniDumpExceptionInformation
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = 0
            };
            MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, stream.SafeFileHandle, MiniDumpSmall, ref info, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    private const uint ClrException = 0xE0434352;
    private const uint StackOverflow = 0xC00000FD;
    private const int ExceptionContinueSearch = 0;
    private const int MiniDumpSmall = 0x00000020 | 0x00001000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnhandledExceptionFilter(IntPtr exceptionPointers);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public IntPtr ExceptionRecord;
        public IntPtr ContextRecord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecordPtr;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MiniDumpExceptionInformation
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        public int ClientPointers;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(UnhandledExceptionFilter filter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr process,
        uint processId,
        SafeFileHandle file,
        int dumpType,
        ref MiniDumpExceptionInformation exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
