namespace MeshVault.Web;

public class MeshVaultOptions
{
    public const string SectionName = "MeshVault";

    /// <summary>Where the SQLite database, thumbnail cache and logs live.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>Library roots declared in configuration, seeded on first run.</summary>
    public List<LibraryOptions> Libraries { get; set; } = [];

    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// Skip the startup scan when a library was scanned this recently. A full
    /// walk of a library on a slow share costs minutes, so rescanning on every
    /// restart is pure waste. Set to 0 to always rescan.
    /// </summary>
    public double RescanIntervalHours { get; set; } = 12;
}

public class LibraryOptions
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool AllowOrganize { get; set; }
}
