using Aml.Api;
using Aml.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Aml.Tests.Unit.Screening;

/// <summary>A stage reader's missing answer is logged for operations, without the text or names it was given.</summary>
public class LoggingStageReaderTests
{
    private const string News = "臺北地檢署今日依詐欺罪嫌起訴陳志明。";

    private sealed class Capturing : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }

    // Takes 1.5 s, then gives no answer or throws an exception whose message quotes the request (as a failing client's may).
    private sealed class Failing(ManualTimeProvider clock, bool throws) : IStageReader
    {
        public string Name => "llm";

        public StageAnswer? Read(string text, IReadOnlyList<string> names)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return throws ? throw new InvalidOperationException("request was: " + text + " " + string.Join(",", names)) : null;
        }
    }

    [Theory]
    [InlineData(false, "no answer")]
    [InlineData(true, "InvalidOperationException")]
    public void A_missing_answer_is_one_warning_with_the_reader_the_failure_and_the_time(bool throws, string failure)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var log = new Capturing();
        var reader = new LoggingStageReader(new Failing(clock, throws), log, clock);

        if (throws) Assert.Contains("陳志明", Assert.Throws<InvalidOperationException>(() => reader.Read(News, ["陳志明"])).Message, StringComparison.Ordinal);
        else Assert.Null(reader.Read(News, ["陳志明"]));

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal((LogLevel.Warning, $"Stage reader llm gave no answer ({failure}) after 1500 ms"), (level, message));
        Assert.DoesNotContain("陳志明", message, StringComparison.Ordinal);
        Assert.Equal("llm", reader.Name);
    }
}
