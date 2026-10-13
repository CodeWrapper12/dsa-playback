# DSA Playback — v2: a learning tool for students

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

- JavaScript or other languages beyond those planned below. **Changed in v3:** C++, Rust
  and Java are now planned, in that order (Phase 7).
- A debugger: no breakpoints, watch expressions or editing values mid-run. The run view
  replays a recorded run.
- Accounts, hosting, sharing, classrooms or progress tracking. Everything runs locally.
- Telemetry or analytics of any kind. Code never leaves the machine **by default**.
  **Changed in v3:** an optional AI explanation of each step, off by default, may send
  the code to the student's own Copilot when they turn it on (Phase 6).
- A problem catalogue. Students bring their own problems.
- VS Code for the web (vscode.dev). The engines need a native process.
- Bundling Python. Students who write Python already have it (see Open questions).

## Acceptance criteria

Each can be checked by someone outside this conversation. The tests that check each
one are listed under **Status**.

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

## Status

**All four phases are done.** CI passes on Windows, macOS and Linux, and a release dry run
built and verified all four platform packages. Publishing waits only for the publisher's
two tokens. The extension is named **DSA Playback**: "DSA Visualizer" was already
taken on the Marketplace.

| # | Status | Checked by |
|---|---|---|
| 1 | Met on all four platforms — Windows x64, macOS Intel and Apple silicon, Linux x64 — each package built, tested and verified on its own platform by the release workflow: C# with no .NET in the environment, Python with the machine's own. | `release.yml` (run as a dry run from the Actions tab), `npm run verify-package`. Integration: *a solution outside the open folder visualizes*, on all three operating systems in `ci.yml`. |
| 2 | Met: 40.6 MB (macOS Apple silicon) to 44.0 MB (Windows). | `npm run verify-package` on each platform |
| 3 | Met for C# and Python. | Integration: *the ▶ Visualize button appears*; *a Python solution gets the buttons*. `EntryPointScannerTests`, `PythonTests.Methods_and_top_level_functions_are_listed` |
| 4 | Met: three samples in each language. | Integration: *the walkthrough opens an editable copy*. `npm run verify-package` checks all six ship. The walkthrough page has not yet been looked at by hand in VS Code. |
| 5 | Met, by construction. | The extension activates on `onLanguage:csharp` and `onLanguage:python`. Listing methods only parses, in either language. |
| 6–12 | Met for C# and Python, including calls under `if __name__ == "__main__":` and a parameter without a hint taking its type from an example. | `ExampleFinderTests`, `StepInputTests`, `LiteralsTests`, `PythonTests`, panel tests, integration |
| 13 | Met: speed from 0.25× to 4×, ← → and Space. | Panel tests: *speed changes how fast it plays*; *Space plays and pauses* |
| 14 | Met. | Panel test: *every step of every practice structure draws* |
| 15 | Met: pointers slide and written cells flash. A tuple swap flies both values; a swap through a temporary variable shows as two writes. | Panel tests: *swapped values fly past each other*; *a value written into a cell flashes* |
| 16 | Met. | Panel test: *every step… draws* (NumIslands) |
| 17 | Met: one drawing for the whole list, a rewired arrow bends to its new target. | `HeapRecorderTests`. Panel tests: *a linked list is one drawing*; *a rewired link bends* |
| 18 | Met: the whole tree stays drawn deep in a recursive walk, with the path of calls marked. | Panel test: *a recursive tree walk always shows the whole tree* |
| 19 | Met for edge lists and adjacency lists. | Panel test: *a graph is drawn from its edge list* |
| 20 | Met. | Panel test: *map entries animate in* |
| 21 | Met: C#'s Stack, Queue and PriorityQueue (a four-way heap); Python's deque, a list used only with append and pop() (drawn as a stack), and a list kept by heapq (drawn as a two-way heap). | Panel tests: *a queue shows what joined and what left*; *a heap is drawn level by level*; *a heapq list is drawn as a two-way heap*. `PythonTests.Structures_are_recorded_as_what_they_are_used_as` |
| 22 | Met. | `HeapRecorderTests.Two_variables_holding_one_node_hold_the_same_id`. Sabotaged identity failed four tests. |
| 23 | Met, including arrow-bodied recursive helpers. | `HeapRecorderTests.Every_call_returns_its_value_at_its_depth`. Panel test: *the recursion tree shows each call* |
| 24 | Met. Python's figures come from tracemalloc and are labelled approximate. | `HeapRecorderTests.Each_step_carries…`; `PythonTests.Recording_its_own_variables_never_shows_up…`. Panel tests: *memory shows this step*; *Python's memory figures are labelled approximate* |
| 25 | Met. | `HeapRecorderTests.Ten_thousand_steps_are_kept_by_default`. Panel tests: *a long array is windowed*; *a capped run says so* |
| 26 | Met. In the browser harness, stepping with one frame between steps: 1,000-element array median 3.4 ms, worst 14.9 ms; 200-node tree median 6.3 ms, worst 8.7 ms. Stepping hundreds of times with no pause at all shows the browser's own garbage-collection pauses, which real playback (≥ 175 ms between steps) does not trigger. | Browser harness, by hand |
| 27 | Met, in both languages. | `StepInputTests`, `PythonTests.An_exception_in_the_students_code_names_its_line`. Panel test: *an exception names the line* |
| 28 | Met: the v1 C# tests run unchanged in the engine suite. | Engine test suite (359 tests) |
| 29 | Not met in v2 — missed, and carried into v3 as criterion 35, where it is met. | See 35 |
| 30 | Met. Measured class, derivation and per-line counts as for C#; hidden cost charged for `in` on a list, tuple or string, `insert`/`pop` by position, `remove`, `index`, `count`, slicing, `+=` on a string, `[…] * n`, `sorted`/`sort`, `min`/`max`/`sum`, heapq and bisect, and every comprehension's loop. Space is the peak held, measured directly with tracemalloc. The reading of the code covers loop nesting, halving loops, two-pointer loops, list scans, worklist loops and the recursion kinds, and says *unknown* with a reason otherwise. Python 3.9 to 3.13 checked; 3.8 is refused with a clear message. | `PythonTests` (32 tests), panel tests for every Python sample |
| 31 | Met: *Show line*, *Show details*, and *Install Python* / *Select Interpreter* when Python is missing or older than 3.9. | `PythonTests.No_Python_is_reported_with_a_code…`. Panel test: *the visualizer's own failure offers its details* |
| 32 | Met: accents tuned per theme kind and checked at 4.5:1 or better against each default background; high-contrast themes outline every shape; every control is reachable by Tab with a visible focus ring, and keeps focus while stepping. | `contrast.test.ts` (4 themes). Panel tests: *a keyboard user keeps focus*; *rows that go to a line can be used from the keyboard*. Light and high-contrast checked by hand in the browser harness. |
| 33 | Ready: icon, three animated screenshots and one still, a README for students, CHANGELOG, MIT licence, CI on three operating systems, and a release workflow that builds, tests, verifies and publishes four platform packages. Publishing needs the publisher's two tokens. | `npm run verify-package` checks the icon, licence, changelog and that every README image resolves. |

Test counts: 349 engine tests (32 of them Python), 54 extension tests, and 9 integration
checks in a real VS Code — on Windows, macOS and Linux in CI.

**Found and fixed in Phase 3.** Generated inputs were meant to be the same on every run
but were not: their seed came from `HashCode.Combine`, which .NET randomises in every
process. A solution with an early exit could measure differently from one run to the
next. The seed is now fixed, and a test pins the values.

**Found and fixed in Phase 2.** Memory measurement could absorb about 1.5 KB from the
JIT recompiling a running loop (on-stack replacement). That bent one statement's
largest-allocation curve, which showed up as a test failing in one order and passing in
another. Loops are now compiled fully on their first call, and the engine ships
precompiled, so it is about twice as fast to start.

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
- **Licence: MIT. Name: "DSA Playback".** Both are changeable before release.
  The name must be checked for a clash on the Marketplace.
- **Platforms: Windows x64, macOS x64 and arm64, Linux x64.** Windows and Linux on
  ARM are deferred.
- **Python's reading of the code** sees list operations only on names it can tell are
  lists — parameters hinted `List[...]` and names assigned a list. Python's dynamic
  typing hides the rest, so library cost there is charged only as it runs.
- **Python's budget** is one million operations, a fiftieth of C#'s: traced Python runs
  about 220,000 operations a second, so it stops a runaway loop in about the same time.
- **Python's space reading of the code** is not attempted. The measured peak is the answer.
- **Python's recursion limit** is 3,000 calls, not C#'s 20,000. With tracemalloc running,
  CPython's recursion slows with the square of its depth, so a runaway recursion has to
  be stopped early to be stopped quickly.


---

# v3 — after release

DSA Playback 1.0 is published. What holds it back now is not features but being found
and trusted: installs and reviews decide where it appears in Marketplace search, and it
has neither yet. One promised item was also missed: criterion 29, plain words.

## Phase 5 — found and trusted (planned in detail)

34. **Listing written for search.** The display name is
    *"DSA Playback — Algorithm Visualizer & Big-O Explainer"*. The keywords grow to the
    Marketplace's maximum of 30 and include *algorithm visualizer*, *time complexity*,
    *space complexity*, *recursion tree*, *linked list* and *step by step*. The README's
    first lines say:
    - it runs on your machine
    - there is no sign-in
    - your code never leaves your computer
    - it explains *why* a solution is O(n²), line by line

    No competitor is named. Checked by `verify-package`.
35. **Plain words (criterion 29, missed in v2).** Every time and space verdict, for C#
    and Python, is followed by a sentence with no notation, for example:
    - *"When the input doubles, this takes about four times as long, because line 10
      runs once for every pair of items."*
    - *"Doubling the input adds about one more step."*
    - *"It holds about twice as much when the input doubles."*

    Every complexity class has its sentence, and each is tested.
36. **One rating prompt.** After five successful runs, one notification asks for a
    rating, with three choices:
    - *Rate* opens the review page, and the prompt is never shown again.
    - *Later* asks again after twenty more runs.
    - *Don't ask again* stops it for good.

    Counted locally. Nothing is sent anywhere until the student clicks *Rate*.
37. **Report a problem.** The command *DSA Playback: Report a Problem*, and a button on
    every "the visualizer stopped" message, opens a new GitHub issue pre-filled with:
    - the extension and VS Code versions
    - the operating system and the language
    - the last 50 log lines, with the home folder shown as `~`

    The student reads it in GitHub's own form before submitting. Their source code is
    never included.
38. **Questions have a home.** GitHub Discussions is turned on, and the listing's `qna`
    points there.
39. **C# runs whenever the example supplies the values.** A method taking a type the
    engine cannot generate (`double`, `char`, `List<string>`, `IList<IList<string>>`…)
    is offered and runs when an example comment or a call gives every value. Without an
    example, it says to add one. Python already behaves this way.
40. **Run all examples.** A *Run all examples* button in the Run tab:
    - runs every example found in the file
    - lists each with its expected and actual output and ✓ or ✗ (in any order where
      the problem allows it), and the error where one occurred
    - opens the run view on a row when it is clicked

    Tested with a file whose second example fails.
41. **The walkthrough is checked by hand** inside VS Code, in a light and a dark theme.
    This is the publisher's own check, not an automated one.

Released as 1.1.0 through the existing release workflow.

### Phase 5 status

| # | Status | Checked by |
|---|---|---|
| 34 | Met in the package: the name, 30 keywords, and the README's opening four promises. No other product is named. | `verify-package`: *the name says what it is*, *30 keywords*, *the README opens with…*, *no other product is named* |
| 35 | Met, for C# and Python: a sentence under every verdict card, and the measured time names the line that causes it. | Panel tests: *every complexity class has a plain sentence…*, *the measured time says which line causes it*, *a line that costs more than it runs blames the library call*, *a measured Python solution gets the same plain sentences* |
| 36 | Met: after 5 successful runs; Later asks again after 20; closing counts as Later; counted in VS Code's own storage. | `rating.test.ts` (6 tests) |
| 37 | Met: a command, a button on the visualizer's own failures (notification and panel), versions, system, language, last 50 log lines with the home folder as `~`, shortened to fit an address. Opened with VS Code's string form of `openExternal`, because a `vscode.Uri` re-encodes the `&` between title and body. | `report.test.ts` (5 tests); integration: *a problem report carries the versions…* |
| 38 | Partly: `qna` points at Discussions. Turning Discussions on is a repository setting the publisher switches on. | `verify-package`: *questions go to Discussions* |
| 39 | Met: `double`, `char`, and arrays and lists of them, nested, run when an example or call gives the value; without one, the reason says to add one. Such a method gets ▶ Visualize but no Complexity button. | `ScannerAgreementTests` (5 new), `StepInputTests.A_method_taking_types_that_cannot_be_generated…`; integration: *a method taking a double runs from its example* |
| 40 | Met, for C# and Python, against `samples/TwoSumExamples.cs`, whose second example fails and third throws. | `StepInputTests.Every_example_is_run…`, `PythonTests.Every_example_is_run…`; panel test: *run all examples lists each result*; integration: *Run all examples lists every example's result* |
| 41 | Open: the publisher's own check. | By hand |

## Phase 6 — reach (planned in detail)

Found while planning: a C# file made by the LeetCode extension does not compile here at
all. LeetCode adds `using` lines behind the scenes and leaves `TreeNode` and `ListNode`
commented out, so `Dictionary`, `Math` and `TreeNode` are all "not found". Python files
from the same extension already work.

42. **C# written for LeetCode compiles as it does on LeetCode.**
    - The `using` lines LeetCode adds itself are assumed: `System`,
      `System.Collections.Generic`, `System.Linq` and `System.Text`.
    - `TreeNode` and `ListNode` are supplied when the file uses them but does not define
      them, as Python's already are. A file that defines its own is left alone.
    - Checked with files in the LeetCode extension's layout (`1.two-sum.cs`, the
      `@lc` header, the description in a `/* */` comment, `@lc code=start/end`): Two
      Sum, Maximum Depth of Binary Tree and Reverse Linked List, in C# and Python, each
      showing ▶ Visualize, running its example and showing ✓.
43. **Design problems.** A class whose example is LeetCode's two lines — operations and
    arguments — such as LRU Cache, Min Stack or Implement Trie:
    ```
    Input
    ["LRUCache","put","put","get"]
    [[2],[1,1],[2,2],[1]]
    Output
    [null,null,null,1]
    ```
    - ▶ Visualize appears above the class.
    - The run creates the object and makes every call in order, as one recording; the
      run view shows which call is running.
    - The list of results is checked against Output (✓ / ✗), and Run all examples works.
    - A value can be typed for both lines, in the same format.
    - C# and Python. No Complexity button: a design problem's cost is per operation,
      which this phase does not measure.
44. **ARM packages.** Windows on ARM (`win32-arm64`) and Linux on ARM (`linux-arm64`)
    packages are built and checked by `verify-package` on GitHub's own ARM machines,
    and published with the other four.
45. **Optional AI explanation, off by default.**
    - Setting `dsaPlayback.explainWithAI`, off by default. Its description says plainly:
      turning it on sends your code and the current step to GitHub Copilot.
    - When on, the run view has an **Explain this step** button. It sends the method's
      code, the current line and the variables at that step to the student's own
      Copilot, through VS Code's language-model API, and shows the answer under the step.
    - Nothing is sent until the button is pressed. VS Code asks the student's permission
      the first time.
    - No Copilot, or an editor without the API (VSCodium, for example): the button says
      why and does nothing else.
    - Tested with a stand-in model. The real Copilot answer is checked by hand.

Release plan (chosen 2026-10-12): 42 ships first as 1.1.1, because LeetCode-extension C#
files fail today; 43–45 follow as 1.2.0. The AI explanation runs only when its button
is pressed.

### Phase 6 status

| # | Status | Checked by |
|---|---|---|
| 42 | Met: `using` lines and `TreeNode`/`ListNode` supplied beside the file, never into it, so error lines are still the student's own. A file's own definitions are kept. | `LeetCodeExtensionTests` (13 tests over `samples/leetcode-extension`, C# and Python); integration: *a C# file made by the LeetCode extension…* |
| 43 | Met, in C# and Python: one ▶ Visualize on the class (no Complexity), its example read in either layout LeetCode uses, every call made in order, results checked, typed calls accepted, calls that do not fit named in plain words, a thrown exception's own line reported. The run view titles the call running, *Call 4 of 10: get*, from where the engine recorded each call starting. | `DesignTests` (23); panel tests: *a design problem plays every call…* (C#, Python), *…calls Python could not make are explained*; integration: *a design problem gets one ▶ Visualize…* |
| 44 | Met: `win32-arm64` on `windows-11-arm` and `linux-arm64` on `ubuntu-24.04-arm` passed every test and `verify-package` in release dry run 38329086933. | Release dry run |
| 45 | Met, except the real Copilot answer, which is the publisher's check by hand. Off by default; the setting says the code is sent; nothing is sent until pressed; no API or no model says why. VS Code 1.85 is still supported: the API is found at run time. | `explain.test.ts` (6, stand-in model); panel tests: *Explain this step is absent until…*, *an answer from an earlier run is not shown*; integration: *Explain this step sends nothing while off…* |

## Phase 7 — new languages (outline)

C++, then Rust, then Java. Each needs the student's own compiler: g++ or clang, rustc,
or a JDK.

The planned approach for C++ and Rust shares most of its code:
- **Complexity** comes from the compiler's own line counting (clang's and rustc's coverage
  instrumentation counts every line exactly), plus a counting memory allocator.
- **The run view** is recorded through a debugger.

Java uses its own debugging interface. Each language is planned and approved separately.
