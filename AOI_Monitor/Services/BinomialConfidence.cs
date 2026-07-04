using System;

namespace AOI_Monitor.Services;

/// <summary>
/// Exact (Clopper-Pearson) binomial confidence intervals for pass/fail rates.
///
/// AOI measurement systems are validated the way Measurement System Analysis (MSA)
/// and Six Sigma require: a rate is never reported as a bare percentage. It is reported
/// with a confidence interval and, for production, as a DPMO/PPM figure. On small sample
/// sizes the interval is wide, which honestly communicates that the point estimate is not
/// yet trustworthy — far better than hiding or over-claiming a percentage.
///
/// The interval is the exact Clopper-Pearson interval, derived from the Beta quantile
/// (equivalently the F distribution). The regularized incomplete beta function and its
/// inverse are implemented here (Numerical Recipes betai/betacf + bisection) so the app
/// carries no external statistics dependency.
/// </summary>
public static class BinomialConfidence
{
    /// <summary>Defects/false-calls are reported per million opportunities (PPM / DPMO).</summary>
    public const int OpportunitiesPerMillion = 1_000_000;

    // Lanczos coefficients for the log-gamma approximation (Numerical Recipes, gammln).
    private static readonly double[] GammaCoefficients =
    {
        76.18009172947146, -86.50532032941677, 24.01409824083091,
        -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
    };

    /// <summary>
    /// Two-sided Clopper-Pearson interval for <paramref name="successes"/> out of
    /// <paramref name="trials"/> at the given <paramref name="confidence"/> (default 95%).
    /// </summary>
    public static (double Lower, double Upper) ClopperPearson(int successes, int trials, double confidence = 0.95)
    {
        if (trials <= 0)
            throw new ArgumentOutOfRangeException(nameof(trials), "Trials must be positive.");
        if (successes < 0 || successes > trials)
            throw new ArgumentOutOfRangeException(nameof(successes), "Successes must be within [0, trials].");
        if (confidence <= 0 || confidence >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be in (0, 1).");

        var alpha = 1.0 - confidence;
        var lower = successes == 0
            ? 0.0
            : InverseRegularizedBeta(alpha / 2.0, successes, trials - successes + 1);
        var upper = successes == trials
            ? 1.0
            : InverseRegularizedBeta(1.0 - alpha / 2.0, successes + 1, trials - successes);
        return (lower, upper);
    }

    /// <summary>Convert a proportion (0..1) to parts per million (DPMO/PPM).</summary>
    public static double ToPpm(double proportion) => proportion * OpportunitiesPerMillion;

    // Regularized incomplete beta I_x(a,b).
    private static double RegularizedBeta(double a, double b, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;

        var front = Math.Exp(GammaLn(a + b) - GammaLn(a) - GammaLn(b)
                             + a * Math.Log(x) + b * Math.Log(1.0 - x));
        return x < (a + 1.0) / (a + b + 2.0)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1.0 - front * BetaContinuedFraction(b, a, 1.0 - x) / b;
    }

    // Inverse of the regularized incomplete beta: the x where I_x(a,b) == p.
    // I_x is strictly increasing in x, so a bisection converges reliably.
    private static double InverseRegularizedBeta(double p, double a, double b)
    {
        if (p <= 0.0) return 0.0;
        if (p >= 1.0) return 1.0;

        double lo = 0.0, hi = 1.0;
        for (var i = 0; i < 200; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (RegularizedBeta(a, b, mid) < p) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // Continued-fraction evaluation used by the incomplete beta (Numerical Recipes betacf).
    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 300;
        const double epsilon = 3.0e-12;
        const double tiny = 1.0e-300;

        var qab = a + b;
        var qap = a + 1.0;
        var qam = a - 1.0;
        var c = 1.0;
        var d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1.0 / d;
        var h = d;

        for (var m = 1; m <= maxIterations; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1.0 / d;
            var delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) < epsilon) break;
        }

        return h;
    }

    // Log-gamma via the Lanczos approximation (Numerical Recipes gammln).
    private static double GammaLn(double value)
    {
        var x = value;
        var y = value;
        var tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        var series = 1.000000000190015;
        for (var j = 0; j < GammaCoefficients.Length; j++)
        {
            y += 1.0;
            series += GammaCoefficients[j] / y;
        }
        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}

/// <summary>
/// A pass/fail rate reported the way an AOI measurement study reports it: point estimate,
/// exact 95% confidence interval, and DPMO/PPM. Wide intervals on small samples are the
/// honest signal that the rate is not yet proven.
/// </summary>
public sealed class RateEstimate
{
    public RateEstimate(int successes, int trials, double confidence = 0.95)
    {
        Successes = successes;
        Trials = trials;
        Confidence = confidence;

        if (trials > 0)
        {
            Point = (double)successes / trials;
            (Lower, Upper) = BinomialConfidence.ClopperPearson(successes, trials, confidence);
        }
        else
        {
            Point = double.NaN;
            Lower = 0.0;
            Upper = 1.0;
        }
    }

    public int Successes { get; }
    public int Trials { get; }
    public double Confidence { get; }
    public double Point { get; }
    public double Lower { get; }
    public double Upper { get; }

    public bool IsMeasurable => Trials > 0;
    public double PointPpm => BinomialConfidence.ToPpm(Point);
    public double LowerPpm => BinomialConfidence.ToPpm(Lower);
    public double UpperPpm => BinomialConfidence.ToPpm(Upper);

    private int ConfidencePercent => (int)Math.Round(Confidence * 100.0);

    /// <summary>e.g. "1 of 20 = 5.0% (95% CI 0.1%-24.9%, n=20)".</summary>
    public string DescribeRate() => IsMeasurable
        ? $"{Successes} of {Trials} = {Point:P1} ({ConfidencePercent}% CI {Lower:P1}–{Upper:P1}, n={Trials})"
        : "not measurable (no samples)";

    /// <summary>e.g. "50,000 PPM (95% CI 1,235-248,725, n=20)".</summary>
    public string DescribePpm() => IsMeasurable
        ? $"{PointPpm:N0} PPM ({ConfidencePercent}% CI {LowerPpm:N0}–{UpperPpm:N0}, n={Trials})"
        : "not measurable (no samples)";

    /// <summary>
    /// For a zero-count outcome (e.g. no escapes observed), the meaningful statistic is the
    /// upper bound: "0 in n, 95% CI upper bound X%" — you can only bound the true rate.
    /// </summary>
    public string DescribeUpperBound() => IsMeasurable
        ? $"{Successes} of {Trials}; {ConfidencePercent}% CI upper bound {Upper:P1} ({UpperPpm:N0} PPM, n={Trials})"
        : "not measurable (no samples)";
}
