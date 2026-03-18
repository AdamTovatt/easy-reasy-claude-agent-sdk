using EasyReasy.Claude.AgentSdk.Mcp;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Tests that NaN and Infinity values do not crash JSON serialization
/// in the MCP tool result pipeline. See GitHub issue #220.
/// </summary>
public sealed class NonFiniteFloatSerializationTests
{
    #region Tool returning scalar NaN/Infinity

    [Fact]
    public async Task Tool_ReturningDoubleNaN_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("nan_tool", () => double.NaN)
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "nan_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.Equal("text", result.Content[0].Type);
        // The value should be converted to a string, not crash
        Assert.NotNull(result.Content[0].Text);
    }

    [Fact]
    public async Task Tool_ReturningDoublePositiveInfinity_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("inf_tool", () => double.PositiveInfinity)
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "inf_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.NotNull(result.Content[0].Text);
    }

    [Fact]
    public async Task Tool_ReturningDoubleNegativeInfinity_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("neg_inf_tool", () => double.NegativeInfinity)
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "neg_inf_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.NotNull(result.Content[0].Text);
    }

    [Fact]
    public async Task Tool_ReturningFloatNaN_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("float_nan_tool", () => float.NaN)
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "float_nan_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.NotNull(result.Content[0].Text);
    }

    #endregion

    #region Tool returning complex object with NaN/Infinity properties

    private sealed class ResultWithNaN
    {
        public string Name { get; set; } = "test";
        public double Value { get; set; } = double.NaN;
        public double Score { get; set; } = 42.0;
    }

    private sealed class ResultWithInfinity
    {
        public double Min { get; set; } = double.NegativeInfinity;
        public double Max { get; set; } = double.PositiveInfinity;
        public double Normal { get; set; } = 3.14;
    }

    private sealed class ResultWithFloatNaN
    {
        public float Value { get; set; } = float.NaN;
        public float Other { get; set; } = 1.5f;
    }

    [Fact]
    public async Task Tool_ReturningObjectWithNaN_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("obj_nan_tool", () => new ResultWithNaN())
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "obj_nan_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        // Should serialize to valid JSON without crashing
        string text = result.Content[0].Text!;
        // Verify it's valid JSON by parsing it
        JsonDocument.Parse(text);
    }

    [Fact]
    public async Task Tool_ReturningObjectWithInfinity_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("obj_inf_tool", () => new ResultWithInfinity())
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "obj_inf_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        string text = result.Content[0].Text!;
        JsonDocument.Parse(text);
    }

    [Fact]
    public async Task Tool_ReturningObjectWithFloatNaN_DoesNotThrow()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("obj_float_nan_tool", () => new ResultWithFloatNaN())
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        McpToolResult result = await config.Handlers.CallTool!(
            "obj_float_nan_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Single(result.Content);
        string text = result.Content[0].Text!;
        JsonDocument.Parse(text);
    }

    #endregion

    #region SdkMcpBridge serialization with NaN/Infinity

    [Fact]
    public async Task SdkMcpBridge_ToolCallReturningObjectWithNaN_ProducesValidJsonResponse()
    {
        McpServerHandlers handlers = new McpServerHandlers
        {
            CallTool = (name, args, ct) =>
            {
                return Task.FromResult(new McpToolResult
                {
                    Content = [new McpContent { Type = "text", Text = "result with NaN: " + double.NaN }]
                });
            }
        };

        await using SdkMcpBridge bridge = new SdkMcpBridge(handlers, "test-server");
        await bridge.StartAsync();

        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "test_tool", arguments = new { } }
        });

        JsonElement response = await bridge.SendMessageAsync(request);

        // Should produce valid JSONRPC response
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.True(response.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task SdkMcpBridge_ToolCallReturningComplexObjectWithNaN_ProducesValidJsonResponse()
    {
        McpServerHandlers handlers = new McpServerHandlers
        {
            CallTool = (name, args, ct) =>
            {
                // Simulate a tool that returns a complex object with NaN
                // The SDK should serialize this without crashing
                ResultWithNaN data = new ResultWithNaN();
                string serialized = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                });
                return Task.FromResult(McpToolResults.Text(serialized));
            }
        };

        await using SdkMcpBridge bridge = new SdkMcpBridge(handlers, "test-server");
        await bridge.StartAsync();

        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "test_tool", arguments = new { } }
        });

        JsonElement response = await bridge.SendMessageAsync(request);

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.True(response.TryGetProperty("result", out _));
    }

    #endregion

    #region End-to-end: tool result through full serialization chain

    [Fact]
    public async Task FullChain_ToolReturningObjectWithNaN_CanBeSerializedToJson()
    {
        // This test simulates the full serialization chain:
        // 1. Tool handler returns a complex object with NaN
        // 2. ConvertToToolResult serializes it
        // 3. SdkMcpBridge wraps it in JSONRPC
        // 4. The response can be serialized to a JSON string (as QueryHandler does)

        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("data_tool", () => new ResultWithNaN())
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        await using SdkMcpBridge bridge = new SdkMcpBridge(config.Handlers, "test-server");
        await bridge.StartAsync();

        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "data_tool", arguments = new { } }
        });

        JsonElement response = await bridge.SendMessageAsync(request);

        // This is what QueryHandler does at line 403 — should not throw
        object? deserialized = JsonSerializer.Deserialize<object>(response.GetRawText());

        // This is what QueryHandler does at line 270 — should not throw
        Dictionary<string, object?> wrapper = new Dictionary<string, object?>
        {
            ["mcp_response"] = deserialized
        };

        string finalJson = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = "test-1",
                response = wrapper
            }
        });

        // The final JSON should be parseable
        JsonDocument.Parse(finalJson);
    }

    [Fact]
    public async Task FullChain_ToolReturningObjectWithInfinity_CanBeSerializedToJson()
    {
        McpServerRegistry servers = McpServers.Sdk(
            "test",
            s => s.Tool("data_tool", () => new ResultWithInfinity())
        );

        McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(servers["test"]);

        await using SdkMcpBridge bridge = new SdkMcpBridge(config.Handlers, "test-server");
        await bridge.StartAsync();

        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "data_tool", arguments = new { } }
        });

        JsonElement response = await bridge.SendMessageAsync(request);

        object? deserialized = JsonSerializer.Deserialize<object>(response.GetRawText());

        Dictionary<string, object?> wrapper = new Dictionary<string, object?>
        {
            ["mcp_response"] = deserialized
        };

        string finalJson = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = "test-1",
                response = wrapper
            }
        });

        JsonDocument.Parse(finalJson);
    }

    #endregion
}
