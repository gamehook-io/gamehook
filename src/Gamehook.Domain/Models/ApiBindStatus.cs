namespace Gamehook.Domain.Models;

/// <summary>Shared application state for a non-fatal embedded API bind failure.</summary>
public sealed class ApiBindStatus
{
    public string? Error { get; set; }
}
