using System;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public class BinomialConfidenceTests
{
    // Reference values are the standard two-sided 95% Clopper-Pearson intervals
    // (cross-checked against published binomial CI tables / R's binom.test).
    [Theory]
    [InlineData(0, 20, 0.0, 0.16842)]
    [InlineData(0, 100, 0.0, 0.03621)]
    [InlineData(1, 20, 0.00126, 0.24873)]
    [InlineData(2, 10, 0.02521, 0.55610)]
    [InlineData(5, 20, 0.08657, 0.49104)]
    [InlineData(20, 20, 0.83158, 1.0)]
    [InlineData(0, 1, 0.0, 0.975)]
    public void ClopperPearsonMatchesKnownReferenceIntervals(int k, int n, double expectedLower, double expectedUpper)
    {
        var (lower, upper) = BinomialConfidence.ClopperPearson(k, n);
        Assert.Equal(expectedLower, lower, 3);
        Assert.Equal(expectedUpper, upper, 3);
    }

    [Fact]
    public void ClopperPearsonIntervalAlwaysContainsThePointEstimate()
    {
        for (var n = 1; n <= 60; n++)
        for (var k = 0; k <= n; k++)
        {
            var (lower, upper) = BinomialConfidence.ClopperPearson(k, n);
            var point = (double)k / n;
            Assert.True(lower >= 0.0 && upper <= 1.0 && lower <= upper, $"malformed interval for {k}/{n}");
            Assert.True(lower <= point + 1e-9 && point <= upper + 1e-9, $"CI [{lower},{upper}] must contain {point} for {k}/{n}");
        }
    }

    [Fact]
    public void ClopperPearsonLowerIsComplementOfUpper()
    {
        // By construction lower(k, n) == 1 - upper(n-k, n).
        var (lower, _) = BinomialConfidence.ClopperPearson(5, 20);
        var (_, upperComplement) = BinomialConfidence.ClopperPearson(15, 20);
        Assert.Equal(lower, 1.0 - upperComplement, 6);
    }

    [Fact]
    public void ZeroSuccessesGivesZeroLowerBoundAndPositiveUpperBound()
    {
        var (lower, upper) = BinomialConfidence.ClopperPearson(0, 30);
        Assert.Equal(0.0, lower);
        Assert.InRange(upper, 0.0, 1.0);
        Assert.True(upper > 0.0);
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BinomialConfidence.ClopperPearson(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinomialConfidence.ClopperPearson(5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinomialConfidence.ClopperPearson(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinomialConfidence.ClopperPearson(1, 10, 1.5));
    }

    [Fact]
    public void RateEstimateReportsPointConfidenceIntervalAndPpm()
    {
        var estimate = new RateEstimate(1, 20);
        Assert.True(estimate.IsMeasurable);
        Assert.Equal(0.05, estimate.Point, 6);
        Assert.Equal(50000.0, estimate.PointPpm, 0);
        Assert.InRange(estimate.Lower, 0.0, 0.05);
        Assert.InRange(estimate.Upper, 0.05, 1.0);
        Assert.Contains("95% CI", estimate.DescribeRate(), StringComparison.Ordinal);
        Assert.Contains("n=20", estimate.DescribeRate(), StringComparison.Ordinal);
        Assert.Contains("PPM", estimate.DescribePpm(), StringComparison.Ordinal);
    }

    [Fact]
    public void SmallSampleProducesWideIntervalThatCommunicatesUncertainty()
    {
        // Zero false calls on only 3 images cannot prove a low rate: the upper bound stays high.
        var estimate = new RateEstimate(0, 3);
        Assert.Equal(0.0, estimate.Point, 6);
        Assert.True(estimate.Upper > 0.5, "3-sample zero-count upper bound should remain > 50%");
    }

    [Fact]
    public void RateEstimateWithNoTrialsIsNotMeasurable()
    {
        var estimate = new RateEstimate(0, 0);
        Assert.False(estimate.IsMeasurable);
        Assert.Contains("not measurable", estimate.DescribeRate(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToPpmConvertsProportion()
    {
        Assert.Equal(12000.0, BinomialConfidence.ToPpm(0.012), 6);
    }
}
