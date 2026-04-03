using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Unit tests for hooks types and structures.
/// </summary>
public class HooksUnitTests
{
    #region HookEvent Tests

    [Fact]
    public void HookEvent_HasExpectedValues()
    {
        Assert.Equal("PreToolUse", HookEvent.PreToolUse.ToString());
        Assert.Equal("PostToolUse", HookEvent.PostToolUse.ToString());
        Assert.Equal("UserPromptSubmit", HookEvent.UserPromptSubmit.ToString());
    }

    #endregion

    #region HookMatcher Tests

    [Fact]
    public void HookMatcher_CreatesWithMatcherAndHooks()
    {
        List<HookCallback> hooks = new List<HookCallback>
        {
            (input, toolUseId, ctx, ct) => Task.FromResult(new HookOutput { Continue = true })
        };

        HookMatcher matcher = new HookMatcher("Bash", hooks);

        Assert.Equal("Bash", matcher.Matcher);
        Assert.NotNull(matcher.Hooks);
        Assert.Single(matcher.Hooks);
    }

    [Fact]
    public void HookMatcher_WildcardMatcherWorks()
    {
        HookMatcher matcher = new HookMatcher(
            Matcher: "*",
            Hooks: [(input, toolUseId, ctx, ct) => Task.FromResult(new HookOutput { Continue = true })]
        );

        Assert.Equal("*", matcher.Matcher);
    }

    [Fact]
    public void HookMatcher_SupportsMultipleHooks()
    {
        HookMatcher matcher = new HookMatcher(
            Matcher: "Read",
            Hooks: [
                (input, toolUseId, ctx, ct) => Task.FromResult(new HookOutput { Continue = true }),
                (input, toolUseId, ctx, ct) => Task.FromResult(new HookOutput { Continue = true }),
                (input, toolUseId, ctx, ct) => Task.FromResult(new HookOutput { Continue = false })
            ]
        );

        Assert.NotNull(matcher.Hooks);
        Assert.Equal(3, matcher.Hooks.Count);
    }

    #endregion

    #region HookOutput Tests

    [Fact]
    public void HookOutput_DefaultsContinueToNull()
    {
        HookOutput output = new HookOutput();
        Assert.Null(output.Continue);
    }

    [Fact]
    public void HookOutput_CanSetContinueTrue()
    {
        HookOutput output = new HookOutput { Continue = true };
        Assert.True(output.Continue);
    }

    [Fact]
    public void HookOutput_CanSetDecision()
    {
        HookOutput output = new HookOutput
        {
            Continue = false,
            Decision = "reject"
        };

        Assert.Equal("reject", output.Decision);
    }

    [Fact]
    public void HookOutput_CanSetReason()
    {
        HookOutput output = new HookOutput
        {
            Continue = false,
            Decision = "reject",
            Reason = "Command not allowed"
        };

        Assert.Equal("Command not allowed", output.Reason);
    }

    [Fact]
    public void HookOutput_CanSetAllProperties()
    {
        HookOutput output = new HookOutput
        {
            Continue = false,
            Decision = "reject",
            Reason = "Security policy violation"
        };

        Assert.False(output.Continue);
        Assert.Equal("reject", output.Decision);
        Assert.Equal("Security policy violation", output.Reason);
    }

    #endregion

    #region HookCallback Delegate Tests

    [Fact]
    public async Task HookCallback_CanBeInvokedDirectly()
    {
        bool invoked = false;

        HookCallback callback = (input, toolUseId, ctx, ct) =>
        {
            invoked = true;
            return Task.FromResult(new HookOutput { Continue = true });
        };

        JsonElement input = JsonSerializer.SerializeToElement(new { command = "ls" });
        HookContext context = new HookContext();
        HookOutput result = await callback(input, "tool-123", context, CancellationToken.None);

        Assert.True(invoked);
        Assert.True(result.Continue);
    }

    [Fact]
    public async Task HookCallback_ReceivesInputCorrectly()
    {
        JsonElement? receivedInput = null;

        HookCallback callback = (input, toolUseId, ctx, ct) =>
        {
            receivedInput = input.Clone();
            return Task.FromResult(new HookOutput { Continue = true });
        };

        JsonElement input = JsonSerializer.SerializeToElement(new { command = "echo hello", timeout = 30 });
        HookContext context = new HookContext();
        await callback(input, "tool-456", context, CancellationToken.None);

        Assert.NotNull(receivedInput);
        Assert.Equal("echo hello", receivedInput.Value.GetProperty("command").GetString());
        Assert.Equal(30, receivedInput.Value.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task HookCallback_ReceivesToolUseId()
    {
        string? receivedToolUseId = null;

        HookCallback callback = (input, toolUseId, ctx, ct) =>
        {
            receivedToolUseId = toolUseId;
            return Task.FromResult(new HookOutput { Continue = true });
        };

        JsonElement input = JsonSerializer.SerializeToElement(new { });
        HookContext context = new HookContext();
        await callback(input, "tool-use-abc123", context, CancellationToken.None);

        Assert.Equal("tool-use-abc123", receivedToolUseId);
    }

    [Fact]
    public async Task HookCallback_ReceivesContext()
    {
        HookContext? receivedContext = null;

        HookCallback callback = (input, toolUseId, ctx, ct) =>
        {
            receivedContext = ctx;
            return Task.FromResult(new HookOutput { Continue = true });
        };

        JsonElement input = JsonSerializer.SerializeToElement(new { });
        HookContext context = new HookContext(Signal: "test-signal");
        await callback(input, "tool-789", context, CancellationToken.None);

        Assert.NotNull(receivedContext);
        Assert.Equal("test-signal", receivedContext.Signal);
    }

    [Fact]
    public async Task HookCallback_SupportsCancellation()
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        bool cancellationObserved = false;

        HookCallback callback = async (input, toolUseId, ctx, ct) =>
        {
            if (ct.IsCancellationRequested)
                cancellationObserved = true;

            await Task.Delay(10, ct);
            return new HookOutput { Continue = true };
        };

        cts.Cancel();
        JsonElement input = JsonSerializer.SerializeToElement(new { });
        HookContext context = new HookContext();

        try
        {
            await callback(input, "tool", context, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        Assert.True(cancellationObserved);
    }

    #endregion

    #region HookContext Tests

    [Fact]
    public void HookContext_DefaultSignalIsNull()
    {
        HookContext context = new HookContext();
        Assert.Null(context.Signal);
    }

    [Fact]
    public void HookContext_CanSetSignal()
    {
        HookContext context = new HookContext(Signal: "my-signal");
        Assert.Equal("my-signal", context.Signal);
    }

    #endregion

    #region PostToolUseHookInput Deserialization Tests

    [Fact]
    public void PostToolUseHookInput_DeserializesIsError_WhenTrue()
    {
        string json = """{"session_id":"s","transcript_path":"/t","cwd":"/c","tool_name":"Bash","tool_input":{},"tool_response":{},"is_error":true}""";
        PostToolUseHookInput input = JsonSerializer.Deserialize<PostToolUseHookInput>(json)!;

        Assert.True(input.IsError);
    }

    [Fact]
    public void PostToolUseHookInput_DeserializesIsError_WhenFalse()
    {
        string json = """{"session_id":"s","transcript_path":"/t","cwd":"/c","tool_name":"Bash","tool_input":{},"tool_response":{},"is_error":false}""";
        PostToolUseHookInput input = JsonSerializer.Deserialize<PostToolUseHookInput>(json)!;

        Assert.False(input.IsError);
    }

    [Fact]
    public void PostToolUseHookInput_IsErrorIsNull_WhenAbsent()
    {
        string json = """{"session_id":"s","transcript_path":"/t","cwd":"/c","tool_name":"Bash","tool_input":{},"tool_response":{}}""";
        PostToolUseHookInput input = JsonSerializer.Deserialize<PostToolUseHookInput>(json)!;

        Assert.Null(input.IsError);
    }

    #endregion

    #region ClaudeAgentOptions Hooks Integration

    [Fact]
    public void ClaudeAgentOptions_HooksDefaultsToNull()
    {
        ClaudeAgentOptions options = new ClaudeAgentOptions();
        Assert.Null(options.Hooks);
    }

    [Fact]
    public void ClaudeAgentOptions_CanSetHooks()
    {
        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [
                    new HookMatcher("*", [(input, id, ctx, ct) =>
                        Task.FromResult(new HookOutput { Continue = true })])
                ]
            }
        };

        Assert.NotNull(options.Hooks);
        Assert.Single(options.Hooks);
        Assert.True(options.Hooks.ContainsKey(HookEvent.PreToolUse));
    }

    [Fact]
    public void ClaudeAgentOptions_CanSetMultipleHookEvents()
    {
        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [
                    new HookMatcher("Bash", [(_, _, _, _) => Task.FromResult(new HookOutput { Continue = true })])
                ],
                [HookEvent.PostToolUse] = [
                    new HookMatcher("*", [(_, _, _, _) => Task.FromResult(new HookOutput { Continue = true })])
                ],
                [HookEvent.UserPromptSubmit] = [
                    new HookMatcher("*", [(_, _, _, _) => Task.FromResult(new HookOutput { Continue = true })])
                ]
            }
        };

        Assert.Equal(3, options.Hooks!.Count);
    }

    #endregion
}
