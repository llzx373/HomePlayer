namespace HomePlayer.Core;

/// <summary>
/// 复用整帧像素缓冲。帧尺寸远大于 <see cref="System.Buffers.ArrayPool{T}.Shared"/>
/// 的缓存上限（1 MB），直接使用共享池会让每一帧都落到大对象堆上。
/// 播放时的帧尺寸是固定不变的，因此这里按字节长度维护一个很小的空闲栈。
/// </summary>
public sealed class FrameBufferPool
{
    private readonly object _gate = new();
    private readonly Stack<byte[]> _free;
    private readonly int _capacity;
    private int _length;
    private long _rented;
    private long _reused;
    private long _allocated;

    public FrameBufferPool(int capacity = 4)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _free = new Stack<byte[]>(capacity);
    }

    public long RentedCount
    {
        get
        {
            lock (_gate)
                return _rented;
        }
    }

    public long ReusedCount
    {
        get
        {
            lock (_gate)
                return _reused;
        }
    }

    public long AllocatedCount
    {
        get
        {
            lock (_gate)
                return _allocated;
        }
    }

    public int FreeCount
    {
        get
        {
            lock (_gate)
                return _free.Count;
        }
    }

    public byte[] Rent(int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        lock (_gate)
        {
            if (_length != length)
            {
                _length = length;
                _free.Clear();
            }

            while (_free.Count > 0)
            {
                var buffer = _free.Pop();
                if (buffer.Length >= length)
                {
                    _rented++;
                    _reused++;
                    return buffer;
                }
            }

            _rented++;
            _allocated++;
            return GC.AllocateUninitializedArray<byte>(length);
        }
    }

    public void Return(byte[]? buffer)
    {
        if (buffer == null)
            return;

        lock (_gate)
        {
            if (buffer.Length != _length || _free.Count >= _capacity)
                return;
            _free.Push(buffer);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _free.Clear();
            _length = 0;
        }
    }
}
