namespace MeshVault.Web.Components.Shared;

public static class Formats
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {Units[unit]}";
    }

    public static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "never";
        var span = DateTimeOffset.UtcNow - when.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return when.Value.LocalDateTime.ToString("d MMM yyyy");
    }
}
