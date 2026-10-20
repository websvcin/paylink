using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using PayLink.Api;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<BootstrapStore>();
builder.Services.AddSingleton<PlatformDb>();
builder.Services.AddSingleton<PlatformSettings>();
builder.Services.AddSingleton<PlatformRepository>();
builder.Services.AddSingleton<TenantDb>();
builder.Services.AddSingleton<LoginLinkClient>();
builder.Services.AddSingleton<OidcSignIn>();
builder.Services.AddScoped<AdmissionService>();
builder.Services.AddScoped<OrgConnector>();
builder.Services.AddScoped<TenantProvisioning>();
builder.Services.AddScoped<PayLink.Pages.Signup.SignupSessions>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<HealthState>();
builder.Services.AddHostedService<StartupMigrations>();

builder.Services.AddHttpClient("loginlink", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("hosting", c => c.Timeout = TimeSpan.FromSeconds(60));

// Cookie encryption keys live next to the other keys, so a restart doesn't sign everyone out.
var dataRoot = builder.Configuration["PayLink:DataRoot"] is { Length: > 0 } configured
    ? configured : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
builder.Services.AddDataProtection()
    .SetApplicationName("PayLink")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys", "dataprotection")));

builder.Services
    .AddAuthentication(AuthSchemes.Console)
    .AddCookie(AuthSchemes.Admin, o => CookieDefaults.Configure(o, "paylink.admin", "/", "/admin/login"))
    .AddCookie(AuthSchemes.Console, o => CookieDefaults.Configure(o, "paylink.console", "/", "/"))
    .AddScheme<AuthenticationSchemeOptions, DeveloperKeyHandler>(AuthSchemes.DeveloperKey, null);

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("PlatformAny", p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireClaim(PayLinkClaims.Access, "admin", "support"));
    o.AddPolicy("PlatformAdmin", p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireClaim(PayLinkClaims.Access, "admin"));
    o.AddPolicy("DeveloperKey", p => p.AddAuthenticationSchemes(AuthSchemes.DeveloperKey).RequireAuthenticatedUser());
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", "PlatformAny");
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
    o.Conventions.AllowAnonymousToPage("/Admin/Logout");
});
builder.Services.AddControllers();

// Behind a reverse proxy, set PayLink:TrustedProxies (comma-separated IPs) so client IPs and https are read correctly.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var ip in (builder.Configuration["PayLink:TrustedProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (System.Net.IPAddress.TryParse(ip, out var address)) o.KnownProxies.Add(address);
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseMiddleware<SetupGate>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", async (BootstrapStore bootstrap, PlatformDb platformDb, HealthState health) =>
{
    var setup = await bootstrap.IsSetupCompleteAsync();
    var platformOk = false;
    string? platformError = null;
    if (await bootstrap.GetPlatformDatabaseAsync() is not null)
    {
        try { await using var c = await platformDb.OpenAsync(); platformOk = true; }
        catch (Exception ex) { platformError = ex.Message; }
    }
    var healthy = !setup || platformOk;
    return Results.Json(new
    {
        status = healthy ? "ok" : "degraded",
        setupComplete = setup,
        platformDatabase = platformOk ? "ok" : platformError ?? "not configured",
        degradedTenants = health.DegradedTenants,
        version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    }, statusCode: healthy ? 200 : 503);
});

app.MapRazorPages();
app.MapControllers();

app.Run();

public partial class Program;
