using System.ComponentModel.DataAnnotations;

namespace MeshVault.Web.Components.Account;

/// <summary>
/// Validates an email address only when one was actually given.
/// </summary>
/// <remarks>
/// <see cref="EmailAddressAttribute"/> skips <c>null</c> but rejects an empty
/// string, and static SSR form mapping binds an untouched text box to <c>""</c>
/// rather than <c>null</c>. Applied to a field labelled "optional", it makes
/// that field mandatory in practice: leaving it blank fails registration with
/// "that does not look like an email address".
/// </remarks>
public sealed class OptionalEmailAddressAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Inner = new();

    public override bool IsValid(object? value) =>
        value is not string given || string.IsNullOrWhiteSpace(given) || Inner.IsValid(given);
}
