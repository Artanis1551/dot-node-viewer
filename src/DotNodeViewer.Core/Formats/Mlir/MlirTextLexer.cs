using System.Buffers;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Specifies the lexical token kinds produced by <see cref="MlirTextLexer"/>.
/// </summary>
internal enum MlirTokenKind
{
    Eof,
    Identifier,
    SsaValue,
    SymbolRef,
    AttrAlias,
    TypeAlias,
    BlockLabel,
    StringLiteral,
    Number,
    Arrow,          // ->
    Colon,          // :
    Comma,          // ,
    Equals,         // =
    LParen,         // (
    RParen,         // )
    LBrace,         // {
    RBrace,         // }
    LBracket,       // [
    RBracket,       // ]
    LAngle,         // <
    RAngle,         // >
    Question,       // ?
    Star,           // *
    ResourceBlock,  // {-# ... #-}
}

/// <summary>
/// Represents a single lexical token in a textual MLIR stream.
/// </summary>
internal readonly record struct MlirToken(
    MlirTokenKind Kind,
    int StartChar,
    int Length,
    int Line,
    long ByteOffset);

/// <summary>
/// Bounded, streaming UTF-8 lexer for textual MLIR (<c>.mlir</c>) files with hard token and metadata limits.
/// </summary>
internal sealed class MlirTextLexer
{
    public const int MaxTokens = 2_000_000;
    public const int MaxStringChars = 1_048_576;

    private readonly string _text;
    private readonly List<MlirToken> _tokens = [];

    private MlirTextLexer(string text)
    {
        _text = text;
        Tokenize();
    }

    public string Text => _text;

    public IReadOnlyList<MlirToken> Tokens => _tokens;

    public static MlirTextLexer Create(RandomAccessSource source, ModelReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Length > options.MaxMetadataBytes)
        {
            throw new ModelParseException(
                $"Textual MLIR file size ({source.Length} bytes) exceeds MaxMetadataBytes limit ({options.MaxMetadataBytes} bytes).",
                fileOffset: 0,
                context: options.FileName);
        }

        if (source.Length == 0)
        {
            return new MlirTextLexer(string.Empty);
        }

        int byteCount = (int)source.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, byteCount);
            source.ReadExactly(0, buffer);

            // Strip UTF-8 BOM if present
            if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                buffer = buffer[3..];
            }

            string text = Encoding.UTF8.GetString(buffer);
            return new MlirTextLexer(text);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public ReadOnlySpan<char> GetSpan(MlirToken token) =>
        _text.AsSpan(token.StartChar, token.Length);

    public string GetText(MlirToken token) =>
        token.Length == 0 ? string.Empty : _text.Substring(token.StartChar, token.Length);

    public string GetUnescapedString(MlirToken token)
    {
        if (token.Kind != MlirTokenKind.StringLiteral || token.Length < 2)
        {
            return GetText(token);
        }

        ReadOnlySpan<char> inner = _text.AsSpan(token.StartChar + 1, token.Length - 2);
        if (!inner.Contains('\\'))
        {
            return inner.ToString();
        }

        StringBuilder sb = new(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                char next = inner[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => next,
                });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public string SliceChars(int startChar, int endChar)
    {
        int clampedStart = Math.Clamp(startChar, 0, _text.Length);
        int clampedEnd = Math.Clamp(endChar, clampedStart, _text.Length);
        return _text[clampedStart..clampedEnd];
    }

    private void Tokenize()
    {
        int i = 0;
        int len = _text.Length;
        int line = 1;

        while (i < len)
        {
            if (_tokens.Count >= MaxTokens)
            {
                throw new ModelParseException(
                    $"MLIR textual token count exceeded maximum limit ({MaxTokens}).",
                    fileOffset: i);
            }

            char c = _text[i];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n')
                {
                    line++;
                }

                i++;
                continue;
            }

            // Line comments // ...
            if (c == '/' && i + 1 < len && _text[i + 1] == '/')
            {
                i += 2;
                while (i < len && _text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Dialect resource metadata block: {-# ... #-}
            if (c == '{' && i + 2 < len && _text[i + 1] == '-' && _text[i + 2] == '#')
            {
                int start = i;
                int startLine = line;
                i += 3;
                bool closed = false;
                while (i < len)
                {
                    if (_text[i] == '\n')
                    {
                        line++;
                    }
                    else if (_text[i] == '#' && i + 2 < len && _text[i + 1] == '-' && _text[i + 2] == '}')
                    {
                        i += 3;
                        closed = true;
                        break;
                    }

                    i++;
                }

                if (!closed)
                {
                    throw new ModelParseException("Unterminated MLIR file-level metadata block '{-#'.", fileOffset: start);
                }

                _tokens.Add(new MlirToken(MlirTokenKind.ResourceBlock, start, i - start, startLine, start));
                continue;
            }

            // Arrow ->
            if (c == '-' && i + 1 < len && _text[i + 1] == '>')
            {
                _tokens.Add(new MlirToken(MlirTokenKind.Arrow, i, 2, line, i));
                i += 2;
                continue;
            }

            // Single-char punctuation
            MlirTokenKind? singleKind = c switch
            {
                ':' => MlirTokenKind.Colon,
                ',' => MlirTokenKind.Comma,
                '=' => MlirTokenKind.Equals,
                '(' => MlirTokenKind.LParen,
                ')' => MlirTokenKind.RParen,
                '{' => MlirTokenKind.LBrace,
                '}' => MlirTokenKind.RBrace,
                '[' => MlirTokenKind.LBracket,
                ']' => MlirTokenKind.RBracket,
                '<' => MlirTokenKind.LAngle,
                '>' => MlirTokenKind.RAngle,
                '?' => MlirTokenKind.Question,
                '*' => MlirTokenKind.Star,
                _ => null,
            };

            if (singleKind is MlirTokenKind kind)
            {
                _tokens.Add(new MlirToken(kind, i, 1, line, i));
                i++;
                continue;
            }

            // String literal "..."
            if (c == '"')
            {
                int start = i;
                int startLine = line;
                i++;
                bool closed = false;
                while (i < len)
                {
                    char sc = _text[i];
                    if (sc == '\\' && i + 1 < len)
                    {
                        if (_text[i + 1] == '\n')
                        {
                            line++;
                        }

                        i += 2;
                        continue;
                    }

                    if (sc == '"')
                    {
                        i++;
                        closed = true;
                        break;
                    }

                    if (sc == '\n')
                    {
                        line++;
                    }

                    if (i - start > MaxStringChars)
                    {
                        throw new ModelParseException($"MLIR string literal exceeds maximum length ({MaxStringChars} chars).", fileOffset: start);
                    }

                    i++;
                }

                if (!closed)
                {
                    throw new ModelParseException("Unterminated MLIR string literal.", fileOffset: start);
                }

                _tokens.Add(new MlirToken(MlirTokenKind.StringLiteral, start, i - start, startLine, start));
                continue;
            }

            // Sigil identifiers: %ssa, @sym, #attr, !type, ^bb
            if (c is '%' or '@' or '#' or '!' or '^')
            {
                int start = i;
                int startLine = line;
                i++;
                if (c == '@' && i < len && _text[i] == '"')
                {
                    i++;
                    while (i < len && _text[i] != '"')
                    {
                        if (_text[i] == '\\' && i + 1 < len)
                        {
                            i += 2;
                        }
                        else
                        {
                            i++;
                        }
                    }

                    if (i < len && _text[i] == '"')
                    {
                        i++;
                    }
                }
                else
                {
                    while (i < len && IsSigilIdentifierPart(_text[i]))
                    {
                        i++;
                    }
                }

                MlirTokenKind sigilKind = c switch
                {
                    '%' => MlirTokenKind.SsaValue,
                    '@' => MlirTokenKind.SymbolRef,
                    '#' => MlirTokenKind.AttrAlias,
                    '!' => MlirTokenKind.TypeAlias,
                    _ => MlirTokenKind.BlockLabel,
                };

                _tokens.Add(new MlirToken(sigilKind, start, i - start, startLine, start));
                continue;
            }

            // Numbers (including negative sign followed by digit)
            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < len && char.IsAsciiDigit(_text[i + 1])))
            {
                int start = i;
                if (c == '-')
                {
                    i++;
                }

                if (i + 1 < len && _text[i] == '0' && (_text[i + 1] == 'x' || _text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < len && char.IsAsciiHexDigit(_text[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    while (i < len && char.IsAsciiDigit(_text[i]))
                    {
                        i++;
                    }

                    if (i < len && _text[i] == '.' && i + 1 < len && char.IsAsciiDigit(_text[i + 1]))
                    {
                        i++;
                        while (i < len && char.IsAsciiDigit(_text[i]))
                        {
                            i++;
                        }
                    }

                    if (i < len && (_text[i] == 'e' || _text[i] == 'E'))
                    {
                        int expPos = i + 1;
                        if (expPos < len && (_text[expPos] == '+' || _text[expPos] == '-'))
                        {
                            expPos++;
                        }

                        if (expPos < len && char.IsAsciiDigit(_text[expPos]))
                        {
                            i = expPos;
                            while (i < len && char.IsAsciiDigit(_text[i]))
                            {
                                i++;
                            }
                        }
                    }
                }

                _tokens.Add(new MlirToken(MlirTokenKind.Number, start, i - start, line, start));
                continue;
            }

            // Bare identifier: [A-Za-z_][A-Za-z0-9_$.]*
            if (IsBareIdentifierStart(c))
            {
                int start = i;
                i++;
                while (i < len && IsBareIdentifierPart(_text[i]))
                {
                    i++;
                }

                _tokens.Add(new MlirToken(MlirTokenKind.Identifier, start, i - start, line, start));
                continue;
            }

            // Other single punctuation characters (like ; + /) -> skip or advance
            i++;
        }

        _tokens.Add(new MlirToken(MlirTokenKind.Eof, len, 0, line, len));
    }

    private static bool IsBareIdentifierStart(char c) =>
        char.IsAsciiLetter(c) || c == '_';

    private static bool IsBareIdentifierPart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$';

    private static bool IsSigilIdentifierPart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$' or '-';
}
