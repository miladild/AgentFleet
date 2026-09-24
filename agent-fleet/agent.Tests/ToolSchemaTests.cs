using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

/// <summary>
/// Ollama parses a tool's parameter schema into typed structs and rejects any property that is
/// not a JSON object ("cannot unmarshal bool into ... ToolProperty"). A raw JsonElement
/// parameter is described as the literal "true", so it needs a description to become an object.
/// This has already cost one failed planning run, so it is pinned here.
/// </summary>
public sealed class ToolSchemaTests
{
    private static JsonElement Properties(AIFunction function) => function.JsonSchema.GetProperty("properties");

    [Fact]
    public void A_raw_json_parameter_with_a_description_is_an_object_schema_which_ollama_accepts()
    {
        AIFunction function = AIFunctionFactory.Create(
            ([Description("Array of strings")] JsonElement? items, string name) => "x",
            name: "t");

        foreach (JsonProperty property in Properties(function).EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
        }

        Assert.Equal("Array of strings", Properties(function).GetProperty("items").GetProperty("description").GetString());
    }

    [Fact]
    public void Without_a_description_a_raw_json_parameter_is_the_bare_boolean_ollama_rejects()
    {
        AIFunction function = AIFunctionFactory.Create((JsonElement? items) => "x", name: "t");

        Assert.NotEqual(JsonValueKind.Object, Properties(function).GetProperty("items").ValueKind);
    }

    // A nullable type is not the same as optional. Measured on the running fleet: a model that
    // left out `diagram` (typed string?) got "missing a value for the required parameter". Only a
    // default value makes a parameter optional in the schema and in binding.
    [Fact]
    public async Task Only_parameters_with_defaults_are_optional_and_may_be_left_out()
    {
        AIFunction strict = AIFunctionFactory.Create((string a, string? b, int? c) => a, name: "strict");
        AIFunction lenient = AIFunctionFactory.Create(
            (string a, string? b = null, int? c = null, CancellationToken cancellationToken = default) => a + (b ?? "-") + (c?.ToString() ?? "-"),
            name: "lenient");

        string[] Required(AIFunction f) =>
            f.JsonSchema.TryGetProperty("required", out JsonElement r) ? r.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];

        Assert.Contains("b", Required(strict));
        Assert.Equal(["a"], Required(lenient));
        Assert.Equal("x--", (await lenient.InvokeAsync(new AIFunctionArguments { ["a"] = "x" }))?.ToString());
        await Assert.ThrowsAsync<ArgumentException>(async () => await strict.InvokeAsync(new AIFunctionArguments { ["a"] = "x" }));
    }

    [Fact]
    public void The_optional_scalar_parameters_the_tools_use_are_object_schemas()
    {
        AIFunction function = AIFunctionFactory.Create(
            (string path, int? startLine, int? endLine, bool? replaceAll, string? note) => "x",
            name: "t");

        foreach (JsonProperty property in Properties(function).EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
        }
    }
}
