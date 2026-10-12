# DSA Visualizer — VS Code extension

Shows what each statement of a C# solution costs, and how that cost grows.

## Running it

Open the **repository root** in VS Code — not this folder. The extension looks for the
engine at `engine/DsaViz.Engine/bin/Debug/net8.0/` relative to the open workspace, so
opening `extension/` on its own leaves it unable to find the engine. (Set
`dsaViz.enginePath` if you keep the engine somewhere else.)

```
code /path/to/dsa-visualizer
```

Install the extension's dev dependencies once:

```
cd extension && npm install
```

Then press **F5** and pick **Run DSA Visualizer**. The bundled launch configuration
builds the engine, compiles the extension, and opens an Extension Development Host with
the repository already loaded.

In that new window, open `samples/SortParamSample.cs` and run **DSA Visualizer: Measure
Current File** from the command palette.

## Where the results appear

**On your code.** Every executed line is annotated in the editor itself with its
execution count and the bytes it allocated, and shaded by how hot it is. Hovering a
line gives the per-statement breakdown — which matters on a line like
`if (a[j] > a[j+1]) swap;`, where the total is a sum of the test and the work.

Editing the file clears the annotations. Once a line moves, the number beside it is no
longer about that line, and a stale annotation is worse than none because it still
looks authoritative. **DSA Visualizer: Clear Cost Annotations** removes them by hand.

The shading is logarithmic. Bubble sort's inner line runs 749,625 times while its outer
loop runs once; scaled linearly the outer loop would shade at 0.0000013 and read as
never executed, which is the opposite of what the shading is for.

## The panel

**Statement cost** ranks the most expensive statements, with the count, the memory and
where each one sits. Clicking a row jumps to it. The annotated file is the detailed
view; this is the summary that tells you where to look.

**Growth** plots statements executed against input size, with the fitted curve. The
log-log toggle is worth reaching for: every complexity class is a straight line there
and the slope is the exponent, which separates neighbouring classes far better than
linear axes do.

**Comparison** puts two solutions on one chart, measured over the same sizes and the
same generated data, so the gap between the curves is the algorithms and nothing else.

## Step through

The **Step through** tab records a run at a small size — n = 8 unless you change it — and
replays it one statement at a time. Every variable that is in scope and has a value is
shown at every step; changed values are highlighted. Arrays and strings are drawn as
cells, with index variables (`i`, `j`, `left`, `right`, `mid`, `lo`, `hi`, `slow`, `fast`
and the like) drawn as pointers under the cell they point at; a grid shows the cell at
`[r][c]`. The editor highlights the line the step is on as you scrub. ← and → step.

Recording uses a separate build, made only when the tab is opened, so measuring is never
slowed down by it.

## Tests

`npm test` runs unit tests and smoke tests that load the panel's script into a DOM, feed
it real engine output, and click every tab and control. `npm run test:integration`
launches the installed VS Code and drives the real command against the real engine.

## Input arrangement

The arrangement of the generated data often decides the complexity you observe, so it
is a control rather than a detail. A search that stops at the first duplicate is O(1)
on `AllDuplicates` and O(n²) on `Distinct` — same code, same sizes. `Random` draws from
a bounded range and therefore repeats, which is why `Distinct` exists for asking about
the worst case explicitly.
