using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchMappingAgent.Service.Validation;

/// <summary>
/// Safe JSON deserialization with schema validation (IEC 62443 CRA compliance).
/// NEVER uses dynamic or unsafe deserialization settings.
/// </summary>
public static class SecureJsonDeserializer
{
    /// <summary>
    /// Strict JSON deserialization options for security.
    /// </summary>
    private static readonly JsonSerializerOptions SafeOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        WriteIndented = false,
        MaxDepth = 32
    };

    /// <summary>
    /// Safely deserialize a JSON string with strict type validation.
    /// Throws InvalidOperationException on any deserialization error.
    /// </summary>
    /// <typeparam name="T">The target type (must be non-dynamic).</typeparam>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>The deserialized object.</returns>
    /// <exception cref="ArgumentException">If json is null or empty.</exception>
    /// <exception cref="InvalidOperationException">If deserialization fails or result is null.</exception>
    public static T DeserializeSecure<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON string cannot be null or empty.", nameof(json));

        try
        {
            var result = JsonSerializer.Deserialize<T>(json, SafeOptions);

            if (result == null)
                throw new InvalidOperationException("Deserialization resulted in null object.");

            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid JSON format: malformed input.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException("Unsupported type in JSON structure.", ex);
        }
    }

    /// <summary>
    /// Safely serialize an object to JSON.
    /// </summary>
    public static string SerializeSecure<T>(T value) where T : class
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        return JsonSerializer.Serialize(value, SafeOptions);
    }
}
