using Aml.Core.Ports;

namespace Aml.Services;

/// <summary>
/// A stage reader whose calls take a timeout (the language model), with one budget for all its calls in one analysis of a
/// Case (watchlist-and-adverse-information.md#three-readers, ADR-0012). CaseService takes <see cref="ForAnalysis"/> at the start
/// of each analysis; there a call gets the timeout or what is left of the budget, whichever is less, and with less than
/// <see cref="MinCall"/> left the model is not asked: it throws <see cref="StageBudgetSpentException"/>, which the vote takes
/// as a missing answer (STAGE_READER_NO_ANSWER) and the host logs by its type name. So a slow model costs a Case its
/// answers, never its revision.
/// </summary>
/// <param name="read">One call of the model, cut off after the given time; throws when it cannot answer.</param>
public sealed class BudgetedStageReader(string name, Func<string, IReadOnlyList<string>, TimeSpan, StageAnswer?> read, TimeSpan timeout, TimeSpan budget)
    : IStageReader
{
    /// <summary>Less than this left of the budget is not worth a call.</summary>
    public static readonly TimeSpan MinCall = TimeSpan.FromSeconds(2);

    public string Name => name;

    /// <summary>Outside an analysis: one call with its own timeout, no budget.</summary>
    public StageAnswer? Read(string text, IReadOnlyList<string> names) => read(text, names, timeout);

    /// <summary>The reader for one analysis, spending one budget on the time its calls take by <paramref name="clock"/>.</summary>
    public IStageReader ForAnalysis(TimeProvider clock) => new Spending(name, read, timeout, budget, clock);

    private sealed class Spending(string name, Func<string, IReadOnlyList<string>, TimeSpan, StageAnswer?> read, TimeSpan timeout, TimeSpan budget,
        TimeProvider clock) : IStageReader
    {
        private TimeSpan _spent;

        public string Name => name;

        public StageAnswer? Read(string text, IReadOnlyList<string> names)
        {
            var left = budget - _spent;
            if (left < MinCall) throw new StageBudgetSpentException();
            var started = clock.GetTimestamp(); // monotonic: a system clock jump does not move the budget
            try
            {
                return read(text, names, left < timeout ? left : timeout);
            }
            finally
            {
                _spent += clock.GetElapsedTime(started);
            }
        }
    }
}

/// <summary>The language model was not asked: less than <see cref="BudgetedStageReader.MinCall"/> was left of this analysis's budget.</summary>
public sealed class StageBudgetSpentException() : Exception("The stage reader's budget for this analysis is spent");
