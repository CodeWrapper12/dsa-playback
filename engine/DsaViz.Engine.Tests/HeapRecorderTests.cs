using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// The recording behind the animated run view: objects with identity, sent only when they
/// change, plus what every call returned and what the code had allocated by each step.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class HeapRecorderTests
{
    private static StepTrace Record(string source, string entry, int size = 8, int maxSteps = Stepper.DefaultMaxSteps)
    {
        var trace = Stepper.Record(SourceAnalysis.Create(source), entry, size: size, maxSteps: maxSteps);
        Assert.True(trace.Status == RunStatus.Completed, string.Join("; ", trace.Errors));
        return trace;
    }

    private static StepTrace Sample(string name, string entry, int size = 8)
        => Record(Samples.Read(name), entry, size);

    private const string ReverseList = """
        // Input: head = [1,2,3]
        public class ListNode { public int val; public ListNode next; public ListNode(int val = 0, ListNode next = null) { this.val = val; this.next = next; } }
        public class Solution {
            public ListNode ReverseList(ListNode head) {
                ListNode previous = null;
                while (head != null) {
                    var next = head.next;
                    head.next = previous;
                    previous = head;
                    head = next;
                }
                return previous;
            }
        }
        """;

    [Fact]
    public void Two_variables_holding_one_node_hold_the_same_id()
    {
        var trace = Record(ReverseList, "Solution.ReverseList");

        // After `previous = head;` both name the same node — one drawing, two labels.
        var step = trace.Steps.First(s => s.Variables.Any(v => v.Name == "previous" && v.Ref is not null));
        var previous = step.Variables.Single(v => v.Name == "previous").Ref;
        var head = trace.Steps[step.Index - 1].Variables.Single(v => v.Name == "head").Ref;
        Assert.Equal(head, previous);
    }

    [Fact]
    public void Rewiring_a_link_sends_only_the_node_that_changed()
    {
        var trace = Record(ReverseList, "Solution.ReverseList");

        // The whole list is sent once, at the start; then each `head.next = previous`
        // changes exactly one node.
        Assert.Equal(3, trace.Steps[0].Heap!.Count);
        var later = trace.Steps.Skip(1).Where(s => s.Heap is not null).ToList();
        Assert.Equal(3, later.Count);
        Assert.All(later, s => Assert.Single(s.Heap!));

        var final = StepHeap.At(trace, trace.Steps.Count - 1);
        Assert.Equal("ListNode{val=1, next=null}", StepHeap.Write(final[1]));
        Assert.Equal("ListNode{val=2, next=ListNode(1)}", StepHeap.Write(final[2]));
    }

    [Fact]
    public void A_long_array_that_changes_in_one_place_is_sent_as_that_place()
    {
        var trace = Record("""
            // Input: values = [9,8,7,6,5,4,3,2,1,0,11,12,13,14,15,16,17,18,19,20]
            public class Solution {
                public int Bump(int[] values) {
                    values[3] = 100;
                    values[4] = 200;
                    return values[3];
                }
            }
            """, "Solution.Bump");

        var patched = trace.Steps.Select(s => s.Heap?.SingleOrDefault()).Where(o => o?.Patch is not null).ToList();
        Assert.Equal(2, patched.Count);
        Assert.Equal(new HeapPatch(3, new HeapValue("100") { Number = 100 }), patched[0]!.Patch!.Single());
        Assert.Null(patched[0]!.Items);
        Assert.Equal("[9, 8, 7, 100, 200, 4, 3, 2, 1, 0, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]",
            StepHeap.Write(StepHeap.At(trace, trace.Steps.Count - 1).Values.Single()));
    }

    [Fact]
    public void Every_practice_structure_is_recorded_as_its_own_kind()
    {
        var trace = Record("""
            using System.Collections.Generic;
            public class Solution {
                public int Everything(int[] nums) {
                    var list = new List<int> { 1, 2 };
                    var map = new Dictionary<string, int> { ["a"] = 1 };
                    var set = new HashSet<int> { 5 };
                    var stack = new Stack<int>(); stack.Push(1); stack.Push(2);
                    var queue = new Queue<int>(); queue.Enqueue(1); queue.Enqueue(2);
                    var heap = new PriorityQueue<string, int>(); heap.Enqueue("x", 3); heap.Enqueue("y", 1);
                    var grid = new char[][] { new[] { '1', '0' } };
                    return list.Count;
                }
            }
            """, "Solution.Everything");

        var last = trace.Steps[^1];
        string Kind(string name) => last.Variables.Single(v => v.Name == name).Kind;
        Assert.Equal("list", Kind("list"));
        Assert.Equal("map", Kind("map"));
        Assert.Equal("set", Kind("set"));
        Assert.Equal("stack", Kind("stack"));
        Assert.Equal("queue", Kind("queue"));
        Assert.Equal("heap", Kind("heap"));
        Assert.Equal("array", Kind("grid"));

        string Written(string name) => StepHeap.Write(StepHeap.Object(trace, last, name));
        Assert.Equal("{\"a\": 1}", Written("map"));
        Assert.Equal("[2, 1]", Written("stack"));     // top first
        Assert.Equal("[1, 2]", Written("queue"));     // front first
        Assert.Equal("{\"y\": 1, \"x\": 3}", Written("heap"));   // the heap's own order: smallest priority at the root

        // A grid is an array of rows, each its own object.
        var rows = StepHeap.Object(trace, last, "grid").Items!;
        Assert.Equal("[1, 0]", StepHeap.Write(StepHeap.At(trace, last.Index)[rows[0].Ref!.Value]));
    }

    [Fact]
    public void A_tree_is_recorded_node_by_node()
    {
        var trace = Record("""
            // Input: root = [3,9,20,null,null,15,7]
            public class TreeNode { public int val; public TreeNode left, right; public TreeNode(int val = 0, TreeNode left = null, TreeNode right = null) { this.val = val; this.left = left; this.right = right; } }
            public class Solution {
                public int MaxDepth(TreeNode root) => root == null ? 0 : 1 + System.Math.Max(MaxDepth(root.left), MaxDepth(root.right));
            }
            """, "Solution.MaxDepth");

        var heap = StepHeap.At(trace, 0);
        Assert.Equal(5, heap.Count);
        Assert.All(heap.Values, node => Assert.Equal("tree", node.Kind));
        Assert.Equal("TreeNode{val=3, left=TreeNode(9), right=TreeNode(20)}", StepHeap.Write(heap[1]));
    }

    [Fact]
    public void Every_call_returns_its_value_at_its_depth()
    {
        var trace = Record("""
            // Input: n = 4
            public class Solution {
                public int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            }
            """, "Solution.Fib");

        // fib(4) makes 9 calls, each returning once.
        Assert.Equal(9, trace.Returns.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, trace.Returns.Select(r => r.Depth).Distinct().Order());
        Assert.Equal("3", trace.Returns[^1].Value.Text);
        Assert.Equal(1, trace.Returns[^1].Depth);

        // An arrow-bodied method is stepped like any other.
        Assert.Equal(9, trace.Steps.Count);
        Assert.Equal(new[] { "n" }, trace.Methods.Single(m => m.Name == "Fib").Parameters);
    }

    [Fact]
    public void A_lambdas_returns_are_not_calls_and_a_null_return_is_recorded()
    {
        var trace = Record("""
            using System.Linq;
            public class Solution {
                public int Go(int[] values) {
                    var sorted = values.OrderBy(v => { return -v; }).ToArray();
                    return Find(null) is null ? sorted.Length : 0;
                }
                string Find(string s) { return null; }
            }
            """, "Solution.Go", size: 3);

        Assert.Equal(new[] { "null", "3" }, trace.Returns.Select(r => r.Value.Text));
    }

    [Fact]
    public void Each_step_carries_what_the_code_had_allocated_and_none_of_the_recording()
    {
        var trace = Record("""
            // Input: n = 3
            public class Solution {
                public int Make(int n) {
                    int total = 0;
                    int[] a = new int[100];
                    total += a.Length;
                    total += n;
                    return total;
                }
            }
            """, "Solution.Make");

        // Only `new int[100]` allocates: 24 bytes of header and 400 of elements. Capturing
        // the variables at every step allocates too, and none of it may show here.
        var allocated = trace.Steps.Select(s => s.Allocated).ToList();
        Assert.Equal(new long[] { 0, 0, 424, 424, 424 }, allocated);
        Assert.Equal(424, trace.TotalAllocated);
    }

    [Fact]
    public void Objects_report_their_approximate_size()
    {
        Assert.Equal(424, HeapRecorder.Bytes(new int[100]));
        Assert.Equal(32, HeapRecorder.Bytes(new long[1]));
        Assert.True(HeapRecorder.Bytes(new Dictionary<int, int> { [1] = 1, [2] = 2 }) > HeapRecorder.Bytes(new Dictionary<int, int>()));
    }

    [Fact]
    public void A_lazy_query_is_never_enumerated_by_the_recorder()
    {
        // Enumerating it would run the student's lambda inside the recorder, as extra steps.
        var trace = Record("""
            using System.Linq;
            public class Solution {
                public int Go(int[] values) {
                    var query = values.Where(v => { return v > 0; });
                    int seen = 0;
                    return seen;
                }
            }
            """, "Solution.Go", size: 3);

        Assert.DoesNotContain(trace.Steps, s => s.Method.StartsWith("lambda"));
        Assert.Equal("sequence", trace.Steps[^1].Variables.Single(v => v.Name == "query").Kind);
    }

    [Fact]
    public void Ten_thousand_steps_are_kept_by_default()
    {
        var trace = Record("""
            // Input: n = 20000
            public class Solution {
                public int Count(int n) {
                    int total = 0;
                    for (int i = 0; i < n; i++) total++;
                    return total;
                }
            }
            """, "Solution.Count");

        Assert.Equal(10_000, trace.Steps.Count);
        Assert.True(trace.Truncated);
        Assert.Equal("20000", trace.ReturnValue);
    }

    [Fact]
    public void A_graph_input_is_named_for_drawing()
    {
        var trace = Sample("leetcode/CountComponents.cs", "Solution.CountComponents", 5);
        Assert.Equal(new GraphInput("edges", "edges") { NodeCount = "n" }, trace.Graph);
    }
}
