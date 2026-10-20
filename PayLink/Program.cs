using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using PayLink.Api;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;

DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<BootstrapStore>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<PlatformDb>();
builder.Services.AddSingleton<PlatformSettings>();
builder.Services.AddSingleton<PlatformRepository>();
builder.Services.AddSingleton<TenantDb>();
builder.Services.AddSingleton<LoginLinkClient>();
builder.Services.AddSingleton<OidcSignIn>();
builder.Services.AddScoped<AdmissionService>();
builder.Services.AddScoped<OrgConnector>();
builder.Services.AddScoped<TenantProvisioning>();
builder.Services.AddScoped<DatabaseMoves>();
builder.Services.AddScoped<PayLink.Pages.Signup.SignupSessions>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<HealthState>();
builder.Services.AddHostedService<StartupMigrations>();

// Payments (Phase 1)
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<ConnectorPackages>();
builder.Services.AddSingleton<PlatformPayments>();
builder.Services.AddSingleton<ConnectorSettingsService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton<RiskRuleStore>();
builder.Services.AddSingleton<TenantKeyService>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<PaymentJobs>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PaymentJobs>());
builder.Services.AddHttpClient(ConnectorRegistry.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(ConnectorPackages.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(WebhookService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => WebhookService.CreateHandler(builder.Configuration.GetValue<bool>("PayLink:Webhooks:AllowPrivateNetworks")));

builder.Services.AddHttpClient("loginlink", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("hosting", c => c.Timeout = TimeSpan.FromSeconds(60));

// Cookie and form-signing keys live in bootstrap.db with the secret key, so the data folder is self-contained.
var dataRoot = builder.Configuration["PayLink:DataRoot"] is { Length: > 0 } configured
    ? configured : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataRoot);
builder.Services.AddDataProtection().SetApplicationName("PayLink");
builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
    o.XmlRepository = new BootstrapKeyRepository(Path.Combine(Path.GetFullPath(dataRoot), "bootstrap.db"), Path.Combine(Path.GetFullPath(dataRoot), "keys", "dataprotection")));

builder.Services
    .AddAuthentication(AuthSchemes.Console)
    .AddCookie(AuthSchemes.Admin, o => CookieDefaults.Configure(o, "paylink.admin", "/", "/admin/login"))
    .AddCookie(AuthSchemes.Console, o => CookieDefaults.Configure(o, "paylink.console", "/", "/"))
    .AddScheme<AuthenticationSchemeOptions, DeveloperKeyHandler>(AuthSchemes.DeveloperKey, null)
    .AddScheme<AuthenticationSchemeOptions, TenantKeyHandler>(TenantKeyHandler.Scheme, null);

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("PlatformAny", p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireClaim(PayLinkClaims.Access, "admin", "support"));
    o.AddPolicy("PlatformAdmin", p => p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireClaim(PayLinkClaims.Access, "admin"));
    o.AddPolicy("DeveloperKey", p => p.AddAuthenticationSchemes(AuthSchemes.DeveloperKey).RequireAuthenticatedUser());
    o.AddPolicy("PaymentsKey", p => p.AddAuthenticationSchemes(TenantKeyHandler.Scheme).RequireClaim(TenantKeyHandler.KindClaim, TenantKeyKinds.Secret));
    o.AddPolicy("TenantDeveloperKey", p => p.AddAuthenticationSchemes(TenantKeyHandler.Scheme).RequireClaim(TenantKeyHandler.KindClaim, TenantKeyKinds.Developer));
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", "PlatformAny");
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
    o.Conventions.AllowAnonymousToPage("/Admin/Logout");
});
builder.Services.AddControllers();
// Write ₹ and non-Latin names as themselves instead of &#x…; escapes. HTML-significant characters are still encoded.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

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
app.UseMiddleware<TenantHosts>();
app.UseRouting();
// Wraps the API so failed sign-ins are logged too; the key handler leaves the tenant in HttpContext.Items.
app.UseMiddleware<ApiRequestLogging>();
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

// Gateways send the payer back here (GET, or a form post for PayU and CCAvenue). A verified result is applied, or the
// gateway is asked; then the checkout shows the outcome.
app.MapMethods("/pay/{id}/return", ["GET", "POST"], async (string id, string? attempt, HttpContext http, PlatformDb platformDb, PlatformRepository repo, PaymentService payments) =>
{
    long? tenantId;
    await using (var c = await platformDb.OpenAsync())
        tenantId = await c.ExecuteScalarAsync<long?>("SELECT tenant_id FROM payment_routes WHERE payment_id = @id;", new { id });
    if (tenantId is not null && !string.IsNullOrEmpty(attempt) && await repo.TenantByIdAsync(tenantId.Value) is { IsActive: true } tenant)
        await payments.ReturnAsync(tenant, id, attempt, http.Request, EventSource.Payer(http));
    return Results.Redirect($"/pay/{Uri.EscapeDataString(id)}");
}).DisableAntiforgery();

app.MapRazorPages();
app.MapControllers();

app.Run();

public partial class Program;
