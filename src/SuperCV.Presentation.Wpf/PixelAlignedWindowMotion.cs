namespace SuperCV;

/// <summary>
/// Keeps native child-window motion stable when WPF device-independent coordinates land between
/// physical pixels.
/// </summary>
internal static class PixelAlignedWindowMotion
{
    internal static int RoundPhysicalCoordinate(double exactPhysicalCoordinate) =>
        checked((int)Math.Round(exactPhysicalCoordinate, MidpointRounding.AwayFromZero));

    internal static double AlignLogicalCoordinate(double logicalCoordinate, double dpiScale)
    {
        if (!double.IsFinite(logicalCoordinate))
        {
            throw new ArgumentOutOfRangeException(nameof(logicalCoordinate));
        }

        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }

        return Math.Round(
            logicalCoordinate * dpiScale,
            MidpointRounding.AwayFromZero) / dpiScale;
    }

    internal static double AlignLogicalStep(double logicalStep, double dpiScale)
    {
        if (!double.IsFinite(logicalStep))
        {
            throw new ArgumentOutOfRangeException(nameof(logicalStep));
        }

        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }

        // Align the repeated step once, then multiply it by the item index. Rounding every item
        // position independently can otherwise create alternating one-pixel gaps at 125%/150% DPI.
        double physicalStep = Math.Round(
            logicalStep * dpiScale,
            MidpointRounding.AwayFromZero);
        return physicalStep / dpiScale;
    }

    internal static double SnapToTargetPhysicalPixel(
        double currentCoordinate,
        double targetCoordinate,
        double dpiScale)
    {
        ValidateLogicalValue(currentCoordinate, nameof(currentCoordinate));
        ValidateLogicalValue(targetCoordinate, nameof(targetCoordinate));
        ValidateDpiScale(dpiScale);

        double halfPhysicalPixelInLogicalUnits = 0.5 / dpiScale;
        return Math.Abs(currentCoordinate - targetCoordinate) <=
               halfPhysicalPixelInLogicalUnits
            ? targetCoordinate
            : currentCoordinate;
    }

    internal static bool IsResidualMotionSettled(
        double displacement,
        double velocity,
        double dpiScale)
    {
        ValidateLogicalValue(displacement, nameof(displacement));
        ValidateLogicalValue(velocity, nameof(velocity));
        ValidateDpiScale(dpiScale);

        return Math.Abs(displacement * dpiScale) <= 0.5 &&
               Math.Abs(velocity * dpiScale) <= 1.0;
    }

    private static void ValidateLogicalValue(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateDpiScale(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }
    }
}
