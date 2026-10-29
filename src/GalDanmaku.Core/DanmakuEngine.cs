using System.Collections.ObjectModel;

namespace GalDanmaku.Core;

public sealed class DanmakuEngine
{
    public const int MaximumComments = 64;
    // Frame deltas above this (window stalls, system sleep, debugger pauses) are treated as a pause, not as elapsed story time.
    public const double MaximumFrameSeconds = 0.5;
    private const double EdgePadding = 8;
    private const double CommentGap = 24;
    private readonly List<DanmakuItem> _items = [];
    private readonly ReadOnlyCollection<DanmakuItem> _readOnlyItems;
    private readonly List<DanmakuItem> _occupied = [];
    private readonly List<DanmakuItem> _movementOrder = [];
    private DanmakuItem? _pausedItem;
    private DanmakuSettings _settings = new();
    private double _width;
    private double _height;
    private double _layoutWidth;
    private bool _hasDeferredItems;
    private int _nextLane;

    public DanmakuEngine() => _readOnlyItems = _items.AsReadOnly();

    public static double ClampFrameSeconds(double elapsedSeconds)
    {
        ValidateNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        return Math.Min(elapsedSeconds, MaximumFrameSeconds);
    }

    public IReadOnlyList<DanmakuItem> Items => _readOnlyItems;
    public double LineHeight => _settings.FontSize * 1.6;
    // Ordinary displays must fit before playback consumes records; smaller engine samples remain supported.
    public bool HasUsableViewport => _width > 2 * EdgePadding &&
        _height * _settings.AreaFraction >= Math.Min(_settings.FontSize, DanmakuSettings.DefaultFontSize) * 1.6 + 2 * EdgePadding;
    public bool CanFit(StoredDanmaku comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return HasUsableViewport && DanmakuSettings.DefaultFontSize * 1.6 + 2 * EdgePadding <= _height * _settings.AreaFraction;
    }

    public bool CanFit(DanmakuDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return HasUsableViewport && display.FontSize * 1.6 + 2 * EdgePadding <= _height * _settings.AreaFraction;
    }

    public bool RemoveDisplay(string displayId)
    {
        bool removed = _items.RemoveAll(item => item.Display?.DisplayId == displayId) > 0;
        if (_pausedItem?.Display?.DisplayId == displayId) _pausedItem = null;
        return removed;
    }

    // Only the current, individually addressable visual may be paused; stale instances cannot pin a replay.
    public bool SetPausedItem(DanmakuItem? item)
    {
        if (item is not null && (!item.IsLayoutVisible || item.Display is not { CanLike: true, Source: not null } || !_items.Contains(item)))
            return false;
        _pausedItem = item;
        return true;
    }

    public bool Configure(DanmakuSettings settings, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateNonNegativeFinite(width, nameof(width));
        ValidateNonNegativeFinite(height, nameof(height));
        bool layoutChanged = width != _width || height != _height || settings.AreaFraction != _settings.AreaFraction || settings.FontSize != _settings.FontSize;
        if (settings.FontSize != _settings.FontSize)
        {
            // Stored display projections keep their measured geometry; only default-font samples become stale.
            _items.RemoveAll(item => item.UsesDefaultFontSize);
            _nextLane = 0;
        }

        _settings = settings;
        _width = width;
        _height = height;
        if (layoutChanged)
        {
            _pausedItem = null;
            if (HasUsableViewport)
            {
                if (_layoutWidth > 0 && _layoutWidth != width)
                    foreach (DanmakuItem item in _items)
                        item.X = width - (_layoutWidth - item.X) / (_layoutWidth + item.Width) * (width + item.Width);
                _layoutWidth = width;
            }
            Reflow();
        }
        return layoutChanged;
    }

    public void UpdateMeasurements(Func<DanmakuItem, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        _pausedItem = null;
        foreach (DanmakuItem item in _items)
        {
            double width = measure(item);
            ValidateNonNegativeFinite(width, nameof(measure));
            double progress = (_layoutWidth - item.X) / (_layoutWidth + item.Width);
            item.Width = width;
            item.X = _layoutWidth - progress * (_layoutWidth + width);
        }
        Reflow();
    }

    private void Reflow(bool preserveVisible = false)
    {
        _nextLane = 0;
        _hasDeferredItems = false;
        if (!preserveVisible)
            foreach (DanmakuItem item in _items) item.IsLayoutVisible = false;
        foreach (DanmakuItem item in _items)
        {
            if (item.IsLayoutVisible) continue;
            double y = EdgePadding;
            _occupied.Clear();
            foreach (DanmakuItem placed in _items)
                if (placed.IsLayoutVisible && placed.X < item.X + item.Width + CommentGap && item.X < placed.X + placed.Width + CommentGap)
                    _occupied.Add(placed);
            _occupied.Sort(static (left, right) => left.Y.CompareTo(right.Y));
            foreach (DanmakuItem placed in _occupied)
            {
                if (y + item.FontSize * 1.6 <= placed.Y) break;
                y = Math.Max(y, placed.Y + placed.FontSize * 1.6);
            }
            if (HasUsableViewport && y + item.FontSize * 1.6 + EdgePadding <= _height * _settings.AreaFraction)
            {
                item.Y = y;
                item.IsLayoutVisible = true;
            }
            else _hasDeferredItems = true;
        }
    }

    public bool TryAdd(string text, double width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateNonNegativeFinite(width, nameof(width));
        if (width == 0 || !HasUsableViewport || _items.Count >= MaximumComments)
        {
            return false;
        }

        int lanes = (int)Math.Min(MaximumComments, Math.Floor((_height * _settings.AreaFraction - 2 * EdgePadding) / LineHeight));
        for (int offset = 0; offset < lanes; offset++)
        {
            int lane = (_nextLane + offset) % lanes;
            double y = EdgePadding + lane * LineHeight;
            // Adjacent calculated lanes can differ by a floating-point rounding error at their shared edge.
            if (_items.Exists(item => item.IsLayoutVisible && y < item.Y + item.FontSize * 1.6 - 1e-6 && item.Y < y + LineHeight - 1e-6 && item.X + item.Width + CommentGap > _width))
            {
                continue;
            }

            _items.Add(new DanmakuItem(text, _width, y, width, _settings.FontSize, "#FFFFFF", usesDefaultFontSize: true));
            _nextLane = (lane + 1) % lanes;
            return true;
        }

        return false;
    }

    public bool TryAdd(StoredDanmaku comment, double width)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return TryAdd(new DanmakuDisplay(DanmakuAggregation.DisplayId(comment, aggregate: false), comment, 1, comment.OffsetMilliseconds), width);
    }

    public bool TryAdd(DanmakuDisplay comment, double width)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ValidateNonNegativeFinite(width, nameof(width));
        if (width == 0 || !HasUsableViewport || _items.Count >= MaximumComments)
        {
            return false;
        }

        double height = comment.FontSize * 1.6;
        double y = EdgePadding;
        // Vertical intervals allow ordinary and aggregated displays to share the viewport safely.
        // A reused buffer keeps this per-frame retry path allocation-free (it runs for every pending comment).
        _occupied.Clear();
        foreach (DanmakuItem item in _items)
        {
            if (item.IsLayoutVisible && item.X + item.Width + CommentGap > _width)
            {
                _occupied.Add(item);
            }
        }

        _occupied.Sort(static (left, right) => left.Y.CompareTo(right.Y));
        foreach (DanmakuItem item in _occupied)
        {
            if (y + height <= item.Y)
            {
                break;
            }

            if (y < item.Y + item.FontSize * 1.6)
            {
                y = item.Y + item.FontSize * 1.6;
            }
        }

        if (y + height + EdgePadding > _height * _settings.AreaFraction)
        {
            return false;
        }

        _items.Add(new DanmakuItem(comment.Text, _width, y, width, comment.FontSize, comment.Color, usesDefaultFontSize: false, display: comment));
        return true;
    }

    public void Advance(double elapsedSeconds)
    {
        ValidateNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        double distance = _settings.Speed * elapsedSeconds;
        _movementOrder.Clear();
        _movementOrder.AddRange(_items);
        _movementOrder.Sort(static (left, right) => left.X.CompareTo(right.X));
        for (int index = 0; index < _movementOrder.Count; index++)
        {
            DanmakuItem item = _movementOrder[index];
            if (!item.IsLayoutVisible || ReferenceEquals(item, _pausedItem)) continue;
            double x = item.X - distance;
            // Same-speed entry spacing is no longer enough while one item is stationary. Only followers
            // whose vertical intervals intersect queue behind it; other lanes and node time keep moving.
            for (int ahead = 0; ahead < index; ahead++)
            {
                DanmakuItem front = _movementOrder[ahead];
                if (front.IsLayoutVisible && front.X + front.Width > 0 && item.Y < front.Y + front.FontSize * 1.6 && front.Y < item.Y + item.FontSize * 1.6)
                    x = Math.Max(x, front.X + front.Width + CommentGap);
            }
            item.X = x;
        }

        _items.RemoveAll(static item => item.IsLayoutVisible && item.X + item.Width <= 0);
        if (_hasDeferredItems) Reflow(preserveVisible: true);
    }

    public void Clear()
    {
        _items.Clear();
        _movementOrder.Clear();
        _pausedItem = null;
        _hasDeferredItems = false;
        _nextLane = 0;
    }

    internal static void ValidateNonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Expected a finite non-negative value.");
        }
    }
}
