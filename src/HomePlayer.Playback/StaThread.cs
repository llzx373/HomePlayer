using System.Collections.Concurrent;

namespace HomePlayer.Playback;

internal sealed class StaThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private volatile bool _stop;

    public StaThread(string name)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public void Invoke(Action action)
    {
        if (!TryInvoke(action, Timeout.Infinite))
            throw new ObjectDisposedException(nameof(StaThread));
    }

    public bool TryInvoke(Action action, int timeoutMs)
    {
        if (_stop)
            return false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    action();
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmApp, 0, 0);
        if (!done.Task.Wait(timeoutMs))
            return false;
        done.Task.GetAwaiter().GetResult();
        return true;
    }

    public T Invoke<T>(Func<T> action)
    {
        T result = default!;
        Invoke(() => { result = action(); });
        return result;
    }

    public void Dispose()
    {
        if (_stop)
            return;
        _stop = true;
        _queue.CompleteAdding();
        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmQuit, 0, 0);
        _thread.Join(2000);
        _ready.Dispose();
    }

    private void Loop()
    {
        NativeMethods.CoInitializeEx(0, NativeMethods.CoinitApartmentThreaded);
        _threadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessageW(out _, 0, 0, 0, 0);
        _ready.Set();
        while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == NativeMethods.WmApp)
            {
                while (_queue.TryTake(out var work))
                    work();
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        while (_queue.TryTake(out var leftover))
        {
            try
            {
                leftover();
            }
            catch
            {
            }
        }
    }
}
