using Gamehook.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Infrastructure;

public sealed record GamehookSettings(bool ContinuousRead);

/// Runtime-changeable settings (currently just ContinuousRead). The initial value comes from
/// configuration; a change applies to the live session immediately and lasts only for the current
/// session - nothing is written back to appsettings.json. The UI's Settings menu and the REST
/// API's POST /settings share this.
public sealed class SettingsService
{
    public const string ContinuousReadKey = "ContinuousRead";

    private readonly GamehookSession session;
    private readonly ILogger<SettingsService> logger;

    public SettingsService(GamehookSession session, bool initialContinuousRead, ILogger<SettingsService>? logger = null)
    {
        this.session = session;
        this.logger = logger ?? NullLogger<SettingsService>.Instance;
        session.SetContinuousRead(initialContinuousRead);
    }

    public static SettingsService Create(GamehookSession session, IConfiguration configuration, ILogger<SettingsService>? logger = null) =>
        new(session, configuration.GetValue(ContinuousReadKey, true), logger);

    public GamehookSettings Current => new(session.IsContinuousReadEnabled);

    public GamehookSettings Update(bool? continuousRead)
    {
        if (continuousRead is { } enabled && enabled != session.IsContinuousReadEnabled)
        {
            session.SetContinuousRead(enabled);
            logger.LogInformation("Continuous read mode {State} for this session.", enabled ? "enabled" : "disabled");
        }

        return Current;
    }
}
