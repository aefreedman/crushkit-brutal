using System.Text;

namespace CrushKit.Brutal.ImGui;

/// <summary>Presentation severity only. Persistent errors, conflicts and pending work remain application state.</summary>
public enum ToolFeedbackSeverity { Info, Success, Warning, Error }

/// <summary>An immutable, independently retainable event publication. Native text encodings are privately owned.</summary>
public sealed class ToolFeedbackNotice
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _id, _heading, _detail;

    public string Id { get; }
    public ToolFeedbackSeverity Severity { get; }
    public string Title { get; }
    public string Detail { get; }
    public double CreatedAtSeconds { get; }
    public double ExpiresAtSeconds { get; }
    internal ReadOnlySpan<byte> IdUtf8 => _id;
    internal ReadOnlySpan<byte> HeadingUtf8 => _heading;
    internal ReadOnlySpan<byte> DetailUtf8 => _detail;

    internal ToolFeedbackNotice(string id, ToolFeedbackSeverity severity, string title, string detail,
        double createdAtSeconds, double expiresAtSeconds)
    {
        Id = id; Severity = severity; Title = title; Detail = detail;
        CreatedAtSeconds = createdAtSeconds; ExpiresAtSeconds = expiresAtSeconds;
        _id = Utf8.GetBytes(id);
        _heading = Utf8.GetBytes($"{severity}: {title}");
        _detail = Utf8.GetBytes(detail);
    }
}

/// <summary>Small owner-thread transient feedback, ordered oldest to newest. Explicit host time drives expiry;
/// drawing never posts, expires or consumes events. Capacity saturation evicts the oldest notice regardless of severity.</summary>
public sealed class ToolFeedbackBuffer
{
    public const int MaximumCapacity = 1024;
    public const int MinimumTextCharacters = 2; // At least a one-character ID and title.
    public const int MaximumTextCharacters = 65536;
    private readonly ToolFeedbackNotice?[] _items;
    private int _count;
    private double _lastTimeSeconds;

    /// <summary>Both limits are explicit. The text limit bounds the combined ID, title and detail UTF-16 length
    /// of each event before UTF-8 allocation. These ceilings are implementation bounds, not application defaults.</summary>
    public ToolFeedbackBuffer(int capacity, int maxTextCharacters)
    {
        if (capacity is < 1 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxTextCharacters is < MinimumTextCharacters or > MaximumTextCharacters) throw new ArgumentOutOfRangeException(nameof(maxTextCharacters));
        _items = new ToolFeedbackNotice[capacity]; MaxTextCharacters = maxTextCharacters;
    }

    public int Capacity => _items.Length;
    public int MaxTextCharacters { get; }
    public int Count => _count;

    public ToolFeedbackNotice this[int index] => (uint)index < (uint)_count
        ? _items[index]! : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Posts one owner-update event. Same-ID replacement moves to newest and restarts expiry.
    /// Expired notices are removed before coalescing/eviction. Previously returned notices remain unchanged.</summary>
    /// <remarks>nowSeconds must be finite, nonnegative and nondecreasing across Post/Advance calls.
    /// durationSeconds must produce a finite deadline strictly after nowSeconds. Reposting is an explicit
    /// event, not something to do each UI frame merely because a status string is still present.</remarks>
    public ToolFeedbackNotice Post(string id, ToolFeedbackSeverity severity, string title, string detail,
        double nowSeconds, double durationSeconds)
    {
        ValidateTime(nowSeconds);
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(detail);
        if (title.Contains('\0') || detail.Contains('\0')) throw new ArgumentException("Feedback text must not contain NUL.");
        if ((long)id.Length + title.Length + detail.Length > MaxTextCharacters)
            throw new ArgumentException("Feedback text exceeds the explicit per-notice limit.");
        if (severity is < ToolFeedbackSeverity.Info or > ToolFeedbackSeverity.Error)
            throw new ArgumentOutOfRangeException(nameof(severity));
        double expiresAt = nowSeconds + durationSeconds;
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || !double.IsFinite(expiresAt) || expiresAt <= nowSeconds)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        // Finish validation/owned encoding before changing clock or storage, including malformed UTF-16 failures.
        var notice = new ToolFeedbackNotice(id, severity, title, detail, nowSeconds, expiresAt);
        Advance(nowSeconds);
        int existing = Find(id);
        if (existing >= 0) RemoveAt(existing);
        if (_count == Capacity) RemoveAt(0);
        _items[_count++] = notice;
        return notice;
    }

    /// <summary>Expires notices at or beyond their deadlines, preserving survivor order. Returns removals.
    /// Call from the owner update even when no inspector is drawn; no hidden clock, allocations or native work.</summary>
    public int Advance(double nowSeconds)
    {
        ValidateTime(nowSeconds);
        _lastTimeSeconds = nowSeconds;
        int written = 0, previousCount = _count;
        for (int i = 0; i < previousCount; i++)
            if (_items[i]!.ExpiresAtSeconds > nowSeconds) _items[written++] = _items[i];
        Array.Clear(_items, written, previousCount - written);
        _count = written;
        return previousCount - written;
    }

    public bool Dismiss(string id)
    {
        ValidateId(id);
        int index = Find(id);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    /// <summary>Removes notices without resetting the monotonic clock epoch. Recreate the buffer for a new clock.</summary>
    public void Clear()
    {
        Array.Clear(_items, 0, _count);
        _count = 0;
    }

    private int Find(string id)
    {
        for (int i = 0; i < _count; i++) if (string.Equals(_items[i]!.Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    private void RemoveAt(int index)
    {
        int remaining = _count - index - 1;
        if (remaining > 0) Array.Copy(_items, index + 1, _items, index, remaining);
        _items[--_count] = null;
    }

    private void ValidateTime(double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds) || nowSeconds < _lastTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Expected finite nonnegative nondecreasing host time.");
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Contains('\0') || id.Contains("##", StringComparison.Ordinal))
            throw new ArgumentException("Feedback IDs must not contain native label delimiters or NUL.", nameof(id));
    }
}
