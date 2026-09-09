# DSA Visualizer — VS Code extension

Shows what each statement of a C# solution costs, and how that cost grows.

## Running it

1. Build the engine:

   ```
   dotnet build engine/DsaViz.sln
   ```

2. Install the extension's dev dependencies and compile it:

   ```
   cd extension && npm install && npm run compile
   ```

3. Open the `extension` folder in VS Code and press **F5**. An Extension Development
   Host opens.

4. In that window, open a C# file with a `public static` method and run
   **DSA Visualizer: Measure Current File** from the command palette.

The extension finds the engine under `engine/DsaViz.Engine/bin/Debug/net8.0/` in the
open workspace. Set `dsaViz.enginePath` if it lives somewhere else.

## The three views

**Cost per line** lays execution counts and allocated bytes over your source. Bars are
logarithmic so a line that ran once stays visible beside one that ran half a million
times; the numbers are exact. A line holding more than one statement shows a count you
can expand — the line total is a sum, and the split is usually what you want to see.

**Growth** plots statements executed against input size, with the fitted curve. The
log-log toggle is worth reaching for: every complexity class is a straight line there
and the slope is the exponent, which separates neighbouring classes far better than
linear axes do.

**Comparison** puts two solutions on one chart, measured over the same sizes and the
same generated data, so the gap between the curves is the algorithms and nothing else.

## Input arrangement

The arrangement of the generated data often decides the complexity you observe, so it
is a control rather than a detail. A search that stops at the first duplicate is O(1)
on `AllDuplicates` and O(n²) on `Distinct` — same code, same sizes. `Random` draws from
a bounded range and therefore repeats, which is why `Distinct` exists for asking about
the worst case explicitly.
