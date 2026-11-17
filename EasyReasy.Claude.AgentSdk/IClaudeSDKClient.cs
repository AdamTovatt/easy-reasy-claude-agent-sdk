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
    /// <param name="prompt">
    /// Optional initial prompt. Can be a string or null for interactive mode.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer the <c>initialize</c> handshake within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered the <c>initialize</c> handshake with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task ConnectAsync(
        string? prompt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Connect to Claude with an input message stream.
    /// </summary>
    /// <param name="promptStream">The user messages to stream to the CLI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Passing a stream will start streaming it immediately after initialization.
    /// If the stream completes, stdin will be closed and the session may no longer accept new input.
    /// </remarks>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer the <c>initialize</c> handshake within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered the <c>initialize</c> handshake with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Change permission mode during conversation.
    /// </summary>
    /// <param name="mode">
    /// The permission mode to set:
    /// <list type="bullet">
    ///   <item><description>'default': CLI prompts for dangerous tools</description></item>
    ///   <item><description>'acceptEdits': Auto-accept file edits</description></item>
    ///   <item><description>'bypassPermissions': Allow all tools (use with caution)</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Change the AI model during conversation.
    /// </summary>
    /// <param name="model">The model to use, or null to use default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task SetModelAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    /// <param name="userMessageId">UUID of the user message to rewind to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Requires <see cref="ClaudeAgentOptions.EnableFileCheckpointing"/> to be true.
    /// </remarks>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move in-flight foreground tasks (Bash commands and subagents) to the background.
    /// </summary>
    /// <param name="toolUseId">
    /// When set, backgrounds only the task whose originating tool_use block has this id.
    /// When null, backgrounds all foreground tasks (the equivalent of pressing Ctrl+B in the terminal).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True if the tasks were backgrounded; false when the CLI answered successfully but backgrounded nothing.
    /// </returns>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within the timeout.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error; <see cref="ControlRequestRefusedException.CliError"/> holds its reason.</exception>
    Task<bool> BackgroundTasksAsync(string? toolUseId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get server initialization info including available commands and output styles.
    /// </summary>
    JsonElement? GetServerInfo();

    /// <summary>
    /// Disconnect from Claude.
    /// </summary>
    Task DisconnectAsync();
}
