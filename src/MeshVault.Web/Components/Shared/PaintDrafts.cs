namespace MeshVault.Web.Components.Shared;

/// <summary>What the scheme dialog collected, before anything is written.</summary>
public record SchemeDraft(string Name, string? Notes);

/// <summary>
/// What the step dialog collected. <see cref="PaintId"/> is null when the paint
/// was typed rather than picked, which is how a recipe can name something the
/// painter does not own yet.
/// </summary>
public record StepDraft(int? PaintId, string PaintName, string? Technique, string? Area);
