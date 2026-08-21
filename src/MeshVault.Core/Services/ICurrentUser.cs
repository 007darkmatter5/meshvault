using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>
/// Who owns per-user data (collections, favorites). Exists ahead of real
/// authentication so those tables can carry ownership from day one instead of
/// being migrated later.
/// </summary>
public interface ICurrentUser
{
    string UserId { get; }
}

/// <summary>Stand-in used until accounts are added: everyone is the local user.</summary>
public sealed class LocalUser : ICurrentUser
{
    public string UserId => Users.LocalUserId;
}
