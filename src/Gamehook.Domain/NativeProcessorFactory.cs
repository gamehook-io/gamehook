using Gamehook.Domain.NativeProcessors;

namespace Gamehook.Domain;

/// <summary>Maps stable mapper XML ids to built-in processor implementations.</summary>
public static class NativeProcessorFactory
{
    public static INativeProcessor Create(string id, GamehookSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(session);

        return id switch
        {
            "b42f3152" => new b42f3152(session),
            _ => throw new NotSupportedException($"Mapper requests unknown native processor '{id}'."),
        };
    }
}
