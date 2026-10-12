## Why it's O(n²) — not just that it is

**Complexity** measures the method at sizes from 10 to 1,000 and shows:

- how much each line costs, written beside the line in your editor
- the line that sets the overall class, and how many times it runs
- hidden work inside library calls — `List.Contains` in a loop is O(n²) even though every line looks O(1)
- memory: what is allocated, the largest single allocation, and how deep the recursion goes
