namespace BaBaBooeyStrap.App.Services.Pipeline;

public interface IBootstrapPipelineStep
{
    string StepName { get; }
    Task ExecuteAsync(BootstrapPipelineContext context, CancellationToken ct = default);
}
