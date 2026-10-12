using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Which values a run uses: typed ones first, then an example found in the code, then
/// generated ones. And what happens when a value is wrong, or the code throws.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class StepInputTests
{
    private const string TwoSum = """
        // Example 1:
        // Input: nums = [2,7,11,15], target = 9
        // Output: [0,1]
        //
        // Example 2:
        // Input: nums = [3,2,4], target = 6
        // Output: [2,1]
        using System.Collections.Generic;
        public class Solution {
            public int[] TwoSum(int[] nums, int target) {
                var seen = new Dictionary<int, int>();
                for (int i = 0; i < nums.Length; i++) {
                    if (seen.TryGetValue(target - nums[i], out int j)) return new[] { j, i };
                    seen[nums[i]] = i;
                }
                return new int[0];
            }
        }
        """;

    private static StepTrace Record(string source, string entry = "Solution.TwoSum",
        string?[]? values = null, int? example = null, int size = 8)
        => Stepper.Record(SourceAnalysis.Create(source), entry, size: size, values: values, example: example);

    [Fact]
    public void The_first_example_in_the_code_is_used_without_asking()
    {
        var trace = Record(TwoSum);

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal(0, trace.SelectedExample);
        Assert.All(trace.Inputs, i => Assert.Equal(InputSource.Example, i.Source));
        Assert.Equal("[2,7,11,15]", trace.Inputs[0].Literal);
        Assert.Equal("[0,1]", trace.ReturnValue);
        Assert.True(trace.Check!.Matches);
    }

    [Fact]
    public void A_wrong_answer_is_reported_against_the_expected_output()
    {
        // Example 2's comment says [2,1]; the code returns [1,2], and the problem text
        // never says any order is accepted.
        var trace = Record(TwoSum, example: 1);
        Assert.Equal(new OutputCheck("[2,1]", "[1,2]", false), trace.Check);
    }

    [Fact]
    public void Typed_values_win_over_the_example_one_parameter_at_a_time()
    {
        var trace = Record(TwoSum, values: new[] { null, "18" });

        Assert.Equal(InputSource.Example, trace.Inputs[0].Source);
        Assert.Equal(InputSource.Typed, trace.Inputs[1].Source);
        Assert.Equal("[1,2]", trace.ReturnValue);

        // The expected output belongs to the example's own inputs, so it is not applied.
        Assert.Null(trace.Check);
    }

    [Fact]
    public void A_value_that_cannot_be_read_stops_the_run_and_says_which_input()
    {
        var trace = Record(TwoSum, values: new[] { "[2,7,", "x" });

        Assert.Equal(RunStatus.InvalidInput, trace.Status);
        Assert.Empty(trace.Steps);
        Assert.Equal("expected a value at the end", trace.Inputs[0].Error);
        Assert.Equal("expected a whole number, like 9", trace.Inputs[1].Error);
        Assert.Equal(new[] { "nums: expected a value at the end", "target: expected a whole number, like 9" }, trace.Errors);
    }

    [Fact]
    public void With_no_example_every_value_is_generated_at_the_chosen_size()
    {
        var trace = Record(TwoSum, example: -1, size: 5);

        Assert.Null(trace.SelectedExample);
        Assert.All(trace.Inputs, i => Assert.Equal(InputSource.Generated, i.Source));
        Assert.Equal(5, trace.Inputs[0].Literal.Split(',').Length);
        Assert.Equal(2, trace.Examples.Count);
    }

    [Fact]
    public void An_example_that_no_longer_exists_falls_back_to_the_first()
    {
        var trace = Record(TwoSum, example: 7);
        Assert.Equal(0, trace.SelectedExample);
    }

    [Fact]
    public void A_parameter_the_example_cannot_supply_is_generated_and_says_why()
    {
        var trace = Record(TwoSum.Replace("// Example 1:\n// Input: nums = [2,7,11,15], target = 9\n// Output: [0,1]\n//\n// Example 2:\n// Input: nums = [3,2,4], target = 6\n// Output: [2,1]\n", "")
            + "\npublic static class P { static int[] Make() => new int[2]; public static void Main() { new Solution().TwoSum(Make(), 4); } }");

        Assert.Equal(InputSource.Generated, trace.Inputs[0].Source);
        Assert.Equal("Line 12 passes `Make()`, which is not a value written out.", trace.Inputs[0].Note);
        Assert.Equal(InputSource.Example, trace.Inputs[1].Source);
        Assert.Equal("4", trace.Inputs[1].Literal);
    }

    [Fact]
    public void Trees_from_an_example_comment_are_built_and_walked()
    {
        var trace = Record("""
            // Input: root = [3,9,20,null,null,15,7]
            // Output: 3
            public class TreeNode {
                public int val; public TreeNode left; public TreeNode right;
                public TreeNode(int val = 0, TreeNode left = null, TreeNode right = null) { this.val = val; this.left = left; this.right = right; }
            }
            public class Solution {
                public int MaxDepth(TreeNode root) {
                    if (root == null) return 0;
                    return 1 + System.Math.Max(MaxDepth(root.left), MaxDepth(root.right));
                }
            }
            """, "Solution.MaxDepth");

        Assert.Equal("3", trace.ReturnValue);
        Assert.True(trace.Check!.Matches);
        // Three levels of tree, and one more for the calls on the missing children below them.
        Assert.Equal(4, trace.Steps.Max(s => s.Depth));
    }

    [Fact]
    public void An_exception_in_the_students_code_names_the_line_and_keeps_the_steps_before_it()
    {
        var trace = Record("""
            // Input: nums = [1,2,3]
            public class Solution {
                public int Last(int[] nums) {
                    int i = 0;
                    while (true) {
                        i += nums[i];
                    }
                }
            }
            """, "Solution.Last");

        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Equal(6, trace.ErrorLine);
        Assert.Equal("Your code threw IndexOutOfRangeException at line 6: Index was outside the bounds of the array.",
            trace.Errors.Single());
        Assert.NotEmpty(trace.Steps);
        Assert.Equal(6, trace.Steps[^1].Line);
    }

    [Fact]
    public void An_exception_caught_inside_the_code_does_not_take_the_line_of_a_later_one()
    {
        var trace = Record("""
            // Input: s = "x"
            public class Solution {
                public int Parse(string s) {
                    int value = 0;
                    try { value = int.Parse(s); } catch (System.FormatException) { value = -1; }
                    int[] empty = new int[0];
                    return empty[value + 1];
                }
            }
            """, "Solution.Parse");

        Assert.Equal(7, trace.ErrorLine);
    }

    [Fact]
    public void A_measurement_reports_the_throwing_line_too()
    {
        var result = Runner.Run("""
            public class Solution {
                public int Go() {
                    string s = null;
                    return s.Length;
                }
            }
            """, "Go");

        Assert.Equal(RunStatus.RuntimeError, result.Status);
        Assert.Equal(4, result.ErrorLine);
        Assert.StartsWith("Your code threw NullReferenceException at line 4", result.Errors.Single());
    }
}
