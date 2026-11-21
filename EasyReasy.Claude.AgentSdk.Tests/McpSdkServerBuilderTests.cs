using EasyReasy.Claude.AgentSdk.Mcp;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

public sealed class McpSdkServerBuilderTests
{
    [Fact]
    public async Task Tool_WithPrimitiveParameters_GeneratesSchemaAndInvokes()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "calculator",
            s => s.Tool("add", (double a, double b) => a + b, "Add two numbers")
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["calculator"]);
        Assert.NotNull(config.Handlers);

        IReadOnlyList<McpToolDefinition> tools = await config.Handlers.ListTools!(CancellationToken.None);
        Assert.Single(tools);
        Assert.Equal("add", tools[0].Name);
        Assert.Equal("Add two numbers", tools[0].Description);

        JsonElement schema = tools[0].InputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("a").GetProperty("type").GetString());
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("b").GetProperty("type").GetString());

        McpToolResult result = await config.Handlers.CallTool!(
            "add",
            JsonSerializer.SerializeToElement(new { A = 2, B = 3 }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.Equal("text", result.Content[0].Type);
        Assert.Equal("5", result.Content[0].Text);
    }

    private sealed record AddArgs(double A, double B);

    [Fact]
    public async Task Tool_WithArgsObject_BindsWholeArgsObject()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "calculator",
            s => s.Tool("add", (AddArgs args) => args.A + args.B)
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["calculator"]);
        IReadOnlyList<McpToolDefinition> tools = await config.Handlers.ListTools!(CancellationToken.None);
        Assert.Single(tools);

        JsonElement schema = tools[0].InputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("a", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("b", out _));

        McpToolResult result = await config.Handlers.CallTool!(
            "add",
            JsonSerializer.SerializeToElement(new { A = 10, B = 20 }),
            CancellationToken.None
        );

        Assert.Equal("30", result.Content[0].Text);
    }

    [Fact]
    public async Task Tool_WithParameters_RequiresOnlyThoseThatAreNotNullableAndHaveNoDefault()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "greeter",
            s => s.Tool(
                "greet",
                (string name, string? nickname, int count, int? limit, string greeting = "Hello") => greeting)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "greeter");

        Assert.Equal(["name", "count"], RequiredNames(schema));
    }

    private sealed record RequiredArgs(
        string Name,
        string? Nickname,
        [property: System.ComponentModel.DataAnnotations.Required] string? Code,
        int Count,
        int? Limit);

    [Fact]
    public async Task Tool_WithArgsObject_RequiresPropertiesThatAreNotNullableOrMarkedRequired()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "greeter",
            s => s.Tool("greet", (RequiredArgs args) => args.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "greeter");

        Assert.Equal(["name", "code", "count"], RequiredNames(schema));
    }

    private static async Task<JsonElement> GetOnlyToolSchemaAsync(McpServerRegistry servers, string serverName)
    {
        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers[serverName]);
        IReadOnlyList<McpToolDefinition> tools = await config.Handlers!.ListTools!(CancellationToken.None);
        return Assert.Single(tools).InputSchema!.Value;
    }

    private static IEnumerable<string?> RequiredNames(JsonElement schema)
        => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString());
}
