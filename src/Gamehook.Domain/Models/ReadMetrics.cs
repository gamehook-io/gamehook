namespace Gamehook.Domain.Models;

/// <summary>Timing breakdown for one mapper read cycle.</summary>
public sealed record ReadMetrics(
    TimeSpan Driver,
    TimeSpan PropertyTranslation,
    TimeSpan InlineCalculations,
    TimeSpan Postprocessor,
    TimeSpan Total);
