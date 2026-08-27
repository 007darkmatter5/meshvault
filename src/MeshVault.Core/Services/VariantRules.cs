using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>
/// Holds the variant vocabulary currently in force.
///
/// The definitions are rows the user curates, but they are consulted for every
/// indexed file. Reading them from the database each time would put a query in
/// the middle of a tight loop, and rebuilding the classifier per file would
/// re-index the terms. One instance is built when they change and shared until
/// they change again.
/// </summary>
public class VariantRules
{
    private volatile VariantClassifier _current = new();

    public VariantClassifier Current => _current;

    /// <summary>Swaps in a vocabulary. Null falls back to the starter set.</summary>
    public VariantClassifier Set(IEnumerable<VariantDefinition>? definitions) =>
        _current = new VariantClassifier(definitions);
}
