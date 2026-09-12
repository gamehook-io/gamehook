namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Mapper-specific CLR work that runs around its JavaScript lifecycle.
/// Processors are session-scoped: they may use the loaded mapper through their session, but must
/// not retain a mapper after that session unloads it.
/// </summary>
public interface INativeProcessor
{
    /// <summary>
    /// Runs before the mapper JavaScript preprocessor. Return false to skip this read's property
    /// translation and postprocessors, matching a JavaScript preprocessor returning false.
    /// </summary>
    bool Preprocessor();

    /// <summary>Runs before the mapper JavaScript postprocessor.</summary>
    void Postprocessor();
}

/// <summary>
/// Optional mapper capability used by <see cref="GamehookSession"/> to attach the processor named
/// by mapper XML. Keeping this separate from <c>IMapper</c> avoids making alternate mapper
/// implementations support native processors.
/// </summary>
public interface INativeProcessorHost
{
    string? NativeProcessorId { get; }

    void SetNativeProcessor(INativeProcessor nativeProcessor);

    /// <summary>Reads bytes from current driver snapshot; never performs device I/O.</summary>
    ReadOnlyMemory<byte> ReadMemory(ulong address, int length);

    /// <summary>Declares a virtual region before it can receive bytes.</summary>
    void DefineMemoryRegion(string id, ulong? sourceAddress, int length);

    /// <summary>Publishes bytes for a declared virtual region.</summary>
    void SetMemoryRegionBytes(string id, ReadOnlyMemory<byte> bytes);

    /// <summary>Sets a runtime variable used by a mapper's deferred addresses.</summary>
    void SetRuntimeVariable(string name, ulong value);

    IEnumerable<string> PropertyNames { get; }

    object? GetPropertyValue(string path);

    void SetPropertyValue(string path, object? value);
}
