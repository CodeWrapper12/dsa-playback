using System.Globalization;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Reads a design problem's measured runs per operation: what one call to each method
/// cost on average at each size, how that grows, and the line in the method that makes it
/// grow. A cache whose <c>put</c> scans a list is O(n) per call however cheap <c>get</c> is,
/// and the two have to be told apart — their total alone would only say O(n²) for n calls.
/// </summary>
public static class DesignGrowthReader
{
    /// <summary>
    /// A cost that grows by less than this share over the sizes measured is flat: per-call
    /// averages wobble with the mix of hits and misses, and a fitted curve through that
    /// wobble is not a complexity class.
    /// </summary>
    private const double FlatShare = 0.2;

    /// <summary>Averages are noisier than counts; a growing shape needs at least this fit to be named.</summary>
    private const double MinimumRSquared = 0.9;

    private const int MinimumPoints = 3;

    /// <summary>A simpler class is preferred when it fits within this much of the best.</summary>
    private const double SimplerWithin = 0.01;

    public static DesignGrowth Read(IReadOnlyList<MeasuredRun> runs, DesignWorkload workload, Func<int, string?> methodOf)
    {
        var operations = workload.Calls.Distinct(StringComparer.Ordinal).Select(name => Operation(name, runs, workload, methodOf)).ToList();
        return new DesignGrowth { Workload = workload.Describe(), Operations = operations };
    }

    private static OperationGrowth Operation(string name, IReadOnlyList<MeasuredRun> runs, DesignWorkload workload, Func<int, string?> methodOf)
    {
        var points = new List<OperationPoint>();
        var used = new List<MeasuredRun>();
        foreach (var run in runs)
        {
            if (run.Trace.CallCosts is not { } costs || costs.Count != run.Size + 1) continue;
            var mine = CallsTo(name, run.Size, workload).Select(i => costs[i]).ToList();
            if (mine.Count == 0) continue;
            points.Add(new OperationPoint(run.Size, mine.Count, mine.Average(), mine.Max()));
            used.Add(run);
        }

        var fit = PerCall(points.Select(p => p.Size).ToList(), points.Select(p => p.Average).ToList());
        var (because, line) = Growth.Rank(fit.Class) >= 1
            ? Because(name, fit.Class, used, points, methodOf, workload.Calls.Append(workload.ClassName).ToList())
            : (null, null);

        return new OperationGrowth
        {
            Name = name,
            PerCall = fit.Class,
            Formula = fit.Formula,
            Plain = Plain(fit.Class),
            Because = because,
            Line = line,
            Points = points
        };
    }

    /// <summary>The numbers of the calls, constructor 0, that the workload makes to <paramref name="name"/>.</summary>
    private static IEnumerable<int> CallsTo(string name, int size, DesignWorkload workload)
    {
        for (int i = 1; i <= size; i++)
            if (workload.Calls[(i - 1) % workload.Calls.Count] == name) yield return i;
    }

    public sealed record PerCallFit(ComplexityClass Class, string? Formula);

    private static readonly (ComplexityClass Class, Func<double, double> Basis)[] Growing =
    {
        (ComplexityClass.Logarithmic, n => n < 2 ? 1.0 : Math.Log2(n)),
        (ComplexityClass.Linear, n => n),
        (ComplexityClass.Linearithmic, n => n < 2 ? 1.0 : n * Math.Log2(n)),
        (ComplexityClass.Quadratic, n => n * n)
    };

    /// <summary>
    /// How an average cost per call grows. Fitted with an intercept — every call has a fixed
    /// part, the calling itself and its first lines — so a growing part is found on top of it,
    /// and a cost that barely moves is called constant rather than given the best-fitting curve.
    /// </summary>
    public static PerCallFit PerCall(IReadOnlyList<int> sizes, IReadOnlyList<double> averages)
    {
        if (sizes.Count < MinimumPoints) return new PerCallFit(ComplexityClass.Unknown, null);

        double highest = averages.Max();
        if (highest <= 0) return new PerCallFit(ComplexityClass.Constant, "≈ 0 operations per call");

        var x = sizes.Select(s => (double)s).ToArray();
        var fits = new List<(ComplexityClass Class, double R2, double Slope)>();
        foreach (var (cls, basis) in Growing)
        {
            var (r2, slope) = Regress(x.Select(basis).ToArray(), averages);
            if (slope > 0) fits.Add((cls, r2, slope));
        }
        // Neighbouring classes fit averages almost equally over two orders of magnitude —
        // n and n log n differ by a slowly growing factor — so the simpler one is named
        // unless the other fits clearly better.
        (ComplexityClass Class, double R2, double Slope)? best = null;
        if (fits.Count > 0)
        {
            double top = fits.Max(f => f.R2);
            best = fits.Where(f => f.R2 >= top - SimplerWithin).OrderBy(f => Growth.Rank(f.Class)).First();
        }

        double growth = 0;
        if (best is { } found)
        {
            var basis = Growing.First(g => g.Class == found.Class).Basis;
            growth = found.Slope * (basis(x.Max()) - basis(x.Min())) / highest;
        }

        if (best is null || growth < FlatShare)
        {
            double spread = (highest - averages.Min()) / highest;
            return spread < 0.5
                ? new PerCallFit(ComplexityClass.Constant, $"≈ {Number(averages.Average())} {(Number(averages.Average()) == "1" ? "operation" : "operations")} per call")
                : new PerCallFit(ComplexityClass.Unknown, null);
        }

        if (best.Value.R2 < MinimumRSquared) return new PerCallFit(ComplexityClass.Unknown, null);

        string term = best.Value.Class switch
        {
            ComplexityClass.Logarithmic => "log n",
            ComplexityClass.Linear => "n",
            ComplexityClass.Linearithmic => "n log n",
            _ => "n²"
        };
        return new PerCallFit(best.Value.Class, $"≈ {Significant(best.Value.Slope)}·{term} per call");
    }

    private static (double R2, double Slope) Regress(double[] x, IReadOnlyList<double> y)
    {
        double meanX = x.Average(), meanY = y.Average();
        double covariance = 0, varianceX = 0, total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            covariance += (x[i] - meanX) * (y[i] - meanY);
            varianceX += (x[i] - meanX) * (x[i] - meanX);
            total += (y[i] - meanY) * (y[i] - meanY);
        }
        if (varianceX == 0) return (double.NegativeInfinity, 0);
        double slope = covariance / varianceX;
        if (total == 0) return (1, slope);
        double intercept = meanY - slope * meanX, residual = 0;
        for (int i = 0; i < x.Length; i++) residual += Math.Pow(y[i] - intercept - slope * x[i], 2);
        return (1 - residual / total, slope);
    }

    private static string Plain(ComplexityClass perCall) => perCall switch
    {
        ComplexityClass.Constant =>
            "One call costs about the same however many calls came before it: O(1) per call.",
        ComplexityClass.Logarithmic =>
            "One call costs a little more as the object grows — about one step more each time n doubles: O(log n) per call.",
        ComplexityClass.Linear =>
            "One call costs more the bigger the object is: when n doubles, so does the cost of each call. O(n) per call.",
        ComplexityClass.Linearithmic =>
            "When n doubles, one call costs a little more than twice as much: O(n log n) per call.",
        ComplexityClass.Quadratic =>
            "When n doubles, one call costs about four times as much: O(n²) per call.",
        _ => "The cost of one call did not follow a standard shape over these sizes, so no class is named."
    };

    /// <summary>
    /// The statement in the operation's own method whose cost per call grows the same way the
    /// call's does, and how: a library call that scans, or a line that runs n times a call.
    /// </summary>
    private static (string?, int?) Because(string name, ComplexityClass target, IReadOnlyList<MeasuredRun> runs,
        IReadOnlyList<OperationPoint> points, Func<int, string?> methodOf, IReadOnlyList<string> operations)
    {
        if (runs.Count == 0) return (null, null);
        var last = runs[^1].Trace;

        // The operation's own lines first; failing those, a helper it calls — a cache's
        // "touch", shared by get and put — whose line then carries the cost.
        var own = last.Statements.Where(s => SameMethod(methodOf(s.Id), name)).ToList();
        var found = Explaining(own, target, runs, points);
        string? helper = null;
        if (found is null)
        {
            var helpers = last.Statements.Where(s => methodOf(s.Id) is { Length: > 0 } m && !operations.Any(o => SameMethod(m, o))).ToList();
            found = Explaining(helpers, target, runs, points);
            if (found is { } h) helper = Short(methodOf(h.Statement.Id)!);
        }
        if (found is not { } chosen) return (null, null);

        var s = chosen.Statement;
        string where = helper is null ? $"on line {s.Line}" : $"on line {s.Line}, in {helper}, which it calls";
        if (s.LibraryWork > s.Hits &&
            last.LibraryCalls.Where(c => c.Line == s.Line).OrderByDescending(c => c.Work).FirstOrDefault() is { } call)
            return ($"`{s.Snippet}` {where}: {call.Member} is {call.Cost} per call", s.Line);

        string runsFormula = chosen.Fit.Formula?.Replace(" per call", "") ?? "more";
        return ($"`{s.Snippet}` {where} runs {runsFormula} times per call", s.Line);
    }

    /// <summary>The statement among <paramref name="candidates"/> whose cost per call grows as the call's does, the costliest.</summary>
    private static (StatementCost Statement, PerCallFit Fit, double PerCall)? Explaining(IReadOnlyList<StatementCost> candidates,
        ComplexityClass target, IReadOnlyList<MeasuredRun> runs, IReadOnlyList<OperationPoint> points)
    {
        (StatementCost Statement, PerCallFit Fit, double PerCall)? best = null;
        foreach (var statement in candidates)
        {
            var series = runs.Select((run, i) =>
            {
                var at = run.Trace.Statements.FirstOrDefault(s => s.Id == statement.Id);
                return at is null ? 0 : (at.Hits + at.LibraryWork) / (double)points[i].Calls;
            }).ToList();
            var fit = PerCall(runs.Select(r => r.Size).ToList(), series);
            if (fit.Class != target) continue;
            if (best is null || series[^1] > best.Value.PerCall) best = (statement, fit, series[^1]);
        }
        return best;
    }

    private static string Short(string method)
    {
        int cut = Math.Max(method.LastIndexOf('.'), method.LastIndexOf(':'));
        return cut >= 0 ? method[(cut + 1)..] : method;
    }

    /// <summary>Whether a statement's method is the operation: "LRUCache.Put", "put" and "LRUCache::put" all are "put".</summary>
    private static bool SameMethod(string? method, string operation)
    {
        if (string.IsNullOrEmpty(method)) return false;
        int cut = Math.Max(method.LastIndexOf('.'), method.LastIndexOf(':'));
        string last = cut >= 0 ? method[(cut + 1)..] : method;
        return string.Equals(last.Trim(), operation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two significant figures, so a small slope reads 0.004 rather than 0.</summary>
    private static string Significant(double value)
    {
        if (value >= 10) return Number(value);
        int decimals = Math.Clamp(1 - (int)Math.Floor(Math.Log10(Math.Abs(value))), 1, 6);
        return Math.Round(value, decimals).ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    private static string Number(double value)
        => value.ToString(Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
}
