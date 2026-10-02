namespace Showcase.Server;

/// <summary>Input rules for chat messages, shared by the WebSocket and the HTTP path.</summary>
internal static class Validation
{
    public const int MaxNameLength = 24;
    public const int MaxTextLength = 280;
    private const string Anonymous = "anonymous";

    public static bool TryChat(string? name, string? text, out string cleanName, out string cleanText)
    {
        cleanName = string.IsNullOrWhiteSpace(name) ? Anonymous : Trim(name, MaxNameLength);
        cleanText = string.IsNullOrWhiteSpace(text) ? string.Empty : Trim(text, MaxTextLength);
        return cleanText.Length > 0;
    }

    private static string Trim(string value, int maxLength)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
