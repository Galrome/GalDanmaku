using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GalDanmaku.Core;

namespace GalDanmaku.App.Overlay;

public sealed class OverlayRenderer : FrameworkElement, IDisposable
{
    private const int MaximumCachedDrawings = 256;
    private readonly NodePlayback _playback;
    private readonly DanmakuEngine _engine = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<(string Text, double FontSize, string Color), TextDrawing> _textCache = [];
    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Pen _outline = new(Brushes.Black, 2.5) { LineJoin = PenLineJoin.Round };
    private DanmakuSettings _settings = new();
    private double _previousSeconds;
    private double _pixelsPerDip;
    private long _resetVersion = -1;
    private long _displayVersion = -1;
    private bool _playing;
    private bool _hasDrawnItems;
    private bool _disposed;
    private DanmakuItem? _hoveredItem;

    public event EventHandler? FrameAdvanced;
    public event EventHandler? HoveredCommentChanged;
    public StoredDanmaku? HoveredComment => _hoveredItem?.Display?.Source;
    public Rect HoveredBounds => _hoveredItem is { } item ? ItemBounds(item) : Rect.Empty;

    public void ClearHover() => SetHoveredItem(null);

    public void HoverAt(Point position)
    {
        for (int index = _engine.Items.Count - 1; index >= 0; index--)
        {
            DanmakuItem item = _engine.Items[index];
            if (item.IsLayoutVisible && item.Display is { CanLike: true, Source: not null } && ItemBounds(item).Contains(position))
            {
                SetHoveredItem(item);
                return;
            }
        }
    }

    private void SetHoveredItem(DanmakuItem? item)
    {
        if (ReferenceEquals(item, _hoveredItem) || !_engine.SetPausedItem(item)) return;
        _hoveredItem = item;
        HoveredCommentChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rect ItemBounds(DanmakuItem item)
    {
        if (!item.IsLayoutVisible) return Rect.Empty;
        Rect bounds = GetText(item.Text, item.FontSize, item.Color).Geometry.Bounds;
        if (bounds.IsEmpty) return Rect.Empty;
        bounds.Offset(item.X + 4, item.Y);
        bounds.Inflate(3, 3);
        bounds.Intersect(new Rect(0, 0, ActualWidth, ActualHeight * _settings.AreaFraction));
        return bounds;
    }

    private void ValidateHover()
    {
        if (_hoveredItem is { } item && (!_engine.Items.Contains(item) || ItemBounds(item).IsEmpty)) ClearHover();
    }

    public OverlayRenderer(NodePlayback playback)
    {
        _playback = playback;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _outline.Freeze();
    }

    public DanmakuSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _settings = value;
            _playback.AggregationEnabled = value.AggregationEnabled;
            ResetIfNeeded();
            ConfigureEngine();
            InvalidateVisual();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _playback.IsPlaying = true;
        ResetIfNeeded();
        if (_playing)
        {
            return;
        }

        _playing = true;
        _previousSeconds = 0;
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        ClearHover();
        _playback.IsPlaying = false;
        if (!_playing)
        {
            return;
        }

        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
        _playing = false;
        // Retain active visuals and pending comments across foreground changes.
    }

    public void ResetVisuals()
    {
        ClearHover();
        _resetVersion = _playback.ResetVersion;
        _displayVersion = _playback.DisplayVersion;
        _engine.Clear();
        _textCache.Clear();
        InvalidateVisual();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _engine.Clear();
        _textCache.Clear();
        _disposed = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        ConfigureEngine();
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight * _settings.AreaFraction)));
        drawingContext.PushOpacity(_settings.Opacity);
        foreach (DanmakuItem item in _engine.Items)
        {
            if (!item.IsLayoutVisible) continue;
            TextDrawing text = GetText(item.Text, item.FontSize, item.Color);
            drawingContext.PushTransform(new TranslateTransform(item.X + 4, item.Y));
            // Paint the fill last so the centered outline cannot obscure narrow strokes.
            drawingContext.DrawGeometry(null, _outline, text.Geometry);
            drawingContext.DrawGeometry(text.Brush, null, text.Geometry);
            drawingContext.Pop();
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        ResetIfNeeded();
        RefreshDisplays();
        ConfigureEngine();
        double now = _clock.Elapsed.TotalSeconds;
        // Window drags, sleep or debugger pauses must not fast-forward node time or flush every comment at once.
        double elapsed = DanmakuEngine.ClampFrameSeconds(Math.Max(0, now - _previousSeconds));
        _previousSeconds = now;
        if (_engine.HasUsableViewport)
        {
            _engine.Advance(elapsed);
            _playback.AdvanceDisplay(elapsed, comment =>
            {
                // In a laid-out viewport, an unsupported saved font must not block the queue.
                if (!_engine.CanFit(comment)) return true;
                // All slots busy: skip text measurement for entries that cannot be placed this frame.
                if (_engine.Items.Count >= DanmakuEngine.MaximumComments) return false;
                TextDrawing text = GetText(comment.Text, comment.FontSize, comment.Color);
                return _engine.TryAdd(comment, text.Width);
            });
        }
        ValidateHover();
        FrameAdvanced?.Invoke(this, EventArgs.Empty);
        // Idle frames with nothing on screen need no re-render.
        bool hasItems = _engine.Items.Count > 0;
        if (hasItems || _hasDrawnItems)
        {
            InvalidateVisual();
        }

        _hasDrawnItems = hasItems;
    }

    private void ResetIfNeeded()
    {
        if (_resetVersion != _playback.ResetVersion)
        {
            ResetVisuals();
        }
    }

    private void RefreshDisplays()
    {
        if (_displayVersion == _playback.DisplayVersion) return;
        _displayVersion = _playback.DisplayVersion;
        // A saved duplicate replaces its presentation; discard old geometry before placing the larger group.
        // Scan only the bounded active items when the projection changes, never historical records per frame.
        for (int index = _engine.Items.Count - 1; index >= 0; index--)
        {
            if (_engine.Items[index].Display is { } display && !_playback.IsCurrentDisplay(display))
                _engine.RemoveDisplay(display.DisplayId);
        }
        ValidateHover();
    }

    private void ConfigureEngine()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool dpiChanged = pixelsPerDip != _pixelsPerDip;
        if (dpiChanged)
        {
            ClearHover();
            _pixelsPerDip = pixelsPerDip;
            _textCache.Clear();
        }

        if (_engine.Configure(_settings, ActualWidth, ActualHeight)) ClearHover();
        if (dpiChanged) _engine.UpdateMeasurements(item => GetText(item.Text, item.FontSize, item.Color).Width);
        ValidateHover();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        ClearHover();
        base.OnRenderSizeChanged(sizeInfo);
        ConfigureEngine();
    }

    private TextDrawing GetText(string text, double fontSize, string color)
    {
        var key = (text, fontSize, color);
        if (_textCache.TryGetValue(key, out TextDrawing? drawing))
        {
            return drawing;
        }

        if (_textCache.Count >= MaximumCachedDrawings)
        {
            _textCache.Clear();
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        var formattedText = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, _typeface, fontSize, brush, _pixelsPerDip);
        Geometry geometry = formattedText.BuildGeometry(new Point(0, 0));
        geometry.Freeze();
        drawing = new TextDrawing(geometry, Math.Max(formattedText.WidthIncludingTrailingWhitespace, geometry.Bounds.Right) + 8, brush);
        _textCache.Add(key, drawing);
        return drawing;
    }

    private sealed record TextDrawing(Geometry Geometry, double Width, Brush Brush);
}
