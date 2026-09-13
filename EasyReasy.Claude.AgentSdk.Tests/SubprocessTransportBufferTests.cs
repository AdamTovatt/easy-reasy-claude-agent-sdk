using EasyReasy.Claude.AgentSdk.Transport;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// The read buffer bounds a malformed stream, not a legitimate message. The CLI repeats an image's
/// base64 twice in one stdout line — once as the API content block's <c>source.data</c> and again as
/// <c>tool_use_result.file.base64</c> — so a 400 KB screenshot arrives as a single line of roughly
/// 1.1 MB. Against the old 1 MB ceiling that threw, which killed the read loop and with it the only
/// thing answering the CLI's hook callbacks, wedging the session for good.
/// </summary>
public class SubprocessTransportBufferTests : IDisposable
{
    private readonly List<string> _scripts = new List<string>();

    [PosixFact]
    public async Task ReadMessagesAsync_WhenLineIsLargerThanOneMegabyte_YieldsTheMessage()
    {
        // Above the old ceiling and in the size range a phone screenshot actually produces.
        string payload = new string('A', 1_200_000);
        SubprocessTransport transport = ConnectedTransportEmitting(payload, maxBufferSize: null);

        JsonElement message = await SingleMessageAsync(transport);

        Assert.Equal(payload, message.GetProperty("message").GetProperty("content").GetString());
    }

    [PosixFact]
    public async Task ReadMessagesAsync_WhenLineExceedsConfiguredMaxBufferSize_Throws()
    {
        // The guard still exists — which is also what stops the test above passing for the wrong
        // reason, by showing the reader does refuse a line once one is genuinely too long.
        string payload = new string('A', 1_200_000);
        SubprocessTransport transport = ConnectedTransportEmitting(payload, maxBufferSize: 64 * 1024);

        await Assert.ThrowsAsync<JsonDecodeException>(async () => await SingleMessageAsync(transport));
    }

    private static async Task<JsonElement> SingleMessageAsync(SubprocessTransport transport)
    {
        await foreach (JsonElement message in transport.ReadMessagesAsync())
        {
            return message;
        }

        throw new InvalidOperationException("The transport yielded no message.");
    }

    /// <summary>
    /// A transport connected to a script standing in for the CLI, which answers the version probe and
    /// then writes one SDK message carrying the given payload as a single stdout line.
    /// </summary>
    private SubprocessTransport ConnectedTransportEmitting(string payload, int? maxBufferSize)
    {
        string line = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = payload }
        });

        string scriptPath = Path.Combine(Path.GetTempPath(), $"fake-claude-{Guid.NewGuid():N}.sh");
        string payloadPath = scriptPath + ".json";

        // The line goes in its own file rather than inline in the script, so nothing has to survive
        // shell quoting at a megabyte and the script stays a `cat`.
        File.WriteAllText(payloadPath, line + "\n", new UTF8Encoding(false));
        File.WriteAllText(
            scriptPath,
            $"""
            #!/bin/sh
            case "$*" in
              *--version*) echo "2.1.0 (Claude Code)"; exit 0;;
            esac
            cat "{payloadPath}"
            """,
            new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _scripts.Add(scriptPath);
        _scripts.Add(payloadPath);

        ClaudeAgentOptions options = new ClaudeAgentOptions
        {
            CliPath = scriptPath,
            MaxBufferSize = maxBufferSize
        };

        SubprocessTransport transport = new SubprocessTransport("hello", options);
        transport.ConnectAsync().GetAwaiter().GetResult();
        return transport;
    }

    public void Dispose()
    {
        foreach (string path in _scripts)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
