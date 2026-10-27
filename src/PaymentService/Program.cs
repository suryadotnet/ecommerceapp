using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using PaymentService.Domain;
using PaymentService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("PaymentDb")));
builder.Services.AddHostedService<PaymentSagaConsumer>();
builder.Services.AddHostedService<PaymentOutboxDispatcher>();

//enable swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Payment Service API", // change per microservice
        Version = "v1",
        Description = "Microservice API documentation"
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment Service API v1");
        c.RoutePrefix = string.Empty; // serves Swagger UI at root
    });
}
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync(
        """
        IF OBJECT_ID(N'dbo.PaymentEventReceipts', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.PaymentEventReceipts
            (
                MessageId nvarchar(128) NOT NULL CONSTRAINT PK_PaymentEventReceipts PRIMARY KEY,
                ProcessedAtUtc datetime2 NOT NULL
            );
        END;
        IF OBJECT_ID(N'dbo.PaymentOutboxMessages', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.PaymentOutboxMessages
            (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentOutboxMessages PRIMARY KEY,
                DestinationName nvarchar(256) NOT NULL,
                Subject nvarchar(256) NOT NULL,
                Payload nvarchar(max) NOT NULL,
                CreatedAtUtc datetime2 NOT NULL,
                ProcessedAtUtc datetime2 NULL,
                NextAttemptAtUtc datetime2 NOT NULL,
                Attempts int NOT NULL,
                LastError nvarchar(4000) NULL
            );
            CREATE INDEX IX_PaymentOutboxMessages_ProcessedAtUtc_NextAttemptAtUtc
                ON dbo.PaymentOutboxMessages(ProcessedAtUtc, NextAttemptAtUtc);
        END;
        """);
}

app.MapPost("/api/payments/process", async (ProcessPaymentRequest request, PaymentDbContext db, CancellationToken ct) =>
{
    // Training/demo rule: amounts >= 99,999 intentionally fail to make the Saga compensation visible.
    var payment = new Payment
    {
        Id = Guid.NewGuid(),
        OrderId = request.OrderId,
        CustomerId = request.CustomerId,
        Amount = request.Amount,
        CreatedAtUtc = DateTime.UtcNow
    };

    if (request.Amount >= 99999)
    {
        payment.Status = PaymentStatus.Failed;
        payment.FailureReason = "Demo payment declined because the amount is at or above the failure threshold.";
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return Results.BadRequest(payment.FailureReason);
    }

    payment.Status = PaymentStatus.Processed;
    db.Payments.Add(payment);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { request.OrderId, paymentId = payment.Id, status = payment.Status.ToString() });
});

app.MapGet("/api/payments/order/{orderId:guid}", async (Guid orderId, PaymentDbContext db, CancellationToken ct) =>
    await db.Payments.AsNoTracking().Where(x => x.OrderId == orderId).ToListAsync(ct));

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "PaymentService" }));
app.Run();

public sealed record ProcessPaymentRequest(Guid OrderId, Guid CustomerId, decimal Amount);
