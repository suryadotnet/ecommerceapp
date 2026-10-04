using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrderService.Application.Commands.CreateOrder;
using OrderService.Application.Queries.GetOrder;
using OrderService.Infrastructure;
using OrderService.Infrastructure.ExternalServices;
using OrderService.Infrastructure.Resilience;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("OrderDb")));

// Typed HttpClients for downstream services. Each gets its own resilience pipeline
// (Rate Limiter -> Retry -> Circuit Breaker -> Timeout); see
// Infrastructure/Resilience/ResiliencePipelineExtensions.cs. Separate pipeline names
// mean Inventory and Payment have independent circuit breakers and rate limiters.
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
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Service API", // change per microservice
        Version = "v1",
        Description = "Microservice API documentation"
    });
});

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(
        typeof(CreateOrderCommand).Assembly);
});
builder.Services.AddControllers();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service API v1");
        c.RoutePrefix = string.Empty; // serves Swagger UI at root
    });
}
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
}

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
