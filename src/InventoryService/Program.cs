using InventoryService.Domain;
using InventoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("InventoryDb")));
builder.Services.AddHostedService<ServiceBusConsumer>();
builder.Services.AddHostedService<InventoryOutboxDispatcher>();

// Application Insights telemetry & logging
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
});
builder.Logging.AddApplicationInsights(
    configureTelemetryConfiguration: tc => tc.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"],
    configureApplicationInsightsLoggerOptions: options => { });

//enable swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Inventory Service API", // change per microservice
        Version = "v1",
        Description = "Microservice API documentation"
    });
});
builder.Services.AddControllers();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory Service API v1");
        c.RoutePrefix = string.Empty; // serves Swagger UI at root
    });
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync(
        """
        IF OBJECT_ID(N'dbo.OrderCreatedReceipts', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.OrderCreatedReceipts
            (
                MessageId nvarchar(128) NOT NULL CONSTRAINT PK_OrderCreatedReceipts PRIMARY KEY,
                OrderId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                TotalAmount decimal(18,2) NOT NULL,
                CreatedAtUtc datetime2 NOT NULL,
                ReceivedAtUtc datetime2 NOT NULL
            );
        END;
        IF OBJECT_ID(N'dbo.PaymentInfoReceipts', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.PaymentInfoReceipts
            (
                MessageId nvarchar(128) NOT NULL CONSTRAINT PK_PaymentInfoReceipts PRIMARY KEY,
                OrderId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                Amount decimal(18,2) NOT NULL,
                Status nvarchar(32) NOT NULL,
                ProcessedAtUtc datetime2 NOT NULL,
                ReceivedAtUtc datetime2 NOT NULL
            );
        END;
        IF OBJECT_ID(N'dbo.InventoryReservations', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.InventoryReservations
            (
                ReservationId uniqueidentifier NOT NULL,
                OrderId uniqueidentifier NOT NULL,
                ProductId uniqueidentifier NOT NULL,
                Quantity int NOT NULL,
                IsReleased bit NOT NULL CONSTRAINT DF_InventoryReservations_IsReleased DEFAULT 0,
                CreatedAtUtc datetime2 NOT NULL,
                CONSTRAINT PK_InventoryReservations PRIMARY KEY (ReservationId, ProductId)
            );
            CREATE INDEX IX_InventoryReservations_OrderId ON dbo.InventoryReservations(OrderId);
        END;
        IF OBJECT_ID(N'dbo.InventoryEventReceipts', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.InventoryEventReceipts
            (
                MessageId nvarchar(128) NOT NULL CONSTRAINT PK_InventoryEventReceipts PRIMARY KEY,
                ProcessedAtUtc datetime2 NOT NULL
            );
        END;
        IF OBJECT_ID(N'dbo.InventoryOutboxMessages', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.InventoryOutboxMessages
            (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_InventoryOutboxMessages PRIMARY KEY,
                DestinationName nvarchar(256) NOT NULL,
                Subject nvarchar(256) NOT NULL,
                Payload nvarchar(max) NOT NULL,
                CreatedAtUtc datetime2 NOT NULL,
                ProcessedAtUtc datetime2 NULL,
                NextAttemptAtUtc datetime2 NOT NULL,
                Attempts int NOT NULL,
                LastError nvarchar(4000) NULL
            );
            CREATE INDEX IX_InventoryOutboxMessages_ProcessedAtUtc_NextAttemptAtUtc
                ON dbo.InventoryOutboxMessages(ProcessedAtUtc, NextAttemptAtUtc);
        END;
        """);
    var productId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    if (!await db.Products.AnyAsync(x => x.ProductId == productId))
    {
        db.Products.Add(new ProductStock { ProductId = productId, ProductName = "Demo Laptop", AvailableQuantity = 10 });
        await db.SaveChangesAsync();
    }

    // //DB seeding
    // if(!db.EmailNotification.Any())
    // {
    //     db.EmailNotification.Add(new EmailNotification { ID= new Guid()});
    //     await db.SaveChangesAsync();
    // }
}

// Reserve stock for an order line item. Called by OrderService during checkout.
app.MapPost("/api/inventory/reserve", async (ReserveRequest request, InventoryDbContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    logger.LogInformation("Reserve requested for OrderId {OrderId}, ProductId {ProductId}, Quantity {Quantity}",
        request.OrderId, request.ProductId, request.Quantity);

    var product = await db.Products.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
    if (product is null)
    {
        logger.LogWarning("Product {ProductId} not found for OrderId {OrderId}", request.ProductId, request.OrderId);
        return Results.NotFound("Product not found.");
    }
    if (product.AvailableQuantity < request.Quantity)
    {
        logger.LogWarning("Insufficient inventory for ProductId {ProductId}. Available: {Available}, Requested: {Requested}",
            request.ProductId, product.AvailableQuantity, request.Quantity);
        return Results.BadRequest("Insufficient inventory.");
    }
//    product.AvailableQuantity -= request.Quantity;
    product.AvailableQuantity -= request.Quantity;
    product.ReservedQuantity += request.Quantity;
    await db.SaveChangesAsync(ct);
    logger.LogInformation("Reserved {Quantity} units of ProductId {ProductId} for OrderId {OrderId}",
        request.Quantity, request.ProductId, request.OrderId);
    return Results.Ok(new { request.OrderId, status = "Reserved" });
});
//app.MapPost("/api/inventory/release", async (ReleaseRequest request, InventoryDbContext db, CancellationToken ct) =>
// Release previously reserved stock. Called by OrderService as Saga compensation
// when a later step (e.g. payment) fails after inventory was already reserved.
app.MapPost("/api/inventory/release", async (ReleaseRequest request, InventoryDbContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    logger.LogInformation("Release requested for OrderId {OrderId}, ProductId {ProductId}, Quantity {Quantity}",
        request.OrderId, request.ProductId, request.Quantity);

    var product = await db.Products.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
    if (product is null)
    {
        logger.LogWarning("Product {ProductId} not found for OrderId {OrderId}", request.ProductId, request.OrderId);
        return Results.NotFound("Product not found.");
    }
//    await db.SaveChangesAsync(ct);
    product.ReservedQuantity = Math.Max(0, product.ReservedQuantity - request.Quantity);
    product.AvailableQuantity += request.Quantity;
    await db.SaveChangesAsync(ct);
    logger.LogInformation("Released {Quantity} units of ProductId {ProductId} for OrderId {OrderId}",
        request.Quantity, request.ProductId, request.OrderId);
    return Results.Ok(new { request.OrderId, status = "Released" });
});
//        ? Results.Ok(product) : Results.NotFound());

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "InventoryService" }));
app.MapControllers();
app.Run();

public sealed record ReserveRequest(Guid OrderId, Guid ProductId, int Quantity);
public sealed record ReleaseRequest(Guid OrderId, Guid ProductId, int Quantity);
