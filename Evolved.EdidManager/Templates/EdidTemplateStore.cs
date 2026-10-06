using System.Text;
using Evolved.EdidManager.Edid;
using Microsoft.Extensions.Logging;

namespace Evolved.EdidManager.Templates;

/// <summary>
/// Manages reusable EDID templates on disk.
/// </summary>
public interface IEdidTemplateStore
{
    /// <summary>Directory the templates live in.</summary>
    string TemplateDirectory { get; }

    /// <summary>Lists every template that parses as a valid EDID.</summary>
    IReadOnlyList<EdidTemplate> ListTemplates();

    /// <summary>Loads one template by file name, or null when missing or invalid.</summary>
    EdidTemplate? LoadTemplate(string fileName);

    /// <summary>Reads an EDID from an arbitrary .bin or .hex file chosen by the operator.</summary>
    EdidTemplate? LoadFromFile(string path);

    /// <summary>Writes a template into the store. Returns the resulting path, or null on failure.</summary>
    string? SaveTemplate(string fileName, byte[] edid);

    /// <summary>
    /// Reads every EDID declared by a Windows monitor driver (.inf).
    /// </summary>
    IReadOnlyList<InfEdidEntry> LoadFromInf(string path);

    /// <summary>
    /// Recomputes block 0's checksum on a candidate template and returns the corrected bytes.
    ///
    /// Deliberately a SEPARATE, explicit call rather than something <see cref="LoadFromFile"/>
    /// does automatically. A wrong checksum usually means the block was hand-edited or came
    /// from a generator that got it wrong, and in that case the payload itself is suspect —
    /// silently "fixing" it would install whatever else is wrong in there with a checksum that
    /// now says it is fine. The operator has to ask for the repair.
    /// </summary>
    /// <returns>The repaired bytes, or null when the data is not an EDID at all.</returns>
    byte[]? RepairChecksum(byte[] candidate);

    /// <summary>Creates the directory and seeds the built-in templates if they are absent.</summary>
    void EnsureSeeded();
}

/// <summary>One EDID template: its origin and the parsed block.</summary>
/// <param name="Name">File name, e.g. "DM7000_Native.bin".</param>
/// <param name="Path">Full path on disk, or null for a built-in template not yet written.</param>
/// <param name="Edid">The parsed, checksum-valid EDID.</param>
/// <param name="IsBuiltIn">True for templates shipped with the agent.</param>
public sealed record EdidTemplate(string Name, string? Path, EdidBlock Edid, bool IsBuiltIn)
{
    /// <summary>Operator-facing one-line description.</summary>
    public string Description =>
        $"{Name} — {Edid.ManufacturerCode} {Edid.ProductCode:X4} " +
        $"{(Edid.MonitorName is { Length: > 0 } n ? n : "ohne Namen")}, " +
        $"{Edid.BlockCount} Block/Blöcke";
}

/// <summary>
/// File-backed template store under %ProgramData%\Evolved\EdidTemplates\.
///
/// Every template is validated on load — header, whole blocks, and a correct checksum per
/// block. That check is the point of the class, not a nicety: an EDID whose block does not sum
/// to zero mod 256 is rejected by the graphics driver, and the usual symptom is that the port
/// goes dark rather than falling back to the hardware EDID. A template file is exactly the
/// place a bad block gets in, because it is hand-edited, copied between machines, or produced
/// by a tool nobody verified.
///
/// This is not hypothetical. The block originally proposed as "DM7000_Native.bin" for this
/// feature carried checksum 0x30 where 0xE0 was required, and identified as manufacturer CSR
/// product 0 rather than the CHR 0x8910 the panels actually report. Injecting it would have
/// darkened the port and changed the monitors' PnP hardware id, breaking every stored touch
/// mapping. The built-in template below is the EDID read from the real hardware instead.
/// </summary>
public sealed class EdidTemplateStore : IEdidTemplateStore
{
    /// <summary>File name of the built-in DM7000 template.</summary>
    public const string Dm7000TemplateName = "DM7000_Native.bin";

    /// <summary>
    /// The DM7000's real EDID, captured verbatim from
    /// HKLM\...\Device Parameters\EDID on the target installation and reported as
    /// "edidRawHex" by the hardware survey. Manufacturer CHR (0x0D12), product 0x8910,
    /// serial 880, preferred timing 1920x1080 @ 138.5 MHz, 28x16 cm.
    ///
    /// Kept as a constant rather than a shipped .bin so the value is reviewable in a diff and
    /// cannot silently diverge from the file on disk.
    /// </summary>
    public const string Dm7000NativeHex =
        "00FFFFFFFFFFFF000D12108970030000011B0104801C1078EE8042AC5130B4251050530000" +
        "00010101010101010101010101010101011A3680A070381F403020350007442100001A0000" +
        "00000000000000000000000000000000000000FD00384C1E5311000A202020202020000000" +
        "FC00444D373030300A20202020202000B0";

    private readonly ILogger<EdidTemplateStore> _logger;

    /// <summary>Initializes a store rooted at the default ProgramData location.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public EdidTemplateStore(ILogger<EdidTemplateStore> logger)
        : this(logger, System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Evolved", "EdidTemplates"))
    {
    }

    /// <summary>
    /// Testing seam: lets a test point the store at a temporary directory.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="templateDirectory">Directory holding the templates.</param>
    public EdidTemplateStore(ILogger<EdidTemplateStore> logger, string templateDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TemplateDirectory = templateDirectory ?? throw new ArgumentNullException(nameof(templateDirectory));
    }

    /// <inheritdoc/>
    public string TemplateDirectory { get; }

    /// <summary>The built-in DM7000 template, always available even with no files on disk.</summary>
    public static EdidTemplate BuiltInDm7000 => new(
        Dm7000TemplateName,
        Path: null,
        Edid: EdidBlock.FromBytes(Convert.FromHexString(Dm7000NativeHex)),
        IsBuiltIn: true);

    /// <inheritdoc/>
    public void EnsureSeeded()
    {
        try
        {
            Directory.CreateDirectory(TemplateDirectory);

            var target = System.IO.Path.Combine(TemplateDirectory, Dm7000TemplateName);
            if (File.Exists(target))
                return;

            File.WriteAllBytes(target, Convert.FromHexString(Dm7000NativeHex));
            _logger.LogInformation("Seeded built-in EDID template {TemplateName} in {Directory}",
                Dm7000TemplateName, TemplateDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The built-in template is still served from memory, so this is not fatal.
            _logger.LogWarning(ex,
                "Could not seed EDID templates in {Directory}; the built-in template remains " +
                "available in memory", TemplateDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not seed EDID templates in {Directory}", TemplateDirectory);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<EdidTemplate> ListTemplates()
    {
        var templates = new List<EdidTemplate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(TemplateDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(TemplateDirectory)
                             .Where(IsTemplateFile)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var template = LoadFromFile(path);
                    if (template == null)
                        continue;

                    templates.Add(template);
                    seen.Add(template.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate EDID templates in {Directory}", TemplateDirectory);
        }

        // Always offer the built-in template, even when the directory is missing or the file
        // was deleted — an operator troubleshooting a dead extender port needs it most exactly
        // when the machine is in a bad state.
        if (!seen.Contains(Dm7000TemplateName))
            templates.Insert(0, BuiltInDm7000);

        return templates;
    }

    /// <inheritdoc/>
    public EdidTemplate? LoadTemplate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        // Guard the path this becomes: a template name is operator input and must not be able
        // to reach outside the template directory.
        if (fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains("..") ||
            System.IO.Path.IsPathRooted(fileName))
        {
            _logger.LogWarning("Rejected template name {TemplateName}", fileName);
            return null;
        }

        var path = System.IO.Path.Combine(TemplateDirectory, fileName);
        var fromDisk = File.Exists(path) ? LoadFromFile(path) : null;
        if (fromDisk != null)
            return fromDisk;

        return string.Equals(fileName, Dm7000TemplateName, StringComparison.OrdinalIgnoreCase)
            ? BuiltInDm7000
            : null;
    }

    /// <inheritdoc/>
    public EdidTemplate? LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("EDID template {Path} does not exist", path);
                return null;
            }

            var raw = File.ReadAllBytes(path);
            var bytes = IsHexFile(path) ? TryDecodeHex(raw, path) : raw;

            if (bytes == null)
                return null;

            if (!TryValidate(bytes, path, out var block) || block == null)
                return null;

            return new EdidTemplate(System.IO.Path.GetFileName(path), path, block, IsBuiltIn: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read EDID template {Path}", path);
            return null;
        }
    }

    /// <inheritdoc/>
    public string? SaveTemplate(string fileName, byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);

        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains("..") ||
            System.IO.Path.IsPathRooted(fileName))
        {
            _logger.LogWarning("Rejected template name {TemplateName}", fileName);
            return null;
        }

        if (!TryValidate(edid, fileName, out _))
            return null;

        try
        {
            Directory.CreateDirectory(TemplateDirectory);
            var path = System.IO.Path.Combine(TemplateDirectory, fileName);
            File.WriteAllBytes(path, edid);

            _logger.LogInformation("Saved EDID template {TemplateName} ({Length} bytes)", fileName, edid.Length);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save EDID template {TemplateName}", fileName);
            return null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<InfEdidEntry> LoadFromInf(string path)
    {
        var entries = InfEdidParser.ParseFile(path, _logger);

        foreach (var entry in entries.Where(e => !e.IsChecksumValid))
        {
            _logger.LogWarning(
                "INF section [{Section}] in {Path} declares an EDID with an invalid checksum; " +
                "it can be repaired explicitly before use",
                entry.SectionName, path);
        }

        _logger.LogInformation(
            "Read {Count} EDID block set(s) from the INF {Path}", entries.Count, path);

        return entries;
    }

    /// <inheritdoc/>
    public byte[]? RepairChecksum(byte[] candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Length == 0 || candidate.Length % EdidBlock.BlockSize != 0)
        {
            _logger.LogWarning(
                "Cannot repair a candidate of {Length} bytes; must be a multiple of {BlockSize}",
                candidate.Length, EdidBlock.BlockSize);
            return null;
        }

        if (!EdidBlock.TryFromBytes(candidate, out _))
        {
            _logger.LogWarning("Cannot repair a candidate that does not start with the EDID header");
            return null;
        }

        var repaired = (byte[])candidate.Clone();
        int blockCount = repaired.Length / EdidBlock.BlockSize;

        for (int block = 0; block < blockCount; block++)
        {
            int offset = block * EdidBlock.BlockSize;
            byte stored = repaired[offset + EdidBlock.ChecksumOffset];
            byte expected = EdidBlock.ComputeChecksum(repaired, offset);

            if (stored == expected)
                continue;

            repaired[offset + EdidBlock.ChecksumOffset] = expected;
            _logger.LogWarning(
                "Repaired checksum of block {Block}: 0x{Stored:X2} -> 0x{Expected:X2}. " +
                "The payload itself was NOT verified — a wrong checksum usually means the block " +
                "was hand-edited, so check the identity fields before installing it.",
                block, stored, expected);
        }

        return repaired;
    }

    /// <summary>
    /// Validates a candidate EDID: header, whole 128-byte blocks, and a correct checksum on
    /// every block. Logs precisely why a template was rejected — "invalid" alone leaves an
    /// operator with a file and no idea what to fix.
    /// </summary>
    internal bool TryValidate(byte[] bytes, string origin, out EdidBlock? block)
    {
        block = null;

        if (bytes.Length == 0 || bytes.Length % EdidBlock.BlockSize != 0)
        {
            _logger.LogWarning(
                "EDID template {Origin} is {Length} bytes; must be a non-zero multiple of {BlockSize}",
                origin, bytes.Length, EdidBlock.BlockSize);
            return false;
        }

        if (!EdidBlock.TryFromBytes(bytes, out var parsed) || parsed == null)
        {
            _logger.LogWarning("EDID template {Origin} does not start with the EDID header", origin);
            return false;
        }

        if (!parsed.IsChecksumValid)
        {
            // Report the expected value: it is what the operator needs to correct the file.
            byte stored = bytes[EdidBlock.ChecksumOffset];
            byte expected = EdidBlock.ComputeChecksum(bytes);

            _logger.LogWarning(
                "EDID template {Origin} has an invalid checksum (block 0 stores 0x{Stored:X2}, " +
                "expected 0x{Expected:X2}). Refusing it: a graphics driver rejects such an EDID " +
                "outright, which usually leaves the port dark.",
                origin, stored, expected);
            return false;
        }

        block = parsed;
        return true;
    }

    /// <summary>File extensions the store accepts, for a file-picker filter.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new[] { ".bin", ".hex", ".dat", ".edid", ".inf" };

    private static bool IsTemplateFile(string path)
    {
        var extension = System.IO.Path.GetExtension(path);

        // .inf is handled by LoadFromInf, not by the flat loader — an INF can declare several
        // EDIDs and the caller has to choose which section to take.
        return extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".hex", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".edid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the path names a Windows monitor driver rather than a raw block.</summary>
    public static bool IsInfFile(string path) =>
        System.IO.Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase);

    private static bool IsHexFile(string path) =>
        System.IO.Path.GetExtension(path).Equals(".hex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a .hex template. Tolerates the formats such files actually arrive in: whitespace,
    /// line breaks, "0x" prefixes and comma separators.
    /// </summary>
    private byte[]? TryDecodeHex(byte[] raw, string path)
    {
        try
        {
            // Decode through a StreamReader with BOM detection rather than
            // Encoding.UTF8.GetString: a .hex file saved from Notepad carries a UTF-8 BOM, and
            // "Save as Unicode" produces UTF-16. Decoding those as raw UTF-8 leaves a U+FEFF
            // (or interleaved NUL bytes) that the scanner below would reject as non-hex — the
            // file looks fine to the operator and the loader refuses it with no obvious cause.
            using var stream = new MemoryStream(raw);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();

            var cleaned = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c) || c is ',' or ';' or ':')
                    continue;

                // Skip an "0x"/"0X" prefix rather than treating the 0 and x as digits.
                if (c == '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
                {
                    i++;
                    continue;
                }

                if (Uri.IsHexDigit(c))
                {
                    cleaned.Append(c);
                    continue;
                }

                _logger.LogWarning(
                    "EDID template {Path} contains the non-hex character '{Character}'", path, c);
                return null;
            }

            if (cleaned.Length % 2 != 0)
            {
                _logger.LogWarning(
                    "EDID template {Path} has an odd number of hex digits ({Count})", path, cleaned.Length);
                return null;
            }

            return Convert.FromHexString(cleaned.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode the hex EDID template {Path}", path);
            return null;
        }
    }
}
