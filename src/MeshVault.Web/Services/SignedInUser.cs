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
            var id = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            // Anonymous requests get an id that owns nothing, rather than the
            // legacy local id. Handing every visitor a real owner would show
            // them somebody's collections and favorites, and make all of them
            // one shared identity the moment public browsing is turned on.
            return string.IsNullOrEmpty(id) ? Users.AnonymousId : id;
        }
    }

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
}
