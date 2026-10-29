namespace GalDanmaku.Core;

public sealed record DanmakuSettings
{
    public const double DefaultFontSize = 28;

    public DanmakuSettings(double fontSize = DefaultFontSize, double opacity = 0.9, double speed = 140, double areaFraction = 0.5,
        bool aggregationEnabled = false)
    {
        ValidateRange(fontSize, 12, 64, nameof(fontSize));
        ValidateRange(opacity, 0.1, 1, nameof(opacity));
        ValidateRange(speed, 40, 400, nameof(speed));
        ValidateRange(areaFraction, 0.25, 1, nameof(areaFraction));
        FontSize = fontSize;
        Opacity = opacity;
        Speed = speed;
        AreaFraction = areaFraction;
        AggregationEnabled = aggregationEnabled;
    }

    public double FontSize { get; }
    public double Opacity { get; }
    public double Speed { get; }
    public double AreaFraction { get; }
    public bool AggregationEnabled { get; }

    private static void ValidateRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Expected a finite value between {minimum} and {maximum}.");
        }
    }
}
