using System.Text.Json;

namespace EasyReasy.Claude.AgentSdk.Transport;

/// <summary>
/// Transport interface for communicating with Claude Code CLI.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// Connect to the CLI process.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Write raw data to the transport (stdin).
    /// </summary>
    Task WriteAsync(string data, CancellationToken cancellationToken = default);

    /// <summary>
    /// End the input stream (close stdin).
    /// </summary>
    Task EndInputAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read and parse messages from the transport (stdout).
    /// </summary>
    IAsyncEnumerable<JsonElement> ReadMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the transport is ready for communication.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Close the transport and clean up resources.
    /// </summary>
    Task CloseAsync();
}
