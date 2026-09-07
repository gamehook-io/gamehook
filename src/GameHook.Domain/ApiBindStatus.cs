namespace GameHook.Domain;

// Set by GameHookApiHostedService (GameHook.Api) when the REST API/WebSocket port fails to bind -
// almost always another GameHook instance already holding it. A plain mutable singleton is enough:
// it's written once during host startup and read once by the UI right after, no event needed.
public sealed class ApiBindStatus
{
    public string? Error { get; set; }
}
