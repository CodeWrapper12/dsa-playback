# DSA Visualizer

A VS Code extension that shows what each line of a C# solution costs — and why.

Write a LeetCode-style solution, run one command, and get:

- **Every line annotated in your editor** with how often it ran, its own complexity, any
  work hidden inside library calls, and the memory it allocated —
  `1,000×  O(n²)  ·  +499,500 library work`
- **The derivation**, not just the verdict — *"O(n²) because `if (values[j] > values[j + 1])`
  on line 10 runs ≈ 0.5·n² times"*
- **Space, measured** — total allocation, the largest single allocation, and call depth,
  so *"allocates O(n²) but never holds more than O(n)"* is something you can see
- **A step-through** of a small run, with index variables drawn as pointers into your
  arrays and the editor following along
- **Two readings of everything** — what the run measured, and what the code says — with
  the disagreements explained, because the gap is usually the lesson

## Run it

Needs the .NET 8 SDK or later, Node, and VS Code.

```
cd extension && npm install
```

Open this folder — the repository root — in VS Code, press **F5**, and pick **Run DSA
Visualizer**. In the window that opens, open any file under `samples/leetcode/` and run
**DSA Visualizer: Measure Current File**.

## Test it

```
dotnet test engine/DsaViz.sln
```

```
cd extension && npm test
```

```
cd extension && npm run test:integration
```

The last one opens VS Code for about a minute and drives the real command against the
real engine.

## Layout

| | |
|---|---|
| `engine/DsaViz.Runtime` | The tracer that instrumented code calls into |
| `engine/DsaViz.Engine` | Instrumentation, measurement, analysis, and the CLI the extension runs |
| `engine/DsaViz.Engine.Tests` | Engine tests |
| `extension/` | The VS Code extension and its panel |
| `samples/` | Solutions used as test fixtures — `samples/leetcode/` is written the way LeetCode is |
| `REQUIREMENTS.md` | What was asked for, what is met, and the known limitations |
