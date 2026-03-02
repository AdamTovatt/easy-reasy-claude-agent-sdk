using EasyReasy.Claude.AgentSdk.Mcp;
using EasyReasy.Claude.AgentSdk.Transport;
using System.Runtime.CompilerServices;
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
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly Dictionary<string, HookCallback> _hookCallbacks = new();
    private readonly Dictionary<string, SdkMcpBridge> _sdkMcpBridges = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private bool _initialized;
    private bool _closed;
    private int _closeState;
    private int _requestCounter;
    private int _nextCallbackId;
    private TaskCompletionSource _firstResultEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement? _initializationResult;

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

        Dictionary<string, object?> request = new Dictionary<string, object?>
        {
            ["subtype"] = "initialize",
            ["hooks"] = hooksConfig.Count > 0 ? hooksConfig : null
        };

        JsonElement response = await SendControlRequestAsync(request, _initializeTimeout, cancellationToken);
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
                    await HandleControlResponseAsync(message);
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
            // Signal all pending control requests
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                foreach ((string? requestId, TaskCompletionSource<JsonElement>? tcs) in _pendingRequests)
                {
                    tcs.TrySetException(ex);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            _messageChannel.Writer.Complete();
        }
    }

    private async Task HandleControlResponseAsync(JsonElement message)
    {
        if (!message.TryGetProperty("response", out JsonElement response))
            return;

        if (!response.TryGetProperty("request_id", out JsonElement requestIdElement))
            return;

        string? requestId = requestIdElement.GetString();
        if (requestId == null)
            return;

        await _lock.WaitAsync();
        try
        {
            if (_pendingRequests.TryGetValue(requestId, out TaskCompletionSource<JsonElement>? tcs))
            {
                if (response.TryGetProperty("subtype", out JsonElement subtypeElement) &&
                    subtypeElement.GetString() == "error")
                {
                    string errorMsg = response.TryGetProperty("error", out JsonElement e)
                        ? e.GetString() ?? "Unknown error"
                        : "Unknown error";
                    tcs.TrySetException(new ClaudeSDKException(errorMsg));
                }
                else
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
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

    private async Task<JsonElement> SendControlRequestAsync(
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{Guid.NewGuid():N}";
        TaskCompletionSource<JsonElement> tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _pendingRequests[requestId] = tcs;
        }
        finally
        {
            _lock.Release();
        }

        var controlRequest = new
        {
            type = "control_request",
            request_id = requestId,
            request
        };

        await _transport.WriteAsync(JsonSerializer.Serialize(controlRequest) + "\n", cancellationToken);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ClaudeSDKException($"Control request timeout: {request}");
        }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                _pendingRequests.Remove(requestId);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// Send interrupt control request.
    /// </summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "interrupt" },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Change permission mode.
    /// </summary>
    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "set_permission_mode", mode },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Change the AI model.
    /// </summary>
    public async Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "set_model", model },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "rewind_files", user_message_id = userMessageId },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
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
            yield return MessageParser.Parse(json);
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
            try { await readTask; }
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

        _lock.Dispose();
    }
}
