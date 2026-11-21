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

    private sealed class GetOnlyArgs
    {
        public GetOnlyArgs(string name, string? nickname)
        {
            Name = name;
            Nickname = nickname;
        }

        public string Name { get; }
        public string? Nickname { get; }
    }

    [Fact]
    public async Task Tool_WithGetOnlyArgsProperties_RequiresOnlyThoseThatAreNotNullable()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "greeter",
            s => s.Tool("greet", (GetOnlyArgs args) => args.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "greeter");

        Assert.Equal(["name"], RequiredNames(schema));
    }

    private sealed record TreeNode(string Name, TreeNode? Child);

    [Fact]
    public async Task Tool_WithSelfReferencingArgsObject_InlinesItAtTheRootAndDefinesItForItsReferences()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "trees",
            s => s.Tool("walk", (TreeNode root) => root.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "trees");

        AssertJson(
            """
            {"type":"object","properties":{"name":{"type":"string"},"child":{"$ref":"#/$defs/TreeNode"}},"required":["name"],
             "$defs":{"TreeNode":{"type":"object","properties":{"name":{"type":"string"},"child":{"$ref":"#/$defs/TreeNode"}},"required":["name"]}}}
            """,
            schema);
    }

    [Fact]
    public async Task Tool_WithOneCyclicTypeAsTwoParameters_ReferencesOneDefinitionFromBoth()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "trees",
            s => s.Tool("compare", (TreeNode left, TreeNode? right) => left.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "trees");

        AssertJson(
            """
            {"type":"object","properties":{"left":{"$ref":"#/$defs/TreeNode"},"right":{"$ref":"#/$defs/TreeNode"}},"required":["left"],
             "$defs":{"TreeNode":{"type":"object","properties":{"name":{"type":"string"},"child":{"$ref":"#/$defs/TreeNode"}},"required":["name"]}}}
            """,
            schema);
    }

    private sealed record Employee(string Name, Team Team);
    private sealed record Team(string Title, Employee? Lead);

    [Fact]
    public async Task Tool_WithIndirectCycle_DefinesEveryTypeOnTheCycle()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "org",
            s => s.Tool("hire", (Employee employee, int salary) => employee.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "org");

        AssertJson(
            """
            {"type":"object","properties":{"employee":{"$ref":"#/$defs/Employee"},"salary":{"type":"integer"}},"required":["employee","salary"],
             "$defs":{
               "Employee":{"type":"object","properties":{"name":{"type":"string"},"team":{"$ref":"#/$defs/Team"}},"required":["name","team"]},
               "Team":{"type":"object","properties":{"title":{"type":"string"},"lead":{"$ref":"#/$defs/Employee"}},"required":["title"]}}}
            """,
            schema);
    }

    private sealed record Folder(string Name, List<Folder> Children);

    [Fact]
    public async Task Tool_WithCycleThroughACollection_ReferencesTheDefinitionFromTheItems()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "files",
            s => s.Tool("list", (Folder folder) => folder.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "files");

        AssertJson(
            """
            {"type":"object","properties":{"name":{"type":"string"},"children":{"type":"array","items":{"$ref":"#/$defs/Folder"}}},"required":["name","children"],
             "$defs":{"Folder":{"type":"object","properties":{"name":{"type":"string"},"children":{"type":"array","items":{"$ref":"#/$defs/Folder"}}},"required":["name","children"]}}}
            """,
            schema);
    }

    private sealed record City(string Name, Dictionary<string, City> Roads);

    [Fact]
    public async Task Tool_WithCycleThroughADictionary_ReferencesTheDefinitionFromTheValues()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "maps",
            s => s.Tool("route", (string from, City to) => from)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "maps");

        AssertJson(
            """
            {"type":"object","properties":{"from":{"type":"string"},"to":{"$ref":"#/$defs/City"}},"required":["from","to"],
             "$defs":{"City":{"type":"object","properties":{"name":{"type":"string"},"roads":{"type":"object","additionalProperties":{"$ref":"#/$defs/City"}}},"required":["name","roads"]}}}
            """,
            schema);
    }

    private static class Elsewhere
    {
        public sealed record TreeNode(string Tag, TreeNode? Parent);
    }

    private sealed record Forest(TreeNode Oak, Elsewhere.TreeNode Pine);

    [Fact]
    public async Task Tool_WithCyclicTypesSharingAName_NumbersTheLaterDefinition()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "woods",
            s => s.Tool("plant", (Forest forest) => "planted")
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "woods");

        AssertJson(
            """
            {"type":"object","properties":{"oak":{"$ref":"#/$defs/TreeNode"},"pine":{"$ref":"#/$defs/TreeNode2"}},"required":["oak","pine"],
             "$defs":{
               "TreeNode":{"type":"object","properties":{"name":{"type":"string"},"child":{"$ref":"#/$defs/TreeNode"}},"required":["name"]},
               "TreeNode2":{"type":"object","properties":{"tag":{"type":"string"},"parent":{"$ref":"#/$defs/TreeNode2"}},"required":["tag"]}}}
            """,
            schema);
    }

    private sealed record Link<T>(T Value, Link<T>? Next);

    [Fact]
    public async Task Tool_WithCyclicGenericTypes_NamesEachDefinitionWithItsTypeArguments()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "chains",
            s => s.Tool("join", (Link<int> numbers, Link<string[]> words) => "joined")
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "chains");

        AssertJson(
            """
            {"type":"object","properties":{"numbers":{"$ref":"#/$defs/LinkOfInt32"},"words":{"$ref":"#/$defs/LinkOfArrayOfString"}},"required":["numbers","words"],
             "$defs":{
               "LinkOfInt32":{"type":"object","properties":{"value":{"type":"integer"},"next":{"$ref":"#/$defs/LinkOfInt32"}},"required":["value"]},
               "LinkOfArrayOfString":{"type":"object","properties":{"value":{"type":"array","items":{"type":"string"}},"next":{"$ref":"#/$defs/LinkOfArrayOfString"}}}}}
            """,
            schema);
    }

    private sealed record Leaf(string Name);
    private sealed record Branch(Leaf First, Leaf? Second);

    [Fact]
    public async Task Tool_WithAcyclicTypeUsedTwice_InlinesItAtEachUseWithoutDefinitions()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "trees",
            s => s.Tool("grow", (Branch branch) => branch.First.Name)
        );

        JsonElement schema = await GetOnlyToolSchemaAsync(servers, "trees");

        AssertJson(
            """
            {"type":"object","properties":{
               "first":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]},
               "second":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}},
             "required":["first"]}
            """,
            schema);
    }

    private static async Task<JsonElement> GetOnlyToolSchemaAsync(McpServerRegistry servers, string serverName)
    {
        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers[serverName]);
        IReadOnlyList<McpToolDefinition> tools = await config.Handlers!.ListTools!(CancellationToken.None);
        return Assert.Single(tools).InputSchema!.Value;
    }

    private static IEnumerable<string?> RequiredNames(JsonElement schema)
        => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString());

    /// <summary>
    /// Compares the schema's exact serialized text, property order included, to the expected JSON written with any whitespace.
    /// </summary>
    private static void AssertJson(string expected, JsonElement actual)
    {
        using JsonDocument expectedDocument = JsonDocument.Parse(expected);
        Assert.Equal(JsonSerializer.Serialize(expectedDocument.RootElement), actual.GetRawText());
    }
}
