namespace ChaoDesk.Host.Panel;

public sealed record GlassOptions
{
    public string Mode { get; init; } = "standard";
    public double Displacement { get; init; } = 70;
    public double Blur { get; init; } = .0625;
    public double Saturation { get; init; } = 140;
    public double Aberration { get; init; } = 2;
    public double Radius { get; init; } = 24;
    public double Highlight { get; init; } = 1;
    public GlassOptions Normalize() => this with {
        Mode = Mode is "standard" or "polar" or "prominent" or "shader" ? Mode : "standard",
        Displacement = Clamp(Displacement, 0, 200, 70), Blur = Clamp(Blur, 0, 1, .0625),
        Saturation = Clamp(Saturation, 100, 300, 140), Aberration = Clamp(Aberration, 0, 20, 2),
        Radius = Clamp(Radius, 0, 48, 24), Highlight = Clamp(Highlight, 0, 2, 1)
    };
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
