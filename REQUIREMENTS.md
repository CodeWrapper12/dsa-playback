# DSA Visualizer

## Problem

When practising data-structures and algorithms, complexity arrives as a **verdict** — "this is O(n²)" — rather than a **derivation**. There is no way to see which line produced the cost, or how a particular declaration or operation affected time and memory.

The consequence is that complexity stays memorised instead of predictable: the next unfamiliar problem still requires someone else to supply the answer. The need is to make the causal link between a written line and its cost directly visible, so complexity becomes something derived rather than recalled.

## Users

A single user, practising DSA problems in C# inside VS Code, alongside an existing Claude chat workflow that supplies problems and reviews solutions. This tool does not replace that loop — it fills the gap in it, after a solution is written.

## In scope (v1)

- A **VS Code extension**, running entirely locally
- **C# solutions**
- **Both** complexity methods, shown side by side: empirical measurement and a static loop-structure estimate
- Four views over one shared trace:
  1. **Per-line cost table** — execution count and memory allocated, per line
  2. **Growth curve** — operations vs input size, with the fitted complexity class
  3. **Step-through** — local variable state at each executed step, navigable both directions
  4. **Comparison** — two solutions plotted on one chart

## Non-goals

Explicitly excluded, to keep v1 finishable:

- **No VPS, hosting, or multi-user access.** Dropped deliberately; the extension runs locally, which also removes the arbitrary-code-execution risk that a public instance would have created.
- **No Python engine in v1.** Python is phase 2, behind the same UI.
- **No Java or C++.**
- **No problem catalogue or scheduling.** Problems come from the existing Claude chat.
- **No accounts, auth, or sharing.**
- **Not a replacement for the debugger.** This measures cost; it does not do breakpoints or expression evaluation.

## Acceptance criteria

Each is objectively checkable by someone who was not part of this conversation.

1. Given a C# file containing a solution method, a single VS Code command produces a report without the user leaving the editor. No generator method or boilerplate is required in the solution file.
2. The report shows, for every executed **statement**, its execution count and the bytes it allocated, with the source snippet, line, and column. Per-line totals are also given, each carrying the number of statements it sums so a shared line is never reported ambiguously.
2b. Inputs are supplied through a **configuration panel**, not code: the user selects an input shape (integer array, string, matrix, tree, linked list, graph), a generation strategy (random, sorted, reverse-sorted, all-duplicates, nearly-sorted), a value range, and the set of input sizes to measure. Changing the strategy and re-running produces different measured curves for the same solution.
3. Running at n ∈ {10, 100, 1000} produces a chart of operation count against n.
4. The report shows a complexity class fitted from measured data **and** a separately labelled static estimate derived from loop structure. Where the two disagree, both are shown and the disagreement is flagged rather than silently resolved.
5. Step-through displays the values of declared locals at each executed step and can move forward and backward through them.
6. Two solution files can be run over the same input sizes and their growth curves drawn on one chart.
7. A solution containing an infinite loop is terminated by a timeout and reported as timed out — VS Code does not hang.
8. **Instrumentation does not alter behaviour**: for a set of sample solutions, instrumented and uninstrumented runs produce identical output.

Criterion 8 is the one that protects every other number in the report. If instrumentation changes what the program does, all measurements are describing a program that does not exist.

## Status

Verified against the engine as of M2. UNVERIFIED is used where a criterion's data
exists but the interface that presents it does not, so nothing is claimed that has
not actually been demonstrated.

| # | Criterion | Status |
|---|-----------|--------|
| 1 | Single VS Code command produces a report | Not attempted (M3) |
| 2 | Per-statement execution count and bytes | Met |
| 2b | Inputs configured rather than coded | Met in the engine; the panel is M3 |
| 3 | Chart across input sizes | Data met; the chart is M3 |
| 4 | Measured class and static estimate, disagreement flagged | Met |
| 5 | Step-through of local state | Not attempted (M4) |
| 6 | Two solutions compared on one chart | Data met; the chart is M3 |
| 7 | Infinite loop terminated, editor survives | Met |
| 8 | Instrumentation does not alter behaviour | Met |

## Constraints

- **C# is the primary language** — it is what the user writes DSA solutions in day to day. The C# engine is the product; Python is a possible later addition, not a near-term commitment.
- Requires the .NET SDK on the machine (Roslyn is used to instrument and compile)
- Local only — no network service
- VS Code extension in TypeScript; tracing engine as a separate .NET process communicating over JSON
- The webview UI is written as an ordinary web page so it can be developed in a browser and later hosted unchanged inside the extension

## Open questions and assumptions

Recorded rather than resolved, so the choices are visible and can be revisited.

1. **RESOLVED — inputs come from a configuration panel, not from user-written code.** The engine generates inputs from a declared shape and strategy and binds them to the solution's parameters by type. Remaining sub-question: what happens when a signature is ambiguous or unsupported (for example a custom class parameter). Assumption: the tool reports the signature as unsupported and names the shapes it can bind, rather than guessing.
2. **RESOLVED — memory attribution is exact, not approximate.** The earlier assumption that a garbage collection could shift bytes between statements was wrong: `GC.GetAllocatedBytesForCurrentThread()` is a monotonic cumulative counter of bytes *allocated*, which a collection cannot decrease. Measured against known object sizes it reports `new int[100]` as exactly 424 bytes and `new int[1000]` as exactly 4,024, identically across repeated runs.

   The honest caveats are about scope rather than precision, and the engine ships them in `MemoryModel` for the UI to display verbatim: the figures count **allocation, not retention** (a temporary that is immediately garbage still costs), and cover **the executing thread's GC heap only** — stack allocations and any work the solution moves onto other threads are not counted.
3. **RESOLVED — statements sharing a line are measured separately.** Cost is attributed per statement rather than per line, so `if (a[j] > a[j+1]) { swap; }` reports the comparison and the swap as two entries of 1,770 rather than one ambiguous total of 3,540. Line totals remain available as a summed convenience and declare how many statements they cover.

4. **Step-through capture cost.** Serialising every local at every line is expensive at large n. Assumption: step-through runs at small n only (default ≤ 50) as a separate run from the measurement runs.
5. **Static estimate scope.** Assumption: a loop-nesting and recursion-depth heuristic over the Roslyn syntax tree. It will report "unknown" where it cannot tell, rather than guessing — a confident wrong answer is worse than no answer in a tool being learned from.
