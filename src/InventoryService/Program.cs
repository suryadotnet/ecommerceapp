using InventoryService.Domain;
using InventoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("InventoryDb")));

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
app.MapPost("/api/inventory/reserve", async (ReserveRequest request, InventoryDbContext db, CancellationToken ct) =>
{
    var product = await db.Products.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
    if (product is null) return Results.NotFound("Product not found.");
    if (product.AvailableQuantity < request.Quantity) return Results.BadRequest("Insufficient inventory.");

    product.AvailableQuantity -= request.Quantity;
    product.ReservedQuantity += request.Quantity;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { request.OrderId, status = "Reserved" });
});

// Release previously reserved stock. Called by OrderService as Saga compensation
// when a later step (e.g. payment) fails after inventory was already reserved.
app.MapPost("/api/inventory/release", async (ReleaseRequest request, InventoryDbContext db, CancellationToken ct) =>
{
    var product = await db.Products.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
    if (product is null) return Results.NotFound("Product not found.");

    product.ReservedQuantity = Math.Max(0, product.ReservedQuantity - request.Quantity);
    product.AvailableQuantity += request.Quantity;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { request.OrderId, status = "Released" });
});

//app.MapGet("/api/inventory/{productId:guid}", async (Guid productId, InventoryDbContext db, CancellationToken ct) =>
//    await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId, ct) is { } product
//        ? Results.Ok(product) : Results.NotFound());

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "InventoryService" }));
app.MapControllers();
app.Run();

public sealed record ReserveRequest(Guid OrderId, Guid ProductId, int Quantity);
public sealed record ReleaseRequest(Guid OrderId, Guid ProductId, int Quantity);
