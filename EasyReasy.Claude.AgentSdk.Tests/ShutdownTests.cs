using EasyReasy.Claude.AgentSdk.Internal;
using EasyReasy.Claude.AgentSdk.Transport;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Regression tests for the shutdown deadlock where a blocked stdout read that ignores
/// cancellation (as observed on macOS) could wedge disposal indefinitely, because the read
/// loop was awaited unbounded before the transport (and thus the process) was closed.
/// </summary>
public class ShutdownTests
{
    // Generous ceiling for the "did shutdown complete" assertions. It sits well above the
    // production grace bounds (QueryHandler.ReadDrainGrace / ClaudeSDKClient.InputDrainGrace,
    // both 2s) so it only fails on a genuine deadlock, not on a future grace bump.
    private static readonly TimeSpan DeadlockCeiling = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CloseAsync_WhenReadLoopIgnoresCancellation_CompletesAndClosesTransport()
    {
        (QueryHandler handler, HangingUntilClosedTransport transport) = await StartParkedReadLoopAsync();

        Task closeTask = handler.CloseAsync();

        // Without bounding the wait on the read task, CloseAsync would never reach the
        // transport close (which unblocks the read) and would deadlock here forever.
        await closeTask.WaitAsync(DeadlockCeiling);

        Assert.True(transport.CloseCalled);
    }

    [Fact]
    public async Task DisposeAsync_WhenReadLoopIgnoresCancellation_CompletesAndClosesTransport()
    {
        (QueryHandler handler, HangingUntilClosedTransport transport) = await StartParkedReadLoopAsync();

        Task disposeTask = handler.DisposeAsync().AsTask();

        await disposeTask.WaitAsync(DeadlockCeiling);

        Assert.True(transport.CloseCalled);
    }

    [Fact]
    public async Task DisconnectAsync_WhenInputWriteHangs_CompletesAndClosesTransport()
    {
        HangingInputTransport transport = new HangingInputTransport();
        ClaudeSDKClient client = new ClaudeSDKClient(new ClaudeAgentOptions(), transport);

        await client.ConnectAsync(SingleUserMessageStream());
        await transport.InputWriteParked; // the background input task is now wedged in the write

        Task disconnectTask = client.DisconnectAsync();

        // The input-drain wait is bounded (ClaudeSDKClient.InputDrainGrace), so disconnect must
        // not hang on the wedged write. Without the bound this would deadlock here.
        await disconnectTask.WaitAsync(DeadlockCeiling);

        Assert.True(transport.CloseCalled);
    }

    private static async Task<(QueryHandler handler, HangingUntilClosedTransport transport)> StartParkedReadLoopAsync()
    {
        HangingUntilClosedTransport transport = new HangingUntilClosedTransport();
        QueryHandler handler = new QueryHandler(transport, new ClaudeAgentOptions(), TimeSpan.FromSeconds(30));

        await handler.StartAsync();
        await transport.ReadStarted; // the read loop is now parked in the blocking read
        return (handler, transport);
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> SingleUserMessageStream()
    {
        await Task.CompletedTask;
        yield return new Dictionary<string, object?> { ["type"] = "user" };
    }

    /// <summary>
    /// A transport whose read loop stays blocked no matter how the read token is cancelled and
    /// only unblocks once the transport is closed — mirroring a pipe read that the OS won't
    /// interrupt on cancellation but that ends on EOF once the process is killed.
    /// </summary>
    private sealed class HangingUntilClosedTransport : ITransport
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public bool CloseCalled { get; private set; }
        public bool IsReady => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteAsync(string data, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            // Deliberately ignore cancellationToken: the read only ends when the transport is
            // closed (the process is killed), never on token cancellation alone.
            await _closed.Task;
            yield break;
        }

        public Task CloseAsync()
        {
            CloseCalled = true;
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A transport that answers the initialize handshake so <see cref="ClaudeSDKClient"/> can
    /// finish connecting, then wedges on the first user message written by the background input
    /// task — mirroring a stdin write blocked on a full pipe the CLI has stopped draining.
    /// </summary>
    private sealed class HangingInputTransport : ITransport
    {
        private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
        private readonly TaskCompletionSource _inputWriteParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InputWriteParked => _inputWriteParked.Task;
        public bool CloseCalled { get; private set; }
        public bool IsReady => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            JsonElement message = JsonSerializer.Deserialize<JsonElement>(data);
            string? type = message.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

            if (type == "control_request")
            {
                // Complete the initialize handshake so ConnectAsync returns and the input task starts.
                string requestId = message.GetProperty("request_id").GetString()!;
                JsonElement response = JsonSerializer.SerializeToElement(new
                {
                    type = "control_response",
                    response = new { subtype = "success", request_id = requestId, response = new { } }
                });
                await _incoming.Writer.WriteAsync(response, cancellationToken);
                return;
            }

            // A user message from the background input stream: park forever to simulate a wedged
            // stdin write, so the input task cannot drain and teardown must rely on its grace bound.
            _inputWriteParked.TrySetResult();
            await _closed.Task;
        }

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await _incoming.Reader.WaitToReadAsync(CancellationToken.None))
            {
                while (_incoming.Reader.TryRead(out JsonElement message))
                    yield return message;
            }
        }

        public Task CloseAsync()
        {
            CloseCalled = true;
            _closed.TrySetResult();
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
