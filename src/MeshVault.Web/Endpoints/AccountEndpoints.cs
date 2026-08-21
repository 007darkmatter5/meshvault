using MeshVault.Data;
using Microsoft.AspNetCore.Identity;

namespace MeshVault.Web.Endpoints;

public static class AccountEndpoints
{
    /// <summary>
    /// Sign-out has to be a form post to a plain endpoint: it clears a cookie,
    /// and a Blazor circuit cannot touch response headers.
    /// </summary>
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/logout", async (
            SignInManager<ApplicationUser> signIn,
            HttpContext context) =>
        {
            await signIn.SignOutAsync();
            context.Response.Redirect("/login");
        });
    }
}
