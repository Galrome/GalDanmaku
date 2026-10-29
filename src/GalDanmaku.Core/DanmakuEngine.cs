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
    private DanmakuSettings _settings = new();
    private double _width;
    private double _height;
    private int _nextLane;

    public DanmakuEngine() => _readOnlyItems = _items.AsReadOnly();

    public static double ClampFrameSeconds(double elapsedSeconds)
    {
        ValidateNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        return Math.Min(elapsedSeconds, MaximumFrameSeconds);
    }

    public IReadOnlyList<DanmakuItem> Items => _readOnlyItems;
    public double LineHeight => _settings.FontSize * 1.6;
    public bool CanFit(StoredDanmaku comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return _width > 0 && comment.FontSize * 1.6 + 2 * EdgePadding <= _height * _settings.AreaFraction;
    }

    public void Configure(DanmakuSettings settings, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateNonNegativeFinite(width, nameof(width));
        ValidateNonNegativeFinite(height, nameof(height));
        if (width != _width || height != _height || settings.AreaFraction != _settings.AreaFraction)
        {
            Clear();
        }
        else if (settings.FontSize != _settings.FontSize)
        {
            // Stored entries own their font and measured geometry; only default-font samples become stale.
            _items.RemoveAll(item => item.UsesDefaultFontSize);
            _nextLane = 0;
        }

        _settings = settings;
        _width = width;
        _height = height;
    }

    public bool TryAdd(string text, double width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateNonNegativeFinite(width, nameof(width));
        if (width == 0 || _width == 0 || _items.Count >= MaximumComments)
        {
            return false;
        }

        int lanes = (int)Math.Min(MaximumComments, Math.Floor((_height * _settings.AreaFraction - 2 * EdgePadding) / LineHeight));
        for (int offset = 0; offset < lanes; offset++)
        {
            int lane = (_nextLane + offset) % lanes;
            double y = EdgePadding + lane * LineHeight;
            if (_items.Exists(item => item.Y == y && item.X + item.Width + CommentGap > _width))
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
        ValidateNonNegativeFinite(width, nameof(width));
        if (width == 0 || _width == 0 || _items.Count >= MaximumComments)
        {
            return false;
        }

        double height = comment.FontSize * 1.6;
        double y = EdgePadding;
        // Vertical intervals allow persisted comments of different sizes to share the viewport safely.
        // A reused buffer keeps this per-frame retry path allocation-free (it runs for every pending comment).
        _occupied.Clear();
        foreach (DanmakuItem item in _items)
        {
            if (item.X + item.Width + CommentGap > _width)
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

        _items.Add(new DanmakuItem(comment.Text, _width, y, width, comment.FontSize, comment.Color, usesDefaultFontSize: false));
        return true;
    }

    public void Advance(double elapsedSeconds)
    {
        ValidateNonNegativeFinite(elapsedSeconds, nameof(elapsedSeconds));
        double distance = _settings.Speed * elapsedSeconds;
        for (int index = _items.Count - 1; index >= 0; index--)
        {
            DanmakuItem item = _items[index];
            item.X -= distance;
            if (item.X + item.Width <= 0)
            {
                _items.RemoveAt(index);
            }
        }
    }

    public void Clear()
    {
        _items.Clear();
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
