namespace DotNodeViewer.Core.Abstractions;

/// <summary>
/// Represents an error that occurs when a neural network model file is malformed, truncated, or violates its format specification.
/// </summary>
public sealed class ModelParseException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ModelParseException"/> class.
    /// </summary>
    public ModelParseException()
        : base("Failed to parse the neural network model stream.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelParseException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public ModelParseException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelParseException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or <see langword="null"/> if no inner exception is specified.</param>
    public ModelParseException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelParseException"/> class with a specified error message, byte offset, parser context, and optional inner exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="fileOffset">The optional zero-based byte offset in the model stream where the error occurred.</param>
    /// <param name="context">An optional node name, tensor name, or structural context describing what was being parsed.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or <see langword="null"/> if no inner exception is specified.</param>
    public ModelParseException(
        string message,
        long? fileOffset,
        string? context = null,
        Exception? innerException = null)
        : base(FormatMessage(message, fileOffset, context), innerException)
    {
        FileOffset = fileOffset;
        Context = context;
    }

    /// <summary>
    /// Gets the zero-based byte offset in the model stream where the parse failure occurred, if known.
    /// </summary>
    /// <value>The zero-based byte offset, or <see langword="null"/> if not applicable.</value>
    public long? FileOffset { get; }

    /// <summary>
    /// Gets the operator node name, tensor name, or structural section being parsed when the failure occurred, if known.
    /// </summary>
    /// <value>The parser context string, or <see langword="null"/> if not applicable.</value>
    public string? Context { get; }

    private static string FormatMessage(string message, long? fileOffset, string? context)
    {
        if (fileOffset is null && string.IsNullOrWhiteSpace(context))
        {
            return message;
        }

        if (fileOffset is not null && !string.IsNullOrWhiteSpace(context))
        {
            return $"{message} (offset: 0x{fileOffset.Value:X}, context: {context})";
        }

        if (fileOffset is not null)
        {
            return $"{message} (offset: 0x{fileOffset.Value:X})";
        }

        return $"{message} (context: {context})";
    }
}

