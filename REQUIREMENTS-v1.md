# DSA Visualizer

## What was asked

> "I want to build an application which helps me to visualise how it works based on my
> code — like when debugging or running. And I want to know the time complexity and how
> the calculation is happening, and based on the code how the memory is assigned and
> allocated — how each declaration and operation affects these two things."

Three requests in one sentence: **see the code run**, **see how the complexity is
derived**, and **see how memory is allocated** — each attributed to the line that causes
it. The first specification of this project captured the third only as "bytes per
statement" and dropped the derivation entirely. This version restores all three.

## Problem

When practising data structures and algorithms, complexity arrives as a **verdict** —
"this is O(n²)" — rather than a **derivation**. There is no way to see which line
produced the cost, why a one-line library call can be the most expensive line in the
file, or how a particular declaration affects time and memory. Complexity stays
memorised instead of predictable.

## Users

One person practising DSA problems in C# inside VS Code, alongside a Claude chat that
supplies problems in rotation — two pointers and sliding windows, binary search, trees and
graphs, heaps, dynamic programming — and reviews solutions. This tool fills the gap in
that loop, after a solution is written. Solutions are written in LeetCode's own C#
template.

## In scope

- A **VS Code extension**, entirely local, over a **C# engine** that instruments the
  solution with Roslyn, runs it at a ladder of input sizes, and reports what happened.
- **Time**: measured growth, a reading of the code, and the derivation between them.
- **Space**: allocation, largest single allocation, call depth, and a reading of the code.
- **Step-through**: a recorded run at a small size, one statement at a time.
- **Comparison** of two solutions, on time and on memory.

## Non-goals

- No hosting, accounts or sharing. It runs locally, which also means a solution is never
  executed on anyone else's machine.
- No Python, Java or C++. C# is the language practised in; the engine is C#-specific.
- No problem catalogue. Problems come from the Claude chat.
- Not a debugger. No breakpoints or expression evaluation — the step-through replays a
  recorded run rather than pausing a live one.

## Acceptance criteria

Each is objectively checkable by someone who was not part of the conversation that
produced it, and each names the tests that check it.

1. **One command, real code.** A single VS Code command measures a solution with no
   boilerplate in the file, including LeetCode's `public class Solution` template.
   — `LeetCodeTests`; integration: *the measure command runs a LeetCode solution end to end*.
2. **Per-statement cost.** Every executed statement reports how often it ran, the work
   hidden in library calls it made, and the bytes it allocated, with its snippet, line
   and column. Line totals declare how many statements they sum.
   — `MeasurementTests`, `HiddenCostTests`.
3. **Inputs configured, not coded.** Each parameter gets the arrangements that apply to
   it (Skewed for a tree, never for an array); fixed values are editable; sizes are
   optional and adapt to the algorithm. — `ParameterBinderTests`, `MeasurerTests`.
4. **The practice topics' inputs.** Arrays, strings, lists, `char[][]` grids, LeetCode's
   `TreeNode` and `ListNode`, graphs as `(int n, int[][] edges)` and as adjacency lists,
   and scalars can all be generated. — `LeetCodeTests`, `InputGeneratorTests`.
5. **Growth chart.** Operations against n, including 10, 100 and 1000, on linear and
   log-log axes. — `MeasurerTests`; panel smoke tests.
6. **Two readings of time.** A class fitted from measurement and a separately labelled
   reading of the code; where they disagree, both are shown with an explanation.
   — `MeasurerTests`, `StaticEstimatorTests`, `StaticRecursionTests`.
7. **Hidden cost counted.** Library calls with known costs — `List.Contains` O(n),
   `RemoveAt(0)` O(n) but `RemoveAt(Count − 1)` O(1), `string +=` O(n), sorting
   O(n log n), heap operations O(log n), `new T[n]` O(n) — are charged as they execute,
   so a trap and its fix measure as different classes. — `HiddenCostTests`.
8. **The derivation.** Each statement carries its own growth class and formula, and a
   summary names the statement that sets the method's class — bubble sort's comparison
   runs ≈ 0.5·n² times. — `DerivationTests`.
9. **Space.** Total allocation, largest single allocation and call depth are each
   fitted; churn (O(n²) allocated, O(n) held) is told apart from data that is kept; a
   reading of the code gives the worst-case answer. — `SpaceTests`, `StaticSpaceTests`.
10. **Step-through.** Every variable that is in scope and definitely assigned is shown at
    every step, forward and back. Arrays and strings are drawn with index variables as
    pointers, grids with the current cell; the editor follows the current line.
    — `StepThroughTests`; panel smoke tests; integration: *the step view records a run*.
11. **Comparison.** Two solutions over the same sizes and data, on time or memory.
    — `JobRunnerTests`; panel smoke tests.
12. **Nothing hangs or crashes.** Infinite loops stop at the statement budget, a single
    endless statement at the wall-clock limit, runaway recursion at the depth limit — each
    with a readable explanation, and the editor never waits forever. — `RobustnessTests`,
    `BudgetTests`; integration: *a wrong engine path is reported, not hung on*.
13. **Instrumentation does not alter behaviour**, in the measurement build and the step
    build alike. This protects every other number: a measurement of a changed program
    describes code nobody wrote. — `DifferentialTests`, `BehaviourPreservedTests`,
    `StepThroughTests`.
14. **Errors point at the user's own lines**, not at the instrumented copy.
    — `RobustnessTests`.

## Status

All fourteen criteria are met. 251 engine tests, 19 extension tests including panel
smoke tests that render real engine output and click every control, and 4 integration
checks that drive the real command inside a running VS Code.

Two of the safety nets were tested by breaking what they guard: disabling brace
normalisation (M1) and corrupting a charging wrapper (Phase A) each made exactly the
tests that should fail, fail.

## Known limitations

Recorded so that nothing here is mistaken for more than it is.

- **The reading of the code is a heuristic.** It recognises loops, two-pointer
  amortisation, and five kinds of recursion — structural, memoised, visited-marking,
  divide and conquer, branching. Outside those it says *unknown* with a reason rather
  than guessing. A BFS is reported as amortised O(V + E) in words, not as a class.
- **Library cost is charged only for known members**, and a lazy LINQ chain has no size
  until it is enumerated, so work inside one is under-counted. The reading of the code
  still flags it.
- **Memory is exact on a single thread.** A garbage collection set off by *another*
  thread can add up to about 8 KB to whichever statement is running. The engine runs
  alone in its own process, where that does not occur; small runs that see a collection
  are repeated and the cleanest reading kept, and the panel states the caveat.
- **Allocation, not retention.** A temporary that is immediately garbage still counts.
  "Likely peak" is inferred from allocation, largest single allocation and depth; the
  engine does not observe live heap size directly.
- **Limits.** Recursion stops at 20,000 frames, a step-through keeps 2,000 steps, a run
  is stopped after 30 seconds, and a whole measurement after 120 by default.
- **Distinct** is not offered for strings: a 26-letter alphabet cannot supply unique
  characters beyond 26.

## Constraints

- Requires the .NET SDK (8 or later) on the machine.
- Local only — no network service.
- VS Code extension in TypeScript; engine as a separate .NET process speaking JSON.
