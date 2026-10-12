# DSA Visualizer — v2: a learning tool for students

## What was asked

> "I want more UI — how the value is changing and how the code is running, in visual
> movement of data, like when debugging or running. I want to see how the data flows
> using visual representation, and more user friendly. I want to release this VS Code
> extension. I built this to learn how the code flows, how the data is consumed, how
> much storage it takes at each step — to analyse and study. For students, build it
> that way too."

v1 (see `REQUIREMENTS-v1.md`) answered *"what does each line cost?"* as a measurement
report. v2 answers *"what is my code doing?"* by showing it — and makes the tool
something a student can install and use without help.

## Problem

A student learning data structures cannot see their code run. Values change, pointers
move, nodes get rewired, recursive calls pile up and memory grows — all invisibly. A
debugger shows the same state as a text tree of variables, one pause at a time, after
setup a beginner rarely gets through. So the mental model of how an algorithm moves
through its data has to be built by imagination, and complexity stays memorised rather
than understood.

v1 does not close this gap for anyone but its author:

- It runs on **generated** inputs. A student cannot watch *their own* example
  (`nums = [2,7,11,15], target = 9`) go through their code.
- The step view shows values as text and labels; nothing **moves**.
- It needs F5, a setting for the engine path, and the .NET SDK. A student gives up at
  the first of these.
- Its output is a wall of numbers written for someone who already reads O-notation.
- C# only. Most students learn DSA in Python.

## Users

Students learning data structures and algorithms — beginner to intermediate — writing
LeetCode-style solutions in **C# or Python** in VS Code, on Windows, macOS or Linux.
They can write a function; they may not yet read O-notation fluently. The original
author practising in C# remains a user.

## In scope

1. **Install and go.** Free on the VS Code Marketplace and Open VSX. The C# engine ships
   inside the extension, so no .NET is needed. One click on a method visualises it.
2. **Inputs without effort.** Values already in the code — a call, a LeetCode example
   comment, or declared variables — are used automatically. Otherwise inputs are
   generated at a size n. The student can always type their own in LeetCode's format.
3. **Animated run view.** Play, pause and scrub a recorded run. Arrays, strings, grids,
   linked lists, trees, graphs, hash maps, sets, stacks, queues, heaps, the call stack
   and the recursion tree are drawn, and they *move* as the code changes them. Memory is
   shown at every step.
4. **Complexity in plain words.** The v1 measurement for C#, explained without
   requiring notation; the same for Python.
5. **Python**: a second engine with the same features as C#.

## Non-goals

- Java, C++, JavaScript or any other language.
- A debugger: no breakpoints, watch expressions or editing values mid-run. The run view
  replays a recorded run.
- Accounts, hosting, sharing, classrooms or progress tracking. Everything runs locally.
- Telemetry or analytics of any kind.
- A problem catalogue. Students bring their own problems.
- VS Code for the web (vscode.dev). The engines need a native process.
- Bundling Python. Students who write Python already have it (see Open questions).

## Acceptance criteria

Each can be checked by someone outside this conversation. Each will name its tests
once they exist, as v1's did.

### A. Install and first run

1. On Windows x64, macOS (Intel and Apple silicon) and Linux x64, with **no .NET
   installed**, a fresh install visualises a C# sample with no setting changed. Checked
   by CI on each platform against the packaged extension, with .NET removed from the
   path.
2. Each platform package is at most 60 MB.
3. A **"▶ Visualize"** CodeLens appears above every method in a C# or Python file
   that it can run. One click opens the run view on that method. A method it cannot
   run shows the reason instead of the button.
4. A *Get Started* walkthrough opens on first install. It runs three samples in both
   languages: two pointers, tree BFS and memoised DP.
5. No code runs until the student presses a button. The extension activates only for
   C# and Python files.

### B. Where the input comes from

Inputs are chosen in this order, and the panel always says which source is in use:
**values found in the code**, otherwise **generated values of size n**, and the
student can **type their own** at any time.

6. **Values in the code are found and used automatically**, from any of:
   - **a call to the method with literal arguments**:
     - C#: in `Main` or top-level statements,
       e.g. `new Solution().TwoSum(new[] {2, 7, 11, 15}, 9)`
     - Python: under `if __name__ == "__main__":`,
       e.g. `Solution().twoSum([2,7,11,15], 9)`
   - **LeetCode example comments** pasted above the solution:
     `Input: nums = [2,7,11,15], target = 9`
   - **variables assigned literal values** and then passed to the call:
     `int[] nums = {2, 7, 11, 15}; int target = 9;`

   The run view visualises the solution method itself, not `Main`.
7. **Several examples found:** they are offered as a list, for example *"Example 1",
   "Example 2", "call on line 30"*. The first is selected.
8. **Expected output is checked.** When an example comment gives `Output: [0,1]`, the
   result shows *"✓ matches the expected output"* or *"✗ expected [0,1], got
   [1,0]"*.
9. **Values that cannot be found are skipped with the reason**, for example
   *"line 31 passes `BuildInput()`, which is not a literal value"*. That parameter
   falls back to generated values, and the rest of the found example is still used.
10. **No values found:** each parameter is generated at a size n the student picks
    (default 8), using the v1 arrangements (random, sorted, skewed tree…).
11. **Typing your own:** every parameter has an input box in LeetCode's own format,
    pre-filled with the value currently in use:
    - `9`
    - `"abcabcbb"`
    - `[2,7,11,15]`
    - `[[1,0],[0,1]]`
    - `[["1","0"],["0","1"]]`
    - trees as `[3,9,20,null,null,15,7]`
    - linked lists as `[1,2,3]`
    - graphs as `n` plus an edge list

    Invalid input is reported under its own box with the position, for example
    *"expected a number at character 7"*, and nothing runs.
12. Typed values are remembered per file and method across VS Code restarts. A
    *"Use values from code"* button returns to the found ones.

### C. The animated run view

13. **Controls:** play, pause, step forward, step back, jump to start and end, speed
    from 0.25× to 4×, and a scrubber. Keyboard: ← → to step, Space to play. The editor
    highlights the current line at every step.
14. **Every step shows:**
    - each variable in scope with its value
    - the variables this step changed, marked
    - the line and the step number
15. **Arrays and strings:** indexed cells. Integer variables used to index the
    array are drawn as labelled pointers that slide to their new cell. Written cells
    flash. Swapped values visibly trade places.
16. **Grids:** 2-D cells, with the cell being read or written highlighted.
17. **Linked lists:** nodes and arrows. Reference variables (`prev`, `curr`, `next`)
    label the node they point at. A rewired arrow visibly moves to its new target.
18. **Trees:** drawn as trees, with reference variables labelling their node and
    nodes already visited marked.
19. **Graphs:** a node-and-edge diagram built from the adjacency list or edge
    list, with visited nodes marked and the BFS queue or DFS stack shown beside it.
20. **Hash maps and sets:** entries are animated in when added and out when
    removed, and flash when updated.
21. **Stacks, queues and heaps:**
    - C#: `Stack<T>`, `Queue<T>`, `PriorityQueue<T,P>`
    - Python: `list` used as a stack, `deque`, `heapq`

    Pushes and pops animate. A heap is drawn as its tree.
22. **Shared objects:** two variables that refer to the same object label one
    drawing, never two copies. This is what makes linked-list and tree code
    understandable.
23. **Call stack and recursion:**
    - Frames push and pop, each with its own variables.
    - A recursion tree shows every call with its arguments and its return value as it
      returns.
24. **Memory at every step:**
    - the bytes this step allocated
    - the running total
    - a small chart over the run
    - each structure's approximate size

    Python's figures are labelled approximate.
25. **Limits are visible, not silent.**
    - Runs record up to 10,000 steps. Past that, the view says *"showing the first
      10,000 steps"* and stays fully navigable.
    - Arrays longer than 64 cells are windowed around their pointers.
26. **Smooth.** Rendering one step of a 1,000-element array or a 200-node tree takes
    under 50 ms in the panel, measured in the browser test harness.
27. **The student's own exceptions** read *"Your code threw IndexOutOfRange at
    line 12"*. The run view plays every step up to that point and stops on the
    failing line.

### D. Complexity and memory, explained

28. Every v1 acceptance criterion for C# still passes.
29. **Plain words.** Every time and space verdict is followed by a sentence
    without notation, for example *"When the input doubles, this takes about four times
    as long, because line 10 runs once for every pair of items."*
30. **Python gets:**
    - per-line run counts
    - a measured growth class with its derivation
    - memory per line, measured with `tracemalloc`
    - hidden cost for `in` on a list, `list.insert(0, …)`, `pop(0)`, slicing,
      string `+=`, `sorted`/`sort` and `heapq`
    - a reading of the code covering loops and the same recursion kinds as C#,
      answering *unknown* with a reason otherwise

### E. Friendliness and release

31. **Every error reaching the student has:**
    - a plain message
    - an action where one exists: *Install Python*, *Edit input*, *Show line*

    No stack trace from the tool itself is ever shown.
32. Readable and fully keyboard-operable in light, dark and high-contrast themes.
33. **Release:**
    - a Marketplace listing with an icon, animated screenshots, a README written for
      students, a CHANGELOG and a licence
    - one CI workflow that, on a version tag, builds every platform package, runs the
      tests on each, and publishes to the Marketplace and Open VSX

## Constraints

- **Your accounts.** The Marketplace publisher account and the Open VSX account must
  be created by you. Their access tokens go into the repository's CI secrets; Claude
  cannot create accounts or handle the tokens.
- **Python** 3.9 or later: the one selected in the VS Code Python extension, or else
  the first on the path.
- **C# runs on .NET 8**, bundled.
- **Student code runs on the student's own machine** and is never sent anywhere.

## Open questions (assumptions chosen meanwhile)

- **Python is not bundled.** Students writing Python have it installed, and bundling
  would add about 30 MB per platform. If none is found, the tool shows a message with
  an install link.
- **Licence: MIT. Name: "DSA Visualizer".** Both are changeable before release.
  The name must be checked for a clash on the Marketplace.
- **Platforms: Windows x64, macOS x64 and arm64, Linux x64.** Windows and Linux on
  ARM are deferred.
- **Python's reading of the code** covers loops and recursion only. Python's
  dynamic typing makes C#'s symbol-based analysis of library costs impossible
  statically, so library cost in Python is charged only as it runs.
