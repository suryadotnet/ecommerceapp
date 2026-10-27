namespace InventoryService.Infrastructure;

public sealed class InventoryOutboxMessage
{
    private InventoryOutboxMessage()
    {
    }

    public Guid Id { get; private set; }
    public string DestinationName { get; private set; } = "";
    public string Subject { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static InventoryOutboxMessage Create(string destinationName, string subject, string payload)
    {
        var now = DateTime.UtcNow;
        return new InventoryOutboxMessage
        {
            Id = Guid.NewGuid(),
            DestinationName = destinationName,
            Subject = subject,
            Payload = payload,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now
        };
    }

    public void MarkProcessed(DateTime processedAtUtc)
    {
        ProcessedAtUtc = processedAtUtc;
        LastError = null;
    }

    public void MarkFailed(DateTime nextAttemptAtUtc, string error)
    {
        Attempts++;
        NextAttemptAtUtc = nextAttemptAtUtc;
        LastError = error.Length <= 4000 ? error : error[..4000];
    }
}
