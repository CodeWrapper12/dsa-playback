# DSA Visualizer

A VS Code extension that shows what each line of a C# or Python solution costs — and why.

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

**Install it** (no .NET needed — the engine is inside the package):

```
cd extension && npm install && npm run package
```

```
code --install-extension extension/dist/dsa-visualizer-darwin-arm64.vsix
```

Then open any C# solution and press **▶ Visualize** above a method. Values from LeetCode
example comments (`// Input: nums = [2,7,11,15], target = 9`) or from a call in `Main`
are used automatically.

**Or run it from source:** open this folder in VS Code, press **F5** and pick **Run DSA
Visualizer**. From source it uses the engine's debug build, so build that first with
`dotnet build engine/DsaViz.sln`.

## Test it

The engine tests include Python: they need a Python 3.9 or later as `python3` on the path,
and fail without one rather than skip.

```
dotnet test engine/DsaViz.sln
```

```
cd extension && npm test
```

```
cd extension && npm run test:integration
```

The last one opens VS Code for about a minute and drives the real commands against the
real engine. To check a built package the way a student gets it — unpacked, with no .NET
anywhere:

```
cd extension && npm run verify-package dist/dsa-visualizer-darwin-arm64.vsix
```

## Layout

| | |
|---|---|
| `engine/DsaViz.Runtime` | The tracer that instrumented code calls into |
| `engine/DsaViz.Engine` | Instrumentation, measurement, analysis, and the CLI the extension runs |
| `engine/DsaViz.Engine/python` | The Python runner: instruments and runs Python files for the engine |
| `engine/DsaViz.Engine.Tests` | Engine tests |
| `extension/` | The VS Code extension and its panel |
| `samples/` | Solutions used as test fixtures — `samples/leetcode/` (C#) and `samples/python/` are written the way LeetCode is |
| `REQUIREMENTS.md` | What was asked for, what is met, and the known limitations |
