using SuperCV.Domain.AI;

namespace SuperCV.Application.Ports;

public interface IAiClient
{
    Task<string> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);

    Task<AiCompletionResult> CompleteDetailedAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AiCompletionChunk> StreamDetailedAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AiAgentStreamChunk> StreamAgentAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);
}
