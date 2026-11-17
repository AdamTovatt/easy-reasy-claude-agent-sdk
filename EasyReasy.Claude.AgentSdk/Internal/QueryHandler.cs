using EasyReasy.Claude.AgentSdk.Mcp;
using EasyReasy.Claude.AgentSdk.Transport;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;

namespace EasyReasy.Claude.AgentSdk.Internal;

/// <summary>
/// Handles bidirectional control protocol on top of Transport.
/// Manages control request/response routing, hook callbacks, tool permission callbacks,
/// message streaming, and initialization handshake.
/// </summary>
internal class QueryHandler : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ClaudeAgentOptions _options;
    private readonly TimeSpan _initializeTimeout;
    private readonly Channel<JsonElement> _messageChannel;
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new();
    private readonly Dictionary<string, HookCallback> _hookCallbacks = new();
    private readonly Dictionary<string, SdkMcpBridge> _sdkMcpBridges = new();
    private readonly object _pendingLock = new();

    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private bool _initialized;
    private bool _closed;
    private int _closeState;
    private int _requestCounter;
    private int _nextCallbackId;
    private TaskCompletionSource _firstResultEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement? _initializationResult;

    // Set under _pendingLock once the read loop has ended, so a control request sent afterwards fails at
    // once instead of waiting out its timeout for a response nothing will ever route back.
    private bool _readLoopEnded;
    private Exception? _readLoopFailure;

    // Bounds the wait for the read loop to drain after cancellation so shutdown always
    // proceeds to close the transport (which kills the process and unblocks the reader).
    private static readonly TimeSpan ReadDrainGrace = TimeSpan.FromSeconds(2);

    // How long a control request other than initialize waits for the CLI's answer.
    private static readonly TimeSpan ControlRequestTimeout = TimeSpan.FromSeconds(60);

    public QueryHandler(
        ITransport transport,
        ClaudeAgentOptions options,
        TimeSpan? initializeTimeout = null)
    {
        _transport = transport;
        _options = options;
        _initializeTimeout = initializeTimeout ?? TimeSpan.FromSeconds(60);
        _messageChannel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Start reading messages from transport.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = ReadMessagesLoopAsync(_readCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initialize control protocol if in streaming mode.
    /// </summary>
    public async Task<JsonElement?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return _initializationResult;

        // Build hooks configuration for initialization
        Dictionary<string, List<Dictionary<string, object?>>> hooksConfig = new Dictionary<string, List<Dictionary<string, object?>>>();

        if (_options.Hooks != null)
        {
            foreach ((HookEvent hookEvent, IReadOnlyList<HookMatcher>? matchers) in _options.Hooks)
            {
                string eventName = hookEvent.ToString();
                hooksConfig[eventName] = [];

                foreach (HookMatcher matcher in matchers)
                {
                    List<string> callbackIds = new List<string>();
                    if (matcher.Hooks != null)
                    {
                        foreach (HookCallback callback in matcher.Hooks)
                        {
                            string callbackId = $"hook_{_nextCallbackId++}";
                            _hookCallbacks[callbackId] = callback;
                            callbackIds.Add(callbackId);
                        }
                    }

                    Dictionary<string, object?> hookMatcherConfig = new Dictionary<string, object?>
                    {
                        ["matcher"] = matcher.Matcher,
                        ["hookCallbackIds"] = callbackIds
                    };

                    if (matcher.Timeout.HasValue)
                        hookMatcherConfig["timeout"] = matcher.Timeout.Value;

                    hooksConfig[eventName].Add(hookMatcherConfig);
                }
            }
        }

        Dictionary<string, object?> fields = new Dictionary<string, object?>
        {
            ["hooks"] = hooksConfig.Count > 0 ? hooksConfig : null
        };

        JsonElement response = await SendControlRequestAsync("initialize", fields, _initializeTimeout, cancellationToken);
        _initialized = true;
        _initializationResult = response;
        return response;
    }

    /// <summary>
    /// Get initialization result.
    /// </summary>
    public JsonElement? GetInitializationResult() => _initializationResult;

    private async Task ReadMessagesLoopAsync(CancellationToken cancellationToken)
    {
        // What killed the loop, if anything, so the message stream can be completed *with* it. This loop
        // is the only thing routing control responses back to the CLI, so once it is gone the CLI blocks
        // forever on its next hook callback and the session is unrecoverable. Completing the channel
        // cleanly made that indistinguishable from a stream that ended normally: the consumer saw an
        // empty enumeration, reported the turn finished, and went on writing to a process nobody was
        // reading. Faulting the channel is what lets a consumer tell the two apart — and tearing the
        // transport down is then the consumer's to do, since it owns the client's lifetime.
        Exception? readFailure = null;

        try
        {
            await foreach (JsonElement message in _transport.ReadMessagesAsync(cancellationToken))
            {
                if (_closed)
                    break;

                if (!message.TryGetProperty("type", out JsonElement typeElement))
                    continue;

                string? msgType = typeElement.GetString();

                // Route control messages
                if (msgType == "control_response")
                {
                    HandleControlResponse(message);
                    continue;
                }

                if (msgType == "control_request")
                {
                    _ = HandleControlRequestAsync(message, cancellationToken);
                    continue;
                }

                if (msgType == "control_cancel_request")
                {
                    // TODO: Implement cancellation support
                    continue;
                }

                // Track results for proper stream closure
                if (msgType == "result")
                {
                    _firstResultEvent.TrySetResult();
                }

                // Regular SDK messages go to the stream
                await _messageChannel.Writer.WriteAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            readFailure = ex;
        }
        finally
        {
            // Nothing routes control responses once this loop is gone, however it ended, so every pending
            // request fails now rather than waiting out its timeout and being misreported as one.
            FailPendingRequests(readFailure);

            // Null completes the channel cleanly, which is still what a cancelled shutdown wants.
            _messageChannel.Writer.Complete(readFailure);
        }
    }

    private void FailPendingRequests(Exception? readFailure)
    {
        lock (_pendingLock)
        {
            _readLoopEnded = true;
            _readLoopFailure = readFailure;

            foreach (PendingRequest pending in _pendingRequests.Values)
                pending.Completion.TrySetException(ConnectionLostException(pending.Subtype));
        }
    }

    /// <summary>
    /// The exception for a control request that can no longer be answered because the read loop has ended:
    /// the transport's own failure when the loop died with one, else a <see cref="CliConnectionException"/>
    /// for output that simply ended (EOF or shutdown). Call under <see cref="_pendingLock"/>.
    /// </summary>
    private Exception ConnectionLostException(string subtype) =>
        _readLoopFailure ?? new CliConnectionException(
            $"The CLI's output ended before it answered control request '{subtype}'");

    private void HandleControlResponse(JsonElement message)
    {
        if (!message.TryGetProperty("response", out JsonElement response))
            return;

        if (!response.TryGetProperty("request_id", out JsonElement requestIdElement))
            return;

        string? requestId = requestIdElement.GetString();
        if (requestId == null)
            return;

        lock (_pendingLock)
        {
            if (!_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
                return;

            if (response.TryGetProperty("subtype", out JsonElement subtypeElement) &&
                subtypeElement.GetString() == "error")
            {
                pending.Completion.TrySetException(
                    new ControlRequestRefusedException(pending.Subtype, ReadCliError(response)));
            }
            else
            {
                pending.Completion.TrySetResult(response);
            }
        }
    }

    /// <summary>
    /// The <c>error</c> field of an error control response as the CLI sent it: the string itself, null when
    /// the field is absent or JSON null, and the raw JSON for any other shape so nothing the CLI said is lost.
    /// </summary>
    private static string? ReadCliError(JsonElement response)
    {
        if (!response.TryGetProperty("error", out JsonElement error))
            return null;

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Null => null,
            _ => error.GetRawText()
        };
    }

    private async Task HandleControlRequestAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("request_id", out JsonElement requestIdElement) ||
            !message.TryGetProperty("request", out JsonElement request))
            return;

        string requestId = requestIdElement.GetString()!;
        string? subtype = request.GetProperty("subtype").GetString();

        try
        {
            object? responseData = null;

            switch (subtype)
            {
                case "can_use_tool":
                    responseData = await HandleCanUseToolAsync(request, cancellationToken);
                    break;

                case "hook_callback":
                    responseData = await HandleHookCallbackAsync(request, cancellationToken);
                    break;

                case "mcp_message":
                    responseData = await HandleMcpMessageAsync(request, cancellationToken);
                    break;

                default:
                    throw new ClaudeSDKException($"Unsupported control request subtype: {subtype}");
            }

            // Send success response
            var successResponse = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = responseData
                }
            };

            await _transport.WriteAsync(JsonSerializer.Serialize(successResponse) + "\n", cancellationToken);
        }
        catch (Exception ex)
        {
            // Send error response
            var errorResponse = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "error",
                    request_id = requestId,
                    error = ex.Message
                }
            };

            await _transport.WriteAsync(JsonSerializer.Serialize(errorResponse) + "\n", cancellationToken);
        }
    }

    private async Task<object> HandleCanUseToolAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (_options.CanUseTool == null)
            throw new ClaudeSDKException("canUseTool callback is not provided");

        string toolName = request.GetProperty("tool_name").GetString()!;
        JsonElement input = request.GetProperty("input");
        List<PermissionUpdate>? suggestions = request.TryGetProperty("permission_suggestions", out JsonElement s)
            ? JsonSerializer.Deserialize<List<PermissionUpdate>>(s.GetRawText())
            : null;

        ToolPermissionContext context = new ToolPermissionContext(null, suggestions);
        PermissionResult result = await _options.CanUseTool(toolName, input, context, cancellationToken);

        if (result is PermissionResultAllow allow)
        {
            Dictionary<string, object?> response = new Dictionary<string, object?>
            {
                ["behavior"] = "allow",
                ["updatedInput"] = allow.UpdatedInput.HasValue
                    ? JsonSerializer.Deserialize<object>(allow.UpdatedInput.Value.GetRawText())
                    : JsonSerializer.Deserialize<object>(input.GetRawText())
            };

            if (allow.UpdatedPermissions != null)
            {
                response["updatedPermissions"] = allow.UpdatedPermissions
                    .Select(p => p.ToDictionary())
                    .ToList();
            }

            return response;
        }
        else if (result is PermissionResultDeny deny)
        {
            Dictionary<string, object?> response = new Dictionary<string, object?>
            {
                ["behavior"] = "deny",
                ["message"] = deny.Message
            };

            if (deny.Interrupt)
                response["interrupt"] = true;

            return response;
        }

        throw new ClaudeSDKException($"Invalid permission result type: {result.GetType().Name}");
    }

    private async Task<object> HandleHookCallbackAsync(JsonElement request, CancellationToken cancellationToken)
    {
        string callbackId = request.GetProperty("callback_id").GetString()!;

        if (!_hookCallbacks.TryGetValue(callbackId, out HookCallback? callback))
            throw new ClaudeSDKException($"No hook callback found for ID: {callbackId}");

        JsonElement input = request.TryGetProperty("input", out JsonElement i) ? i : default;
        string? toolUseId = request.TryGetProperty("tool_use_id", out JsonElement t) ? t.GetString() : null;
        HookContext context = new HookContext(null);

        HookOutput output = await callback(input, toolUseId, context, cancellationToken);

        // Convert to dictionary, converting C# property names to CLI expected names
        Dictionary<string, object?> result = new Dictionary<string, object?>();

        if (output.Continue.HasValue)
            result["continue"] = output.Continue.Value;
        if (output.SuppressOutput.HasValue)
            result["suppressOutput"] = output.SuppressOutput.Value;
        if (output.StopReason != null)
            result["stopReason"] = output.StopReason;
        if (output.Decision != null)
            result["decision"] = output.Decision;
        if (output.SystemMessage != null)
            result["systemMessage"] = output.SystemMessage;
        if (output.Reason != null)
            result["reason"] = output.Reason;
        if (output.HookSpecificOutput.HasValue)
            result["hookSpecificOutput"] = JsonSerializer.Deserialize<object>(output.HookSpecificOutput.Value.GetRawText());

        return result;
    }

    private async Task<object> HandleMcpMessageAsync(JsonElement request, CancellationToken cancellationToken)
    {
        string serverName = request.GetProperty("server_name").GetString()!;
        JsonElement message = request.GetProperty("message");

        if (!_sdkMcpBridges.TryGetValue(serverName, out SdkMcpBridge? bridge))
        {
            // Return JSONRPC error for unknown server wrapped in mcp_response
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message.TryGetProperty("id", out JsonElement id) ? id.Clone() : null,
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = -32601,
                        ["message"] = $"SDK MCP server '{serverName}' not found"
                    }
                }
            };
        }

        try
        {
            JsonElement response = await bridge.SendMessageAsync(message, cancellationToken);
            // Wrap the MCP response as expected by the control protocol
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = JsonSerializer.Deserialize<object>(response.GetRawText())
            };
        }
        catch (Exception ex)
        {
            // Return JSONRPC error wrapped in mcp_response
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message.TryGetProperty("id", out JsonElement id) ? id.Clone() : null,
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = -32603,
                        ["message"] = $"MCP server error: {ex.Message}"
                    }
                }
            };
        }
    }

    /// <summary>
    /// Register an SDK MCP server bridge.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <param name="bridge">The bridge instance.</param>
    internal void RegisterSdkMcpBridge(string serverName, SdkMcpBridge bridge)
    {
        _sdkMcpBridges[serverName] = bridge;
    }

    /// <summary>
    /// Send a control request built from <paramref name="subtype"/> and <paramref name="fields"/>, and wait
    /// for the CLI's answer.
    /// </summary>
    /// <exception cref="ControlRequestTimeoutException">The CLI did not answer within <paramref name="timeout"/>.</exception>
    /// <exception cref="ControlRequestRefusedException">The CLI answered with an error response.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// When the read loop ends while the request is pending (or has already ended), the request fails at
    /// once with the transport's own exception, or with a <see cref="CliConnectionException"/> when the
    /// CLI's output simply ended.
    /// </remarks>
    private async Task<JsonElement> SendControlRequestAsync(
        string subtype,
        IReadOnlyDictionary<string, object?>? fields,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{Guid.NewGuid():N}";
        PendingRequest pending = new PendingRequest(subtype);

        lock (_pendingLock)
        {
            // ExceptionDispatchInfo keeps the stored transport failure's original stack trace.
            if (_readLoopEnded)
                ExceptionDispatchInfo.Throw(ConnectionLostException(subtype));

            _pendingRequests[requestId] = pending;
        }

        Dictionary<string, object?> request = new Dictionary<string, object?> { ["subtype"] = subtype };
        if (fields != null)
        {
            foreach ((string key, object? value) in fields)
                request[key] = value;
        }

        var controlRequest = new
        {
            type = "control_request",
            request_id = requestId,
            request
        };

        try
        {
            await _transport.WriteAsync(JsonSerializer.Serialize(controlRequest) + "\n", cancellationToken);

            Task<JsonElement> completion = pending.Completion.Task;
            try
            {
                return await completion.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Either WaitAsync's own timeout fired, or the transport failed the request with a
                // TimeoutException of its own. A completion that is set by now (that failure, or an answer
                // that landed just as the timeout fired) speaks for itself; only an unset one timed out.
                if (completion.IsCompleted)
                    return await completion;

                throw new ControlRequestTimeoutException(subtype, timeout);
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingRequests.Remove(requestId);
            }
        }
    }

    /// <summary>
    /// Send interrupt control request.
    /// </summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync("interrupt", null, ControlRequestTimeout, cancellationToken);
    }

    /// <summary>
    /// Change permission mode.
    /// </summary>
    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            "set_permission_mode",
            new Dictionary<string, object?> { ["mode"] = mode },
            ControlRequestTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Change the AI model.
    /// </summary>
    public async Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            "set_model",
            new Dictionary<string, object?> { ["model"] = model },
            ControlRequestTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            "rewind_files",
            new Dictionary<string, object?> { ["user_message_id"] = userMessageId },
            ControlRequestTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Move in-flight foreground tasks (Bash commands and subagents) to the background.
    /// </summary>
    public async Task<bool> BackgroundTasksAsync(string? toolUseId, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?>? fields = toolUseId == null
            ? null
            : new Dictionary<string, object?> { ["tool_use_id"] = toolUseId };

        JsonElement result = await SendControlRequestAsync(
            "background_tasks",
            fields,
            ControlRequestTimeout,
            cancellationToken);

        return ParseBackgroundedResult(result);
    }

    /// <summary>
    /// Extract the <c>backgrounded</c> flag from a successful <c>background_tasks</c> control response.
    /// </summary>
    /// <remarks>
    /// The flag lives at <c>response.response.backgrounded</c>. The <see cref="JsonValueKind"/> guards
    /// keep this defensive: <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws when
    /// the inner <c>response</c> is present but not an object (e.g. JSON <c>null</c>), and
    /// <see cref="JsonElement.GetBoolean"/> throws when the field is not a boolean. When the flag is
    /// absent or unreadable we return <c>true</c>: an error response never reaches here, because the control
    /// request already threw <see cref="ControlRequestRefusedException"/> for it, so a successful response
    /// with no explicit flag means the tasks were backgrounded. An explicit <c>false</c> is still a successful
    /// answer (nothing was backgrounded), not a refusal.
    /// </remarks>
    internal static bool ParseBackgroundedResult(JsonElement result)
    {
        if (result.TryGetProperty("response", out JsonElement response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("backgrounded", out JsonElement backgrounded) &&
            (backgrounded.ValueKind == JsonValueKind.True || backgrounded.ValueKind == JsonValueKind.False))
        {
            return backgrounded.GetBoolean();
        }

        return true;
    }

    /// <summary>
    /// Stream input messages to transport.
    /// </summary>
    public async Task StreamInputAsync(
        IAsyncEnumerable<Dictionary<string, object?>> stream,
        CancellationToken cancellationToken = default)
    {
        await foreach (Dictionary<string, object?>? message in stream.WithCancellation(cancellationToken))
        {
            if (_closed)
                break;
            await _transport.WriteAsync(JsonSerializer.Serialize(message) + "\n", cancellationToken);
        }

        // If we have SDK MCP servers or hooks, wait for the first result before closing stdin
        // to allow bidirectional control protocol communication (matches Python behavior).
        bool hasHooks = _options.Hooks != null && _options.Hooks.Count > 0;
        bool hasSdkMcpServers = _sdkMcpBridges.Count > 0;
        if (hasHooks || hasSdkMcpServers)
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                await _firstResultEvent.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }

        await _transport.EndInputAsync(cancellationToken);
    }

    /// <summary>
    /// Receive SDK messages (not control messages).
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (JsonElement json in _messageChannel.Reader.ReadAllAsync(cancellationToken))
        {
            Message? message = MessageParser.Parse(json);
            if (message != null)
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// Close the query handler.
    /// </summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeState, 1) == 1)
            return;

        _closed = true;

        CancellationTokenSource? readCts = Interlocked.Exchange(ref _readCts, null);
        if (readCts != null)
        {
            try
            {
                await readCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                readCts.Dispose();
            }
        }

        Task? readTask = Interlocked.Exchange(ref _readTask, null);
        if (readTask != null)
        {
            // The read loop is usually parked in a blocking stdout read that platform pipe
            // I/O may not unblock on cancellation alone. Bound the wait so we always proceed
            // to close the transport, which kills the process and lets the read loop end on
            // EOF. Without this bound, shutdown can deadlock here.
            try { await readTask.WaitAsync(ReadDrainGrace); }
            catch { }
        }

        await _transport.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();

        // Dispose SDK MCP bridges
        foreach (SdkMcpBridge bridge in _sdkMcpBridges.Values)
        {
            await bridge.DisposeAsync();
        }
        _sdkMcpBridges.Clear();
    }

    /// <summary>
    /// A control request awaiting the CLI's answer, with the subtype it was sent as so a failure can name it.
    /// </summary>
    private sealed class PendingRequest
    {
        public PendingRequest(string subtype)
        {
            Subtype = subtype;
        }

        public string Subtype { get; }

        public TaskCompletionSource<JsonElement> Completion { get; } =
            new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
