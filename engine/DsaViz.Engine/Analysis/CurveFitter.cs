using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Fits measured operation counts against the standard complexity classes.
///
/// This measures rather than asserts, which is the point: a fitted curve shows the
/// growth and can say how well it matched, where a static label can only claim. The
/// input is statement counts rather than wall-clock time — timing noise would make the
/// fit describe the machine as much as the algorithm.
/// </summary>
public static class CurveFitter
{
    /// <summary>
    /// Below this, the shape is not being described well enough to name it. Reporting
    /// Unknown is more useful than the least-bad label from a poor set of fits.
    /// </summary>
    public const double MinimumRSquared = 0.99;

    private const int MinimumPoints = 3;

    private static readonly (ComplexityClass Class, Func<double, double> Basis)[] Candidates =
    {
        (ComplexityClass.Constant, _ => 1.0),
        (ComplexityClass.Logarithmic, n => n < 2 ? 1.0 : Math.Log2(n)),
        (ComplexityClass.Linear, n => n),
        (ComplexityClass.Linearithmic, n => n < 2 ? 1.0 : n * Math.Log2(n)),
        (ComplexityClass.Quadratic, n => n * n),
        (ComplexityClass.Cubic, n => n * n * n)
        // Exponential is deliberately absent here. Fitting a fixed 2^n basis only
        // recognises algorithms that happen to double; naive Fibonacci grows as
        // phi^n (about 1.618^n) and scores poorly against 2^n despite being plainly
        // exponential. It is detected separately, by base, below.
    };

    /// <param name="allowIntercept">
    /// Operation counts are fitted through the origin: complexity classes describe growth
    /// up from zero work, and an intercept would let a wrong shape fake a good fit. Space
    /// readings are different — a balanced tree's call depth is log n plus one, and a
    /// curve forced through zero cannot fit 5, 6, 7 … 11 at all. Those are fitted with an
    /// intercept, so the constant is absorbed and the growth is what gets classified.
    /// </param>
    public static CurveFit Fit(IReadOnlyList<SizePoint> points, bool allowIntercept = false)
    {
        if (points.Count < MinimumPoints)
            return new CurveFit(ComplexityClass.Unknown, 0, Array.Empty<ClassFit>(),
                $"Only {points.Count} size(s) measured; at least {MinimumPoints} are needed " +
                "before a growth curve means anything.");

        var sizes = points.Select(p => (double)p.Size).ToArray();
        var observed = points.Select(p => (double)p.Operations).ToArray();

        double mean = observed.Average();
        double totalSumOfSquares = observed.Sum(y => (y - mean) * (y - mean));

        // Every measurement identical: the shape is flat, and no curve fit is needed
        // to say so. Guarded separately because R² is undefined when there is no
        // variance to explain.
        if (totalSumOfSquares == 0)
            return new CurveFit(ComplexityClass.Constant, 1.0,
                new[] { new ClassFit(ComplexityClass.Constant, 1.0, mean) },
                "Operation count did not change with input size.");

        var fits = new List<ClassFit>();

        foreach (var (complexityClass, basis) in Candidates)
        {
            var basisValues = sizes.Select(basis).ToArray();

            // 2^n overflows to infinity well before n gets large. Skipping keeps a
            // meaningless candidate out of the ranking rather than letting it produce
            // a nonsense score.
            if (basisValues.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v > 1e300))
                continue;

            double coefficient, intercept = 0;
            if (allowIntercept)
            {
                // A constant basis explains none of the variance once an intercept exists.
                if (complexityClass == ComplexityClass.Constant) continue;
                double meanBasis = basisValues.Average();
                double variance = basisValues.Sum(b => (b - meanBasis) * (b - meanBasis));
                if (variance == 0) continue;
                coefficient = basisValues.Zip(observed, (b, y) => (b - meanBasis) * (y - mean)).Sum() / variance;
                intercept = mean - coefficient * meanBasis;
            }
            else
            {
                double denominator = basisValues.Sum(v => v * v);
                if (denominator == 0) continue;
                coefficient = basisValues.Zip(observed, (b, y) => b * y).Sum() / denominator;
            }

            // A shrinking curve is not a complexity class.
            if (coefficient <= 0 && complexityClass != ComplexityClass.Constant) continue;

            double residualSumOfSquares = basisValues
                .Zip(observed, (b, y) => (y - intercept - coefficient * b) * (y - intercept - coefficient * b))
                .Sum();

            fits.Add(new ClassFit(complexityClass, 1.0 - residualSumOfSquares / totalSumOfSquares, coefficient));
        }

        // Exponential growth is identified by family rather than by a fixed base.
        // For a polynomial c*n^k, log y is linear in log n; for an exponential c*b^n,
        // log y is linear in n. Comparing those two regressions says which family the
        // data belongs to, and the second one also yields the base.
        var (exponentialR2, growthBase) = FitExponentialFamily(sizes, observed);
        double polynomialR2 = FitPolynomialFamily(sizes, observed);

        bool looksExponential =
            exponentialR2 >= MinimumRSquared &&
            exponentialR2 > polynomialR2 &&
            growthBase > 1.05;

        if (looksExponential)
            fits.Add(new ClassFit(ComplexityClass.Exponential, exponentialR2, growthBase));

        var ranked = fits.OrderByDescending(f => f.RSquared).ToList();

        if (looksExponential && ranked[0].Class == ComplexityClass.Exponential)
            return new CurveFit(ComplexityClass.Exponential, exponentialR2, ranked,
                $"Measured growth is exponential, roughly {growthBase:F2}^n (R²={exponentialR2:F4}). " +
                "Every increment of n multiplies the work rather than adding to it, which is why " +
                "the usable input size stays small no matter how fast the machine is.");
        if (ranked.Count == 0)
            return new CurveFit(ComplexityClass.Unknown, 0, ranked,
                "No candidate curve could be evaluated for these sizes.");

        var best = ranked[0];

        if (best.RSquared < MinimumRSquared)
            return new CurveFit(ComplexityClass.Unknown, best.RSquared, ranked,
                $"The closest candidate was {ComplexityNames.Format(best.Class)} at R²={best.RSquared:F4}, " +
                $"below the {MinimumRSquared} needed to name a class. The measured growth does not " +
                "match a standard shape — often a sign the input size is not what actually drives the work.");

        string explanation = $"Measured growth matches {ComplexityNames.Format(best.Class)} (R²={best.RSquared:F4}).";

        if (ranked.Count > 1)
        {
            var runnerUp = ranked[1];
            // A near-tie is real information. Saying "it is O(n)" when O(n log n) fit
            // almost as well would present a coin-flip as a conclusion.
            if (best.RSquared - runnerUp.RSquared < 0.01)
                explanation += $" {ComplexityNames.Format(runnerUp.Class)} fits almost as well " +
                               $"(R²={runnerUp.RSquared:F4}); these are hard to separate over this " +
                               "range, so measure a wider spread of sizes to tell them apart.";
        }

        return new CurveFit(best.Class, best.RSquared, ranked, explanation);
    }

    /// <summary>Regression of log(operations) on n. Linear here means exponential growth.</summary>
    private static (double RSquared, double Base) FitExponentialFamily(double[] sizes, double[] observed)
    {
        var usable = sizes.Zip(observed).Where(pair => pair.Second > 0).ToArray();
        if (usable.Length < MinimumPoints) return (double.NegativeInfinity, 0);

        double r2 = Regress(
            usable.Select(pair => pair.First).ToArray(),
            usable.Select(pair => Math.Log(pair.Second)).ToArray(),
            out double slope);

        return (r2, Math.Exp(slope));
    }

    /// <summary>Regression of log(operations) on log(n). Linear here means polynomial growth.</summary>
    private static double FitPolynomialFamily(double[] sizes, double[] observed)
    {
        var usable = sizes.Zip(observed).Where(pair => pair.First > 0 && pair.Second > 0).ToArray();
        if (usable.Length < MinimumPoints) return double.NegativeInfinity;

        return Regress(
            usable.Select(pair => Math.Log(pair.First)).ToArray(),
            usable.Select(pair => Math.Log(pair.Second)).ToArray(),
            out _);
    }

    private static double Regress(double[] x, double[] y, out double slope)
    {
        double meanX = x.Average();
        double meanY = y.Average();

        double covariance = x.Zip(y, (xi, yi) => (xi - meanX) * (yi - meanY)).Sum();
        double varianceX = x.Sum(xi => (xi - meanX) * (xi - meanX));

        if (varianceX == 0) { slope = 0; return double.NegativeInfinity; }

        // Held in a local because an out parameter cannot be captured by the lambda
        // that computes the residuals below.
        double gradient = covariance / varianceX;
        slope = gradient;
        double intercept = meanY - gradient * meanX;

        double totalSumOfSquares = y.Sum(yi => (yi - meanY) * (yi - meanY));
        if (totalSumOfSquares == 0) { return 1.0; }

        double residualSumOfSquares = x.Zip(y, (xi, yi) =>
        {
            double predicted = intercept + gradient * xi;
            return (yi - predicted) * (yi - predicted);
        }).Sum();

        return 1.0 - residualSumOfSquares / totalSumOfSquares;
    }
}
