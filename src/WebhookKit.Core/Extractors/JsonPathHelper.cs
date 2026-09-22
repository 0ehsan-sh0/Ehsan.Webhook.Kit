// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Text.Json;

namespace WebhookKit.Core.Extractors;

internal static class JsonPathHelper
{
    public static string? ExtractValueByPath(ReadOnlyMemory<byte> rawJson, string propertyPath)
    {
        if (rawJson.IsEmpty || string.IsNullOrWhiteSpace(propertyPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var current = document.RootElement;

            string[] segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                current = next;
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
