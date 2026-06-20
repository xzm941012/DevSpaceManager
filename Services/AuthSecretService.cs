using System.Text.Json;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class AuthSecretService
{
    public string ReadOwnerPassword()
    {
        if (!File.Exists(AppPaths.DevSpaceAuthPath)) return "";

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(AppPaths.DevSpaceAuthPath));
            return FindString(json.RootElement, "ownerToken") ??
                   FindString(json.RootElement, "ownerPassword") ??
                   FindString(json.RootElement, "password") ??
                   FindString(json.RootElement, "owner_password") ??
                   "";
        }
        catch
        {
            return "";
        }
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindString(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }

        return null;
    }
}
