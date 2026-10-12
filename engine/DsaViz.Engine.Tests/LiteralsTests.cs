using DsaViz.Engine.Inputs;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Values typed, or found in the code, the way LeetCode writes them. One reader serves all
/// three input sources, so these tests cover each of them at once.
/// </summary>
public class LiteralsTests
{
    public class TreeNode
    {
        public int val;
        public TreeNode? left, right;
        // As LeetCode declares it: optional parameters, so no parameterless constructor.
        public TreeNode(int val = 0, TreeNode? left = null, TreeNode? right = null) { this.val = val; this.left = left; this.right = right; }
    }

    public class ListNode
    {
        public int val;
        public ListNode? next;
        public ListNode(int val = 0, ListNode? next = null) { this.val = val; this.next = next; }
    }

    [Fact]
    public void Every_practice_shape_reads_and_writes_back_unchanged()
    {
        var cases = new (string Text, Type Type)[]
        {
            ("9", typeof(int)),
            ("-42", typeof(int)),
            ("3000000000", typeof(long)),
            ("true", typeof(bool)),
            ("\"abcabcbb\"", typeof(string)),
            ("[2,7,11,15]", typeof(int[])),
            ("[]", typeof(int[])),
            ("[[1,0],[0,1]]", typeof(int[][])),
            ("[[\"1\",\"0\"],[\"0\",\"1\"]]", typeof(char[][])),
            ("[\"eat\",\"tea\"]", typeof(string[])),
            ("[1,2,3]", typeof(IList<int>)),
            ("[[1,2],[0],[0]]", typeof(IList<IList<int>>)),
            ("[3,9,20,null,null,15,7]", typeof(TreeNode)),
            ("[1,null,2,null,3]", typeof(TreeNode)),
            ("[1,2,3,4,5]", typeof(ListNode)),
        };

        foreach (var (text, type) in cases)
            Assert.Equal(text, Literals.Format(Literals.ToObject(text, type)));
    }

    [Fact]
    public void A_tree_is_built_in_level_order_with_null_gaps()
    {
        var root = (TreeNode)Literals.ToObject("[3,9,20,null,null,15,7]", typeof(TreeNode))!;
        Assert.Equal(3, root.val);
        Assert.Equal(9, root.left!.val);
        Assert.Null(root.left.left);
        Assert.Equal(15, root.right!.left!.val);
        Assert.Equal(7, root.right.right!.val);
    }

    [Fact]
    public void Empty_trees_and_lists_are_null()
    {
        Assert.Null(Literals.ToObject("[]", typeof(TreeNode)));
        Assert.Null(Literals.ToObject("[]", typeof(ListNode)));
    }

    [Fact]
    public void A_char_array_can_be_written_as_text_or_as_a_list()
    {
        Assert.Equal("hello".ToCharArray(), Literals.ToObject("\"hello\"", typeof(char[])));
        Assert.Equal("hi".ToCharArray(), Literals.ToObject("[\"h\",\"i\"]", typeof(char[])));
    }

    [Fact]
    public void Spacing_and_quotes_are_forgiving()
    {
        Assert.Equal(new[] { 2, 7 }, Literals.ToObject("  [ 2 , 7 ]  ", typeof(int[])));
        Assert.Equal("ab", Literals.ToObject("'ab'", typeof(string)));
        Assert.Equal("a\"b", Literals.ToObject("\"a\\\"b\"", typeof(string)));
    }

    [Theory]
    [InlineData("[1,2,", typeof(int[]), "expected a value at the end")]
    [InlineData("[1,\"a\"]", typeof(int[]), "expected a whole number at character 4")]
    [InlineData("[1 2]", typeof(int[]), "expected , or ] at character 4")]
    [InlineData("x", typeof(int), "expected a whole number, like 9")]
    [InlineData("abc", typeof(string), "expected text in double quotes, like \"abc\"")]
    [InlineData("\"abc", typeof(string), "the text starting at character 1 has no closing \"")]
    [InlineData("2.5", typeof(int), "expected a whole number at character 1")]
    [InlineData("99999999999", typeof(int), "99999999999 is too large at character 1")]
    [InlineData("[1,2] 3", typeof(int[]), "unexpected text after the value at character 7")]
    [InlineData("[1,null,null,2]", typeof(TreeNode), "the value at character 14 has no parent: every node above it is null")]
    [InlineData("[\"ab\"]", typeof(char[]), "expected a single character, like \"a\" at character 2")]
    [InlineData("", typeof(int), "expected a whole number, like 9")]
    public void A_value_that_cannot_be_read_says_where_and_why(string text, Type type, string message)
    {
        var error = Assert.Throws<LiteralException>(() => Literals.ToObject(text, type));
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("[0,1]", "[0, 1]", true)]
    [InlineData("2.00000", "2", true)]
    [InlineData("[0,1]", "[1,0]", false)]
    [InlineData("[[1],[2,3]]", "[[1],[2,3]]", true)]
    [InlineData("true", "\"true\"", false)]
    [InlineData("null", "[]", false)]
    public void Outputs_are_compared_by_value_not_by_spelling(string expected, string actual, bool same)
        => Assert.Equal(same, Literals.SameValue(Literals.Parse(expected), Literals.Parse(actual)));

    [Theory]
    [InlineData("[0,1]", "[1,0]", true)]
    [InlineData("[[\"ate\",\"eat\"],[\"bat\"]]", "[[\"bat\"],[\"eat\",\"ate\"]]", true)]
    [InlineData("[1,1,2]", "[1,2,2]", false)]
    [InlineData("[0,1]", "[0,1,2]", false)]
    public void Any_order_compares_lists_as_collections_at_every_level(string expected, string actual, bool same)
        => Assert.Equal(same, Literals.SameValueAnyOrder(Literals.Parse(expected), Literals.Parse(actual)));

    [Fact]
    public void A_cyclic_list_is_written_out_with_a_limit_rather_than_forever()
    {
        var a = new ListNode(1);
        a.next = a;
        Assert.EndsWith(",…]", Literals.Format(a));
    }
}
