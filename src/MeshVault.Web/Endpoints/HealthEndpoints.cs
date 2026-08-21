using MeshVault.Data;
using Microsoft.EntityFrameworkCore;

namespace MeshVault.Web.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// A liveness probe for Docker and Unraid. Deliberately anonymous and
    /// deliberately uninformative: it confirms the app is up and the database
    /// is reachable without telling an unauthenticated caller anything about
    /// the library.
    /// </summary>
    public static void MapHealthEndpoint(this WebApplication app)
    {
        app.MapGet("/health", async (
            IDbContextFactory<MeshVaultDbContext> factory,
            CancellationToken ct) =>
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
                return Results.Ok("healthy");
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
    }
}
