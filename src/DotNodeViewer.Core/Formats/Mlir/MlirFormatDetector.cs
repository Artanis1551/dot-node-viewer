using System.Buffers;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Detects MLIR intermediate representation streams—both binary MLIR Bytecode (<c>ML\xEFR</c>)
/// and textual MLIR assembly (<c>.mlir</c>)—without consuming or mutating the caller's stream position.
/// </summary>
internal sealed class MlirFormatDetector : IModelFormatDetector
{
    private const int TextProbeByteLimit = 4096;

    /// <summary>
    /// Gets a shared singleton instance of <see cref="MlirFormatDetector"/>.
    /// </summary>
    public static MlirFormatDetector Instance { get; } = new();

    /// <inheritdoc/>
    public ModelFormatInfo Format { get; } = new(ModelFormatId.Mlir, "MLIR");

    /// <inheritdoc/>
    public bool CanRead(ModelProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.StreamLength < 6 || probe.IsZipArchive)
        {
            return false;
        }

        ReadOnlySpan<byte> header = probe.Header;

        // 1. Binary MLIR Bytecode magic: "ML\xEFR" + valid PrefixVarInt version <= 16
        if (header.Length >= 5 && IsBytecodeMagic(header))
        {
            return MlirBytecodeReader.TryReadPrefixVarInt(header[4..], out ulong version, out _)
                && version <= MlirBytecodeReader.MaxSupportedBytecodeVersion;
        }

        // 2. Reject known binary signatures early
        if (HasKnownNonMlirBinarySignature(header))
        {
            return false;
        }

        // 3. Bounded textual prefix scan
        int bytesToRead = (int)Math.Min(TextProbeByteLimit, probe.StreamLength);
        if (bytesToRead < 8)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            Span<byte> span = rented.AsSpan(0, bytesToRead);
            int read = probe.ReadAt(0, span);
            if (read < 8)
            {
                return false;
            }

            return LooksLikeMlirText(span[..read]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="header"/> starts with the 4-byte MLIR bytecode magic <c>ML\xEFR</c>.
    /// </summary>
    public static bool IsBytecodeMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && header[0] == (byte)'M'
        && header[1] == (byte)'L'
        && header[2] == 0xEF
        && header[3] == (byte)'R';

    private static bool HasKnownNonMlirBinarySignature(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4
            && header[0] == 0x89
            && header[1] == (byte)'H'
            && header[2] == (byte)'D'
            && header[3] == (byte)'F')
        {
            return true;
        }

        if (header.Length >= 8)
        {
            ReadOnlySpan<byte> id = header.Slice(4, 4);
            if (id.SequenceEqual("TFL3"u8) || id.SequenceEqual("ET12"u8))
            {
                return true;
            }
        }

        if (header.Length >= 2 && header[0] == 0x80 && header[1] >= 2 && header[1] <= 5)
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeMlirText(ReadOnlySpan<byte> bytes)
    {
        // Strip UTF-8 BOM if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        // Reject NUL or non-whitespace control characters (binary payloads, Protobuf, etc.)
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == 0 || (b < 0x20 && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n'))
            {
                return false;
            }
        }

        string text = Encoding.UTF8.GetString(bytes);
        string stripped = StripLeadingCommentsAndWhitespace(text);
        if (stripped.Length == 0)
        {
            return false;
        }

        // Reject JSON, XML, and obvious Python source files
        char first = stripped[0];
        if (first is '{' or '[' or '<')
        {
            return false;
        }

        if (stripped.StartsWith("import ", StringComparison.Ordinal)
            || stripped.StartsWith("from ", StringComparison.Ordinal)
            || stripped.StartsWith("def ", StringComparison.Ordinal)
            || stripped.StartsWith("class ", StringComparison.Ordinal))
        {
            return false;
        }

        // Check strong MLIR module / function headers or dialect-qualified IR lines
        string body = SkipLeadingAliasLines(stripped);
        if (body.Length == 0)
        {
            return false;
        }

        if (StartsWithKeyword(body, "module") && body.Contains('{', StringComparison.Ordinal))
        {
            return true;
        }

        if (body.StartsWith("\"builtin.module\"", StringComparison.Ordinal) && body.Contains('{', StringComparison.Ordinal))
        {
            return true;
        }

        if (StartsWithKeyword(body, "func.func")
            || StartsWithKeyword(body, "tt.func")
            || StartsWithKeyword(body, "llvm.func")
            || StartsWithKeyword(body, "util.func")
            || StartsWithKeyword(body, "func.func private")
            || StartsWithKeyword(body, "func.func public"))
        {
            return true;
        }

        // Check for MLIR dialect operations or generic ops + MLIR type signatures in the scanned prefix
        bool hasMlirSyntax = text.Contains("->", StringComparison.Ordinal)
            || text.Contains("tensor<", StringComparison.Ordinal)
            || text.Contains("memref<", StringComparison.Ordinal)
            || text.Contains("!torch.", StringComparison.Ordinal)
            || text.Contains("!tt.", StringComparison.Ordinal)
            || text.Contains("#loc", StringComparison.Ordinal);

        if (!hasMlirSyntax)
        {
            return false;
        }

        ReadOnlySpan<string> dialectMarkers =
        [
            "func.func",
            "tt.func",
            "\"builtin.module\"",
            "\"stablehlo.",
            "stablehlo.",
            "\"vhlo.",
            "vhlo.",
            "\"mhlo.",
            "mhlo.",
            "\"torch.",
            "torch.",
            "\"tosa.",
            "tosa.",
            "\"linalg.",
            "linalg.",
            "\"arith.",
            "arith.",
            "\"tt.",
            "tt.",
            "\"iree_input.",
            "\"flow.",
            "\"hal.",
            "\"scf.",
            "scf.",
        ];

        for (int i = 0; i < dialectMarkers.Length; i++)
        {
            if (text.Contains(dialectMarkers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripLeadingCommentsAndWhitespace(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                int newline = text.IndexOf('\n', i);
                if (newline < 0)
                {
                    return string.Empty;
                }

                i = newline + 1;
                continue;
            }

            break;
        }

        return text[i..];
    }

    private static string SkipLeadingAliasLines(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            string remaining = StripLeadingCommentsAndWhitespace(text[i..]);
            if (remaining.Length == 0)
            {
                return string.Empty;
            }

            if (remaining[0] is '#' or '!')
            {
                int newline = remaining.IndexOf('\n');
                if (newline < 0)
                {
                    return remaining;
                }

                i = text.Length - remaining.Length + newline + 1;
                continue;
            }

            return remaining;
        }

        return string.Empty;
    }

    private static bool StartsWithKeyword(string text, string keyword)
    {
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Length == keyword.Length)
        {
            return true;
        }

        char next = text[keyword.Length];
        return char.IsWhiteSpace(next) || next is '@' or '{' or '(';
    }
}
