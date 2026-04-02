using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

public sealed class ClaudeAgentOptionsBuilderTests
{
    [Fact]
    public void Build_WithDefaults_ReturnsEmptyOptions()
    {
        ClaudeAgentOptions options = Claude.Options().Build();

        Assert.Null(options.SystemPrompt);
        Assert.Null(options.AppendSystemPrompt);
        Assert.Null(options.Model);
        Assert.Empty(options.AllowedTools);
        Assert.Empty(options.DisallowedTools);
    }

    [Fact]
    public void SystemPrompt_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .SystemPrompt("You are helpful.")
            .Build();

        Assert.Equal("You are helpful.", options.SystemPrompt);
    }

    [Fact]
    public void AppendSystemPrompt_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .AppendSystemPrompt("Extra context here.")
            .Build();

        Assert.Equal("Extra context here.", options.AppendSystemPrompt);
        Assert.Null(options.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_And_AppendSystemPrompt_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Claude.Options()
                .SystemPrompt("Replace.")
                .AppendSystemPrompt("Append.")
                .Build());

        Assert.Contains("mutually exclusive", exception.Message);
    }

    [Fact]
    public void Model_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .Model("claude-sonnet-4-20250514")
            .FallbackModel("claude-haiku")
            .Build();

        Assert.Equal("claude-sonnet-4-20250514", options.Model);
        Assert.Equal("claude-haiku", options.FallbackModel);
    }

    [Fact]
    public void MaxTurns_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .MaxTurns(10)
            .Build();

        Assert.Equal(10, options.MaxTurns);
    }

    [Fact]
    public void MaxBudget_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .MaxBudget(5.00m)
            .Build();

        Assert.Equal(5.00m, options.MaxBudgetUsd);
    }

    [Fact]
    public void AllowTools_AddsToList()
    {
        ClaudeAgentOptions options = Claude.Options()
            .AllowTools("Bash", "Read")
            .AllowTools("Write")
            .Build();

        Assert.Equal(["Bash", "Read", "Write"], options.AllowedTools);
    }

    [Fact]
    public void DisallowTools_AddsToList()
    {
        ClaudeAgentOptions options = Claude.Options()
            .DisallowTools("Bash")
            .Build();

        Assert.Equal(["Bash"], options.DisallowedTools);
    }

    [Fact]
    public void Cwd_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .Cwd("/home/user/project")
            .Build();

        Assert.Equal("/home/user/project", options.Cwd);
    }

    [Fact]
    public void PermissionMode_SetsValue()
    {
        ClaudeAgentOptions options = Claude.Options()
            .AcceptEdits()
            .Build();

        Assert.Equal(AgentSdk.PermissionMode.AcceptEdits, options.PermissionMode);
    }

    [Fact]
    public void Env_SetsVariables()
    {
        ClaudeAgentOptions options = Claude.Options()
            .Env("FOO", "bar")
            .Env("BAZ", "qux")
            .Build();

        Assert.Equal("bar", options.Env["FOO"]);
        Assert.Equal("qux", options.Env["BAZ"]);
    }

    [Fact]
    public void Betas_AddsToList()
    {
        ClaudeAgentOptions options = Claude.Options()
            .Betas("feature1", "feature2")
            .Build();

        Assert.Equal(["feature1", "feature2"], options.Betas);
    }

    [Fact]
    public void AllowAllTools_SetsCallback()
    {
        ClaudeAgentOptions options = Claude.Options()
            .AllowAllTools()
            .Build();

        Assert.NotNull(options.CanUseTool);
    }

    [Fact]
    public async Task AllowAllTools_CallbackReturnsAllow()
    {
        ClaudeAgentOptions options = Claude.Options()
            .AllowAllTools()
            .Build();

        PermissionResult result = await options.CanUseTool!(
            "Bash",
            JsonSerializer.SerializeToElement(new { }),
            new ToolPermissionContext(),
            CancellationToken.None
        );

        Assert.IsType<PermissionResultAllow>(result);
    }

    [Fact]
    public void CanUseTool_SetsCallback()
    {
        CanUseToolCallback callback = (_, _, _, _) =>
            Task.FromResult<PermissionResult>(new PermissionResultDeny("Denied"));

        ClaudeAgentOptions options = Claude.Options()
            .CanUseTool(callback)
            .Build();

        Assert.Equal(callback, options.CanUseTool);
    }

    [Fact]
    public void Chaining_ReturnsSameInstance()
    {
        ClaudeAgentOptionsBuilder builder = Claude.Options();

        ClaudeAgentOptionsBuilder result = builder
            .SystemPrompt("test")
            .Model("model")
            .MaxTurns(5)
            .AllowTools("Bash");

        Assert.Same(builder, result);
    }

    [Fact]
    public void CanUseTool_WithBypassPermissions_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Claude.Options()
                .CanUseTool((toolName, input, context, ct) =>
                    Task.FromResult<PermissionResult>(new PermissionResultDeny()))
                .BypassPermissions()
                .Build());

        Assert.Contains("CanUseTool cannot be used with BypassPermissions", exception.Message);
    }

    [Fact]
    public void BypassPermissions_WithCanUseTool_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            Claude.Options()
                .BypassPermissions()
                .CanUseTool((toolName, input, context, ct) =>
                    Task.FromResult<PermissionResult>(new PermissionResultDeny()))
                .Build());

        Assert.Contains("CanUseTool cannot be used with BypassPermissions", exception.Message);
    }

    [Fact]
    public void CompleteExample_BuildsCorrectOptions()
    {
        ClaudeAgentOptions options = Claude.Options()
            .SystemPrompt("You are a helpful assistant.")
            .Model("claude-sonnet-4-20250514")
            .MaxTurns(10)
            .MaxBudget(5.00m)
            .AllowTools("Bash", "Read", "Write")
            .Cwd("/home/user")
            .AcceptEdits()
            .Env("DEBUG", "true")
            .Build();

        Assert.Equal("You are a helpful assistant.", options.SystemPrompt);
        Assert.Equal("claude-sonnet-4-20250514", options.Model);
        Assert.Equal(10, options.MaxTurns);
        Assert.Equal(5.00m, options.MaxBudgetUsd);
        Assert.Equal(["Bash", "Read", "Write"], options.AllowedTools);
        Assert.Equal("/home/user", options.Cwd);
        Assert.Equal(AgentSdk.PermissionMode.AcceptEdits, options.PermissionMode);
        Assert.Equal("true", options.Env["DEBUG"]);
    }
}
