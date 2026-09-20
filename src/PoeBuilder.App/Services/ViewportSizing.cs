namespace PoeBuilder.App.Services;

public static class ViewportSizing
{
    public const double AspectRatio = 16.0 / 9.0;
    // Width and height both follow the window. A scroll fallback handles very small windows.
    public static double Width(double availableWidth, double windowHeight)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 14) return 1;
        double height = double.IsFinite(windowHeight) && windowHeight > 0 ? Math.Max(220, windowHeight - 380) : 540;
        return Math.Max(1, Math.Min(availableWidth - 14, height * AspectRatio));
    }
    public static double Height(double width) => Math.Max(1, width) / AspectRatio;
}
