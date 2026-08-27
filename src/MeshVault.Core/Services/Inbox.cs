using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>
/// Whether a model is still sitting where it was dropped, and what is stopping
/// it being filed.
/// </summary>
/// <remarks>
/// Derived rather than stored. A model is unfiled because of where it is, and
/// where it is changes the moment it is filed — a flag would be one more thing
/// to keep in step with the truth on disk.
/// </remarks>
public static class Inbox
{
    /// <summary>What a library calls its inbox when nothing else is chosen.</summary>
    public const string DefaultPath = "inbox";

    /// <summary>Whether <paramref name="relativePath"/> sits inside the inbox.</summary>
    public static bool Holds(string? inboxPath, string relativePath)
    {
        var inbox = Normalize(inboxPath);
        if (inbox.Length == 0) return false;

        return relativePath.Equals(inbox, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(inbox + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the folder template needs from this model and has not got, phrased
    /// to be read straight into a sentence.
    /// </summary>
    /// <remarks>
    /// Only tokens the template actually uses are checked. Somebody filing by
    /// <c>{tag}/{model}</c> should not be nagged for a designer they never asked
    /// to sort by.
    ///
    /// And only tokens where the fallback would be a lie. "Unsorted" standing in
    /// for a designer means nobody knows who made it, which is worth stopping
    /// for; "Unfiled" standing in for a collection is simply true — the model is
    /// in no collection, and shelving it under that is a reasonable answer
    /// rather than a placeholder to come back and fix. Blocking on those made
    /// the same absence stop a model in the inbox while one outside it filed
    /// away quietly, which is the sort of inconsistency nobody can predict.
    /// </remarks>
    public static List<string> Missing(ModelEntry model, string folderTemplate)
    {
        var missing = new List<string>();

        foreach (var token in PathTemplate.TokenNames(folderTemplate))
        {
            switch (token.ToLowerInvariant())
            {
                case "designer" when model.DesignerId is null:
                    missing.Add("a designer");
                    break;
                case "tag" when model.Tags.Count == 0:
                    missing.Add("a tag");
                    break;
            }
        }

        return missing;
    }

    /// <summary>Trims a configured inbox path to the form paths are compared in.</summary>
    public static string Normalize(string? inboxPath) =>
        (inboxPath ?? "").Replace('\\', '/').Trim('/', ' ');
}
