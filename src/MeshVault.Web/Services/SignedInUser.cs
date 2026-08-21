using System.Security.Claims;
using MeshVault.Core.Models;
using MeshVault.Core.Services;

namespace MeshVault.Web.Services;

/// <summary>
/// Resolves the owner of per-user data from the signed-in principal.
/// </summary>
/// <remarks>
/// Collections and favorites have carried an owner id since before there were
/// accounts, using <see cref="Users.LocalUserId"/> as a stand-in. That stand-in
/// is remapped to the first real account on upgrade, so nothing is orphaned.
/// </remarks>
public class SignedInUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            // Anonymous requests get the legacy id rather than throwing. Pages
            // that matter are behind [Authorize]; media endpoints are not, and
            // they must still be able to serve a thumbnail.
            return string.IsNullOrEmpty(id) ? Users.LocalUserId : id;
        }
    }
}
