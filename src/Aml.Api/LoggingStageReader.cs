using Aml.Core.Ports;

namespace Aml.Api;

/// <summary>
/// Logs a stage reader's missing answer (watchlist-and-adverse-information.md#three-readers): one warning with the reader's
/// name, the kind of failure and the elapsed milliseconds. The kind is the exception's type name: TimeoutException,
/// HttpRequestException (HTTP status or transport), InvalidDataException (an unreadable reply), StageBudgetSpentException,
/// or the classifier's own; <c>no answer</c> when a reader returned null without a reason. Never the news text, the
/// names or an exception message, which may quote the reply or the request. The answer or the exception passes unchanged.
/// </summary>
public sealed partial class LoggingStageReader(IStageReader inner, ILogger logger, TimeProvider clock) : IStageReader
{
    public string Name => inner.Name;

    public StageAnswer? Read(string text, IReadOnlyList<string> names)
    {
        var started = clock.GetTimestamp();
        try
        {
            var answer = inner.Read(text, names);
            if (answer is null) NoAnswer(logger, inner.Name, "no answer", clock.GetElapsedTime(started).TotalMilliseconds);
            return answer;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            NoAnswer(logger, inner.Name, e.GetType().Name, clock.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stage reader {Reader} gave no answer ({Failure}) after {ElapsedMs:0} ms")]
    private static partial void NoAnswer(ILogger logger, string reader, string failure, double elapsedMs);
}
