using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;

namespace SuperCV;

/// <summary>
/// Applies bounded, cancellation-aware recovery to idempotent AI requests.
/// A request gets at most three attempts within one three-minute user-visible budget;
/// an individual attempt is aligned with the AI transport's 90-second request limit.
/// </summary>
internal static class AiRequestRetryPolicy
{
    internal const int MaximumAutomaticRetryCount = 2;

    private const int MaximumAttemptCount = MaximumAutomaticRetryCount + 1;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(90);

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var overallCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCancellation.CancelAfter(OverallTimeout);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? lastTransientFailure = null;

        for (int attempt = 1; attempt <= MaximumAttemptCount; attempt++)
        {
            TimeSpan remaining = OverallTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateOverallTimeoutException(lastTransientFailure);
            }

            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                overallCancellation.Token);
            attemptCancellation.CancelAfter(remaining < AttemptTimeout ? remaining : AttemptTimeout);

            try
            {
                return await operation(attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastTransientFailure = new TimeoutException("AI 请求在限定时间内未完成。", exception);
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception))
            {
                lastTransientFailure = exception;
            }

            if (attempt == MaximumAttemptCount)
            {
                ExceptionDispatchInfo.Capture(lastTransientFailure!).Throw();
            }

            TimeSpan retryDelay = GetRetryDelay(attempt);
            remaining = OverallTimeout - stopwatch.Elapsed;
            if (remaining <= retryDelay)
            {
                throw CreateOverallTimeoutException(lastTransientFailure);
            }

            try
            {
                await Task.Delay(retryDelay, overallCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw CreateOverallTimeoutException(lastTransientFailure);
            }
        }

        throw new InvalidOperationException("AI 重试流程意外结束。");
    }

    internal static bool IsTransientConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                case IOException:
                case OperationCanceledException:
                    return true;
                case HttpRequestException { StatusCode: null }:
                    return true;
                case HttpRequestException { StatusCode: { } statusCode } when
                    (int)statusCode is 408 or 429 or >= 500:
                    return true;
            }
        }

        return false;
    }

    internal static TimeSpan GetRetryDelay(int retryCount) => retryCount switch
    {
        1 => TimeSpan.FromMilliseconds(800),
        _ => TimeSpan.FromSeconds(2),
    };

    internal static string DescribeFailure(Exception exception, string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return IsTransientConnectionFailure(exception)
            ? $"{operationName}因网络波动或服务繁忙未完成，已自动重试 {MaximumAutomaticRetryCount} 次，请稍后再试。"
            : $"{operationName}失败：{exception.GetBaseException().Message}";
    }

    private static TimeoutException CreateOverallTimeoutException(Exception? innerException) =>
        new("AI 请求在三分钟内未能完成。", innerException);
}
