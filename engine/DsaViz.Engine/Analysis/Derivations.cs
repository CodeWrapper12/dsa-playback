using System.Globalization;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Analysis;

/// <summary>One measured size, with the full per-statement trace it produced.</summary>
public sealed record MeasuredRun(int Size, TraceResult Trace);

/// <summary>
/// Builds the derivation — which statements make the method the class it is, and by what
/// formula — and the space reading, from the per-size traces of one measurement.
/// </summary>
public static class Derivations
{
    private const int MinimumRuns = 3;

    public static Derivation? Explain(IReadOnlyList<MeasuredRun> runs, CurveFit overall)
    {
        if (runs.Count < MinimumRuns) return null;

        var sizes = runs.Select(r => r.Size).ToList();
        var ids = runs.SelectMany(r => r.Trace.Statements).Select(s => s.Id).Distinct().ToList();

        var growth = ids.Select(id => GrowthOf(id, runs, sizes)).OrderBy(g => g.Line).ThenBy(g => g.Column).ToList();
        if (growth.Count == 0) return null;

        // The statements that set the method's class. If the overall fit named a class,
        // they are the ones growing at that class; otherwise the fastest-growing ones.
        int target = overall.Best != ComplexityClass.Unknown
            ? Growth.Rank(overall.Best)
            : growth.Max(g => Growth.Rank(g.Cost));

        var dominant = growth.Where(g => Growth.Rank(g.Cost) == target)
            .OrderByDescending(g => g.Costs[^1])
            .ToList();
        if (dominant.Count == 0)
            dominant = growth.OrderByDescending(g => Growth.Rank(g.Cost)).ThenByDescending(g => g.Costs[^1]).Take(1).ToList();

        return new Derivation(Summarise(overall, dominant, growth), dominant.Select(g => g.Id).ToList(), growth);
    }

    private static StatementGrowth GrowthOf(int id, IReadOnlyList<MeasuredRun> runs, List<int> sizes)
    {
        var at = runs.Select(r => r.Trace.Statements.FirstOrDefault(s => s.Id == id)).ToList();
        var known = at.First(s => s is not null)!;

        var hits = at.Select(s => s?.Hits ?? 0).ToList();
        var costs = at.Select(s => s is null ? 0 : s.Hits + s.LibraryWork).ToList();
        var bytes = at.Select(s => s?.BytesAllocated ?? 0).ToList();
        var largest = at.Select(s => s?.LargestAllocation ?? 0).ToList();

        var runsFit = Growth.Fit(sizes, hits);
        var costFit = Growth.Fit(sizes, costs);
        var memoryFit = Growth.Fit(sizes, bytes);
        var largestFit = Growth.Fit(sizes, largest);

        // When a statement costs more than it runs, the difference was spent inside a
        // library call on that line, and naming the call is the explanation.
        string? because = null;
        if (Growth.Rank(costFit.Best) > Growth.Rank(runsFit.Best))
        {
            var call = runs[^1].Trace.LibraryCalls
                .Where(c => c.Line == known.Line)
                .OrderByDescending(c => c.Work)
                .FirstOrDefault();
            if (call is not null) because = $"{call.Member} is {call.Cost} per call";
        }

        return new StatementGrowth
        {
            Id = id,
            Line = known.Line,
            Column = known.Column,
            Snippet = known.Snippet,
            Runs = runsFit.Best,
            RunsFormula = Growth.Formula(runsFit),
            Cost = costFit.Best,
            CostFormula = Growth.Formula(costFit),
            Memory = bytes.All(b => b == 0) ? ComplexityClass.Constant : memoryFit.Best,
            MemoryFormula = bytes.All(b => b == 0) ? null : Growth.Formula(memoryFit, " bytes"),
            LargestAllocation = largest.All(b => b == 0) ? ComplexityClass.Constant : largestFit.Best,
            Because = because,
            Costs = costs
        };
    }

    private static string Summarise(CurveFit overall, List<StatementGrowth> dominant, List<StatementGrowth> all)
    {
        var lead = dominant[0];
        string label = overall.Best != ComplexityClass.Unknown ? ComplexityNames.Format(overall.Best) : ComplexityNames.Format(lead.Cost);

        string what = lead.Because is not null
            ? $"`{lead.Snippet}` on line {lead.Line} runs {lead.RunsFormula ?? "repeatedly"} times, and each run hides more " +
              $"work than one statement — {lead.Because} — so its total cost is {lead.CostFormula}"
            : $"`{lead.Snippet}` on line {lead.Line} runs {lead.CostFormula} times";

        string others = "";
        if (dominant.Count > 1)
            others = $" {Plural(dominant.Count - 1, "other statement")} on " +
                     string.Join(", ", dominant.Skip(1).Select(d => $"line {d.Line}").Distinct()) +
                     (dominant.Count == 2 ? " grows" : " grow") + " the same way.";

        // The usual bound for branching recursion is 2ⁿ, but the measured base is often
        // smaller — naive Fibonacci grows as about 1.618ⁿ. Saying "O(2ⁿ) because ≈ 1.63ⁿ"
        // without comment reads like a contradiction, so the gap is named.
        string bound = "";
        if (overall.Best == ComplexityClass.Exponential && overall.Ranked.FirstOrDefault(f => f.Class == ComplexityClass.Exponential) is { } exp
            && Math.Abs(exp.Coefficient - 2) > 0.05)
            bound = $" O(2ⁿ) is the usual bound for this shape; the measured growth is {exp.Coefficient.ToString("0.00", CultureInfo.InvariantCulture)}ⁿ, " +
                    "slower, because the branches are not equally deep.";

        var slower = all.Where(g => !dominant.Contains(g) && g.Cost != ComplexityClass.Unknown).ToList();
        string rest = slower.Count == 0
            ? ""
            : $" Everything else grows as {ComplexityNames.Format(slower.MaxBy(g => Growth.Rank(g.Cost))!.Cost)} or slower, " +
              "so it stops mattering as n grows.";

        return $"{label} because {what}.{others}{rest}{bound}";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ---- space ------------------------------------------------------------------

    public static SpaceReading? Space(IReadOnlyList<MeasuredRun> runs, Derivation? derivation, StaticEstimate staticSpace)
    {
        if (runs.Count < MinimumRuns) return null;

        var sizes = runs.Select(r => r.Size).ToList();
        // Allocation is fitted through the origin, like operation counts. Collections grow
        // by doubling, so their allocation rises in steps, and with an intercept free to
        // move, n log n fits those steps marginally better than n — a false finding.
        // Call depth is the series that needs the intercept: it is small, offset by a
        // constant, and log n + 1 cannot be fitted through zero at all.
        var allocation = Growth.Fit(sizes, runs.Select(r => r.Trace.TotalBytesAllocated).ToList());
        var largest = Growth.Fit(sizes, runs.Select(r => r.Trace.Statements.Select(s => s.LargestAllocation).DefaultIfEmpty(0).Max()).ToList());
        var depth = Growth.Fit(sizes, runs.Select(r => (long)r.Trace.MaxRecursionDepth).ToList(), allowIntercept: true);

        bool allocates = runs.Any(r => r.Trace.TotalBytesAllocated > 0);
        bool recurses = runs.Any(r => r.Trace.MaxRecursionDepth > 1);

        // Where the peak was measured directly (Python), it is the answer for the heap, and
        // nothing has to be inferred from allocation. A peak that barely moves across the
        // whole range is a fixed amount, however a curve would like to fit it.
        CurveFit? held = null;
        if (runs.All(r => r.Trace.PeakBytes is not null))
        {
            var peaks = runs.Select(r => r.Trace.PeakBytes!.Value).ToList();
            held = peaks.Max() - peaks.Min() < FixedHeldGrowth
                ? new CurveFit(ComplexityClass.Constant, 1, Array.Empty<ClassFit>(), "Barely changes across the sizes measured.")
                : Growth.Fit(sizes, peaks, allowIntercept: true);
        }

        // Allocation that grows faster than its largest single piece is churn: buffers
        // created and dropped again. What is held at once is then bounded by the largest
        // piece, not by the total. Allocation that grows no faster than its largest piece
        // is data being kept, and the total is the space.
        bool churn = allocates && Growth.Rank(allocation.Best) > Growth.Rank(largest.Best) && largest.Best != ComplexityClass.Unknown;
        var heap = held is not null ? held.Best : !allocates ? ComplexityClass.Constant : churn ? largest.Best : allocation.Best;
        var stack = recurses ? depth.Best : ComplexityClass.Constant;
        var peak = heap == ComplexityClass.Unknown || stack == ComplexityClass.Unknown
            ? ComplexityClass.Unknown
            : Growth.Max(heap, stack);

        var lines = new List<string>();
        int n = sizes[^1];
        var last = runs[^1].Trace;

        if (held is not null)
        {
            long most = last.PeakBytes ?? 0;
            lines.Add(held.Best == ComplexityClass.Constant
                ? $"Holds only a fixed amount at once ({Bytes(most)} at most), whatever the input size — measured directly."
                : $"Holds at most {Bytes(most)} at once at n={N(n)}, growing as {ComplexityNames.Format(held.Best)} — measured directly, " +
                  "not inferred from what was allocated.");
            if (allocates)
                lines.Add($"It allocates {Bytes(last.TotalBytesAllocated)} over the run in all; Python creates objects even for " +
                          "arithmetic on large numbers, so much of that is short-lived.");
        }
        else if (!allocates)
            lines.Add("Nothing allocated grows with the input.");
        else if (allocation.Best == ComplexityClass.Constant)
            lines.Add($"Allocates only a fixed amount ({Bytes(last.TotalBytesAllocated)}), whatever the input size.");
        else if (churn)
        {
            var culprit = derivation?.Statements
                .Where(g => g.Memory == allocation.Best)
                .OrderByDescending(g => Growth.Rank(g.Runs))
                .FirstOrDefault();
            string where = culprit is null ? "" :
                $" `{culprit.Snippet}` on line {culprit.Line} runs {culprit.RunsFormula} times and allocates fresh memory each time.";
            lines.Add($"Allocates {ComplexityNames.Format(allocation.Best)} in total, but never more than " +
                      $"{ComplexityNames.Format(largest.Best)} at once.{where} What is held at any moment is likely " +
                      $"{ComplexityNames.Format(largest.Best)}; allocating once, outside the loop, would bring the total down to match.");
        }
        else
            lines.Add($"Allocates {ComplexityNames.Format(allocation.Best)} ({Bytes(last.TotalBytesAllocated)} at n={N(n)}), " +
                      "growing as fast as its largest single allocation — this is data that is kept, so it is the space.");

        if (recurses)
            lines.Add($"The call stack grows as {ComplexityNames.Format(depth.Best)}: {N(last.MaxRecursionDepth)} frames deep at n={N(n)}.");
        else
            lines.Add("The call stack stays flat.");

        string summary = $"Likely peak space: {ComplexityNames.Format(peak)}. " + string.Join(" ", lines);

        bool disagree = peak != ComplexityClass.Unknown && staticSpace.Class != ComplexityClass.Unknown && peak != staticSpace.Class;
        return new SpaceReading
        {
            Held = held,
            Allocation = allocation,
            LargestAllocation = largest,
            CallDepth = depth,
            LikelyPeak = peak,
            Summary = summary,
            Static = staticSpace,
            Disagreement = disagree,
            DisagreementNote = disagree
                ? $"The code reads as {ComplexityNames.Format(staticSpace.Class)} space ({staticSpace.Reasoning}) but measures as " +
                  $"{ComplexityNames.Format(peak)}. The measurement reflects this input's shape — a tree's depth, how full a " +
                  "structure actually got — while the reading from the code assumes the worst case."
                : null
        };
    }

    /// <summary>A measured peak that moves less than this over the whole range of sizes is a fixed amount.</summary>
    private const long FixedHeldGrowth = 1024;

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Bytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{(value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
    };
}
