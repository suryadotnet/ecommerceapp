namespace EcommAzureFunctions.Services;

public sealed class GreetingService
{
    public string CreateGreeting(string? name = null) =>
        string.IsNullOrWhiteSpace(name)
            ? "Hello from Azure Functions!"
            : $"Hello, {name.Trim()}!";
}
