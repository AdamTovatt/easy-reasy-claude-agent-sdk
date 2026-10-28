using EasyReasy.Claude.AgentSdk.Internal;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

public class MessageParserTests
{
    [Fact]
    public void Parse_UserMessage_WithStringContent()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "user",
            "message": {
                "role": "user",
                "content": "Hello, Claude!"
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.IsType<UserMessage>(message);
        UserMessage userMessage = (UserMessage)message;
        Assert.Equal("Hello, Claude!", userMessage.GetTextContent());
    }

    [Fact]
    public void Parse_AssistantMessage_WithTextBlock()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    {
                        "type": "text",
                        "text": "Hello! How can I help you?"
                    }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.IsType<AssistantMessage>(message);
        AssistantMessage assistantMessage = (AssistantMessage)message;
        Assert.Equal("claude-3-opus", assistantMessage.Model);
        Assert.Single(assistantMessage.Content);
        Assert.IsType<TextBlock>(assistantMessage.Content[0]);
        Assert.Equal("Hello! How can I help you?", ((TextBlock)assistantMessage.Content[0]).Text);
    }

    [Fact]
    public void Parse_AssistantMessage_WithToolUseBlock()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    {
                        "type": "tool_use",
                        "id": "tool_123",
                        "name": "Read",
                        "input": {"file_path": "/tmp/test.txt"}
                    }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.IsType<AssistantMessage>(message);
        AssistantMessage assistantMessage = (AssistantMessage)message;
        Assert.Single(assistantMessage.Content);
        Assert.IsType<ToolUseBlock>(assistantMessage.Content[0]);
        ToolUseBlock toolUse = (ToolUseBlock)assistantMessage.Content[0];
        Assert.Equal("tool_123", toolUse.Id);
        Assert.Equal("Read", toolUse.Name);
    }

    [Fact]
    public void Parse_AssistantMessage_WithFallbackBlock()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-sonnet-5-5",
                "content": [
                    {
                        "type": "fallback",
                        "from": { "model": "claude-opus-5-5" },
                        "to": { "model": "claude-sonnet-5-5" }
                    },
                    { "type": "text", "text": "Here is the answer." }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Equal(2, assistantMessage.Content.Count);
        FallbackBlock fallback = Assert.IsType<FallbackBlock>(assistantMessage.Content[0]);
        Assert.Equal("claude-opus-5-5", fallback.FromModel);
        Assert.Equal("claude-sonnet-5-5", fallback.ToModel);
        Assert.Equal("Here is the answer.", Assert.IsType<TextBlock>(assistantMessage.Content[1]).Text);
    }

    [Fact]
    public void Parse_AssistantMessage_WithUnknownBlockType_KeepsItAndTheOtherBlocks()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": "text", "text": "Before" },
                    { "type": "hologram", "frames": [1, 2, 3] },
                    { "type": "text", "text": "After" }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Equal(3, assistantMessage.Content.Count);
        Assert.Equal("Before", Assert.IsType<TextBlock>(assistantMessage.Content[0]).Text);
        UnknownContentBlock unknown = Assert.IsType<UnknownContentBlock>(assistantMessage.Content[1]);
        Assert.Equal("hologram", unknown.Type);
        Assert.Equal(3, unknown.Raw.GetProperty("frames").GetArrayLength());
        Assert.Equal("After", Assert.IsType<TextBlock>(assistantMessage.Content[2]).Text);
    }

    [Fact]
    public void Parse_AssistantMessage_WithFallbackBlockMissingModel_Throws()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": "fallback", "from": {}, "to": { "model": "claude-3-opus" } }
                ]
            }
        }
        """);

        Assert.Throws<MessageParseException>(() => MessageParser.Parse(json));
    }

    [Fact]
    public void Parse_UserMessage_GetContentBlocks_WithUnknownBlockType_KeepsItAndTheOtherBlocks()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "user",
            "message": {
                "role": "user",
                "content": [
                    { "tool_use_id": "tool_123", "type": "tool_result", "content": "ok" },
                    { "type": "hologram", "frames": [1, 2, 3] }
                ]
            }
        }
        """);

        UserMessage userMessage = Assert.IsType<UserMessage>(MessageParser.Parse(json));
        IReadOnlyList<ContentBlock>? blocks = userMessage.GetContentBlocks();

        Assert.NotNull(blocks);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("tool_123", Assert.IsType<ToolResultBlock>(blocks[0]).ToolUseId);
        Assert.Equal("hologram", Assert.IsType<UnknownContentBlock>(blocks[1]).Type);
    }

    [Fact]
    public void Parse_UserMessage_GetContentBlocks_WithBlockMissingField_Throws()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "user",
            "message": {
                "role": "user",
                "content": [
                    { "type": "tool_result", "content": "ok" }
                ]
            }
        }
        """);

        UserMessage userMessage = Assert.IsType<UserMessage>(MessageParser.Parse(json));

        Assert.Throws<MessageParseException>(() => userMessage.GetContentBlocks());
    }

    [Fact]
    public void Parse_AssistantMessage_WithNullBlockType_Throws()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": null, "text": "Hi" }
                ]
            }
        }
        """);

        Assert.Throws<MessageParseException>(() => MessageParser.Parse(json));
    }

    [Fact]
    public void Parse_AssistantMessage_ExposesUsageFromMessage()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-opus-4-8",
                "content": [
                    { "type": "text", "text": "Hi" }
                ],
                "usage": {
                    "input_tokens": 2161,
                    "cache_read_input_tokens": 16507,
                    "cache_creation_input_tokens": 2476,
                    "output_tokens": 124
                }
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.NotNull(assistantMessage.Usage);
        Assert.Equal(2161, assistantMessage.Usage.Value.GetProperty("input_tokens").GetInt32());
        Assert.Equal(16507, assistantMessage.Usage.Value.GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public void Parse_AssistantMessage_WithoutUsage_UsageIsNull()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": "text", "text": "Hi" }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Usage);
    }

    [Fact]
    public void Parse_ResultMessage()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "result",
            "subtype": "success",
            "duration_ms": 1234,
            "duration_api_ms": 1000,
            "is_error": false,
            "num_turns": 3,
            "session_id": "session_abc",
            "total_cost_usd": 0.05
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.IsType<ResultMessage>(message);
        ResultMessage resultMessage = (ResultMessage)message;
        Assert.Equal("success", resultMessage.Subtype);
        Assert.Equal(1234, resultMessage.DurationMs);
        Assert.Equal(1000, resultMessage.DurationApiMs);
        Assert.False(resultMessage.IsError);
        Assert.Equal(3, resultMessage.NumTurns);
        Assert.Equal("session_abc", resultMessage.SessionId);
        Assert.Equal(0.05m, resultMessage.TotalCostUsd);
    }

    [Fact]
    public void Parse_SystemMessage()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "system",
            "subtype": "init",
            "data": {"key": "value"}
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.IsType<SystemMessage>(message);
        SystemMessage systemMessage = (SystemMessage)message;
        Assert.Equal("init", systemMessage.Subtype);
    }

    [Fact]
    public void Parse_ThrowsOnMissingType()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "message": "no type field"
        }
        """);

        Assert.Throws<MessageParseException>(() => MessageParser.Parse(json));
    }

    [Fact]
    public void Parse_ReturnsNullOnUnknownType()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "unknown_type"
        }
        """);

        Message? message = MessageParser.Parse(json);

        Assert.Null(message);
    }

    /// <summary>
    /// The CLI emits <c>error</c> at the record level, as a sibling of <c>message</c>, and flags it
    /// with <c>is_api_error_message</c>. The CLI's transcript files spell that flag
    /// <c>isApiErrorMessage</c> instead, so a fixture taken from a transcript does not match the
    /// wire format the SDK reads; see issue #4, which records both spellings and the capture below.
    /// This record is the stream form, captured with ANTHROPIC_BASE_URL pointed at a server
    /// returning 529 with an overloaded_error body.
    /// <para>
    /// Every key and its ordering is as captured, as are <c>error</c> and
    /// <c>is_api_error_message</c>. Four values are reconstructed, because the capture elided
    /// them: the <c>content</c> array, <c>session_id</c>, <c>uuid</c> and <c>timestamp</c>. The
    /// content text is modelled on the error text the CLI was observed to emit, but it is not
    /// itself captured, so it evidences only that content still parses alongside a record-level
    /// error. The <c>&lt;synthetic&gt;</c> model is not one of the four: it is the CLI's own
    /// literal placeholder for a message it generated instead of a model, captured as-is.
    /// </para>
    /// </summary>
    [Fact]
    public void Parse_AssistantMessage_ExposesErrorFromRecord()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"assistant","message":{"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","type":"message","content":[{"type":"text","text":"API Error: Repeated 529 Overloaded"}]},"parent_tool_use_id":null,"session_id":"synthetic-session-id","uuid":"synthetic-uuid","timestamp":"2026-07-30T12:41:07.512Z","error":"server_error","is_api_error_message":true}
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Equal(AssistantMessageError.ServerError, assistantMessage.Error);
        Assert.Equal("<synthetic>", assistantMessage.Model);
        TextBlock textBlock = Assert.IsType<TextBlock>(Assert.Single(assistantMessage.Content));
        Assert.Equal("API Error: Repeated 529 Overloaded", textBlock.Text);
    }

    /// <summary>
    /// Every mapped error string, plus the two that fall to
    /// <see cref="AssistantMessageError.Unknown"/>: an unrecognised value, and the empty string.
    /// The empty string is a string, so the parser's kind check passes it through to the mapping
    /// rather than treating it as absent. Mapping it to <c>Unknown</c> is the chosen behaviour and
    /// is pinned here; no wire record is known to carry it, so the parser does not special-case it.
    /// </summary>
    [Theory]
    [InlineData("rate_limit", AssistantMessageError.RateLimit)]
    [InlineData("server_error", AssistantMessageError.ServerError)]
    [InlineData("authentication_failed", AssistantMessageError.AuthenticationFailed)]
    [InlineData("billing_error", AssistantMessageError.BillingError)]
    [InlineData("invalid_request", AssistantMessageError.InvalidRequest)]
    [InlineData("something_new", AssistantMessageError.Unknown)]
    [InlineData("", AssistantMessageError.Unknown)]
    public void Parse_AssistantMessage_MapsErrorValue(string errorValue, AssistantMessageError expected)
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "<synthetic>",
                "content": [
                    { "type": "text", "text": "API Error" }
                ]
            },
            "error": "{{errorValue}}"
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Equal(expected, assistantMessage.Error);
    }

    /// <summary>
    /// An explicit <c>"error": null</c> reports no error, which is a different failure mode from
    /// the other non-string kinds and the reason the string check is not merely defensive.
    /// <c>GetString</c> returns null rather than throwing for <see cref="JsonValueKind.Null"/>, so
    /// an unguarded read falls through to the unmapped-value arm and yields
    /// <see cref="AssistantMessageError.Unknown"/>. A message stating it had no error would then
    /// report one, and a caller branching on <c>Error is not null</c> would treat it as failed.
    /// A wrong signal is worse than a loud one: it is the same defect class as the missing error.
    /// </summary>
    [Fact]
    public void Parse_AssistantMessage_WithExplicitNullError_ErrorIsNull()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": "text", "text": "Hi" }
                ]
            },
            "error": null
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Error);
    }

    /// <summary>
    /// A record-level <c>error</c> of any other non-string kind leaves
    /// <see cref="AssistantMessage.Error"/> null rather than failing the message. Unlike the
    /// explicit-null case above, reading one of these as a string throws out of the parse and
    /// takes down an otherwise well-formed message.
    /// </summary>
    [Theory]
    [InlineData("429")]
    [InlineData("true")]
    [InlineData("{ \"type\": \"overloaded_error\" }")]
    public void Parse_AssistantMessage_WithNonStringError_ErrorIsNull(string errorLiteral)
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    { "type": "text", "text": "Hi" }
                ]
            },
            "error": {{errorLiteral}}
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Error);
    }

    [Fact]
    public void Parse_AssistantMessage_WithoutError_ErrorIsNull()
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "assistant",
            "message": {
                "role": "assistant",
                "model": "claude-3-opus",
                "content": [
                    {
                        "type": "text",
                        "text": "Hello!"
                    }
                ]
            }
        }
        """);

        Message? message = MessageParser.Parse(json);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Error);
    }

    // The records below are trimmed from a real CLI stream (CLI 2.1.300, run with
    // --replay-user-messages so the user message is echoed): usage, timestamps and other fields
    // the parser does not read are dropped; the remaining keys, their order and values are as captured.

    private const string CapturedSessionId = "1e0a44cf-c7c1-4445-bd9b-0f3defcc6046";
    private const string CapturedUserUuid = "244e12ee-c9bf-45e0-9d40-305db3ec4b23";
    private const string CapturedAssistantUuid = "9c459827-cabe-42d1-9d82-35954215ce1a";
    private const string CapturedResultUuid = "b17d4379-e332-4f74-9e26-f8c1c59bf6c2";
    private const string CapturedRateLimitEventUuid = "4419d1b1-1266-463f-99df-eaa7142af0cc";

    private const string CapturedUserRecord = $$"""
        {"type":"user","message":{"role":"user","content":"Reply with the single word: pong"},"session_id":"{{CapturedSessionId}}","parent_tool_use_id":null,"uuid":"{{CapturedUserUuid}}","isReplay":true}
        """;

    private const string CapturedAssistantRecord = $$"""
        {"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_011Cg5Hdn29nWfqt4ekQjafN","type":"message","role":"assistant","content":[{"type":"text","text":"pong"}],"stop_reason":null},"parent_tool_use_id":null,"session_id":"{{CapturedSessionId}}","uuid":"{{CapturedAssistantUuid}}"}
        """;

    private const string CapturedResultRecord = $$"""
        {"duration_api_ms":2764,"stop_reason":"end_turn","session_id":"{{CapturedSessionId}}","total_cost_usd":0.00165288,"terminal_reason":"completed","is_error":false,"num_turns":1,"subtype":"success","result":"pong","type":"result","duration_ms":2210,"uuid":"{{CapturedResultUuid}}"}
        """;

    private const string CapturedRateLimitEventRecord = $$"""
        {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792143000,"rateLimitType":"five_hour"},"uuid":"{{CapturedRateLimitEventUuid}}","session_id":"{{CapturedSessionId}}"}
        """;

    /// <summary>
    /// Literals for a record-level <c>uuid</c> that is not a string. Each must leave the uuid null
    /// without failing the message; all but <c>null</c> throw out of an unguarded GetString.
    /// </summary>
    public static TheoryData<string> NonStringUuidLiterals => new TheoryData<string>
    {
        "null",
        "42",
        "true",
        "{ \"id\": \"x\" }",
        "[\"x\"]"
    };

    private static Message? ParseRecord(string record) => MessageParser.Parse(JsonDocument.Parse(record).RootElement);

    /// <summary>
    /// Returns the captured record with its uuid removed (<paramref name="uuidLiteral"/> null) or
    /// replaced by the given raw JSON literal.
    /// </summary>
    private static string WithUuid(string capturedRecord, string? uuidLiteral)
    {
        Dictionary<string, JsonElement> fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(capturedRecord)!;
        fields.Remove("uuid");
        if (uuidLiteral != null)
            fields["uuid"] = JsonSerializer.Deserialize<JsonElement>(uuidLiteral);
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public void Parse_UserMessage_WithUuid_ExposesUuid()
    {
        Message? message = ParseRecord(CapturedUserRecord);

        UserMessage userMessage = Assert.IsType<UserMessage>(message);
        Assert.Equal(CapturedUserUuid, userMessage.Uuid);
    }

    [Fact]
    public void Parse_UserMessage_WithoutUuid_UuidIsNull()
    {
        Message? message = ParseRecord(WithUuid(CapturedUserRecord, null));

        UserMessage userMessage = Assert.IsType<UserMessage>(message);
        Assert.Null(userMessage.Uuid);
    }

    [Theory]
    [MemberData(nameof(NonStringUuidLiterals))]
    public void Parse_UserMessage_WithNonStringUuid_UuidIsNull(string uuidLiteral)
    {
        Message? message = ParseRecord(WithUuid(CapturedUserRecord, uuidLiteral));

        UserMessage userMessage = Assert.IsType<UserMessage>(message);
        Assert.Null(userMessage.Uuid);
        Assert.Equal("Reply with the single word: pong", userMessage.GetTextContent());
    }

    [Fact]
    public void Parse_AssistantMessage_WithUuid_ExposesUuid()
    {
        Message? message = ParseRecord(CapturedAssistantRecord);

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Equal(CapturedAssistantUuid, assistantMessage.Uuid);
    }

    [Fact]
    public void Parse_AssistantMessage_WithoutUuid_UuidIsNull()
    {
        Message? message = ParseRecord(WithUuid(CapturedAssistantRecord, null));

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Uuid);
    }

    [Theory]
    [MemberData(nameof(NonStringUuidLiterals))]
    public void Parse_AssistantMessage_WithNonStringUuid_UuidIsNull(string uuidLiteral)
    {
        Message? message = ParseRecord(WithUuid(CapturedAssistantRecord, uuidLiteral));

        AssistantMessage assistantMessage = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistantMessage.Uuid);
        Assert.Equal("claude-haiku-5-5", assistantMessage.Model);
    }

    [Fact]
    public void Parse_ResultMessage_WithUuid_ExposesUuid()
    {
        Message? message = ParseRecord(CapturedResultRecord);

        ResultMessage resultMessage = Assert.IsType<ResultMessage>(message);
        Assert.Equal(CapturedResultUuid, resultMessage.Uuid);
    }

    [Fact]
    public void Parse_ResultMessage_WithoutUuid_UuidIsNull()
    {
        Message? message = ParseRecord(WithUuid(CapturedResultRecord, null));

        ResultMessage resultMessage = Assert.IsType<ResultMessage>(message);
        Assert.Null(resultMessage.Uuid);
    }

    [Theory]
    [MemberData(nameof(NonStringUuidLiterals))]
    public void Parse_ResultMessage_WithNonStringUuid_UuidIsNull(string uuidLiteral)
    {
        Message? message = ParseRecord(WithUuid(CapturedResultRecord, uuidLiteral));

        ResultMessage resultMessage = Assert.IsType<ResultMessage>(message);
        Assert.Null(resultMessage.Uuid);
        Assert.Equal(CapturedSessionId, resultMessage.SessionId);
    }

    [Fact]
    public void Parse_RateLimitEvent_WithUuid_ExposesUuid()
    {
        Message? message = ParseRecord(CapturedRateLimitEventRecord);

        RateLimitEvent rateLimitEvent = Assert.IsType<RateLimitEvent>(message);
        Assert.Equal(CapturedRateLimitEventUuid, rateLimitEvent.Uuid);
    }

    [Fact]
    public void Parse_RateLimitEvent_WithoutUuid_UuidIsNull()
    {
        Message? message = ParseRecord(WithUuid(CapturedRateLimitEventRecord, null));

        RateLimitEvent rateLimitEvent = Assert.IsType<RateLimitEvent>(message);
        Assert.Null(rateLimitEvent.Uuid);
    }

    [Theory]
    [MemberData(nameof(NonStringUuidLiterals))]
    public void Parse_RateLimitEvent_WithNonStringUuid_UuidIsNull(string uuidLiteral)
    {
        Message? message = ParseRecord(WithUuid(CapturedRateLimitEventRecord, uuidLiteral));

        RateLimitEvent rateLimitEvent = Assert.IsType<RateLimitEvent>(message);
        Assert.Null(rateLimitEvent.Uuid);
        Assert.Equal("allowed", rateLimitEvent.RateLimitInfo.Status);
    }
}
