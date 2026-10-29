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
    private bool _playing;
    private bool _disposed;

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
        _resetVersion = _playback.ResetVersion;
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
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight * _settings.AreaFraction)));
        drawingContext.PushOpacity(_settings.Opacity);
        foreach (DanmakuItem item in _engine.Items)
        {
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
        ConfigureEngine();
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _previousSeconds;
        _previousSeconds = now;
        _engine.Advance(elapsed);
        _playback.Advance(elapsed, comment =>
        {
            // Saved entries remain replayable after resizing; an impossible size must not block the queue.
            if (!_engine.CanFit(comment)) return true;
            TextDrawing text = GetText(comment.Text, comment.FontSize, comment.Color);
            return _engine.TryAdd(comment, text.Width);
        });
        InvalidateVisual();
    }

    private void ResetIfNeeded()
    {
        if (_resetVersion != _playback.ResetVersion)
        {
            ResetVisuals();
        }
    }

    private void ConfigureEngine()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (pixelsPerDip != _pixelsPerDip)
        {
            _pixelsPerDip = pixelsPerDip;
            _textCache.Clear();
            _engine.Clear();
        }

        _engine.Configure(_settings, ActualWidth, ActualHeight);
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
            FlowDirection.LeftToRight, _typeface, fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Geometry geometry = formattedText.BuildGeometry(new Point(0, 0));
        geometry.Freeze();
        drawing = new TextDrawing(geometry, Math.Max(formattedText.WidthIncludingTrailingWhitespace, geometry.Bounds.Right) + 8, brush);
        _textCache.Add(key, drawing);
        return drawing;
    }

    private sealed record TextDrawing(Geometry Geometry, double Width, Brush Brush);
}
