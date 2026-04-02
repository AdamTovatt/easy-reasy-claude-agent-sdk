# Claude Agent SDK for .NET

[![Tests](https://github.com/AdamTovatt/easy-reasy-claude-agent-sdk/actions/workflows/build.yml/badge.svg)](https://github.com/AdamTovatt/easy-reasy-claude-agent-sdk/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/badge/nuget-EasyReasy.Claude.AgentSdk-blue.svg)](https://www.nuget.org/packages/EasyReasy.Claude.AgentSdk)

A modern .NET library for interacting with the Claude Code CLI, providing both a simple one-shot `QueryAsync()` API and a full bidirectional client with control-protocol support.

## Overview

Build .NET applications that leverage the Claude Code CLI — from simple one-shot queries to full multi-turn conversations with tool control, hooks, MCP servers, and custom agents.

## Features

- Simple `Claude.QueryAsync()` API for one-shot requests
- `ClaudeSDKClient` for multi-turn, bidirectional conversations
- Control protocol support (interrupts, modes, dynamic model switching)
- Hook system (PreToolUse, PostToolUse, UserPromptSubmit)
- Tool permission callbacks with allow/deny control
- In-process MCP server support (tools, prompts, resources)
- Cross-platform: Windows, Linux, macOS
- Source-generated JSON models for message types
- Well-tested: 128 tests (123 unit + 5 integration; integration tests are disabled by default)

## Prerequisites

- .NET 8.0 or later
- Claude Code CLI >= 2.0.0 (https://code.claude.com/docs/en/setup)

> The SDK discovers the Claude Code CLI in this order:
> 1. `ClaudeAgentOptions.CliPath` (explicit path)
> 2. `CLAUDE_CLI_PATH` environment variable
> 3. `PATH` search for `claude` (or `claude.cmd` on Windows)

## Quick Start

### One-Shot Query

```csharp
using EasyReasy.Claude.AgentSdk;

await foreach (Message message in Claude.QueryAsync("What is 2+2?"))
{
    if (message is AssistantMessage assistantMessage)
        foreach (ContentBlock block in assistantMessage.Content)
            if (block is TextBlock textBlock)
                Console.Write(textBlock.Text);
}
```

### Multi-Turn Conversation

```csharp
using EasyReasy.Claude.AgentSdk;

await using ClaudeSDKClient client = new ClaudeSDKClient();
await client.ConnectAsync();

await client.QueryAsync("Write a Python hello world");

await foreach (Message message in client.ReceiveResponseAsync())
{
    if (message is AssistantMessage assistantMessage)
        foreach (ContentBlock block in assistantMessage.Content)
            if (block is TextBlock textBlock)
                Console.Write(textBlock.Text);
}
```

### With Options

```csharp
ClaudeAgentOptions options = Claude.Options()
    .SystemPrompt("You are a helpful coding assistant.")
    .MaxTurns(5)
    .Model("claude-sonnet-4-20250514")
    .AcceptEdits()
    .Build();

await foreach (Message message in Claude.QueryAsync("Explain async/await", options))
{
    // handle messages
}
```

## Core Concepts

### Message Types

- `AssistantMessage` - Claude's response with `Content` blocks
- `UserMessage` - User input
- `SystemMessage` - System notifications
- `ResultMessage` - Query completion with cost/duration info

### Content Blocks

- `TextBlock` - Text content
- `ThinkingBlock` - Extended thinking (with signature)
- `ToolUseBlock` - Tool invocation
- `ToolResultBlock` - Tool output

### Configuration

`ClaudeAgentOptions` mirrors the Python SDK's options:

| Property | Type | Description |
|----------|------|-------------|
| `SystemPrompt` | `string?` | Replace the default system prompt |
| `AppendSystemPrompt` | `string?` | Append to the default system prompt |
| `MaxTurns` | `int?` | Maximum conversation turns |
| `MaxBudgetUsd` | `decimal?` | Spending limit in USD |
| `Model` | `string?` | Model to use |
| `FallbackModel` | `string?` | Fallback model |
| `PermissionMode` | `PermissionMode?` | Default, AcceptEdits, Plan, BypassPermissions (cannot combine BypassPermissions with CanUseTool) |
| `McpServers` | `object?` | MCP server configurations |
| `CanUseTool` | `CanUseToolCallback?` | Tool permission callback (cannot combine with BypassPermissions) |
| `Hooks` | `IReadOnlyDictionary<...>?` | Event hooks |
| `AllowedTools` | `IReadOnlyList<string>` | Whitelist tools |
| `DisallowedTools` | `IReadOnlyList<string>` | Blacklist tools |
| `Cwd` | `string?` | Working directory |
| `CliPath` | `string?` | Explicit CLI path |

## Advanced Usage

### Tool Permission Callback

> **Warning:** `CanUseTool` cannot be combined with `BypassPermissions()`. Bypass mode causes the CLI to auto-allow all tools without consulting the callback, silently making it ineffective. The builder will throw `InvalidOperationException` if both are set.

```csharp
ClaudeAgentOptions options = Claude.Options()
    .CanUseTool(async (toolName, input, context, cancellationToken) =>
    {
        if (toolName == "Bash" && input.GetProperty("command").GetString()?.Contains("rm") == true)
            return new PermissionResultDeny("Destructive commands not allowed");
        return new PermissionResultAllow();
    })
    .Build();
```

### Hooks

```csharp
ClaudeAgentOptions options = Claude.Options()
    .AllowTools("Bash")
    .Hooks(hooks => hooks
        .PreToolUse("Bash", (input, toolUseId, context, cancellationToken) =>
        {
            Console.WriteLine($"[Hook] Bash: {input}");
            return Task.FromResult(new HookOutput { Continue = true });
        }))
    .Build();
```

### MCP Tools (In-Process)

```csharp
using EasyReasy.Claude.AgentSdk;
using EasyReasy.Claude.AgentSdk.Mcp;

ClaudeAgentOptions options = Claude.Options()
    .McpServers(servers => servers.AddSdk("calculator", sdk => sdk
        .Tool("add", (double left, double right) => left + right, "Add two numbers")))
    .AllowAllTools()
    .Build();
```

### Custom Agents

```csharp
ClaudeAgentOptions options = Claude.Options()
    .Agents(agents => agents
        .Add("reviewer", "Reviews code", "You are a code reviewer.", "Read", "Grep")
        .Add("writer", "Writes code", "You are a clean coder.", tools: ["Read", "Write"]))
    .Build();
```

### Sandbox Configuration

```csharp
ClaudeAgentOptions options = Claude.Options()
    .Sandbox(sandbox => sandbox
        .Enable()
        .AutoAllowBash()
        .ExcludeCommands("rm", "sudo")
        .Network(network => network.AllowLocalBinding()))
    .Build();
```

## Installation

### NuGet Package (Coming Soon)

```bash
dotnet add package EasyReasy.Claude.AgentSdk
```

### From Source

```bash
git clone https://github.com/anthropics/claude-agent-sdk-dotnet.git
cd claude-agent-sdk-dotnet
dotnet build
```

## Status & Parity

- **Current version:** 0.1.0
- **Status:** Preview (API and behavior may change)
- **Parity:** Designed to match the Python Claude Agent SDK API, behavior, and ergonomics
- **Tests:** 128 tests (123 unit + 5 integration; integration tests are disabled by default)

> **Canonical rule:** The Python `claude-agent-sdk` is the canonical reference. This .NET port tracks its behavior and API.

### Known Limitations

- `control_cancel_request` is currently ignored (cancellation of in-flight control requests is not implemented yet; matches Python SDK TODO).

### Running Integration Tests

Integration tests require a working Claude Code CLI and are disabled by default.

> Enable them with: `CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1 dotnet test`

## Related Projects

| Project | Language | Description |
|---------|----------|-------------|
| [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) | Python | Official Python SDK (canonical reference) |
| [claude-agent-sdk-cpp](https://github.com/0xeb/claude-agent-sdk-cpp) | C++ | C++ port with full feature parity |

## Disclaimer

This is an independent, unofficial port and is not affiliated with or endorsed by Anthropic, PBC.

This repository is originally based on the MIT licensed work by Elias Bachaalany.
See original repository here if interested:
https://github.com/0xeb/claude-agent-sdk-dotnet

This repository has now diverged quite a bit.

## License

Licensed under the MIT License. See `LICENSE` for details.

This is a .NET port of [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) by Anthropic, PBC.
After porting this library from Python it has also slightly evolved so it might not be a 100% match.

Start building Claude-powered .NET applications today!
