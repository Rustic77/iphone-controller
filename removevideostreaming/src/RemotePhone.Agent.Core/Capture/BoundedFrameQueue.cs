namespace RemotePhone.Agent.Core.Capture;

/// <summary>
/// Thread-safe bounded queue that drops the oldest item when capacity is exceeded.
/// </summary>
public sealed class BoundedFrameQueue<T> where T : class
{
    private readonly LinkedList<T> _items = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    public BoundedFrameQueue(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public long DroppedCount { get; private set; }

    public void Enqueue(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_gate)
        {
            while (_items.Count >= _capacity)
            {
                _items.RemoveFirst();
                DroppedCount++;
            }

            _items.AddLast(item);
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = null;
                return false;
            }

            item = _items.First!.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
