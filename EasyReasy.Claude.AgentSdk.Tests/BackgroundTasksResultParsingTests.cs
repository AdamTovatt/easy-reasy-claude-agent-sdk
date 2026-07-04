using EasyReasy.Claude.AgentSdk.Internal;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Tests for <see cref="QueryHandler.ParseBackgroundedResult"/>, which derives the
/// <c>background_tasks</c> return value from the control response payload. Covers the
/// well-formed branches plus the malformed shapes that must not throw.
/// </summary>
public sealed class BackgroundTasksResultParsingTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BackgroundedTrue_ReturnsTrue()
    {
        JsonElement result = Parse("""{ "response": { "backgrounded": true } }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void BackgroundedFalse_ReturnsFalse()
    {
        JsonElement result = Parse("""{ "response": { "backgrounded": false } }""");

        Assert.False(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void BackgroundedFieldAbsent_AssumesSuccess()
    {
        JsonElement result = Parse("""{ "response": { "other": 1 } }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void ResponseFieldAbsent_AssumesSuccess()
    {
        JsonElement result = Parse("""{ "subtype": "success" }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void ResponseIsJsonNull_DoesNotThrow_AssumesSuccess()
    {
        // A present-but-null inner "response" would make TryGetProperty throw
        // without the JsonValueKind.Object guard.
        JsonElement result = Parse("""{ "response": null }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void ResponseIsNonObject_DoesNotThrow_AssumesSuccess()
    {
        JsonElement result = Parse("""{ "response": "backgrounded" }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }

    [Fact]
    public void BackgroundedIsNonBoolean_DoesNotThrow_AssumesSuccess()
    {
        // GetBoolean() would throw on a non-boolean field without the JsonValueKind guard.
        JsonElement result = Parse("""{ "response": { "backgrounded": "yes" } }""");

        Assert.True(QueryHandler.ParseBackgroundedResult(result));
    }
}
