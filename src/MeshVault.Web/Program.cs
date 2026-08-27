using MeshVault.Core.Services;
using MeshVault.Data;
using MeshVault.Web;
using MeshVault.Web.Components;
using MeshVault.Web.Services;
using MeshVault.Core.Imaging;
using MeshVault.Core.Meshes;
using MeshVault.Web.Endpoints;
using MudBlazor.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MeshVaultOptions>(
    builder.Configuration.GetSection(MeshVaultOptions.SectionName));

var options = builder.Configuration.GetSection(MeshVaultOptions.SectionName)
    .Get<MeshVaultOptions>() ?? new MeshVaultOptions();
var dataPath = Path.GetFullPath(options.DataPath);
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContextFactory<MeshVaultDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataPath, "meshvault.db")}",
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

// Blazor Server components own short-lived contexts from the factory; services
// that run per-request or per-scan get a conventional scoped context.
builder.Services.AddScoped<MeshVaultDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<MeshVaultDbContext>>().CreateDbContext());

builder.Services.AddMudServices();

// Auth cookies and antiforgery tokens are encrypted with these keys. Left at
// the default they live in the container's filesystem and vanish on every
// update, signing everyone out and breaking forms mid-session.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")))
    .SetApplicationName("MeshVault");

// Behind a reverse proxy (SWAG, Nginx Proxy Manager, Traefik) the app only
// sees the proxy's http request; without this, redirects after sign-in are
// built with the wrong scheme and host.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // The proxy is on the same Docker network and is not known by address here.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddSingleton<FolderScanner>();
builder.Services.AddSingleton<DirectoryBrowser>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        // A home server behind a login, not a public site: no email confirmation
        // loop to get stuck in.
        o.SignIn.RequireConfirmedAccount = false;
        o.User.RequireUniqueEmail = false;

        // Length rather than composition. Demanding a digit and a symbol pushes
        // people towards short predictable passwords with a "1!" on the end; a
        // longer passphrase with no such rules is stronger and easier to type.
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Lockout.MaxFailedAccessAttempts = 10;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<MeshVaultDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/denied";
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Cookie.Name = "MeshVault.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin))
    .AddPolicy(Policies.View, policy => policy.AddRequirements(new ViewRequirement()));

// Scoped, not singleton: it reads a setting from the database per request, so
// turning public browsing off shuts the door immediately.
builder.Services.AddScoped<IAuthorizationHandler, ViewHandler>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AccountSetup>();
builder.Services.AddScoped<MeshVault.Web.Services.UserAdmin>();

// Owner of per-user data, resolved from the signed-in principal.
builder.Services.AddScoped<ICurrentUser, SignedInUser>();
builder.Services.AddScoped<LibraryIndexer>();

// The vocabulary lives in the database and is curated; the classifier built
// from it does not change under a scan that is already running.
builder.Services.AddSingleton<VariantRules>();
builder.Services.AddScoped(sp => sp.GetRequiredService<VariantRules>().Current);
builder.Services.AddScoped<VariantReindexer>();
builder.Services.AddScoped<VariantStore>();

builder.Services.AddScoped<ModelCatalog>();
builder.Services.AddScoped<ModelEditor>();
builder.Services.AddScoped<OrganizePlanner>();
builder.Services.AddScoped<GroupPlanner>();
builder.Services.AddScoped<OrganizeExecutor>();
builder.Services.AddScoped<GroupStore>();
builder.Services.AddScoped<PaintStore>();
builder.Services.AddSingleton<ScanService>();
builder.Services.AddSingleton<OrganizeService>();
builder.Services.AddSingleton<ForegroundActivity>();

// Diagnostics: a bounded tail of warnings and errors, and a count of live
// circuits, both read by /diagnostics.
//
// The buffer is constructed here rather than resolved, because the logging
// provider is built before the container is and would otherwise end up holding
// a different instance from the one the page reads.
var recentEvents = new RecentEvents();
builder.Services.AddSingleton(recentEvents);
builder.Logging.AddProvider(new RecentEventsLoggerProvider(recentEvents));
builder.Services.AddSingleton<CircuitTracker>();
builder.Services.AddSingleton<CircuitHandler>(sp => sp.GetRequiredService<CircuitTracker>());
builder.Services.AddScoped<DiagnosticsReport>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddHostedService<StartupIndexer>();

builder.Services.AddSingleton(new ThumbnailStore(Path.Combine(dataPath, "thumbnails")));
builder.Services.AddSingleton(new GeometryCache(Path.Combine(dataPath, "geometry")));
builder.Services.AddSingleton(new PhotoStore(Path.Combine(dataPath, "photos")));
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ThumbnailService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Ahead of everything else, so downstream sees the real scheme and host.
app.UseForwardedHeaders();

// Must finish before the first request is served.
await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Only page requests get the friendly not-found page. Re-executing it for a
// media request would answer an <img> tag with 21 KB of HTML, and replaying a
// POST body through the Blazor endpoint turns a 404 into a content-type error.
//
// Scripts and stylesheets are here for the same reason: a missing framework
// asset that answers 200-shaped HTML is far harder to recognise than a bare
// 404, and it is the browser, not a person, reading the reply.
app.Use(async (context, next) =>
{
    if ((MediaEndpoints.IsMediaPath(context.Request.Path)
         || context.Request.Path.StartsWithSegments("/health")
         || context.Request.Path.StartsWithSegments("/diag")
         || context.Request.Path.StartsWithSegments("/_framework")
         || context.Request.Path.StartsWithSegments("/_content"))
        && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
    {
        statusCodePages.Enabled = false;
    }
    await next();
});

// The diagnostics page probes this to tell whether the browser can hold a
// WebSocket open at all, which Blazor Server needs and proxies commonly block.
app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthEndpoint();
app.MapMediaEndpoints();
app.MapAccountEndpoints();
app.MapDiagnosticsEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
