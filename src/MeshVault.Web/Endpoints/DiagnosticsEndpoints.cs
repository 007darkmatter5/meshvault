using System.Net.WebSockets;
using System.Text;
using MeshVault.Web.Services;

namespace MeshVault.Web.Endpoints;

public static class DiagnosticsEndpoints
{
    /// <summary>
    /// A WebSocket that says "ok" and hangs up, so the diagnostics page can tell
    /// whether the browser can hold one open at all.
    /// </summary>
    /// <remarks>
    /// This is the single most useful fact when the UI has gone unresponsive.
    /// Blazor Server needs a WebSocket, and the most common reverse proxies ship
    /// with the upgrade turned off — Nginx Proxy Manager hides it behind a
    /// "Websockets Support" switch that defaults to off. Probing here rather
    /// than against <c>/_blazor</c> means the test can fail without disturbing a
    /// circuit that may be working.
    /// </remarks>
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.Map("/diag/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("Not a WebSocket request.");

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), WebSocketMessageType.Text,
                endOfMessage: true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

            // The response is the socket itself; nothing further to write.
            return Results.Empty;
        }).RequireAuthorization(Policies.Admin);

        // Posted by the diagnostics page. Browser-side faults otherwise never
        // reach the server log, which is the only place an operator can see them
        // after the fact.
        app.MapPost("/diag/note", async (HttpContext context, RecentEvents events) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var note = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(note)) return Results.BadRequest();

            events.Add(new LoggedEvent(
                DateTimeOffset.UtcNow, LogLevel.Warning, "Browser",
                note.Length > 500 ? note[..500] : note));

            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
    }
}
