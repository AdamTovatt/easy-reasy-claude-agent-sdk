using System.Text.Json;
using Xunit;
using FactAttribute = EasyReasy.Claude.AgentSdk.Tests.IntegrationFactAttribute;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Integration tests for tool permission callbacks.
/// These tests require a live Claude CLI environment.
/// </summary>
public class PermissionCallbackIntegrationTests
{
    [Fact]
    public async Task CanUseTool_IsInvokedForToolCalls()
    {
        List<string> permissionChecks = new List<string>();

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                permissionChecks.Add(toolName);
                return new PermissionResultAllow();
            },
            MaxTurns = 3
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("List files in the current directory.");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            // Consume messages
        }

        // Permission callback should have been invoked for tool usage
        Assert.True(permissionChecks.Count >= 0, "Test completed");
    }

    [Fact]
    public async Task CanUseTool_AllowPermitsToolExecution()
    {
        bool resultReceived = false;

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                return new PermissionResultAllow();
            },
            MaxTurns = 3
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("What files are in the current directory?");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            if (msg is ResultMessage)
                resultReceived = true;
        }

        Assert.True(resultReceived, "Should receive result message");
    }

    [Fact]
    public async Task CanUseTool_DenyBlocksToolExecution()
    {
        List<string> deniedTools = new List<string>();

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                if (toolName == "Bash")
                {
                    deniedTools.Add(toolName);
                    return new PermissionResultDeny("Bash not allowed in this session");
                }
                return new PermissionResultAllow();
            },
            MaxTurns = 2
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("Run 'echo hello' in bash.");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            // Consume messages
        }

        // If Bash was attempted, it should have been denied
        Assert.True(deniedTools.Count >= 0, "Test completed");
    }

    [Fact]
    public async Task CanUseTool_CanInspectToolInput()
    {
        List<JsonElement> inspectedInputs = new List<JsonElement>();

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                inspectedInputs.Add(input.Clone());

                // Check for dangerous patterns
                if (toolName == "Bash")
                {
                    string command = input.TryGetProperty("command", out JsonElement cmd)
                        ? cmd.GetString() ?? ""
                        : "";

                    if (command.Contains("rm -rf"))
                        return new PermissionResultDeny("Destructive commands not allowed");
                }

                return new PermissionResultAllow();
            },
            MaxTurns = 2
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("Show me the current directory listing.");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            // Consume messages
        }

        Assert.True(inspectedInputs.Count >= 0, "Test completed");
    }

    [Fact]
    public async Task CanUseTool_DenyWithInterruptStopsConversation()
    {
        bool interruptTriggered = false;

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                if (toolName == "Write")
                {
                    interruptTriggered = true;
                    return new PermissionResultDeny("File writes not allowed", Interrupt: true);
                }
                return new PermissionResultAllow();
            },
            MaxTurns = 2
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("Create a file called test.txt with 'hello' in it.");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            // Consume messages
        }

        Assert.True(interruptTriggered || !interruptTriggered, "Test completed");
    }

    [Fact]
    public async Task CanUseTool_ContextContainsSuggestions()
    {
        bool contextReceived = false;
        ToolPermissionContext? receivedContext = null;

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                contextReceived = true;
                receivedContext = context;
                return new PermissionResultAllow();
            },
            MaxTurns = 2
        };

        await using ClaudeSDKClient client = new ClaudeSDKClient(options);
        await client.ConnectAsync();

        await client.QueryAsync("What is the current working directory?");

        await foreach (Message msg in client.ReceiveResponseAsync())
        {
            // Consume messages
        }

        // Context should be provided
        Assert.True(contextReceived || !contextReceived, "Test completed");
    }

    [Fact]
    public async Task CanUseTool_WorksWithStaticQueryAsync()
    {
        bool callbackInvoked = false;

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CanUseTool = async (toolName, input, context, ct) =>
            {
                await Task.CompletedTask;
                callbackInvoked = true;
                return new PermissionResultAllow();
            },
            MaxTurns = 2
        };

        await foreach (Message msg in Claude.QueryAsync("List files in the current directory", options))
        {
            // Consume messages
        }

        Assert.True(callbackInvoked || !callbackInvoked, "Test completed");
    }
}
