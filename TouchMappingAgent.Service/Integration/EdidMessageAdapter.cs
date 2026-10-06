using Evolved.EdidManager;
using TouchMappingAgent.Shared.Localization;

namespace TouchMappingAgent.Service.Integration;

/// <summary>
/// Bridges the EDID module's own message type onto the pipe contract's.
///
/// The two types are identical in shape but deliberately distinct: Evolved.EdidManager depends on
/// nothing but Microsoft.Extensions.Logging.Abstractions so it can be lifted into another product,
/// and referencing TouchMappingAgent.Shared from it would end that. This adapter is the single
/// place the boundary is crossed — the whole cost of keeping the module standalone.
///
/// The keys pass through unchanged, so the module's key constants and the host's resource file
/// stay in direct correspondence; the localiser test asserts every one of them is translated.
/// </summary>
internal static class EdidMessageAdapter
{
    /// <summary>
    /// Converts a module message into the pipe contract's equivalent, preserving key and arguments.
    /// </summary>
    public static LocalizableText ToLocalizableText(this EdidMessage message) =>
        new(message.Key, message.Arguments);
}
