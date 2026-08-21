namespace MeshVault.Core.Models;

/// <summary>
/// A persisted application preference. Distinct from configuration: these are
/// choices made in the UI that must survive a restart, rather than deployment
/// settings supplied by appsettings or environment variables.
/// </summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

public static class SettingKeys
{
    /// <summary>When true, the background preview builder stays idle.</summary>
    public const string PreviewBuildingPaused = "previews.paused";

    /// <summary>When true, anyone who can reach the site may create an account.</summary>
    public const string RegistrationOpen = "accounts.registrationOpen";
}
