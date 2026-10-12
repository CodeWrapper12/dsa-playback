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

/// <summary>
/// One example run for the "Run all examples" list: what it gave back against what its
/// comment expected. <see cref="Matches"/> is null when there is nothing to compare —
/// no Output, an Output in prose, or a run that did not finish.
/// </summary>
public sealed record ExampleRun(int Index, string Label, int Line, RunStatus Status)
{
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public bool? Matches { get; init; }
    public bool AnyOrder { get; init; }

    /// <summary>Why it did not finish, or which value could not be read, in plain words.</summary>
    public string? Error { get; init; }
    public int? ErrorLine { get; init; }
}

/// <summary>Every example in a file, run one after another.</summary>
public sealed record ExampleReport(RunStatus Status, IReadOnlyList<ExampleRun> Runs)
{
    /// <summary>Why nothing ran: the code does not compile, or the method was not found.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
