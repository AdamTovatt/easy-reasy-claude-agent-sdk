using System.Text.Json;

namespace EasyReasy.Claude.AgentSdk;

/// <summary>
/// Base exception for all Claude SDK errors.
/// </summary>
public class ClaudeSDKException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ClaudeSDKException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ClaudeSDKException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the ClaudeSDKException class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ClaudeSDKException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when unable to connect to Claude Code CLI.
/// </summary>
public class CliConnectionException : ClaudeSDKException
{
    /// <summary>
    /// Initializes a new instance of the CliConnectionException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CliConnectionException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the CliConnectionException class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CliConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when Claude Code CLI is not found or not installed.
/// </summary>
public class CliNotFoundException : CliConnectionException
{
    /// <summary>
    /// Gets the path where the CLI was expected to be found.
    /// </summary>
    public string? CliPath { get; }

    /// <summary>
    /// Initializes a new instance of the CliNotFoundException class with a specified error message and optional CLI path.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="cliPath">The path where the CLI was expected to be found.</param>
    public CliNotFoundException(string message, string? cliPath = null)
        : base(cliPath != null ? $"{message}: {cliPath}" : message)
    {
        CliPath = cliPath;
    }
}

/// <summary>
/// Raised when the CLI process fails.
/// </summary>
public class ProcessException : ClaudeSDKException
{
    /// <summary>
    /// Gets the exit code of the failed process.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Gets the standard error output from the failed process.
    /// </summary>
    public string? Stderr { get; }

    /// <summary>
    /// Initializes a new instance of the ProcessException class with a specified error message, exit code, and stderr output.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="exitCode">The exit code of the failed process.</param>
    /// <param name="stderr">The standard error output from the failed process.</param>
    public ProcessException(string message, int? exitCode = null, string? stderr = null)
        : base(FormatMessage(message, exitCode, stderr))
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    private static string FormatMessage(string message, int? exitCode, string? stderr)
    {
        if (exitCode.HasValue)
            message = $"{message} (exit code: {exitCode})";
        if (!string.IsNullOrEmpty(stderr))
            message = $"{message}\nError output: {stderr}";
        return message;
    }
}

/// <summary>
/// Raised when unable to decode JSON from CLI output.
/// </summary>
public class JsonDecodeException : ClaudeSDKException
{
    /// <summary>
    /// Gets the line of text that failed to decode as JSON.
    /// </summary>
    public string Line { get; }

    /// <summary>
    /// Initializes a new instance of the JsonDecodeException class with the failed line and inner exception.
    /// </summary>
    /// <param name="line">The line of text that failed to decode as JSON.</param>
    /// <param name="innerException">The exception that occurred during JSON decoding.</param>
    public JsonDecodeException(string line, Exception innerException)
        : base($"Failed to decode JSON: {line[..Math.Min(100, line.Length)]}...", innerException)
    {
        Line = line;
    }
}

/// <summary>
/// Raised when unable to parse a message from CLI output.
/// </summary>
public class MessageParseException : ClaudeSDKException
{
    /// <summary>
    /// Gets the raw JSON data that failed to parse.
    /// </summary>
    public JsonElement? RawData { get; }

    /// <summary>
    /// Initializes a new instance of the MessageParseException class with a specified error message and optional raw data.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="rawData">The raw JSON data that failed to parse.</param>
    public MessageParseException(string message, JsonElement? rawData = null)
        : base(message)
    {
        RawData = rawData;
    }
}
