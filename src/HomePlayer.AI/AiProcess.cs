using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomePlayer.AI;

internal static class AiProcess
{
    private static readonly IntPtr ProcessJob = CreateProcessJob();

    public static void Track(Process process)
    {
        if (ProcessJob != IntPtr.Zero && OperatingSystem.IsWindows())
            AssignProcessToJobObject(ProcessJob, process.Handle);
    }

    public static ProcessStartInfo StartInfo(string executable, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    public static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string>? outputLine,
        CancellationToken cancellationToken)
    {
        var info = StartInfo(executable, workingDirectory);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var errors = new BoundedText(24_000);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
                outputLine?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                errors.Append(args.Data);
                outputLine?.Invoke(args.Data);
            }
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动本地 AI 进程。");
            Track(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"本地 AI 进程异常退出（{process.ExitCode}）。{errors.Text}");
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static IntPtr CreateProcessJob()
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation
        {
            Basic = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 }
        };
        var size = Marshal.SizeOf(info);
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            return SetInformationJobObject(job, 9, pointer, (uint)size) ? job : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class BoundedText(int capacity)
    {
        private readonly StringBuilder _builder = new();
        public string Text => _builder.ToString();

        public void Append(string line)
        {
            _builder.AppendLine(line);
            if (_builder.Length > capacity)
                _builder.Remove(0, _builder.Length - capacity);
        }
    }
}
