using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GalDanmaku.Core;

namespace GalDanmaku.App.Overlay;

public sealed class OverlayRenderer : FrameworkElement, IDisposable
{
    private static readonly string[] SampleComments =
    [
        "欢迎来到 GalDanmaku",
        "这是一条模拟弹幕 ✨",
        "故事，在这里继续。",
        "Hello, visual novel!",
        "鼠标可以直接穿过弹幕",
        "保持热爱，慢慢阅读",
    ];

    private readonly DanmakuEngine _engine = new();
    private readonly SimulationCadence _cadence = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, TextDrawing> _textCache = [];
    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Pen _outline = new(Brushes.Black, 2.5) { LineJoin = PenLineJoin.Round };
    private DanmakuSettings _settings = new();
    private double _previousSeconds;
    private double _pixelsPerDip;
    private int _sampleIndex;
    private bool _playing;
    private bool _disposed;

    public OverlayRenderer()
    {
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
            if (value.FontSize != _settings.FontSize)
            {
                _textCache.Clear();
            }

            _settings = value;
            ConfigureEngine();
            InvalidateVisual();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_playing)
        {
            return;
        }

        _playing = true;
        _previousSeconds = 0;
        _cadence.Reset();
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_playing)
        {
            return;
        }

        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
        _playing = false;
        _engine.Clear();
        InvalidateVisual();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
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
            TextDrawing text = GetText(item.Text);
            drawingContext.PushTransform(new TranslateTransform(item.X + 4, item.Y));
            // Paint the fill last so the centered outline cannot obscure narrow strokes.
            drawingContext.DrawGeometry(null, _outline, text.Geometry);
            drawingContext.DrawGeometry(Brushes.White, null, text.Geometry);
            drawingContext.Pop();
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        ConfigureEngine();
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _previousSeconds;
        _previousSeconds = now;
        _engine.Advance(elapsed);
        if (_cadence.Advance(elapsed))
        {
            string sample = SampleComments[_sampleIndex];
            TextDrawing text = GetText(sample);
            if (_engine.TryAdd(sample, text.Width))
            {
                _sampleIndex = (_sampleIndex + 1) % SampleComments.Length;
            }
        }

        InvalidateVisual();
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

    private TextDrawing GetText(string text)
    {
        if (_textCache.TryGetValue(text, out TextDrawing? drawing))
        {
            return drawing;
        }

        var formattedText = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, _typeface, _settings.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Geometry geometry = formattedText.BuildGeometry(new Point(0, 0));
        geometry.Freeze();
        drawing = new TextDrawing(geometry, Math.Max(formattedText.WidthIncludingTrailingWhitespace, geometry.Bounds.Right) + 8);
        _textCache.Add(text, drawing);
        return drawing;
    }

    private sealed record TextDrawing(Geometry Geometry, double Width);
}
