using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyReasy.Claude.AgentSdk;

#region Enums

/// <summary>
/// Permission modes for controlling tool execution.
/// </summary>
public enum PermissionMode
{
    /// <summary>Default permission mode.</summary>
    Default,
    /// <summary>Automatically accept edit operations.</summary>
    AcceptEdits,
    /// <summary>Plan mode for generating action plans.</summary>
    Plan,
    /// <summary>Bypass all permission checks.</summary>
    BypassPermissions
}

/// <summary>
/// Hook event types.
/// </summary>
public enum HookEvent
{
    /// <summary>Fired before a tool is executed.</summary>
    PreToolUse,
    /// <summary>Fired after a tool has been executed.</summary>
    PostToolUse,
    /// <summary>Fired when the user submits a prompt.</summary>
    UserPromptSubmit,
    /// <summary>Fired when the agent stops.</summary>
    Stop,
    /// <summary>Fired when a subagent stops.</summary>
    SubagentStop,
    /// <summary>Fired before conversation compaction.</summary>
    PreCompact
}

/// <summary>
/// Setting sources to load.
/// </summary>
public enum SettingSource
{
    /// <summary>User-level settings.</summary>
    User,
    /// <summary>Project-level settings.</summary>
    Project,
    /// <summary>Local workspace settings.</summary>
    Local
}

/// <summary>
/// Permission behavior options.
/// </summary>
public enum PermissionBehavior
{
    /// <summary>Allow the operation.</summary>
    Allow,
    /// <summary>Deny the operation.</summary>
    Deny,
    /// <summary>Prompt the user for permission.</summary>
    Ask
}

/// <summary>
/// Permission update destinations.
/// </summary>
public enum PermissionUpdateDestination
{
    /// <summary>Update user-level settings.</summary>
    UserSettings,
    /// <summary>Update project-level settings.</summary>
    ProjectSettings,
    /// <summary>Update local workspace settings.</summary>
    LocalSettings,
    /// <summary>Update current session only.</summary>
    Session
}

/// <summary>
/// Permission update types.
/// </summary>
public enum PermissionUpdateType
{
    /// <summary>Add new permission rules.</summary>
    AddRules,
    /// <summary>Replace existing permission rules.</summary>
    ReplaceRules,
    /// <summary>Remove permission rules.</summary>
    RemoveRules,
    /// <summary>Set the permission mode.</summary>
    SetMode,
    /// <summary>Add directories to the allowed list.</summary>
    AddDirectories,
    /// <summary>Remove directories from the allowed list.</summary>
    RemoveDirectories
}

/// <summary>
/// Assistant message error types.
/// </summary>
public enum AssistantMessageError
{
    /// <summary>Authentication failed.</summary>
    AuthenticationFailed,
    /// <summary>Billing error occurred.</summary>
    BillingError,
    /// <summary>Rate limit exceeded.</summary>
    RateLimit,
    /// <summary>Invalid request.</summary>
    InvalidRequest,
    /// <summary>Server error occurred.</summary>
    ServerError,
    /// <summary>Unknown error.</summary>
    Unknown
}

/// <summary>
/// Helper methods for enum string conversion.
/// </summary>
internal static class EnumHelpers
{
    public static string ToJsonString(this PermissionMode mode) => mode switch
    {
        PermissionMode.Default => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        _ => mode.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this SettingSource source) => source switch
    {
        SettingSource.User => "user",
        SettingSource.Project => "project",
        SettingSource.Local => "local",
        _ => source.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this PermissionBehavior behavior) => behavior switch
    {
        PermissionBehavior.Allow => "allow",
        PermissionBehavior.Deny => "deny",
        PermissionBehavior.Ask => "ask",
        _ => behavior.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this PermissionUpdateDestination dest) => dest switch
    {
        PermissionUpdateDestination.UserSettings => "userSettings",
        PermissionUpdateDestination.ProjectSettings => "projectSettings",
        PermissionUpdateDestination.LocalSettings => "localSettings",
        PermissionUpdateDestination.Session => "session",
        _ => dest.ToString()
    };

    public static string ToJsonString(this PermissionUpdateType type) => type switch
    {
        PermissionUpdateType.AddRules => "addRules",
        PermissionUpdateType.ReplaceRules => "replaceRules",
        PermissionUpdateType.RemoveRules => "removeRules",
        PermissionUpdateType.SetMode => "setMode",
        PermissionUpdateType.AddDirectories => "addDirectories",
        PermissionUpdateType.RemoveDirectories => "removeDirectories",
        _ => type.ToString()
    };
}

#endregion

#region Content Blocks

/// <summary>
/// Base class for content blocks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
public abstract record ContentBlock;

/// <summary>
/// Text content block.
/// </summary>
public record TextBlock(
    [property: JsonPropertyName("text")] string Text
) : ContentBlock;

/// <summary>
/// Thinking content block.
/// </summary>
public record ThinkingBlock(
    [property: JsonPropertyName("thinking")] string Thinking,
    [property: JsonPropertyName("signature")] string Signature
) : ContentBlock;

/// <summary>
/// Tool use content block.
/// </summary>
public record ToolUseBlock(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] JsonElement Input
) : ContentBlock;

/// <summary>
/// Tool result content block.
/// </summary>
public record ToolResultBlock(
    [property: JsonPropertyName("tool_use_id")] string ToolUseId,
    [property: JsonPropertyName("content")] JsonElement? Content = null,
    [property: JsonPropertyName("is_error")] bool? IsError = null
) : ContentBlock;

#endregion

#region Messages

/// <summary>
/// Base class for messages.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(StreamEvent), "stream_event")]
public abstract record Message;

/// <summary>
/// User message.
/// </summary>
public record UserMessage : Message
{
    /// <summary>The message content (string or array of content blocks).</summary>
    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }

    /// <summary>Unique identifier for this message.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>ID of the parent tool use if this message is a follow-up.</summary>
    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    /// <summary>
    /// Gets the content as a string if it's a simple text message.
    /// </summary>
    public string? GetTextContent()
    {
        if (Content.ValueKind == JsonValueKind.String)
            return Content.GetString();
        return null;
    }

    /// <summary>
    /// Gets the content blocks if the content is an array.
    /// </summary>
    public IReadOnlyList<ContentBlock>? GetContentBlocks()
    {
        if (Content.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<ContentBlock>();
            foreach (var element in Content.EnumerateArray())
            {
                var block = JsonSerializer.Deserialize<ContentBlock>(element, ClaudeJsonContext.Default.ContentBlock);
                if (block != null)
                    blocks.Add(block);
            }
            return blocks;
        }
        return null;
    }
}

/// <summary>
/// Assistant message with content blocks.
/// </summary>
public record AssistantMessage : Message
{
    /// <summary>The message content blocks.</summary>
    [JsonPropertyName("content")]
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>The model that generated this message.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>ID of the parent tool use if this message is a follow-up.</summary>
    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    /// <summary>Error that occurred during message generation.</summary>
    [JsonPropertyName("error")]
    public AssistantMessageError? Error { get; init; }
}

/// <summary>
/// System message with metadata.
/// </summary>
public record SystemMessage : Message
{
    /// <summary>The subtype of the system message.</summary>
    [JsonPropertyName("subtype")]
    public required string Subtype { get; init; }

    /// <summary>Additional data for the system message.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

/// <summary>
/// Result message with cost and usage information.
/// </summary>
public record ResultMessage : Message
{
    /// <summary>The subtype of the result message.</summary>
    [JsonPropertyName("subtype")]
    public required string Subtype { get; init; }

    /// <summary>Total duration in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public required int DurationMs { get; init; }

    /// <summary>API call duration in milliseconds.</summary>
    [JsonPropertyName("duration_api_ms")]
    public required int DurationApiMs { get; init; }

    /// <summary>Whether the conversation ended with an error.</summary>
    [JsonPropertyName("is_error")]
    public required bool IsError { get; init; }

    /// <summary>Number of conversation turns.</summary>
    [JsonPropertyName("num_turns")]
    public required int NumTurns { get; init; }

    /// <summary>The session identifier.</summary>
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>Total cost in USD.</summary>
    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; init; }

    /// <summary>Token usage information.</summary>
    [JsonPropertyName("usage")]
    public JsonElement? Usage { get; init; }

    /// <summary>The result text.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Structured output if requested.</summary>
    [JsonPropertyName("structured_output")]
    public JsonElement? StructuredOutput { get; init; }
}

/// <summary>
/// Stream event for partial message updates during streaming.
/// </summary>
public record StreamEvent : Message
{
    /// <summary>Unique identifier for this stream event.</summary>
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    /// <summary>The session identifier.</summary>
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>The streaming event data.</summary>
    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    /// <summary>ID of the parent tool use if this event is for a subagent.</summary>
    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }
}

#endregion

#region Permission Types

/// <summary>
/// Permission rule value.
/// </summary>
public record PermissionRuleValue(
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("rule_content")] string? RuleContent = null
);

/// <summary>
/// Permission update configuration.
/// </summary>
public record PermissionUpdate(
    [property: JsonPropertyName("type")] PermissionUpdateType Type,
    [property: JsonPropertyName("rules")] IReadOnlyList<PermissionRuleValue>? Rules = null,
    [property: JsonPropertyName("behavior")] PermissionBehavior? Behavior = null,
    [property: JsonPropertyName("mode")] PermissionMode? Mode = null,
    [property: JsonPropertyName("directories")] IReadOnlyList<string>? Directories = null,
    [property: JsonPropertyName("destination")] PermissionUpdateDestination? Destination = null
)
{
    /// <summary>
    /// Convert to dictionary format matching TypeScript control protocol.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = Type.ToJsonString()
        };

        if (Destination.HasValue)
            result["destination"] = Destination.Value.ToJsonString();

        if (Type is PermissionUpdateType.AddRules or PermissionUpdateType.ReplaceRules or PermissionUpdateType.RemoveRules)
        {
            if (Rules != null)
            {
                result["rules"] = Rules.Select(r => new Dictionary<string, object?>
                {
                    ["toolName"] = r.ToolName,
                    ["ruleContent"] = r.RuleContent
                }).ToList();
            }
            if (Behavior.HasValue)
                result["behavior"] = Behavior.Value.ToJsonString();
        }
        else if (Type == PermissionUpdateType.SetMode)
        {
            if (Mode.HasValue)
                result["mode"] = Mode.Value.ToJsonString();
        }
        else if (Type is PermissionUpdateType.AddDirectories or PermissionUpdateType.RemoveDirectories)
        {
            if (Directories != null)
                result["directories"] = Directories.ToList();
        }

        return result;
    }
}

/// <summary>
/// Context information for tool permission callbacks.
/// </summary>
public record ToolPermissionContext(
    object? Signal = null,
    IReadOnlyList<PermissionUpdate>? Suggestions = null
);

/// <summary>
/// Base class for permission results.
/// </summary>
public abstract record PermissionResult;

/// <summary>
/// Allow permission result.
/// </summary>
public record PermissionResultAllow(
    JsonElement? UpdatedInput = null,
    IReadOnlyList<PermissionUpdate>? UpdatedPermissions = null
) : PermissionResult
{
    /// <summary>The permission behavior (always "allow").</summary>
    public string Behavior => "allow";
}

/// <summary>
/// Deny permission result.
/// </summary>
public record PermissionResultDeny(
    string Message = "",
    bool Interrupt = false
) : PermissionResult
{
    /// <summary>The permission behavior (always "deny").</summary>
    public string Behavior => "deny";
}

/// <summary>
/// Delegate for tool permission callbacks.
/// </summary>
public delegate Task<PermissionResult> CanUseToolCallback(
    string toolName,
    JsonElement input,
    ToolPermissionContext context,
    CancellationToken cancellationToken = default
);

#endregion

#region Hook Types

/// <summary>
/// Base hook input fields.
/// </summary>
public record BaseHookInput
{
    /// <summary>The session identifier.</summary>
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>Path to the conversation transcript file.</summary>
    [JsonPropertyName("transcript_path")]
    public required string TranscriptPath { get; init; }

    /// <summary>Current working directory.</summary>
    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    /// <summary>Current permission mode.</summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }
}

/// <summary>
/// Input data for PreToolUse hook events.
/// </summary>
public record PreToolUseHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PreToolUse";

    /// <summary>Name of the tool about to be executed.</summary>
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    /// <summary>Input parameters for the tool.</summary>
    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }
}

/// <summary>
/// Input data for PostToolUse hook events.
/// </summary>
public record PostToolUseHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostToolUse";

    /// <summary>Name of the tool that was executed.</summary>
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    /// <summary>Input parameters for the tool.</summary>
    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    /// <summary>Response returned by the tool.</summary>
    [JsonPropertyName("tool_response")]
    public required JsonElement ToolResponse { get; init; }
}

/// <summary>
/// Input data for UserPromptSubmit hook events.
/// </summary>
public record UserPromptSubmitHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "UserPromptSubmit";

    /// <summary>The user's submitted prompt text.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
}

/// <summary>
/// Input data for Stop hook events.
/// </summary>
public record StopHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "Stop";

    /// <summary>Whether the stop hook is currently active.</summary>
    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }
}

/// <summary>
/// Input data for SubagentStop hook events.
/// </summary>
public record SubagentStopHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "SubagentStop";

    /// <summary>Whether the stop hook is currently active.</summary>
    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }
}

/// <summary>
/// Input data for PreCompact hook events.
/// </summary>
public record PreCompactHookInput : BaseHookInput
{
    /// <summary>The hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PreCompact";

    /// <summary>What triggered the compaction.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    /// <summary>Custom instructions for the compaction.</summary>
    [JsonPropertyName("custom_instructions")]
    public string? CustomInstructions { get; init; }
}

/// <summary>
/// Hook output configuration.
/// </summary>
public record HookOutput
{
    /// <summary>Whether to continue execution.</summary>
    [JsonPropertyName("continue")]
    public bool? Continue { get; init; }

    /// <summary>Whether to suppress output from the tool.</summary>
    [JsonPropertyName("suppressOutput")]
    public bool? SuppressOutput { get; init; }

    /// <summary>Reason for stopping execution.</summary>
    [JsonPropertyName("stopReason")]
    public string? StopReason { get; init; }

    /// <summary>Decision made by the hook.</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    /// <summary>System message to display.</summary>
    [JsonPropertyName("systemMessage")]
    public string? SystemMessage { get; init; }

    /// <summary>Reason for the decision.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Hook-specific output data.</summary>
    [JsonPropertyName("hookSpecificOutput")]
    public JsonElement? HookSpecificOutput { get; init; }
}

/// <summary>
/// Hook context information.
/// </summary>
public record HookContext(object? Signal = null);

/// <summary>
/// Delegate for hook callbacks.
/// </summary>
public delegate Task<HookOutput> HookCallback(
    JsonElement input,
    string? toolUseId,
    HookContext context,
    CancellationToken cancellationToken = default
);

/// <summary>
/// Hook matcher configuration.
/// </summary>
public record HookMatcher(
    string? Matcher = null,
    IReadOnlyList<HookCallback>? Hooks = null,
    double? Timeout = null
);

#endregion

#region MCP Server Config

/// <summary>
/// MCP stdio server configuration.
/// </summary>
public record McpStdioServerConfig
{
    /// <summary>The server type (always "stdio").</summary>
    [JsonPropertyName("type")]
    public string Type => "stdio";

    /// <summary>Command to execute the server.</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>Command-line arguments for the server.</summary>
    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Environment variables for the server process.</summary>
    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

/// <summary>
/// MCP SSE server configuration.
/// </summary>
public record McpSSEServerConfig
{
    /// <summary>The server type (always "sse").</summary>
    [JsonPropertyName("type")]
    public string Type => "sse";

    /// <summary>URL of the SSE server.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>HTTP headers for the server connection.</summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// MCP HTTP server configuration.
/// </summary>
public record McpHttpServerConfig
{
    /// <summary>The server type (always "http").</summary>
    [JsonPropertyName("type")]
    public string Type => "http";

    /// <summary>URL of the HTTP server.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>HTTP headers for the server connection.</summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// SDK MCP server configuration for in-process servers.
/// </summary>
/// <remarks>
/// Use this configuration to run an MCP server in the same process as your application.
/// The server will communicate with Claude Code via the SDK's control protocol bridge.
/// </remarks>
/// <example>
/// <code>
/// var config = new McpSdkServerConfig
/// {
///     Name = "calculator",
///     Handlers = new McpServerHandlers
///     {
///         ListTools = ct => Task.FromResult&lt;IReadOnlyList&lt;McpToolDefinition&gt;&gt;(
///             [new McpToolDefinition { Name = "add", Description = "Add two numbers" }]
///         ),
///         CallTool = (name, args, ct) => Task.FromResult(
///             new McpToolResult { Content = [new McpContent { Type = "text", Text = "4" }] }
///         )
///     }
/// };
/// </code>
/// </example>
public record McpSdkServerConfig
{
    /// <summary>
    /// The server type identifier. Always "sdk" for in-process servers.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type => "sdk";

    /// <summary>
    /// The name of the server, used for routing MCP messages.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The MCP server handlers for processing requests.
    /// </summary>
    /// <remarks>
    /// Define handlers for tools, prompts, and resources that your server supports.
    /// Only the handlers you provide will be advertised as capabilities.
    /// </remarks>
    [JsonIgnore]
    public Mcp.McpServerHandlers Handlers { get; set; } = null!;
}

#endregion

#region Agent and Sandbox Config

/// <summary>
/// Agent definition configuration.
/// </summary>
public record AgentDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("tools")] IReadOnlyList<string>? Tools = null,
    [property: JsonPropertyName("model")] string? Model = null
);

/// <summary>
/// SDK plugin configuration.
/// </summary>
public record SdkPluginConfig(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path
);

/// <summary>
/// Network configuration for sandbox.
/// </summary>
public record SandboxNetworkConfig
{
    /// <summary>List of allowed Unix socket paths.</summary>
    [JsonPropertyName("allowUnixSockets")]
    public IReadOnlyList<string>? AllowUnixSockets { get; init; }

    /// <summary>Whether to allow all Unix sockets.</summary>
    [JsonPropertyName("allowAllUnixSockets")]
    public bool? AllowAllUnixSockets { get; init; }

    /// <summary>Whether to allow binding to local network interfaces.</summary>
    [JsonPropertyName("allowLocalBinding")]
    public bool? AllowLocalBinding { get; init; }

    /// <summary>HTTP proxy port to use.</summary>
    [JsonPropertyName("httpProxyPort")]
    public int? HttpProxyPort { get; init; }

    /// <summary>SOCKS proxy port to use.</summary>
    [JsonPropertyName("socksProxyPort")]
    public int? SocksProxyPort { get; init; }
}

/// <summary>
/// Violations to ignore in sandbox.
/// </summary>
public record SandboxIgnoreViolations
{
    /// <summary>File access violations to ignore (patterns).</summary>
    [JsonPropertyName("file")]
    public IReadOnlyList<string>? File { get; init; }

    /// <summary>Network access violations to ignore (patterns).</summary>
    [JsonPropertyName("network")]
    public IReadOnlyList<string>? Network { get; init; }
}

/// <summary>
/// Sandbox settings configuration.
/// </summary>
public record SandboxSettings
{
    /// <summary>Whether sandboxing is enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>Automatically allow Bash when sandboxed.</summary>
    [JsonPropertyName("autoAllowBashIfSandboxed")]
    public bool? AutoAllowBashIfSandboxed { get; init; }

    /// <summary>Commands to exclude from sandboxing.</summary>
    [JsonPropertyName("excludedCommands")]
    public IReadOnlyList<string>? ExcludedCommands { get; init; }

    /// <summary>Whether to allow commands to run unsandboxed.</summary>
    [JsonPropertyName("allowUnsandboxedCommands")]
    public bool? AllowUnsandboxedCommands { get; init; }

    /// <summary>Network configuration for the sandbox.</summary>
    [JsonPropertyName("network")]
    public SandboxNetworkConfig? Network { get; init; }

    /// <summary>Violations to ignore.</summary>
    [JsonPropertyName("ignoreViolations")]
    public SandboxIgnoreViolations? IgnoreViolations { get; init; }

    /// <summary>Enable weaker nested sandboxing.</summary>
    [JsonPropertyName("enableWeakerNestedSandbox")]
    public bool? EnableWeakerNestedSandbox { get; init; }
}

#endregion

#region Claude Agent Options

/// <summary>
/// Query options for Claude SDK.
/// </summary>
public class ClaudeAgentOptions
{
    /// <summary>Base set of tools to enable.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Additional tools to allow.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>System prompt for the conversation.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>MCP server configurations (dict or path). Use <c>McpServers</c> helpers for in-process SDK servers.</summary>
    public object? McpServers { get; init; }

    /// <summary>Permission mode for tool execution.</summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>Continue from previous conversation.</summary>
    public bool ContinueConversation { get; init; }

    /// <summary>Session ID to resume.</summary>
    public string? Resume { get; init; }

    /// <summary>Maximum number of turns.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Maximum budget in USD.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Tools to disallow.</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Fallback model if primary unavailable.</summary>
    public string? FallbackModel { get; init; }

    /// <summary>Beta features to enable.</summary>
    public IReadOnlyList<string> Betas { get; init; } = [];

    /// <summary>Permission prompt tool name.</summary>
    public string? PermissionPromptToolName { get; init; }

    /// <summary>Working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Path to CLI binary.</summary>
    public string? CliPath { get; init; }

    /// <summary>Settings path or JSON.</summary>
    public string? Settings { get; init; }

    /// <summary>Additional directories to include.</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary>Environment variables to set.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Extra CLI arguments.</summary>
    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = new Dictionary<string, string?>();

    /// <summary>Maximum buffer size for CLI stdout.</summary>
    public int? MaxBufferSize { get; init; }

    /// <summary>Callback for stderr output from CLI.</summary>
    public Action<string>? StderrCallback { get; init; }

    /// <summary>Tool permission callback.</summary>
    public CanUseToolCallback? CanUseTool { get; init; }

    /// <summary>Hook configurations.</summary>
    public IReadOnlyDictionary<HookEvent, IReadOnlyList<HookMatcher>>? Hooks { get; init; }

    /// <summary>User identifier.</summary>
    public string? User { get; init; }

    /// <summary>Include partial messages during streaming.</summary>
    public bool IncludePartialMessages { get; init; }

    /// <summary>Fork session when resuming.</summary>
    public bool ForkSession { get; init; }

    /// <summary>Agent definitions.</summary>
    public IReadOnlyDictionary<string, AgentDefinition>? Agents { get; init; }

    /// <summary>Setting sources to load.</summary>
    public IReadOnlyList<SettingSource>? SettingSources { get; init; }

    /// <summary>Sandbox settings.</summary>
    public SandboxSettings? Sandbox { get; init; }

    /// <summary>Plugin configurations.</summary>
    public IReadOnlyList<SdkPluginConfig> Plugins { get; init; } = [];

    /// <summary>Maximum thinking tokens.</summary>
    public int? MaxThinkingTokens { get; init; }

    /// <summary>Output format for structured outputs.</summary>
    public JsonElement? OutputFormat { get; init; }

    /// <summary>Enable file checkpointing.</summary>
    public bool EnableFileCheckpointing { get; init; }
}

#endregion

#region JSON Serialization Context

/// <summary>
/// JSON serialization context for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(SystemMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextBlock))]
[JsonSerializable(typeof(ThinkingBlock))]
[JsonSerializable(typeof(ToolUseBlock))]
[JsonSerializable(typeof(ToolResultBlock))]
[JsonSerializable(typeof(HookOutput))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<ContentBlock>))]
internal partial class ClaudeJsonContext : JsonSerializerContext
{
}

#endregion
