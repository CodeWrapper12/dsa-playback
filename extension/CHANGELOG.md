# Changelog

## 1.5.0

- **Java.** ▶ Visualize and Complexity for Java solutions, built with your own JDK (17 or
  later). LeetCode's Java as written: no imports, `TreeNode` and `ListNode` supplied.
  Design problems too. The run view draws arrays, lists, maps, sets, a `Deque` used as a
  stack or a queue, `PriorityQueue` as a heap, linked lists and trees, and a design
  problem's object with its fields.
- Complexity charges the work hidden in `contains` on a list (never a set), `add` and
  `remove` at an index, sorting, copying, `substring` and `String +=`, and measures memory
  exactly. Exceptions name their type and line.
- The Get Started walkthrough has its three samples in Java too.
- Fixed: a run still being recorded for one method could appear under the next method's
  heading when the panel had moved on. Work for a method the panel has left is now
  cancelled, and a late result is not shown.
- Fixed: one stray memory reading at a single size — the runtime's bookkeeping adding a
  one-off amount — could hide the shape of "allocated at once". That reading is now set
  aside, and the explanation says which size it was.

## 1.4.0

- **Rust.** ▶ Visualize and Complexity for Rust solutions, built with your own `rustc`.
  LeetCode's Rust as written: no `struct Solution;`, `TreeNode` and `ListNode` supplied.
  The run view draws `Vec`, `VecDeque`, `HashMap`, `HashSet`, `BTreeMap`, `BinaryHeap`,
  linked lists and trees, and a `Vec` used as a stack as a stack.
- **Ownership, as it happens.** A variable the borrow checker would not let be read at a
  step is shown with the reason — moved, borrowed, not set yet, or no longer used.
- Complexity charges the work hidden in `contains`, `sort`, `insert`, `remove`, `clone` and
  iterator chains — on a `Vec`, never on a set — and measures memory exactly. Panics name
  their line: *"Your code panicked at line 4: index out of bounds…"*.
- The Get Started walkthrough has its three samples in Rust too.

## 1.3.0

- **C++.** ▶ Visualize and Complexity for C++ solutions, built with your own `clang++`
  or `g++`. LeetCode's C++ as written: no `#include`, no `using namespace std`,
  `TreeNode` and `ListNode` supplied. The run view draws vectors, maps, sets, stacks,
  queues, priority queues, linked lists and trees, with the call stack and the recursion
  tree. Complexity counts every line, charges the work hidden in `std::find`, `sort`,
  `erase` and the like, and measures memory exactly. Crashes and exceptions name their
  line in plain words.
- The Get Started walkthrough has its three samples in C++ too.
- Fixed: memory held at once could be read as growing faster than everything allocated,
  when a container's storage grew in steps at small sizes. It is now bounded by the total.

## 1.2.0

- **Design problems.** LRU Cache, Min Stack and the rest: a class with LeetCode's
  two-line example — the operations, then their arguments — gets ▶ Visualize. The object
  is created, every call is made in order, the results are checked against Output, and
  the run view says which call is running. In C# and Python.
- **Explain this step** (optional, off by default). With `dsaPlayback.explainWithAI` on,
  a button in the run view asks your own GitHub Copilot to explain the current step in
  plain English. Turning it on sends your code to Copilot — only when you press the
  button, and VS Code asks your permission first.
- **Windows on ARM and Linux on ARM** packages, built and tested on ARM machines.

## 1.1.1

- Fixed: C# files made by the LeetCode extension, or copied from LeetCode, did not
  compile — "Dictionary could not be found", "TreeNode could not be found". LeetCode
  adds the usual `using` lines itself and leaves `TreeNode` and `ListNode` commented
  out; DSA Playback now does the same, so these files run as they do on LeetCode.

## 1.1.0

- **Plain words.** Every time and space verdict now says what it means without notation:
  *"When the input doubles, this takes about four times as long, because line 10 runs
  once for every pair of items."*
- **Run all examples.** One button runs every example in your file and lists each with
  its expected and actual output, ✓ or ✗, and the error where one occurred. Click a row
  to play that example.
- **More C# methods run.** A parameter the visualizer cannot generate — a `double`, a
  `char`, a `List<string>`, an `IList<IList<string>>` — is fine when an example comment
  or a call gives its value, as in Python. Such a method gets ▶ Visualize; measuring
  growth still needs inputs that can be generated.
- **Report a Problem.** A command, and a button whenever the visualizer itself fails,
  that opens a GitHub issue filled in with the versions and the recent log — for you to
  read and edit before sending. Your code is never included, and your home folder is
  shown as `~`.
- A one-time request for a rating after a few successful runs, with *Later* and
  *Don't ask again*. Counted on your machine only.
- Questions now have a home: GitHub Discussions.

## 1.0.1

- Fixed: the ▶ button in the editor's title bar failed with "The job was not valid
  JSON". VS Code passes that button an editor group as well as the file, and it was
  taken for the method's name. The buttons above each method were not affected.

## 1.0.0

The first release.

- **▶ Visualize** above every method in a C# or Python file plays it step by step:
  arrays with sliding pointers and swaps, linked lists whose arrows rewire, trees and
  graphs with what has been visited, hash maps, sets, stacks, queues and heaps animating
  as they change, the call stack, a recursion tree with every return value, and memory at
  every step. Speed from 0.25× to 4×; ← → and Space.
- **Inputs from your code**: LeetCode `Input:` / `Output:` comments, calls in `Main` or
  under `if __name__ == "__main__":`, and the variables they pass — with your answer
  checked against the expected output. Or type your own in LeetCode's format.
- **Complexity** measures the method at growing sizes and explains the result: each
  line's cost beside it in the editor, the line that sets the class, hidden work inside
  library calls, and memory — with a reading of the code alongside.
- C# needs nothing installed. Python uses yours (3.9 or later).
