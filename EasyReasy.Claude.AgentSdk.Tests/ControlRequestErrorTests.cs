using EasyReasy.Claude.AgentSdk.Internal;
using EasyReasy.Claude.AgentSdk.Transport;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Tests for how a control request fails. A caller has to tell "the CLI never answered" from "the CLI
/// said no, and here is why", and both from its own cancellation and from the CLI going away, without
/// parsing message strings.
/// </summary>
public class ControlRequestErrorTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ErrorResponse_ThrowsRefusedWithTheCliTextAndSubtype()
    {
        ScriptedTransport transport = new ScriptedTransport(
            request => ErrorResponse(request, errorJson: "\"no foreground tasks to background\""));
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        ControlRequestRefusedException thrown = await Assert.ThrowsAsync<ControlRequestRefusedException>(
            () => handler.BackgroundTasksAsync(null));

        Assert.Equal("background_tasks", thrown.Subtype);
        Assert.Equal("no foreground tasks to background", thrown.CliError);
        Assert.Contains("no foreground tasks to background", thrown.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task ErrorResponseWithoutErrorText_ThrowsRefusedWithNullCliError(string? errorJson)
    {
        ScriptedTransport transport = new ScriptedTransport(request => ErrorResponse(request, errorJson));
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        ControlRequestRefusedException thrown = await Assert.ThrowsAsync<ControlRequestRefusedException>(
            () => handler.SetModelAsync("some-model"));

        Assert.Equal("set_model", thrown.Subtype);
        Assert.Null(thrown.CliError);
    }

    [Fact]
    public async Task ErrorResponseWithNonStringError_KeepsTheRawJsonAsCliError()
    {
        ScriptedTransport transport = new ScriptedTransport(request => ErrorResponse(request, errorJson: "{\"code\":5}"));
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        ControlRequestRefusedException thrown = await Assert.ThrowsAsync<ControlRequestRefusedException>(
            () => handler.InterruptAsync());

        Assert.Equal("{\"code\":5}", thrown.CliError);
    }

    [Fact]
    public async Task NoResponseWithinTimeout_ThrowsTimeoutWithSubtypeAndTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(100);
        await using QueryHandler handler = await StartHandlerAsync(new ScriptedTransport(respond: null), timeout);

        ControlRequestTimeoutException thrown = await Assert.ThrowsAsync<ControlRequestTimeoutException>(
            () => handler.InitializeAsync());

        Assert.Equal("initialize", thrown.Subtype);
        Assert.Equal(timeout, thrown.Timeout);
        Assert.Contains("initialize", thrown.Message);
    }

    [Fact]
    public async Task OutputEndsWhileRequestPending_ThrowsCliConnectionInsteadOfTimingOut()
    {
        // Without the early failure the request would wait out the 30s timeout and then throw
        // ControlRequestTimeoutException, which the exact-type assertion rejects.
        ScriptedTransport transport = new ScriptedTransport(respond: null);
        transport.OnRequestWritten = _ => transport.EndOutput();
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        CliConnectionException thrown = await Assert.ThrowsAsync<CliConnectionException>(
            () => handler.InitializeAsync());

        Assert.Contains("initialize", thrown.Message);
    }

    [Fact]
    public async Task RequestSentAfterOutputEnded_ThrowsCliConnectionAtOnce()
    {
        ScriptedTransport transport = new ScriptedTransport(respond: null);
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        transport.EndOutput();
        await foreach (Message _ in handler.ReceiveMessagesAsync())
        {
        }

        await Assert.ThrowsAsync<CliConnectionException>(() => handler.InitializeAsync());
        Assert.Equal(0, transport.RequestsWritten);
    }

    [Fact]
    public async Task ReadLoopFailsWhilePending_ThrowsTheTransportsOwnException()
    {
        // A TimeoutException on purpose: the transport's failure must keep its identity rather than be
        // mistaken for the control request's own timeout.
        TimeoutException failure = new TimeoutException("stdout read timed out");
        ScriptedTransport transport = new ScriptedTransport(respond: null);
        transport.OnRequestWritten = _ => transport.EndOutput(failure);
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() => handler.InterruptAsync());

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task CallerCancels_ThrowsOperationCanceledNotTimeout()
    {
        await using QueryHandler handler = await StartHandlerAsync(new ScriptedTransport(respond: null), LongTimeout);
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InterruptAsync(cts.Token));
    }

    [Fact]
    public async Task SuccessResponseWithBackgroundedFalse_ReturnsFalseRatherThanRefusing()
    {
        ScriptedTransport transport = new ScriptedTransport(
            request => SuccessResponse(request, new { backgrounded = false }));
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout);

        Assert.False(await handler.BackgroundTasksAsync(null));
    }

    /// <summary>
    /// Every control method builds its request from a subtype and its own fields, so each one's wire shape
    /// is pinned here: a field dropped or misnamed would otherwise reach the CLI unnoticed.
    /// </summary>
    [Theory]
    [InlineData("interrupt", """{"subtype":"interrupt"}""")]
    [InlineData("set_permission_mode", """{"subtype":"set_permission_mode","mode":"acceptEdits"}""")]
    [InlineData("set_model", """{"subtype":"set_model","model":null}""")]
    [InlineData("rewind_files", """{"subtype":"rewind_files","user_message_id":"msg_1"}""")]
    [InlineData("background_tasks", """{"subtype":"background_tasks"}""")]
    [InlineData("background_tasks_one", """{"subtype":"background_tasks","tool_use_id":"toolu_1"}""")]
    [InlineData("initialize", """{"subtype":"initialize","hooks":null}""")]
    [InlineData("initialize_hooks", """{"subtype":"initialize","hooks":{"PreToolUse":[{"matcher":"Bash","hookCallbackIds":["hook_0"]}]}}""")]
    public async Task ControlMethod_WritesItsRequestShape(string method, string expectedRequestJson)
    {
        JsonNode? written = null;
        ScriptedTransport transport = new ScriptedTransport(request =>
        {
            written = JsonNode.Parse(request.GetProperty("request").GetRawText());
            return SuccessResponse(request, new { });
        });

        ClaudeAgentOptions options = method != "initialize_hooks"
            ? new ClaudeAgentOptions()
            : new ClaudeAgentOptions
            {
                Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
                {
                    [HookEvent.PreToolUse] = [new HookMatcher("Bash", [(_, _, _, _) => Task.FromResult(new HookOutput())])]
                }
            };
        await using QueryHandler handler = await StartHandlerAsync(transport, LongTimeout, options);

        Task call = method switch
        {
            "interrupt" => handler.InterruptAsync(),
            "set_permission_mode" => handler.SetPermissionModeAsync("acceptEdits"),
            "set_model" => handler.SetModelAsync(null),
            "rewind_files" => handler.RewindFilesAsync("msg_1"),
            "background_tasks" => handler.BackgroundTasksAsync(null),
            "background_tasks_one" => handler.BackgroundTasksAsync("toolu_1"),
            "initialize" or "initialize_hooks" => handler.InitializeAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
        };
        await call;

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedRequestJson), written),
            $"wrote {written?.ToJsonString()}");
    }

    private static async Task<QueryHandler> StartHandlerAsync(
        ITransport transport,
        TimeSpan initializeTimeout,
        ClaudeAgentOptions? options = null)
    {
        QueryHandler handler = new QueryHandler(transport, options ?? new ClaudeAgentOptions(), initializeTimeout);
        await handler.StartAsync();
        return handler;
    }

    /// <param name="request">The control request being answered.</param>
    /// <param name="errorJson">The raw JSON of the response's <c>error</c> field, or null to leave the field out.</param>
    private static JsonElement ErrorResponse(JsonElement request, string? errorJson)
    {
        Dictionary<string, object?> response = new Dictionary<string, object?>
        {
            ["subtype"] = "error",
            ["request_id"] = request.GetProperty("request_id").GetString()
        };
        if (errorJson != null)
            response["error"] = JsonDocument.Parse(errorJson).RootElement;

        return JsonSerializer.SerializeToElement(new { type = "control_response", response });
    }

    private static JsonElement SuccessResponse(JsonElement request, object payload) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = request.GetProperty("request_id").GetString(),
                response = payload
            }
        });

    /// <summary>
    /// A transport that plays the CLI: each control request written to it may get a scripted answer on
    /// its output, and its output can be ended cleanly (EOF) or with a failure at any point. Its read
    /// honours cancellation, so disposing the handler ends the read loop.
    /// </summary>
    private sealed class ScriptedTransport : ITransport
    {
        private readonly Func<JsonElement, JsonElement?>? _respond;
        private readonly Channel<JsonElement> _output = Channel.CreateUnbounded<JsonElement>();
        private Exception? _failure;
        private int _requestsWritten;

        public ScriptedTransport(Func<JsonElement, JsonElement?>? respond)
        {
            _respond = respond;
        }

        /// <summary>
        /// Runs after each control request is written, once its answer (if any) is queued.
        /// </summary>
        public Action<JsonElement>? OnRequestWritten { get; set; }

        public int RequestsWritten => Volatile.Read(ref _requestsWritten);

        public bool IsReady => true;

        public void EndOutput(Exception? failure = null)
        {
            _failure = failure;
            _output.Writer.TryComplete();
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            JsonElement written = JsonDocument.Parse(data).RootElement;
            if (written.GetProperty("type").GetString() != "control_request")
                return Task.CompletedTask;

            Interlocked.Increment(ref _requestsWritten);

            JsonElement? answer = _respond?.Invoke(written);
            if (answer.HasValue)
                _output.Writer.TryWrite(answer.Value);

            OnRequestWritten?.Invoke(written);
            return Task.CompletedTask;
        }

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (JsonElement message in _output.Reader.ReadAllAsync(cancellationToken))
                yield return message;

            if (_failure != null)
                throw _failure;
        }

        public Task CloseAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
