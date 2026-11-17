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

/// <summary>
/// Raised when a control request to the CLI (such as <c>interrupt</c>, <c>set_model</c> or
/// <c>background_tasks</c>) does not succeed. The subclass says how it failed:
/// <see cref="ControlRequestTimeoutException"/> when the CLI never answered, and
/// <see cref="ControlRequestRefusedException"/> when it answered with an error.
/// </summary>
/// <remarks>
/// Losing the CLI while a request is pending is neither: that surfaces as the transport's own
/// exception, or as a <see cref="CliConnectionException"/> when the CLI's output simply ended.
/// </remarks>
public abstract class ControlRequestException : ClaudeSDKException
{
    /// <summary>
    /// Gets the subtype of the control request that failed, e.g. <c>background_tasks</c>.
    /// </summary>
    public string Subtype { get; }

    /// <summary>
    /// Initializes a new instance of the ControlRequestException class with a specified error message and request subtype.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="subtype">The subtype of the control request that failed.</param>
    private protected ControlRequestException(string message, string subtype) : base(message)
    {
        Subtype = subtype;
    }
}

/// <summary>
/// Raised when the CLI did not answer a control request within its timeout.
/// </summary>
public sealed class ControlRequestTimeoutException : ControlRequestException
{
    /// <summary>
    /// Gets how long the request waited for an answer before giving up.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Initializes a new instance of the ControlRequestTimeoutException class with the request subtype and timeout.
    /// </summary>
    /// <param name="subtype">The subtype of the control request that timed out.</param>
    /// <param name="timeout">How long the request waited for an answer.</param>
    public ControlRequestTimeoutException(string subtype, TimeSpan timeout)
        : base($"Control request '{subtype}' got no answer from the CLI within {timeout.TotalSeconds:0.###}s", subtype)
    {
        Timeout = timeout;
    }
}

/// <summary>
/// Raised when the CLI answered a control request with an error response.
/// </summary>
public sealed class ControlRequestRefusedException : ControlRequestException
{
    /// <summary>
    /// Gets the CLI's <c>error</c> text exactly as sent, or null when the error response had none.
    /// </summary>
    public string? CliError { get; }

    /// <summary>
    /// Initializes a new instance of the ControlRequestRefusedException class with the request subtype and the CLI's error text.
    /// </summary>
    /// <param name="subtype">The subtype of the control request the CLI refused.</param>
    /// <param name="cliError">The CLI's error text exactly as sent, or null when the response had none.</param>
    public ControlRequestRefusedException(string subtype, string? cliError)
        : base(cliError != null
            ? $"CLI refused control request '{subtype}': {cliError}"
            : $"CLI refused control request '{subtype}' without giving a reason", subtype)
    {
        CliError = cliError;
    }
}
