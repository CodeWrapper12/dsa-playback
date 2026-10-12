# DSA Visualizer

Watch your data-structures-and-algorithms code run, one step at a time — and see why it
has the complexity it has.

Nothing to set up: the engine is inside the extension, and your code never leaves your
machine.

## Watch it run

Open a C# solution. Above every method you can run there are two buttons:

- **▶ Visualize** — plays your code step by step, with your data drawn and moving:
  - arrays with `i`, `j`, `left`, `right`, `mid`… as pointers that slide, and swapped
    values that trade places
  - linked lists whose arrows bend as you rewire them — `prev`, `curr` and `next` label
    the nodes they point at
  - trees, with the nodes your recursion is working on marked
  - graphs, with visited nodes and the BFS queue
  - hash maps and sets whose entries animate in and out; stacks, queues and heaps
  - the call stack, and a recursion tree showing what every call returned
  - memory: what each line allocates, and the total as the run goes

  The current line lights up in the editor. ← and → step, Space plays, and the speed
  goes from 0.25× to 4×.
- **Complexity** — runs your code on bigger and bigger inputs and explains how its time
  and memory grow, line by line.

## The input comes from your code

Paste LeetCode's example as a comment above your solution:

```csharp
// Example 1:
// Input: nums = [2,7,11,15], target = 9
// Output: [0,1]
```

**▶ Visualize** runs exactly that input and checks your answer against the expected
output — *✓ Matches* or *✗ Expected [0,1], got [1,0]*. A call in `Main` with values
written out works too:

```csharp
int[] nums = { 2, 7, 11, 15 };
new Solution().TwoSum(nums, 9);
```

Several examples? Pick one from the list. No example? Values are generated for you at a
size you choose. Want something else? Type it in LeetCode's format —
`[2,7,11,15]`, `"abcabcbb"`, `[[1,0],[0,1]]`, or a tree as `[3,9,20,null,null,15,7]` —
and press Enter.

## Why it's O(n²), not just that it is

**Complexity** shows:

- how often each line ran and the memory it allocated, written beside the line
- the line that sets the method's class, and how many times it runs (*≈ 0.5·n² times*)
- work hidden inside library calls — `List.Contains` in a loop is O(n²) even though
  every line looks O(1)
- memory: total allocated, the largest single allocation, and how deep recursion goes
- a reading of the code alongside the measurement, with any disagreement explained

## When something goes wrong

- Your code throws: *"Your code threw IndexOutOfRangeException at line 12"* — the run
  plays up to that line, and **Show line** takes you there.
- An endless loop or a missing base case is stopped and explained, never left hanging.
- A typed value that cannot be read is marked under its own box, with the position.

## Get started

Open the Command Palette and run **Welcome: Open Walkthrough…**, then **Get started with
DSA Visualizer**. Three samples — two pointers, a tree BFS, and memoised dynamic
programming — open ready to run.
