using EasyReasy.Claude.AgentSdk.Internal;
using EasyReasy.Claude.AgentSdk.Transport;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Regression tests for a read loop that died without saying so. The loop is the only thing routing
/// control responses back to the CLI, so once it is gone the CLI blocks forever on its next hook
/// callback. Completing the message channel cleanly made that look exactly like a stream that had
/// ended normally — the consumer reported the turn finished and went on writing to a process nobody
/// was reading, leaving a chat session permanently unable to answer with nothing logged anywhere.
/// </summary>
public class ReadLoopFailureTests
{
    [Fact]
    public async Task ReceiveMessagesAsync_WhenTransportReadFails_ThrowsTheFailure()
    {
        InvalidDataException failure = new InvalidDataException("stdout line exceeded the buffer");
        QueryHandler handler = await StartHandlerAsync(new FailingTransport(failure, messagesBeforeFailure: 0));

        InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
            {
                await foreach (Message _ in handler.ReceiveMessagesAsync())
                {
                }
            });

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ReceiveMessagesAsync_WhenTransportReadFails_YieldsMessagesReadBeforeIt()
    {
        QueryHandler handler = await StartHandlerAsync(
            new FailingTransport(new InvalidDataException("boom"), messagesBeforeFailure: 2));

        List<Message> received = new List<Message>();

        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
            {
                await foreach (Message message in handler.ReceiveMessagesAsync())
                {
                    received.Add(message);
                }
            });

        // The failure must not discard what the CLI had already said. Faulting the channel after the
        // buffered messages is what keeps a half-delivered turn readable rather than silently empty.
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task ReceiveMessagesAsync_WhenReadLoopIsCancelled_CompletesWithoutThrowing()
    {
        // Shutdown cancels the read loop, and that is not a failure: turning it into one would report
        // every ordinary disconnect as a broken transport.
        CancellationTokenSource cts = new CancellationTokenSource();
        QueryHandler handler = new QueryHandler(new CancellableTransport(), new ClaudeAgentOptions(), TimeSpan.FromSeconds(30));
        await handler.StartAsync(cts.Token);

        cts.Cancel();

        await foreach (Message _ in handler.ReceiveMessagesAsync())
        {
        }
    }

    private static async Task<QueryHandler> StartHandlerAsync(ITransport transport)
    {
        QueryHandler handler = new QueryHandler(transport, new ClaudeAgentOptions(), TimeSpan.FromSeconds(30));
        await handler.StartAsync();
        return handler;
    }

    /// <summary>
    /// A transport whose read yields a given number of messages and then throws, mirroring a stdout
    /// line the reader refuses part-way through a live session.
    /// </summary>
    private sealed class FailingTransport : ITransport
    {
        private readonly Exception _failure;
        private readonly int _messagesBeforeFailure;

        public FailingTransport(Exception failure, int messagesBeforeFailure)
        {
            _failure = failure;
            _messagesBeforeFailure = messagesBeforeFailure;
        }

        public bool IsReady => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteAsync(string data, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int index = 0; index < _messagesBeforeFailure; index++)
            {
                await Task.Yield();
                yield return UserMessage(index);
            }

            await Task.Yield();
            throw _failure;
        }

        public Task CloseAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static JsonElement UserMessage(int index) =>
            JsonSerializer.SerializeToElement(new
            {
                type = "user",
                message = new { role = "user", content = $"message {index}" }
            });
    }

    /// <summary>
    /// A transport whose read ends only when its token is cancelled — an ordinary shutdown.
    /// </summary>
    private sealed class CancellableTransport : ITransport
    {
        public bool IsReady => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteAsync(string data, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public Task CloseAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
