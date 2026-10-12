namespace DsaViz.Engine.Contracts;

public enum ExampleOrigin
{
    /// <summary>A LeetCode example comment: "Input: nums = [2,7,11,15], target = 9".</summary>
    Comment,

    /// <summary>A call to the method with values written in the code.</summary>
    Call
}

/// <summary>Why one parameter of an example could not be read, in a sentence the student can act on.</summary>
public sealed record ExampleNote(string Parameter, string Message);

/// <summary>
/// Input values found in the code. <see cref="Values"/> holds one LeetCode-format text per
/// parameter, null where the example had nothing readable for it.
/// </summary>
public sealed record FoundExample(
    string Label,
    int Line,
    ExampleOrigin Origin,
    IReadOnlyList<string?> Values,
    IReadOnlyList<ExampleNote> Notes)
{
    /// <summary>The example's "Output:", when it gives one, to check the result against.</summary>
    public string? Expected { get; init; }

    /// <summary>The problem accepts the answer in any order, so lists are compared as collections.</summary>
    public bool AnyOrder { get; init; }
}

/// <summary>Everything found, plus calls that were seen but could not be used at all.</summary>
public sealed record ExampleSearch(IReadOnlyList<FoundExample> Examples, IReadOnlyList<string> Notes);
