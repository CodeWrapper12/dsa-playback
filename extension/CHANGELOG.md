# Changelog

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
