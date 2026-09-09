using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using Xunit;

namespace DsaViz.Engine.Tests;

public class ParameterBinderTests
{
    private static System.Reflection.ParameterInfo[] ParametersOf(string source, string entry)
    {
        var outcome = Runner.Compile(source, entry, instrument: false);
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors));
        using var solution = outcome.Solution!;
        return solution.Parameters;
    }

    [Fact]
    public void A_collection_parameter_is_inferred_as_a_scaling_collection()
    {
        var specs = ParameterBinder.Infer(
            ParametersOf(Samples.Read("LinearSumSample.cs"), "Sum"));

        var spec = Assert.Single(specs);
        Assert.Equal(InputShape.IntArray, spec.Shape);
        Assert.True(spec.ScalesWithSize);
    }

    [Fact]
    public void A_lone_int_parameter_is_treated_as_the_size_itself()
    {
        // Fib(int n) means n; there is no collection for the size to apply to.
        var spec = Assert.Single(ParameterBinder.Infer(
            ParametersOf(Samples.Read("FibonacciParamSample.cs"), "Fib")));

        Assert.Equal(InputShape.IntScalar, spec.Shape);
        Assert.True(spec.ScalesWithSize);
    }

    [Fact]
    public void An_int_beside_a_collection_stays_fixed_while_the_collection_grows()
    {
        // Search(int[] sorted, int target): growing the target with n would change what
        // is being searched for at every size and make the curve meaningless.
        var specs = ParameterBinder.Infer(
            ParametersOf(Samples.Read("BinarySearchParamSample.cs"), "Search"));

        Assert.Equal(2, specs.Count);
        Assert.True(specs[0].ScalesWithSize);
        Assert.False(specs[1].ScalesWithSize);
    }

    [Fact]
    public void An_unsupported_parameter_type_names_what_is_supported()
    {
        var source = """
            public static class Custom
            {
                public class Node { public int Value; }
                public static int Walk(Node node) => 1;
            }
            """;

        var exception = Assert.Throws<UnsupportedSignatureException>(
            () => ParameterBinder.Infer(ParametersOf(source, "Walk")));

        // The message has to say what to do next, not merely that binding failed.
        Assert.Contains("node", exception.Message);
        Assert.Contains("int[]", exception.Message);
    }

    [Fact]
    public void A_configuration_with_the_wrong_number_of_inputs_is_rejected()
    {
        var parameters = ParametersOf(Samples.Read("BinarySearchParamSample.cs"), "Search");
        var tooFew = new[] { new InputSpec { Shape = InputShape.IntArray } };

        var exception = Assert.Throws<UnsupportedSignatureException>(
            () => ParameterBinder.Validate(parameters, tooFew));

        Assert.Contains("2 parameter(s)", exception.Message);
    }

    [Fact]
    public void A_configuration_with_a_mismatched_shape_is_rejected()
    {
        var parameters = ParametersOf(Samples.Read("LinearSumSample.cs"), "Sum");
        var wrongShape = new[] { new InputSpec { Shape = InputShape.StringValue } };

        Assert.Throws<UnsupportedSignatureException>(
            () => ParameterBinder.Validate(parameters, wrongShape));
    }
}
