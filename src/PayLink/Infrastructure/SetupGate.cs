namespace PayLink.Infrastructure;

/// <summary>Until setup finishes, every page redirects to /setup (like LoginLink and CrudApi). After it finishes, /setup is closed.</summary>
public sealed class SetupGate(RequestDelegate next)
{
    private static readonly string[] OpenDuringSetup = ["/setup", "/healthz", "/auth/callback", "/css/", "/js/", "/favicon"];
    private volatile bool _complete;

    public async Task InvokeAsync(HttpContext context, BootstrapStore bootstrap)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!_complete && await bootstrap.IsSetupCompleteAsync()) _complete = true;

        if (!_complete)
        {
            if (!OpenDuringSetup.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new { error = "setup_required", error_description = "Finish /setup first." });
                    return;
                }
                context.Response.Redirect("/setup");
                return;
            }
        }
        else if (path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect("/admin");
            return;
        }
        await next(context);
    }
}
