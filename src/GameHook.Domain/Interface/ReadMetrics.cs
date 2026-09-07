namespace GameHook.Domain.Interface;

public sealed record ReadMetrics(TimeSpan Driver, TimeSpan PropertyTranslation, TimeSpan InlineCalculations, TimeSpan Postprocessor, TimeSpan Total);
