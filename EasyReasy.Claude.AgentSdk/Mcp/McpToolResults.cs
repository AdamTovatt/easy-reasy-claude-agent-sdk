namespace EasyReasy.Claude.AgentSdk.Mcp;

/// <summary>
/// Convenience helpers for producing MCP tool results.
/// </summary>
public static class McpToolResults
{
    /// <summary>
    /// Creates a text-based MCP tool result.
    /// </summary>
    /// <param name="text">The text content to include in the result.</param>
    /// <param name="isError">Indicates whether this result represents an error. Default is false.</param>
    /// <returns>An <see cref="McpToolResult"/> containing the text content.</returns>
    public static McpToolResult Text(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new McpContent { Type = "text", Text = text }]
    };
}

