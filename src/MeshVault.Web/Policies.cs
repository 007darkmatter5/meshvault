namespace MeshVault.Web;

public static class Policies
{
    /// <summary>Managing libraries, scans, imports and accounts.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Reading the catalog. Satisfied by any signed-in account, and by nobody
    /// at all unless an administrator has turned public browsing on.
    /// </summary>
    public const string View = "View";
}
