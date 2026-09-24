using Gamehook.Domain.Interface;
using Gamehook.Domain.Models;

namespace Gamehook.Domain.Mapping;

using Gamehook.Domain.Property;

/// Everything a Mapper runs from, produced by loading a mapper file (Infrastructure's
/// MapperCompiler). Expressions in ExpressionBindings were compiled into ScriptEngine, and
/// ScriptSource (the companion .js, if any) is loaded into it, so the three travel together.
public sealed record MapperDefinition(
    string MapperPath,
    string? Id,
    string GameName,
    GameSystem System,
    string? NativeProcessorId,
    ExpressionEngine ScriptEngine,
    string? ScriptSource,
    IReadOnlyDictionary<string, ReferenceTable> References,
    IReadOnlyList<IDriver.MemorySegmentRequest> Requests,
    IReadOnlyList<Property> Properties,
    // Addresses containing {{token}}s, re-resolved every read once the preprocessor has set them.
    IReadOnlyList<(Property Property, DeferredAddress Address)> DynamicAddressProperties,
    // Distinct script-set variable names every DeferredAddress indexes into, so a read resolves
    // each one once instead of once per property that mentions it.
    IReadOnlyList<string> RuntimeTokenNames,
    IReadOnlyList<(Property Property, Func<double, double> Expression)> ExpressionBindings);
