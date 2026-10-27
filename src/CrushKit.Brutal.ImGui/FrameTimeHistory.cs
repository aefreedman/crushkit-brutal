namespace CrushKit.Brutal.ImGui;

/// <summary>Bounded owner-thread elapsed-frame observations. Collection is explicit and independent of UI frames.
/// Saturation overwrites the oldest sample. No native resources or per-observation allocations.</summary>
public sealed class FrameTimeHistory
{
    private readonly float[] _values, _plot;
    private int _next, _count, _plotCount, _plotLimit;
    private long _lastObservation = -1;
    private ulong _revision, _statisticsRevision, _plotRevision;
    private FrameTimeStatistics _statistics;

    /// <summary>Capacity is explicit; the implementation ceiling is 65,536 samples.</summary>
    public FrameTimeHistory(int capacity)
    {
        if (capacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new float[capacity]; _plot = new float[capacity];
    }

    public int Capacity => _values.Length;
    public int Count => _count;

    /// <summary>Adds one finite nonnegative float-representable duration. IDs must be nonnegative and increase
    /// within a collection epoch; duplicate/older IDs are ignored, so retrying UI/presentation cannot duplicate a sample.
    /// The host owns ID assignment and whether a measurement means elapsed, submitted or presented frame time.</summary>
    public bool Observe(long observationId, double milliseconds)
    {
        if (observationId < 0) throw new ArgumentOutOfRangeException(nameof(observationId));
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (observationId <= _lastObservation) return false;
        _values[_next] = (float)milliseconds;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        _lastObservation = observationId; _revision++;
        return true;
    }

    /// <summary>Starts a new ID epoch and empties the history; does not resize or allocate.</summary>
    public void Clear()
    {
        _next = _count = 0; _lastObservation = -1; _revision++;
    }

    /// <summary>Cached min/mean/max over valid samples only, recomputed at most once per revision.</summary>
    public FrameTimeStatistics Statistics
    {
        get
        {
            if (_statisticsRevision != _revision)
            {
                double sum = 0; float min = float.MaxValue, max = 0;
                for (int i = 0; i < _count; i++) { float value = _values[i]; sum += value; min = Math.Min(min, value); max = Math.Max(max, value); }
                _statistics = _count == 0 ? default : new(_count, min, sum / _count, max);
                _statisticsRevision = _revision;
            }
            return _statistics;
        }
    }

    /// <summary>Copies a chronological min/max envelope into caller storage. Each bucket emits its extrema
    /// in their original order, preserving spikes. The destination must have at least two entries when two samples exist.
    /// Returns the number written; borrowed/private ring storage never escapes.</summary>
    public int CopyEnvelope(Span<float> destination)
    {
        if (_count == 0) return 0;
        if (destination.Length < Math.Min(_count, 2)) throw new ArgumentException("Insufficient envelope storage.", nameof(destination));
        if (_count <= destination.Length)
        {
            for (int i = 0; i < _count; i++) destination[i] = Chronological(i);
            return _count;
        }
        int buckets = destination.Length / 2, written = 0;
        for (int bucket = 0; bucket < buckets; bucket++)
        {
            int begin = bucket * _count / buckets, end = (bucket + 1) * _count / buckets;
            float min = float.MaxValue, max = -1; int minAt = begin, maxAt = begin;
            for (int i = begin; i < end; i++)
            {
                float value = Chronological(i);
                if (value < min) { min = value; minAt = i; }
                if (value > max) { max = value; maxAt = i; }
            }
            destination[written++] = minAt <= maxAt ? min : max;
            destination[written++] = minAt <= maxAt ? max : min;
        }
        return written;
    }

    private float Chronological(int index) => _values[(_next - _count + Capacity + index) % Capacity];

    // Inspector-owned scratch, never published. Width/revision changes invalidate it, not every UI retry.
    internal ReadOnlySpan<float> PlotValues(int maxSamples)
    {
        int limit = Math.Clamp(maxSamples, Math.Min(_count, 2), Capacity);
        if (_plotRevision != _revision || limit != _plotLimit)
        {
            _plotCount = CopyEnvelope(_plot.AsSpan(0, limit));
            _plotLimit = limit; _plotRevision = _revision;
        }
        return _plot.AsSpan(0, _plotCount);
    }
}

public readonly record struct FrameTimeStatistics(int Count, double MinimumMilliseconds, double MeanMilliseconds, double MaximumMilliseconds);
