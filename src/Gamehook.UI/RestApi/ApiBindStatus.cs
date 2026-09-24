namespace Gamehook.RestApi;

/// <summary>Why the embedded REST API could not bind its port, for the UI to surface once; null when it bound.</summary>
public sealed class ApiBindStatus
{
    public string? Error { get; set; }
}
