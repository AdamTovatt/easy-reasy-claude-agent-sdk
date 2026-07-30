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
}
