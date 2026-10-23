using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace PayLink.Api;

/// <summary>
/// The OpenAPI reference: one document per API, because each is for a different reader and takes a different key.
/// Served at /docs/api/{name}/openapi.json, with Swagger UI at /docs/api.
/// </summary>
public static class ApiReference
{
    public sealed record Doc(string Name, string Title, string Prefix, string KeyExample, string Description);

    public static readonly Doc[] Docs =
    [
        new("payments", "Payments API", "api/v1/", "sk_test_acme_…",
            "For the merchant's server: create payments and payment links, read their status, refund, and query reporting data. " +
            "Authenticate with a secret key (sk_test_… for test data, sk_live_… for live) as `Authorization: Bearer <key>`. " +
            "Send an `Idempotency-Key` header on creates. Amounts are integers in paise. Webhooks are signed with your whsec_ secret; see /docs/payments-api. " +
            "A platform key with the TenantOperations capability can call these routes for a tenant with `PayLink-Tenant: {slug}` and `PayLink-Mode: test|live`."),
        new("tenant", "Tenant automation API", "api/tenant/", "tdk_acme_…",
            "For scripts that run one merchant's console: approve or reject UPI payments, upload bank statements, manage methods, webhooks and risk rules. " +
            "Authenticate with a developer key (tdk_…) as `Authorization: Bearer <key>`; each route needs the capability shown on it. " +
            "Choose test or live data with `?livemode=true|false` (default false). Everything a tenant admin can do in the console has a route here. " +
            "A platform key with the TenantOperations capability can call any route for a tenant with `PayLink-Tenant: {slug}` (every capability)."),
        new("platform", "Platform API", "api/admin/", "pdk_…",
            "For whoever runs the PayLink platform: create and manage tenants, auto-provisioning approvals, the audit log. " +
            "Authenticate with a platform developer key (pdk_…) created in /admin → Developer API. With TenantOperations a key also acts for tenants " +
            "(PayLink-Tenant header on /api/tenant and /api/v1) and manages tenants' keys."),
    ];

    public static void Configure(SwaggerGenOptions o)
    {
        foreach (var d in Docs)
        {
            o.SwaggerDoc(d.Name, new OpenApiInfo
            {
                Title = $"PayLink {d.Title}",
                Version = "v1",
                Description = d.Description,
            });
        }
        o.DocInclusionPredicate((name, api) =>
            Docs.FirstOrDefault(d => d.Name == name) is { } d && (api.RelativePath ?? "").StartsWith(d.Prefix, StringComparison.OrdinalIgnoreCase));
        o.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace("PayLink.", "").Replace('+', '.'));
        o.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer",
            Description = "sk_ key for the Payments API, tdk_ key for the tenant API, pdk_ key for the platform API.",
        });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearer" } }] = [],
        });
        o.OperationFilter<CapabilityFilter>();
        var xml = Path.Combine(AppContext.BaseDirectory, "PayLink.xml");
        if (File.Exists(xml)) o.IncludeXmlComments(xml);
    }

    public static void ConfigureUi(SwaggerUIOptions o)
    {
        o.RoutePrefix = "docs/api";
        o.DocumentTitle = "PayLink API reference";
        foreach (var d in Docs) o.SwaggerEndpoint($"/docs/api/{d.Name}/openapi.json", d.Title);
        o.DefaultModelsExpandDepth(0);
        o.DisplayRequestDuration();
        o.EnableTryItOutByDefault();
    }

    private static OpenApiRequestBody Json(Type type) => new()
    {
        Required = true,
        Content = { ["application/json"] = new OpenApiMediaType { Schema = Schema(type) } },
    };

    /// <summary>A schema with the snake_case names PaymentJson uses.</summary>
    private static OpenApiSchema Schema(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return new OpenApiSchema { Type = "string" };
        if (type == typeof(bool)) return new OpenApiSchema { Type = "boolean" };
        if (type == typeof(int) || type == typeof(long)) return new OpenApiSchema { Type = "integer", Format = type == typeof(long) ? "int64" : "int32" };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return new OpenApiSchema { Type = "object", AdditionalProperties = Schema(type.GetGenericArguments()[1]) };
        var schema = new OpenApiSchema { Type = "object" };
        foreach (var p in type.GetProperties())
        {
            var name = p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name ?? System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name);
            schema.Properties[name] = Schema(p.PropertyType);
        }
        return schema;
    }

    /// <summary>Says which tenant developer key capability a route needs, and documents raw-body uploads.</summary>
    private sealed class CapabilityFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var cap = context.MethodInfo.GetCustomAttributes(typeof(RequireTenantCapabilityAttribute), false)
                .Cast<RequireTenantCapabilityAttribute>().FirstOrDefault();
            if (cap is not null)
                operation.Description = $"Needs a developer key with the **{cap.Capability}** capability.\n\n{operation.Description}".Trim();
            // These two read their JSON themselves (snake_case, as the Payments API documents it), so describe the body here.
            if (context.MethodInfo.Name == "Create" && context.MethodInfo.DeclaringType == typeof(PaymentsApiController))
                operation.RequestBody = Json(typeof(PaymentsApiController.CreateBody));
            if (context.MethodInfo.Name == "Create" && context.MethodInfo.DeclaringType == typeof(LinksApiController))
                operation.RequestBody = Json(typeof(LinksApiController.CreateBody));
            if (context.MethodInfo.Name == nameof(TenantApiController.UpiStatement))
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content =
                    {
                        ["text/csv"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "string" } },
                        ["multipart/form-data"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = { ["statement"] = new OpenApiSchema { Type = "string", Format = "binary" } },
                                Required = new HashSet<string> { "statement" },
                            },
                        },
                    },
                };
            }
        }
    }
}
