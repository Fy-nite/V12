using System.Buffers;

namespace V12.Rendering;

/// <summary>
/// A bounded pool of frame leases. Renting a builder reserves one slot; the slot
/// is returned only when the accepted lease is released by the renderer. When the
/// pool is exhausted <see cref="TryRent"/> returns false, giving the engine
/// back-pressure instead of unbounded allocation.
/// </summary>
public sealed class RenderFramePool
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private int _inUse;
    private ulong _sequence;

    public RenderFramePool(int capacity = 4)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");
        _capacity = capacity;
    }

    /// <summary>
    /// Epoch stamped onto rented frames. Update this when the renderer reports a
    /// new <see cref="DeviceEpoch"/>; frames rented before the change keep their old epoch.
    /// </summary>
    public DeviceEpoch Epoch { get; set; } = DeviceEpoch.Initial;

    public int Capacity => _capacity;

    public int InUse
    {
        get { lock (_lock) return _inUse; }
    }

    public bool TryRent(out RenderFrameBuilder builder)
    {
        lock (_lock)
        {
            if (_inUse >= _capacity)
            {
                builder = null!;
                return false;
            }

            _inUse++;
            builder = new RenderFrameBuilder(this, ++_sequence, Epoch);
            return true;
        }
    }

    public RenderFrameBuilder Rent()
    {
        if (!TryRent(out var builder))
            throw new InvalidOperationException($"Render frame pool exhausted (capacity {_capacity}).");
        return builder;
    }

    internal void Return(RenderFrameLease lease)
    {
        lock (_lock)
        {
            if (_inUse > 0) _inUse--;
        }
    }
}

/// <summary>Engine-side write side of a frame. Seal it before submitting.</summary>
public sealed class RenderFrameBuilder
{
    private readonly RenderFramePool _pool;
    private readonly ulong _sequence;
    private readonly DeviceEpoch _epoch;
    private bool _sealed;

    internal RenderFrameBuilder(RenderFramePool pool, ulong sequence, DeviceEpoch epoch)
    {
        _pool = pool;
        _sequence = sequence;
        _epoch = epoch;
    }

    public List<RenderView> Views { get; } = new();
    public List<DrawItem> Items { get; } = new();
    public List<DrawOverrides> DrawOverrides { get; } = new();
    public List<LightItem> Lights { get; } = new();
    public EnvironmentData? Environment { get; set; } = EnvironmentData.Default;
    public float Time { get; set; }
    public float DeltaTime { get; set; }

    /// <summary>Seals the frame and transfers it into a lease. One-shot.</summary>
    public RenderFrameLease Seal()
    {
        if (_sealed) throw new InvalidOperationException("RenderFrameBuilder has already been sealed.");
        _sealed = true;

        var views = RentCopy(Views);
        var items = RentCopy(Items);
        var overrides = RentCopy(DrawOverrides);
        var lights = RentCopy(Lights);

        var frame = new RenderFrame
        {
            Views = views.Memory,
            Items = items.Memory,
            DrawOverrides = overrides.Memory,
            Lights = lights.Memory,
            Environment = Environment ?? EnvironmentData.Default,
            Time = Time,
            DeltaTime = DeltaTime,
        };

        return new RenderFrameLease(_pool, _sequence, _epoch, frame, views, items, overrides, lights);
    }

    private static RentedBuffer<T> RentCopy<T>(List<T> source)
    {
        var array = ArrayPool<T>.Shared.Rent(source.Count);
        if (source.Count > 0) source.CopyTo(array);
        return new RentedBuffer<T>(array, source.Count);
    }
}

/// <summary>
/// A completed frame leased to the renderer. It owns pooled arrays; the renderer
/// must call <see cref="Release"/> exactly once when it no longer reads the frame.
/// </summary>
public sealed class RenderFrameLease : IDisposable
{
    private readonly RenderFramePool _pool;
    private readonly RentedBuffer<RenderView> _views;
    private readonly RentedBuffer<DrawItem> _items;
    private readonly RentedBuffer<DrawOverrides> _overrides;
    private readonly RentedBuffer<LightItem> _lights;
    private readonly RenderFrame _frame;
    private int _released;

    internal RenderFrameLease(
        RenderFramePool pool,
        ulong sequence,
        DeviceEpoch epoch,
        RenderFrame frame,
        RentedBuffer<RenderView> views,
        RentedBuffer<DrawItem> items,
        RentedBuffer<DrawOverrides> overrides,
        RentedBuffer<LightItem> lights)
    {
        _pool = pool;
        _views = views;
        _items = items;
        _overrides = overrides;
        _lights = lights;
        _frame = frame;
        Sequence = sequence;
        Epoch = epoch;
    }

    public ulong Sequence { get; }
    public DeviceEpoch Epoch { get; }

    public RenderFrame Frame
    {
        get
        {
            if (Volatile.Read(ref _released) != 0)
                throw new ObjectDisposedException(nameof(RenderFrameLease));
            return _frame;
        }
    }

    /// <summary>Returns the frame's arrays to the pool. Exactly once, by the renderer.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            throw new InvalidOperationException("RenderFrameLease released twice.");

        _views.Return();
        _items.Return();
        _overrides.Return();
        _lights.Return();
        _pool.Return(this);
    }

    public void Dispose() => Release();
}

internal sealed class RentedBuffer<T>
{
    private readonly T[] _array;
    private int _returned;

    public RentedBuffer(T[] array, int length)
    {
        _array = array;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<T> Memory => new(_array, 0, Length);

    public void Return()
    {
        if (Interlocked.Exchange(ref _returned, 1) != 0) return;
        ArrayPool<T>.Shared.Return(_array, clearArray: true);
    }
}
