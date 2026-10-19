using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Api
{
    public class ConfigureSwaggerOptions
        : IConfigureNamedOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider provider;

        public ConfigureSwaggerOptions(
            IApiVersionDescriptionProvider provider)
        {
            this.provider = provider;
        }

        public void Configure(SwaggerGenOptions options)
        {
            // add swagger document for every API version discovered
            options.AddSecurityDefinition("Basic", new OpenApiSecurityScheme()
            {
                Description = $"Add basic authorization header for endpoint Token - api/oauth2/v{1}/token",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Scheme = "basic",
                Type = SecuritySchemeType.Http
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Basic" }
                        },
                        new List<string>()
                    }
                });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT Authorization header using the Bearer scheme."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        new string[] {}
                    }
                });
            foreach (var description in provider.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, CreateVersionInfo(description));
                options.DescribeAllParametersInCamelCase();
            }
        }
        
        public void Configure(string? name, SwaggerGenOptions options)
        {
            Configure(options);
        }

        private OpenApiInfo CreateVersionInfo(
                ApiVersionDescription description)
        {
            var info = new OpenApiInfo()
            {
                Title = $"PayLink API Docs - v{description.ApiVersion} | WebApis",
                Version = description.ApiVersion.ToString(),
                Description = "PayLink is a unified payment gateway that supports PhonePe, PayU, UPI, and CCAvenue. With a single integration, businesses can process payments across all supported providers using one simplified API contract. For no-code users, visit https://paylink.websvc.in to instantly generate payment links — no coding required.",
                TermsOfService = new Uri("https://paylink.websvc.in/terms"),
                Contact = new OpenApiContact
                {
                    Name = "PayLink Help",
                    Email = "help@websvc.in",
                    Url = new Uri("https://paylink.websvc.in/help"),
                },
                License = new OpenApiLicense
                {
                    Name = "License Information",
                    Url = new Uri("https://paylink.websvc.in/license"),
                },
            };

            if (description.IsDeprecated)
            {
                info.Description += " This API version has been deprecated.";
            }
            return info;
        }
    }
}
