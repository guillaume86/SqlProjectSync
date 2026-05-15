using System.Text;
using Microsoft.Extensions.Logging;

namespace SqlProjectSync;

/// <summary>
/// Strips leading whitespace-only lines from every touched <c>.sql</c> file.
/// DacFx occasionally emits a blank line before the first statement (most
/// commonly before a leading comment that precedes a <c>CREATE TABLE</c>);
/// stripping it produces a quieter first-sync diff. Pure text manipulation
/// — no T-SQL parser involved, runs independently of any folding pass.
/// </summary>
internal static partial class LeadingBlankTrimmer
{
    /// <summary>
    /// Walks <paramref name="filePaths"/> and rewrites each file whose leading
    /// content is whitespace-only lines. Files without leading blanks are
    /// left untouched.
    /// </summary>
    public static void Cleanup(IEnumerable<string> filePaths, ILogger logger)
    {
        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                TrimFile(path, logger);
            }
            catch (Exception ex)
            {
                LogTrimFailed(logger, path, ex.Message);
            }
        }
    }

    internal static bool TrimFile(string filePath, ILogger logger)
    {
        var originalBytes = File.ReadAllBytes(filePath);
        if (originalBytes.Length == 0)
        {
            return false;
        }

        // Preserve a leading UTF-8 BOM byte-for-byte. File.ReadAllText would
        // strip it on read and File.WriteAllText would not put it back, so
        // a BOM-prefixed source file would lose its BOM after a no-op trim.
        var bomLength = HasUtf8Bom(originalBytes) ? 3 : 0;
        var text = Encoding.UTF8.GetString(originalBytes, bomLength, originalBytes.Length - bomLength);

        var trimmed = TrimLeadingBlankLines(text);
        if (string.Equals(trimmed, text, StringComparison.Ordinal))
        {
            return false;
        }

        var newBodyBytes = Encoding.UTF8.GetBytes(trimmed);
        if (bomLength > 0)
        {
            var withBom = new byte[3 + newBodyBytes.Length];
            originalBytes.AsSpan(0, 3).CopyTo(withBom);
            newBodyBytes.AsSpan().CopyTo(withBom.AsSpan(3));
            newBodyBytes = withBom;
        }
        File.WriteAllBytes(filePath, newBodyBytes);
        LogTrimmed(logger, filePath);
        return true;
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>
    /// Strips leading lines that contain only whitespace. Stops at the first
    /// line with any non-whitespace content. A leading BOM (U+FEFF) is in
    /// Unicode category Cf, not whitespace, so it counts as non-blank and
    /// keeps the encoding marker intact.
    /// </summary>
    internal static string TrimLeadingBlankLines(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var nextEol = text.IndexOfAny(['\r', '\n'], i);
            if (nextEol < 0)
            {
                break;
            }

            var lineIsBlank = true;
            for (var j = i; j < nextEol; j++)
            {
                if (!char.IsWhiteSpace(text[j]))
                {
                    lineIsBlank = false;
                    break;
                }
            }
            if (!lineIsBlank)
            {
                break;
            }

            if (text[nextEol] == '\r' && nextEol + 1 < text.Length && text[nextEol + 1] == '\n')
            {
                i = nextEol + 2;
            }
            else
            {
                i = nextEol + 1;
            }
        }
        // If the file is entirely whitespace, leave it alone — destroying
        // content silently is more surprising than a one-line no-op diff.
        return i == 0 || i >= text.Length ? text : text[i..];
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Trimmed leading blank lines from '{File}'.")]
    private static partial void LogTrimmed(ILogger logger, string file);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Leading-blank trim failed for '{File}': {Reason}.")]
    private static partial void LogTrimFailed(ILogger logger, string file, string reason);
}
