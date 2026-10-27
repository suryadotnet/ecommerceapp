using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrderService.Application.Commands.CreateOrder;
using OrderService.Application.Queries.GetOrder;
using OrderService.Infrastructure;
using OrderService.Infrastructure.ExternalServices;
using OrderService.Infrastructure.Resilience;
using MediatR;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

//builder.AddServiceDefaults();

var entraTenantId = builder.Configuration["AzureAd:TenantId"];
var entraClientId = builder.Configuration["AzureAd:ClientId"];
if (!Guid.TryParse(entraTenantId, out _) || !Guid.TryParse(entraClientId, out _))
{
    throw new InvalidOperationException(
        "Configure AzureAd:TenantId and AzureAd:ClientId with the Entra tenant ID and OrderService API app registration client ID.");
}

var entraScopes = builder.Configuration.GetSection("AzureAd:Scopes").Get<string[]>();
if (entraScopes is not { Length: > 0 })
{
    entraScopes = [$"api://{entraClientId}/access_as_user"];
}

var swaggerClientId = builder.Configuration["AzureAd:SwaggerClientId"];
if (string.IsNullOrWhiteSpace(swaggerClientId))
{
    swaggerClientId = entraClientId;
}
else if (!Guid.TryParse(swaggerClientId, out _))
{
    throw new InvalidOperationException(
        "AzureAd:SwaggerClientId must be a valid client ID when configured.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Load secrets (e.g. ConnectionStrings:OrderDb) from Azure Key Vault.
// Skipped in Development so local `dotnet run` never requires Azure CLI /
// Managed Identity / VS sign-in. appsettings.Development.json supplies the
// local connection string instead. In non-Development environments (Azure),
// DefaultAzureCredential resolves via Managed Identity automatically.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("OrderDb")));

// --- Azure Blob Storage client ---
// Development: uses a connection string (appsettings.Development.json ->
// ConnectionStrings:BlobStorage), so no Azure auth is needed locally.
// Other environments: uses the account URL + Managed Identity via
// DefaultAzureCredential, so no secrets are stored at all in Azure.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    if (builder.Environment.IsDevelopment())
    {
        var connectionString = config.GetConnectionString("BlobStorage");
        return new BlobServiceClient(connectionString);
    }

    var blobUri = config["Storage:BlobServiceUri"]; // e.g. https://<account>.blob.core.windows.net
    return new BlobServiceClient(new Uri(blobUri!), new DefaultAzureCredential());
});

// Store outgoing events in the same database transaction as the order/payment work.
builder.Services.AddScoped<IOrderMessagePublisher, OutboxOrderMessagePublisher>();

builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<OrderSagaConsumer>();

// Uploads product images to Blob Storage container "product-images" during order
// creation (see OrdersController.CreateWithImages) and returns the blob URL to
// persist on OrderItem.ImageUrl.
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// Typed HttpClients for downstream services. Each gets its own resilience pipeline
// (Rate Limiter -> Retry -> Circuit Breaker -> Timeout); see
// Infrastructure/Resilience/ResiliencePipelineExtensions.cs. Separate pipeline names
// mean Inventory and Payment have independent circuit breakers and rate limiters.
// NOTE: "Services:Inventory" / "Services:Payment" in appsettings.json are local dev
// defaults. Environment variables "Services__Inventory" and "Services__Payment"
// (double underscore = config section delimiter) automatically override these at
// runtime (e.g. in Azure App Service) - no code changes needed.
builder.Services.AddHttpClient<IInventoryServiceClient, InventoryServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:Inventory"]!);
    // Hard backstop; the per-attempt Timeout strategy (5s) normally triggers first.
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddCustomResiliencePipeline("inventory-service-pipeline");

builder.Services.AddHttpClient<IPaymentServiceClient, PaymentServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:Payment"]!);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddCustomResiliencePipeline("payment-service-pipeline");

//builder.Services.AddScoped<CreateOrderHandler>();
//builder.Services.AddScoped<GetOrderHandler>();

//enable swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    var authority = $"https://login.microsoftonline.com/{entraTenantId}/oauth2/v2.0";
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Service API", // change per microservice
        Version = "v1",
        Description = "Microservice API documentation"
    });
    c.AddSecurityDefinition("OAuth2", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                AuthorizationUrl = new Uri($"{authority}/authorize"),
                TokenUrl = new Uri($"{authority}/token"),
                Scopes = entraScopes.ToDictionary(scope => scope, _ => "Access the OrderService API")
            }
        }
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "OAuth2"
                }
            },
            entraScopes
        }
    });
});

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(
        typeof(CreateOrderCommand).Assembly);
});
builder.Services.AddControllers();
var app = builder.Build();



//app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service API v1");
        c.RoutePrefix = string.Empty; // serves Swagger UI at root
        c.OAuthClientId(swaggerClientId);
        c.OAuthUsePkce();
        c.OAuthScopeSeparator(" ");
        c.OAuthScopes(entraScopes);
    });
}
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
}
app.UseAuthentication();
app.UseAuthorization();
//app.MapPost("/api/orders", async (CreateOrderRequest request, CreateOrderHandler handler, CancellationToken ct) =>
//{
//    try
//    {
//        var result = await handler.HandleAsync(
//            new CreateOrderCommand(request.CustomerId, request.ProductId, request.Quantity, request.Amount), ct);
//        return Results.Ok(result);
//    }
//    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
//});

//app.MapGet("/api/orders/{id:guid}", async (Guid id, GetOrderHandler handler, CancellationToken ct) =>
//    (await handler.HandleAsync(new GetOrderQuery(id), ct)) is { } order
//        ? Results.Ok(order) : Results.NotFound());

//app.MapControllers();
 app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "OrderService" }));

app.Run();

//public sealed record CreateOrderRequest(Guid CustomerId, Guid ProductId, int Quantity, decimal Amount);
