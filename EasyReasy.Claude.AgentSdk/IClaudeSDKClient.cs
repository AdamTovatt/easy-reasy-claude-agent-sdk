using System.Text.Json;

namespace EasyReasy.Claude.AgentSdk;

/// <summary>
/// Abstraction over <see cref="ClaudeSDKClient"/> for testability and decoupling.
/// </summary>
public interface IClaudeSDKClient : IAsyncDisposable
{
    /// <summary>
    /// Connect to Claude with an optional prompt.
    /// </summary>
    Task ConnectAsync(
        string? prompt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Connect to Claude with an input message stream.
    /// </summary>
    Task ConnectAsync(
        IAsyncEnumerable<Dictionary<string, object?>> promptStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive all messages from Claude.
    /// </summary>
    IAsyncEnumerable<Message> ReceiveMessagesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Receive messages from Claude until and including a ResultMessage.
    /// </summary>
    IAsyncEnumerable<Message> ReceiveResponseAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a new request in streaming mode.
    /// </summary>
    Task QueryAsync(
        string prompt,
        string sessionId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send interrupt signal.
    /// </summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Change permission mode during conversation.
    /// </summary>
    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Change the AI model during conversation.
    /// </summary>
    Task SetModelAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get server initialization info including available commands and output styles.
    /// </summary>
    JsonElement? GetServerInfo();

    /// <summary>
    /// Disconnect from Claude.
    /// </summary>
    Task DisconnectAsync();
}
