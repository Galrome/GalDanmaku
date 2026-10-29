using System.Collections.ObjectModel;

namespace GalDanmaku.Core;

public sealed class DanmakuEngine
{
    public const int MaximumComments = 64;
    private const double EdgePadding = 8;
    private const double CommentGap = 24;
    private readonly List<DanmakuItem> _items = [];
    private readonly ReadOnlyCollection<DanmakuItem> _readOnlyItems;
    private DanmakuSettings _settings = new();
    private double _width;
    private double _height;
    private int _nextLane;

    public DanmakuEngine() => _readOnlyItems = _items.AsReadOnly();

    public IReadOnlyList<DanmakuItem> Items => _readOnlyItems;
    public double LineHeight => _settings.FontSize * 1.6;

    public void Configure(DanmakuSettings settings, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateNonNegativeFinite(width, nameof(width));
        ValidateNonNegativeFinite(height, nameof(height));
        if (width != _width || height != _height || settings.FontSize != _settings.FontSize ||
            settings.AreaFraction != _settings.AreaFraction)
        {
            Clear();
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

            _items.Add(new DanmakuItem(text, _width, y, width));
            _nextLane = (lane + 1) % lanes;
            return true;
        }

        return false;
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
