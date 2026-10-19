using System.Globalization;
using System.Text.Json.Serialization;
using Api;
using Api.Extensions;
using Api.Models;
using Api.Services;
using CryptoService;
using CryptoService.Interface;
using Helper;
using Helper.Interface;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Notify.Services;
using SqlConnector.MySql;
using SqlConnector.Utilities;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// var contentRoot = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? string.Empty) 
//                   ?? Directory.GetCurrentDirectory();

var contentRoot = AppContext.BaseDirectory;

// Create WebApplicationOptions with the content root set
var options = new WebApplicationOptions
{
    ContentRootPath = contentRoot
};

var builder = WebApplication.CreateBuilder(options);




builder.Host.UseDefaultServiceProvider(options => options.ValidateScopes = false);
builder.Services.AddHttpContextAccessor();

// Register WebhookUrlService as singleton or scoped
builder.Services.AddScoped<WebhookUrlService>();
builder.Services.AddHttpClient<INotificationService, NotificationService>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["Notify:BaseUrl"] ?? "https://notify.websvc.in";
    client.BaseAddress = new Uri(baseUrl);
});


// Add services to the container.
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddApiVersioning(setup =>
{
    setup.DefaultApiVersion = new ApiVersion(1, 0);
    setup.AssumeDefaultVersionWhenUnspecified = true;
    setup.ReportApiVersions = true;
});
builder.Services.AddVersionedApiExplorer(setup =>
{
    setup.GroupNameFormat = "'v'VVV";
    setup.SubstituteApiVersionInUrl = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSwaggerGen(c =>
{
    c.EnableAnnotations();
});
builder.Services.ConfigureOptions<ConfigureSwaggerOptions>();

builder.Services.Configure<JsonOptions>(x => x.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<JsonOptions>(opt => opt.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Latest).ConfigureApiBehaviorOptions(options => { options.InvalidModelStateResponseFactory = actionContext => { return CustomErrorResponse(actionContext); }; });

builder.Services.AddJWTTokenServices(builder.Configuration);
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<IMySqlCrud, MySqlCrud>();
builder.Services.AddScoped<IPropertyInspector, PropertyInspector>();
builder.Services.AddScoped<IPaymentService , PaymentService>();
builder.Services.AddScoped<IUrlService , UrlService>();
builder.Services.AddScoped<IChecksum , Checksum>();


builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        builder =>
        {
            builder.AllowAnyOrigin()
                                .AllowAnyHeader()
                                .AllowAnyMethod();
        });
});

var app = builder.Build();


app.UseWebSockets();



// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDeveloperExceptionPage();
}
app.UseSwagger();
app.UseSwaggerUI();
app.UseDeveloperExceptionPage();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};

// Trust only internal network or proxy IPs
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

app.UseForwardedHeaders(forwardedHeadersOptions);


app.UseExceptionHandler(appBuilder =>
{
    appBuilder.Run(async context =>
    {
        var Ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        // Log the error
        logger.LogError(Ex, "An unexpected error occurred.");

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var errorResponse = new ErrorModel
        {
            Error = "server_error",
            ErrorDescription = Ex?.Message ?? "Unknown error"
        };

        await context.Response.WriteAsJsonAsync(errorResponse);
    });
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.UseStaticFiles(new StaticFileOptions() {  });
app.UseSwagger(c => c.RouteTemplate = "docs/{documentname}/swagger.json");

app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "WEBAPIS.IN - APIS";
    foreach (var description in app.Services.GetRequiredService<IApiVersionDescriptionProvider>().ApiVersionDescriptions)
    {
        options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", description.GroupName);
    }
    options.RoutePrefix = "docs";
});


app.Run();


BadRequestObjectResult CustomErrorResponse(ActionContext actionContext)
{
    return new BadRequestObjectResult(actionContext.ModelState
        .Select(modelError => new ErrorModel
        {
            Error = modelError.Key,
            ErrorDescription = modelError.Value?.Errors.FirstOrDefault()?.ErrorMessage ?? "Unknown error"
        }).FirstOrDefault());
}