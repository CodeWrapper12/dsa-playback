# Changelog

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
