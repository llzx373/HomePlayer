namespace HomePlayer.Ffmpeg;

internal sealed class BoundedPipelineQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private readonly int _capacity;
    private readonly long _byteBudget;
    private long _bytes;
    private bool _completed;

    public BoundedPipelineQueue(int capacity, long byteBudget)
    {
        _capacity = Math.Max(1, capacity);
        _byteBudget = Math.Max(1, byteBudget);
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public long Bytes
    {
        get
        {
            lock (_gate)
                return _bytes;
        }
    }

    public void Write(T item, long bytes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (!_completed && (_items.Count >= _capacity || (_items.Count > 0 && _bytes + bytes > _byteBudget)))
            {
                Monitor.Wait(_gate, 50);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_completed)
                throw new OperationCanceledException(cancellationToken);
            _items.Enqueue(item);
            _bytes += bytes;
            Monitor.PulseAll(_gate);
        }
    }

    public bool Read(out T item, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (_items.Count == 0)
            {
                if (_completed)
                {
                    item = default!;
                    return false;
                }
                Monitor.Wait(_gate, 50);
                cancellationToken.ThrowIfCancellationRequested();
            }
            item = _items.Dequeue();
            return true;
        }
    }

    public void Release(long bytes)
    {
        lock (_gate)
        {
            _bytes = Math.Max(0, _bytes - bytes);
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Clear(Action<T> release)
    {
        T[] items;
        lock (_gate)
        {
            items = _items.ToArray();
            _items.Clear();
            _bytes = 0;
            Monitor.PulseAll(_gate);
        }
        foreach (var item in items)
            release(item);
    }
}
