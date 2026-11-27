using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Txt;

namespace Aml.Tests.Synthetic;

/// <summary>
/// The real-shape synthetic set (docs/plan/poc-delivery-plan.md#real-shape-synthetic-set): the readers get every value
/// the generator wrote in a real export's layout, and the analysis reads every news item of a Profile.
/// </summary>
public class RealShapeSetTests
{
    private static readonly Lazy<IReadOnlyList<RealShapeCase>> Set = new(SyntheticCaseGenerator.RealShape);

    [Fact]
    public void The_real_shape_generator_is_deterministic_and_separate()
    {
        var cases = Set.Value;
        Assert.Equal(SyntheticCaseGenerator.Digest([.. cases.Select(c => c.Case)]), SyntheticCaseGenerator.Digest([.. SyntheticCaseGenerator.RealShape().Select(c => c.Case)]));
        Assert.Equal((44, 10, 21), (cases.Count, cases.Count(c => c.News.Count == 2), cases.Count(c => c.Case.Candidates[0].SamePerson)));
        Assert.Equal("F15748E64F39BB5B", SyntheticCaseGenerator.Digest([.. cases.Select(c => c.Case)]));
        // The plain and dated-role Cases keep their digest: the notes-event Cases come after them.
        Assert.Equal("8F2C3008CF9B0BB5", SyntheticCaseGenerator.Digest([.. cases.Take(SyntheticCaseGenerator.RealShapeCount + SyntheticCaseGenerator.DatedRoleCases.Length).Select(c => c.Case)]));
        // The first 20 have their own digest, independent of the dated-role Cases after them.
        Assert.Equal("1F854D1C0FEF1703", SyntheticCaseGenerator.Digest([.. cases.Take(SyntheticCaseGenerator.RealShapeCount).Select(c => c.Case)]));
    }

    [Fact]
    public void A_child_customer_against_a_namesake_a_dated_item_reads_as_a_sentenced_director_or_a_defendant_is_hinted_not_concluded()
    {
        var outcomes = SyntheticMeasure.Run([.. Set.Value.Select(c => c.Case)], SyntheticAssumptions.Default.AsOf);
        string KindOf(PairOutcome o) => o.Truth.Tags.FirstOrDefault(t => t.StartsWith("dated-role:", StringComparison.Ordinal) || t.StartsWith("notes-event:", StringComparison.Ordinal)) ?? "plain";
        static bool Hinted(PairOutcome o) => o.Hit!.Identity.Evidence.GetValueOrDefault(EvidenceGroup.TemporalRole)?.Detail == ReasonCodes.AgeImplausibleAtDocument;
        // Advisory: every hinted pair is REVIEW on row 1; nothing in the set is concluded NO_MATCH by it.
        Assert.All(outcomes.Where(Hinted), o => Assert.Equal((IdentityDecision.Review, 1), (o.Hit!.Identity.Decision, o.Hit.Identity.Stage3Row)));
        Assert.DoesNotContain(outcomes, o => o.Hit!.Identity.Decision == IdentityDecision.NoMatch);
        // What-if, for the later decision: the hinted pairs, which a veto would conclude NO_MATCH (all strangers here).
        var byKind = outcomes.GroupBy(KindOf).ToDictionary(g => g.Key, g => (g.Count(), g.Count(Hinted)));
        Assert.DoesNotContain(outcomes, o => o.Truth.SamePerson && Hinted(o));
        Assert.Equal(new Dictionary<string, (int, int)>
        {
            ["plain"] = (20, 0),
            ["dated-role:ChildDirector"] = (4, 4),
            ["dated-role:ChildDefendant"] = (2, 2),
            ["dated-role:ChildVictim"] = (2, 0),
            ["dated-role:ChildJudge"] = (2, 0),
            ["dated-role:AdultDirector"] = (2, 0),
            // From the Profile notes: the three strangers hinted; the same-person traps (a relative's conviction, a company's
            // founding, the review date, the person's birth, a law named by year, 30 or 15 at the event, a later event only,
            // no dates) never.
            ["notes-event:ChargedAtThree"] = (1, 1),
            ["notes-event:ActsBeforeBirth"] = (1, 1),
            ["notes-event:ConvictedAtTen"] = (1, 1),
            ["notes-event:RelativeConviction"] = (1, 0),
            ["notes-event:CompanyFounded"] = (1, 0),
            ["notes-event:ReviewDate"] = (1, 0),
            ["notes-event:PersonsBirth"] = (1, 0),
            ["notes-event:LawByYear"] = (1, 0),
            ["notes-event:ThirtyAtEvent"] = (1, 0),
            ["notes-event:FifteenAtEvent"] = (1, 0),
            ["notes-event:LaterEventOnly"] = (1, 0),
            ["notes-event:NoDates"] = (1, 0),
        }, byKind);
        Assert.DoesNotContain(outcomes, o => o.Truth.SamePerson && o.Hit!.Identity.Decision == IdentityDecision.NoMatch);
    }

    [Fact]
    public void What_if_a_notes_event_concluded_counted_and_not_enabled()
    {
        // Measurement only: the pairs a concluding version would decide NO_MATCH, right (strangers) and wrong (the same person).
        var ages = Set.Value.Select(c =>
        {
            var input = TxtCaseReader.ReadFiles(c.Case.Files);
            var t = c.Case.Candidates[0];
            return (t.SamePerson, Age: HardConflictGate.ImplausibleAgeInNotes(input.Subject, input.Profiles[t.ProfileId])?.MaxAge);
        }).ToList();
        (int Right, int Wrong) WhatIf(int under) => (ages.Count(a => !a.SamePerson && a.Age < under), ages.Count(a => a.SamePerson && a.Age < under));
        Assert.Equal((1, 0), WhatIf(0));  // not yet born at the earliest event
        Assert.Equal((3, 0), WhatIf(HardConflictGate.MinimumRoleAge)); // under 14
    }

    [Fact]
    public void Every_value_written_is_read()
    {
        foreach (var c in Set.Value)
        {
            var input = TxtCaseReader.ReadFiles(c.Case.Files);
            var s = input.Subject;
            Assert.Equal(c.Alias is null ? [] : [c.Alias], s.Aliases);
            Assert.Equal((c.Title, c.BirthPlace), (s.Title, s.BirthPlace));
            Assert.Empty(s.Findings); // no UNSUPPORTED_ATTACHMENT: every numbered news file is read

            var t = c.Case.Candidates[0];
            var p = input.Profiles[t.ProfileId];
            Assert.Equal(c.NoteLabels, p.Notes!.Parts.Select(x => x.Label!));
            Assert.Equal((false, "TW"), (p.Deceased, Assert.Single(p.ResidenceCountries!)));
            Assert.Empty(p.Findings);

            var news = input.NewsByProfileId[t.ProfileId];
            Assert.Equal(c.News.Select(n => (n.Published, n.Publisher, n.Title)), news.Select(n => (n.PublishedOn!.Value, n.Publisher!, n.Title)));
            Assert.All(news, n => Assert.DoesNotContain("UI 3.2.1", n.Text, StringComparison.Ordinal));

            Assert.Equal([("是否符合名單", c.Decision), ("理由", c.Reason)], input.ImportedDecisions.Select(d => (d.Field, d.Value)));
            Assert.All(input.ImportedDecisions, d => Assert.Equal(t.ProfileId, d.ProfileId));
        }
    }

    [Fact]
    public void The_analysis_reads_every_item()
    {
        var outcomes = SyntheticMeasure.Run([.. Set.Value.Select(c => c.Case)], SyntheticAssumptions.Default.AsOf);
        Assert.All(outcomes, o => Assert.Empty(o.FieldErrors));
        Assert.Equal((10, 10), (outcomes.Count(o => o.StageRight == true), outcomes.Count(o => o.StageRight is not null)));

        // An article that indicts and a release that convicts: the stage is the reviewer's to read, not one item's.
        var two = outcomes.Where(o => Set.Value.Single(c => c.Case == o.Case).News.Count == 2).ToList();
        Assert.All(two, o =>
        {
            var c = Assert.Single(o.Hit!.Conditions, x => x.Type == ListType.AdverseMedia);
            Assert.Equal(LegalStage.Unknown, c.LegalStage);
            Assert.Contains(SourceConditions.NewsItemsDiffer, c.ReasonCodes);
        });
        Assert.Equal(10, two.Count);

        // Nothing in the new fields concludes: no pair of the same person is NO_MATCH.
        Assert.DoesNotContain(outcomes, o => o.Truth.SamePerson && o.Hit!.Identity.Decision == IdentityDecision.NoMatch);
    }
}
