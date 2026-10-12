using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// "Check if the code has values, then take those." Examples are found in LeetCode comments,
/// in calls with literal arguments, and in variables declared before such a call.
/// </summary>
public class ExampleFinderTests
{
    private static readonly string[] TwoSumParameters = { "nums", "target" };

    private static ExampleSearch Find(string source, string entry = "Solution.TwoSum", string[]? parameters = null)
        => ExampleFinder.Find(source, entry, parameters ?? TwoSumParameters);

    private const string Solution = """
        public class Solution {
            public int[] TwoSum(int[] nums, int target) { return new int[0]; }
        }
        """;

    [Fact]
    public void LeetCode_example_comments_are_read_with_their_expected_output()
    {
        var search = Find("""
            // Example 1:
            // Input: nums = [2,7,11,15], target = 9
            // Output: [0,1]
            // Explanation: Because nums[0] + nums[1] == 9, we return [0, 1].
            //
            // Example 2:
            // Input: nums = [3,2,4], target = 6
            //
            // Output: [1,2]
            """ + "\n" + Solution);

        Assert.Equal(2, search.Examples.Count);
        Assert.Equal(new[] { "[2,7,11,15]", "9" }, search.Examples[0].Values);
        Assert.Equal("[0,1]", search.Examples[0].Expected);
        Assert.Equal("Example 1", search.Examples[0].Label);
        Assert.Equal(2, search.Examples[0].Line);
        Assert.Equal("[1,2]", search.Examples[1].Expected);
    }

    [Fact]
    public void Block_comments_and_values_spanning_lines_are_read()
    {
        var search = Find("""
            /*
             * Input: nums = [2,7,
             *                11,15], target = 9
             * Output: [0,1]
             */
            """ + "\n" + Solution);

        Assert.Equal(new[] { "[2,7, 11,15]", "9" }, search.Examples.Single().Values);
        Assert.Equal("[0,1]", search.Examples.Single().Expected);
    }

    [Fact]
    public void Any_order_in_the_problem_statement_applies_to_every_example()
    {
        var search = Find("""
            // You can return the answer in any order.
            // Input: nums = [3,2,4], target = 6
            // Output: [1,2]
            """ + "\n" + Solution);
        Assert.True(search.Examples.Single().AnyOrder);
    }

    [Fact]
    public void A_comment_for_another_method_is_not_used()
    {
        var search = Find("// Input: s = \"abc\", k = 2\n" + Solution);
        Assert.Empty(search.Examples);
    }

    [Fact]
    public void A_comment_missing_a_parameter_says_which()
    {
        var search = Find("// Input: nums = [1,2]\n" + Solution);
        var example = search.Examples.Single();
        Assert.Equal(new[] { "[1,2]", null }, example.Values);
        Assert.Equal("Example 1 gives no value for 'target'.", example.Notes.Single().Message);
    }

    [Fact]
    public void Calls_with_literal_arguments_are_examples_in_every_way_CSharp_writes_them()
    {
        var search = Find(Solution + """

            public static class Program {
                public static void Main() {
                    var s = new Solution();
                    s.TwoSum(new[] { 2, 7, 11, 15 }, 9);
                    s.TwoSum(new int[] { -1, 0 }, -1);
                    s.TwoSum([1, 2], 3);
                    s.TwoSum(target: 4, nums: new int[0]);
                }
            }
            """);

        Assert.Equal(new[] { "[2,7,11,15]", "9" }, search.Examples[0].Values);
        Assert.Equal(new[] { "[-1,0]", "-1" }, search.Examples[1].Values);
        Assert.Equal(new[] { "[1,2]", "3" }, search.Examples[2].Values);
        Assert.Equal(new[] { "[]", "4" }, search.Examples[3].Values);
        Assert.All(search.Examples, e => Assert.Equal(ExampleOrigin.Call, e.Origin));
        Assert.Equal("Call on line 7", search.Examples[0].Label);
    }

    [Fact]
    public void Variables_declared_with_values_before_the_call_are_followed()
    {
        var search = Find(Solution + """

            int[] data = { 1, 5, 9 };
            int goal = 14;
            new Solution().TwoSum(data, goal);
            """);
        Assert.Equal(new[] { "[1,5,9]", "14" }, search.Examples.Single().Values);
    }

    [Fact]
    public void Collections_of_every_practice_shape_are_read_from_code()
    {
        var search = ExampleFinder.Find("""
            using System.Collections.Generic;
            public class Solution { public int Go(char[][] grid, IList<int> list, string s, char[] chars) => 0; }
            public static class P {
                public static void Main() {
                    new Solution().Go(
                        new char[][] { new[] { '1', '0' }, new[] { '0', '1' } },
                        new List<int> { 4, 5 },
                        "a\"b",
                        "hey".ToCharArray());
                }
            }
            """, "Solution.Go", new[] { "grid", "list", "s", "chars" });

        Assert.Equal(new[] { "[[\"1\",\"0\"],[\"0\",\"1\"]]", "[4,5]", "\"a\\\"b\"", "\"hey\"" }, search.Examples.Single().Values);
    }

    [Fact]
    public void A_value_that_is_not_written_out_is_skipped_with_the_reason()
    {
        var search = Find(Solution + """

            public static class Program {
                static int[] Build() => new int[3];
                public static void Main() {
                    int[] grows = { 1 };
                    grows[0] = 2;
                    new Solution().TwoSum(Build(), 3);
                    new Solution().TwoSum(grows, 3);
                    new Solution().TwoSum(Build(), Count());
                }
                static int Count() => 1;
            }
            """);

        var computed = search.Examples[0];
        Assert.Equal(new[] { null, "3" }, computed.Values);
        Assert.Equal("Line 9 passes `Build()`, which is not a value written out.", computed.Notes.Single().Message);

        var changed = search.Examples[1];
        Assert.Equal("Line 10 passes `grows`, which changes between where it is declared and the call.", changed.Notes.Single().Message);

        // A call with nothing readable is not offered at all, but it is not silently dropped.
        Assert.Equal(2, search.Examples.Count);
        Assert.Equal("The call on line 11 was not used: none of its arguments are values written in the code.", search.Notes.Single());
    }

    [Fact]
    public void Recursive_calls_inside_the_method_are_not_examples()
    {
        var search = ExampleFinder.Find("""
            public class Solution {
                public int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(5);
            }
            """, "Solution.Fib", new[] { "n" });
        Assert.Empty(search.Examples);
    }

    [Fact]
    public void A_call_repeating_a_comment_example_is_offered_once_keeping_the_expected_output()
    {
        var search = Find("""
            // Input: nums = [2,7,11,15], target = 9
            // Output: [0,1]
            """ + "\n" + Solution + """

            new Solution().TwoSum(new[] { 2, 7, 11, 15 }, 9);
            """);

        var only = Assert.Single(search.Examples);
        Assert.Equal("[0,1]", only.Expected);
    }
}
