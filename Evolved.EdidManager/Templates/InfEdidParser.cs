using System.Globalization;
using System.Text;
using Evolved.EdidManager.Edid;
using Microsoft.Extensions.Logging;

namespace Evolved.EdidManager.Templates;

/// <summary>
/// Extracts EDID blocks from a Windows monitor driver (.inf).
///
/// A monitor INF installs an override with AddReg lines of the form
///
///   HKR, EDID_OVERRIDE, "0", 1, 00,FF,FF,FF,FF,FF,FF,00, ...
///
/// where HKR is the device's Device Parameters key, "0" is the block index, 1 is
/// FLG_ADDREG_BINVALUETYPE, and the remainder is the block as comma-separated hex bytes.
/// That is exactly the layout <see cref="Registry.EdidRegistryStore"/> writes, which is why an
/// INF is a first-class template source rather than a curiosity: vendors ship the corrected
/// EDID for a panel this way, and behind an extender that is often the only correct block
/// available.
///
/// Pure text processing — no registry, no INF API — so every parsing rule is directly testable.
/// </summary>
public static class InfEdidParser
{
    /// <summary>Registry key name an INF uses for the override.</summary>
    public const string OverrideKeyName = "EDID_OVERRIDE";

    /// <summary>
    /// Parses every EDID found in the INF text, keyed by the section it came from.
    /// Blocks belonging to one section are concatenated in index order, so a panel with a CEA
    /// extension comes back as a single 256-byte EDID rather than two fragments.
    /// </summary>
    /// <param name="infText">Complete contents of the .inf file.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public static IReadOnlyList<InfEdidEntry> Parse(string infText, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(infText))
            return Array.Empty<InfEdidEntry>();

        // section -> block index -> bytes
        var sections = new Dictionary<string, SortedDictionary<int, byte[]>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = "(root)";

        foreach (var rawLine in SplitContinuedLines(infText))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (!TryParseAddRegLine(line, out int blockIndex, out byte[]? block))
                continue;

            if (!sections.TryGetValue(currentSection, out var blocks))
            {
                blocks = new SortedDictionary<int, byte[]>();
                sections[currentSection] = blocks;
            }

            if (blocks.ContainsKey(blockIndex))
            {
                logger?.LogWarning(
                    "INF section [{Section}] declares block {BlockIndex} more than once; keeping the first",
                    currentSection, blockIndex);
                continue;
            }

            blocks[blockIndex] = block!;
        }

        var results = new List<InfEdidEntry>();

        foreach (var (section, blocks) in sections)
        {
            // Block indices must be contiguous from 0. A gap means the file is truncated or
            // hand-edited, and concatenating across it would silently produce a wrong EDID.
            if (blocks.Keys.First() != 0 || blocks.Keys.Select((k, i) => k == i).Any(ok => !ok))
            {
                logger?.LogWarning(
                    "INF section [{Section}] has non-contiguous EDID block indices ({Indices}); skipping",
                    section, string.Join(",", blocks.Keys));
                continue;
            }

            var combined = blocks.Values.SelectMany(b => b).ToArray();

            if (!EdidBlock.TryFromBytes(combined, out var edid) || edid == null)
            {
                logger?.LogWarning(
                    "INF section [{Section}] yields {Length} bytes that are not a valid EDID",
                    section, combined.Length);
                continue;
            }

            results.Add(new InfEdidEntry(section, edid, edid.IsChecksumValid));
        }

        return results;
    }

    /// <summary>
    /// Reads an INF from disk and returns the EDIDs it declares.
    /// </summary>
    public static IReadOnlyList<InfEdidEntry> ParseFile(string path, ILogger? logger = null)
    {
        try
        {
            // INF files are commonly ANSI or UTF-16; BOM detection covers the latter and the
            // hex payload is ASCII either way.
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd(), logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not read the INF {Path}", path);
            return Array.Empty<InfEdidEntry>();
        }
    }

    /// <summary>
    /// Recognises an AddReg line that installs an EDID block and extracts the index and bytes.
    ///
    /// Accepts the shapes real INFs use:
    ///   HKR, EDID_OVERRIDE, "0", 1, 00,FF,...
    ///   HKR,"EDID_OVERRIDE","0",1,00,FF,...
    ///   HKR,EDID_OVERRIDE\0,,1,00,FF,...      (index folded into the subkey path)
    /// </summary>
    internal static bool TryParseAddRegLine(string line, out int blockIndex, out byte[]? block)
    {
        blockIndex = -1;
        block = null;

        if (!line.StartsWith("HKR", StringComparison.OrdinalIgnoreCase))
            return false;

        var fields = SplitFields(line);
        if (fields.Count < 5)
            return false;

        var subkey = Unquote(fields[1]);
        if (subkey.IndexOf(OverrideKeyName, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var valueName = Unquote(fields[2]);

        // The block index is either the value name, or the trailing segment of the subkey when
        // the value name is empty.
        if (!int.TryParse(valueName, NumberStyles.Integer, CultureInfo.InvariantCulture, out blockIndex))
        {
            var lastSegment = subkey.Split('\\').Last();
            if (!int.TryParse(lastSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out blockIndex))
                return false;
        }

        if (blockIndex < 0)
            return false;

        // fields[3] is the flag; 1 (or 0x00000001) means binary. Anything else is a string or
        // DWORD value and is not an EDID block.
        var flag = Unquote(fields[3]).Trim();
        if (!IsBinaryFlag(flag))
            return false;

        var bytes = new List<byte>(EdidBlock.BlockSize);
        for (int i = 4; i < fields.Count; i++)
        {
            var token = Unquote(fields[i]).Trim();
            if (token.Length == 0)
                continue;

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                token = token[2..];

            if (token.Length is 0 or > 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                return false;

            bytes.Add(value);
        }

        if (bytes.Count != EdidBlock.BlockSize)
            return false;

        block = bytes.ToArray();
        return true;
    }

    private static bool IsBinaryFlag(string flag)
    {
        if (flag.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(flag[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
                   && (hex & 0x1) == 0x1;
        }

        return int.TryParse(flag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec)
               && (dec & 0x1) == 0x1;
    }

    /// <summary>
    /// Joins lines that a backslash continues. An EDID AddReg is 128 bytes of hex and is almost
    /// always wrapped across a dozen lines, so treating each physical line separately would find
    /// nothing at all.
    /// </summary>
    internal static IEnumerable<string> SplitContinuedLines(string text)
    {
        var current = new StringBuilder();

        foreach (var physical in text.Split('\n'))
        {
            var line = physical.TrimEnd('\r');
            var trimmed = line.TrimEnd();

            if (trimmed.EndsWith('\\'))
            {
                current.Append(trimmed[..^1]);
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>
    /// Removes a trailing ';' comment, respecting quotes so a semicolon inside a quoted value
    /// is not mistaken for one.
    /// </summary>
    internal static string StripComment(string line)
    {
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes)
                return line[..i];
        }

        return line;
    }

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"')
            ? trimmed[1..^1]
            : trimmed;
    }
}

/// <summary>One EDID declared by an INF.</summary>
/// <param name="SectionName">INF section the block came from, e.g. "DM7000.AddReg".</param>
/// <param name="Edid">The parsed EDID.</param>
/// <param name="IsChecksumValid">
/// False when the INF itself carries a bad checksum. Reported rather than silently corrected —
/// the caller decides whether to repair it.
/// </param>
public sealed record InfEdidEntry(string SectionName, EdidBlock Edid, bool IsChecksumValid);
