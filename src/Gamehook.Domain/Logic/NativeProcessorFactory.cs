using Gamehook.Domain.NativeProcessors;

namespace Gamehook.Domain.Logic;

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
            "ccd04ebe" => new ccd04ebe(session),
            "fa4e8780" => new fa4e8780(session),
            "fdb97126" => new fdb97126(session),
            "bdb9cfce" => new bdb9cfce(session),
            "c1b41fa3" => new c1b41fa3(session),
            "f248e50b" => new f248e50b(session),
            "cacbced9" => new cacbced9(session),
            "f2452fcc" => new f2452fcc(session),
            "a44171aa" => new a44171aa(session),
            _ => throw new NotSupportedException($"Mapper requests unknown native processor '{id}'."),
        };
    }
}
