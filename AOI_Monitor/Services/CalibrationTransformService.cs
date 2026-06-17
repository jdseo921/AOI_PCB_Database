using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CalibrationTransformService
{
    public static CalibrationTransform Calculate(IReadOnlyList<CalibrationPointInput> points)
    {
        if (points.Count < 2)
        {
            return new CalibrationTransform(
                false,
                points.Count,
                0,
                0,
                0,
                0,
                "At least two calibration points are required to calculate an approximate 2D transform.");
        }

        var xFit = Fit(points.Select(point => (Input: point.ImageX, Output: point.BoardXMillimeters)).ToArray());
        var yFit = Fit(points.Select(point => (Input: point.ImageY, Output: point.BoardYMillimeters)).ToArray());
        var summary = $"Approximate 2D scale/offset: boardX = {xFit.Scale:F6} * imageX + {xFit.Offset:F3}, boardY = {yFit.Scale:F6} * imageY + {yFit.Offset:F3}. Stage 2 preparation only.";

        return new CalibrationTransform(
            true,
            points.Count,
            xFit.Scale,
            xFit.Offset,
            yFit.Scale,
            yFit.Offset,
            summary);
    }

    public static bool TryConvertImageToBoard(
        CalibrationProfileRecord? profile,
        double imageX,
        double imageY,
        out double boardXMillimeters,
        out double boardYMillimeters)
    {
        boardXMillimeters = 0;
        boardYMillimeters = 0;

        if (profile is null || !profile.HasTransform)
            return false;

        boardXMillimeters = profile.ScaleX * imageX + profile.OffsetX;
        boardYMillimeters = profile.ScaleY * imageY + profile.OffsetY;
        return true;
    }

    private static (double Scale, double Offset) Fit(IReadOnlyList<(double Input, double Output)> samples)
    {
        var n = samples.Count;
        var sumInput = samples.Sum(sample => sample.Input);
        var sumOutput = samples.Sum(sample => sample.Output);
        var sumInputOutput = samples.Sum(sample => sample.Input * sample.Output);
        var sumInputSquared = samples.Sum(sample => sample.Input * sample.Input);
        var denominator = n * sumInputSquared - sumInput * sumInput;

        if (Math.Abs(denominator) < 0.000001)
            return (0, sumOutput / n);

        var scale = (n * sumInputOutput - sumInput * sumOutput) / denominator;
        var offset = (sumOutput - scale * sumInput) / n;
        return (scale, offset);
    }
}
