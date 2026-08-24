using MeshVault.Core.Models;
using MeshVault.Data;
using Microsoft.AspNetCore.Authorization;

namespace MeshVault.Web.Services;

/// <summary>Permission to look at the catalog, as opposed to change it.</summary>
public class ViewRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets a signed-out visitor read the catalog when an administrator has opened
/// the instance up.
/// </summary>
/// <remarks>
/// Read means read. This grants the browse, model and designer pages plus the
/// thumbnails, geometry and photos they need to render, and nothing else:
/// collections, favorites and paint racks belong to an account, and every page
/// that writes anything still demands one.
///
/// The setting is read per request rather than cached, so turning public
/// browsing off shuts the door immediately rather than at the next restart.
/// </remarks>
public class ViewHandler(SettingsStore settings) : AuthorizationHandler<ViewRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ViewRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated ?? false)
        {
            context.Succeed(requirement);
            return;
        }

        if (await settings.GetBoolAsync(SettingKeys.PublicBrowsing))
            context.Succeed(requirement);
    }
}
