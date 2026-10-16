using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Integration test for the property <see cref="AssistantMessage.Uuid"/> documents: a streamed
/// assistant message's uuid equals the uuid of its line in the session <c>.jsonl</c> transcript,
/// which is what lets a caller fork a session at that message. It requires a live Claude CLI
/// environment and reads the transcript the CLI writes under its config directory.
/// </summary>
public class RecordUuidIntegrationTests
{
    [IntegrationFact]
    public async Task AssistantMessageUuid_MatchesItsTranscriptLine()
    {
        string cwd = Directory.CreateTempSubdirectory("record-uuid-").FullName;
        List<AssistantMessage> assistantMessages = new List<AssistantMessage>();
        string? sessionId = null;

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            Model = "haiku",
            Tools = [],
            MaxTurns = 1,
            Cwd = cwd
        };

        try
        {
            // Disposed before the transcript is read, so the CLI has exited and finished writing it.
            await using ClaudeSDKClient client = new ClaudeSDKClient(options);
            await client.ConnectAsync();
            await client.QueryAsync("Reply with the single word: pong");

            await foreach (Message msg in client.ReceiveResponseAsync())
            {
                if (msg is AssistantMessage assistant)
                    assistantMessages.Add(assistant);
                else if (msg is ResultMessage result)
                    sessionId = result.SessionId;
            }
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }

        Assert.NotNull(sessionId);
        Assert.NotEmpty(assistantMessages);

        string configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string transcriptPath = Assert.Single(
            Directory.GetFiles(Path.Combine(configDir, "projects"), $"{sessionId}.jsonl", SearchOption.AllDirectories));

        HashSet<string> assistantLineUuids = new HashSet<string>();
        foreach (string line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonElement record = JsonDocument.Parse(line).RootElement;
            if (record.TryGetProperty("type", out JsonElement type) && type.GetString() == "assistant" &&
                record.TryGetProperty("uuid", out JsonElement uuid))
                assistantLineUuids.Add(uuid.GetString()!);
        }

        foreach (AssistantMessage assistant in assistantMessages)
        {
            Assert.NotNull(assistant.Uuid);
            Assert.Contains(assistant.Uuid, assistantLineUuids);
        }
    }
}
