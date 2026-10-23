using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace HomePlayer.Core;

/// <summary>
/// 一个持续消费委托的后台线程。用于承载必须串行执行的图形工作，
/// 避免每帧的 GPU 上传和呈现占用 UI 线程。
/// </summary>
public sealed class RenderThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private int _stopped;

    public RenderThread(string name = "HomePlayer render")
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name
        };
        _thread.Start();
        _ready.Wait();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _stopped) != 0)
            return;
        try
        {
            _queue.Add(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        _thread.Join(2000);
        _ready.Dispose();
        try
        {
            _queue.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Loop()
    {
        // 渲染线程会调用 D3DCompile、DXGI 等基于 COM 的接口。
        // 没有初始化 COM 的线程上这些调用会失败（D3DCompile 返回空 blob），
        // 表现为交换链正常但没有任何着色器资源，画面一片空白。
        var comInitialized = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded) >= 0;
        _ready.Set();
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (comInitialized)
                CoUninitialize();
        }
    }

    private const uint CoinitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
