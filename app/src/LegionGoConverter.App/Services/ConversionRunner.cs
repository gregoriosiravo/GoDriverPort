using LegionGoConverter.Core;

namespace LegionGoConverter.App;

public sealed class ConversionRunner(ConversionPipeline pipeline) : IConversionRunner
{
    public Task<ConversionResult> RunAsync(ConversionRequest r, IProgress<StageProgress> p, IUserPrompt prompt, CancellationToken ct) =>
        pipeline.RunAsync(r, p, prompt, ct);
}
