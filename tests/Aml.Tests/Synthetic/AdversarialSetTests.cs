using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Pages;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Tests.Synthetic;

/// <summary>
/// The document rules under measurement: B (number agrees), B′ (and type and a known, equal issuing country), B′ with both
/// countries absent allowed; B″ = B′ and a list birth date agreeing with the customer's, exactly at the list's precision
/// (a list 1 January counts as its year only) or with the product's two-year tolerance.
/// </summary>
public enum DocumentRule { B, BPrime, BPrimeOrNoCountry, BDoublePrimeExact, BDoublePrimeTolerance }

/// <summary>
/// Candidate automatic MATCH conditions B and B′ (scoring-rule.md#candidate-match-condition-b), measurement only: no
/// production caller. A pair would MATCH when a list document agrees with the customer's under the rule, the name
/// prerequisite holds (EXACT after the product's folding, or the same reading across romanisations), and nothing stands
/// against it: no hard conflict or downgrade, no evidence group in conflict (birth date, gender, nationality, stated values,
/// document), a Person record, no MODEL_READ person.
/// </summary>
public static class DocumentMatch
{
    public static bool Matches(DocumentRule rule, ScreenedSubject subject, HitResult hit) =>
        subject.Document is { } doc && (hit.Profile.Documents ?? []).Any(d => Agrees(rule, doc, d)) && NameHolds(hit.Identity) && NothingAgainst(hit)
        && rule switch
        {
            DocumentRule.BDoublePrimeExact => AgreesExactly(subject.DateOfBirth, hit.Profile.DatesOfBirth),
            DocumentRule.BDoublePrimeTolerance => hit.Identity.Evidence[EvidenceGroup.DateOfBirth].IsSupport,
            _ => true,
        };

    private static bool Agrees(DocumentRule rule, Document s, Document p) =>
        EvidenceBuilder.DocumentNumber(s.Number) == EvidenceBuilder.DocumentNumber(p.Number) && rule switch
        {
            DocumentRule.B => true,
            DocumentRule.BPrimeOrNoCountry => s.Type == p.Type && s.Country == p.Country,
            _ => s.Type == p.Type && s.Country is not null && s.Country == p.Country,
        };

    /// <summary>A list birth date equal to the customer's at the list's own precision, no tolerance; a list 1 January (a likely placeholder) is read as its year only.</summary>
    public static bool AgreesExactly(PartialDate? customer, IReadOnlyList<PartialDate> list) =>
        customer is { } c && list.Any(d => (d is { Precision: DatePrecision.Day, Month: 1, Day: 1 } ? new PartialDate(d.Year) : d) is var x
            && x.Year == c.Year && (x.Month is null || x.Month == c.Month) && (x.Day is null || x.Day == c.Day));

    private static bool NameHolds(IdentityResult id) =>
        id.Evidence[EvidenceGroup.Name] is { Status: ComparisonStatus.Exact } or { Status: ComparisonStatus.Partial, Detail: Findings.RomanizationVariant };

    private static bool NothingAgainst(HitResult hit) =>
        hit.Identity.HardConflict is null && !hit.Identity.ReasonCodes.Contains(ReasonCodes.SourceQualityDowngrade)
        && IdentityDecisionPolicy.IsPersonRecord(hit.Profile) && !hit.Profile.Findings.Contains(PageCandidates.ModelRead)
        && !NativeNames.In(hit.Identity.Evidence[EvidenceGroup.Name])
        && hit.Identity.Evidence.Values.All(v => v.Status != ComparisonStatus.Conflict && v.Direction != Direction.Conflict);
}

/// <summary>
/// The adversarial synthetic set (docs/plan/poc-delivery-plan.md#adversarial-synthetic-set) through the readers and
/// CaseAnalyzer: what the product decides today per kind, and what B, B′ and B″ would conclude, here and on the end-to-end
/// set. Expected values follow from how each kind is built and the stated rules. Measurement only.
/// </summary>
public class AdversarialSetTests
{
    private sealed record Row(string Kind, bool Same, PairOutcome Outcome, ScreenedSubject Subject)
    {
        public bool Realistic => SyntheticCaseGenerator.AdversarialKinds.FirstOrDefault(k => k.Code == Kind)?.Realistic ?? true;

        public bool Matches(DocumentRule rule) => DocumentMatch.Matches(rule, Subject, Outcome.Hit!);
    }

    private static readonly DocumentRule[] AllRules = Enum.GetValues<DocumentRule>();

    private static readonly Lazy<IReadOnlyList<SyntheticCase>> Cases = new(SyntheticCaseGenerator.Adversarial);

    private static readonly Lazy<List<Row>> Rows = new(() =>
        [.. SyntheticMeasure.Run(Cases.Value, SyntheticAssumptions.Default.AsOf).Select(o => ToRow(o, o.Case.Candidates[0].Tags[0][4..]))]);

    private static Row ToRow(PairOutcome o, string kind) => new(kind, o.Truth.SamePerson, o, TxtCaseReader.ReadFiles(o.Case.Files).Subject);

    private static string Code(IdentityResult id) => id.HardConflict is { } h
        ? h + (id.ReasonCodes.Contains(ReasonCodes.SourceQualityDowngrade) ? "+" + ReasonCodes.SourceQualityDowngrade : "")
        : id.ReasonCodes[0];

    [Fact]
    public void The_adversarial_generator_is_deterministic_and_separate()
    {
        var cases = Cases.Value;
        Assert.Equal(SyntheticCaseGenerator.Digest(cases), SyntheticCaseGenerator.Digest(SyntheticCaseGenerator.Adversarial()));
        Assert.Equal("3E9B9F549814FA45", SyntheticCaseGenerator.Digest(cases));
        Assert.Equal((335, 115), (cases.Count, cases.Count(c => c.Candidates[0].SamePerson)));
        Assert.Equal(SyntheticCaseGenerator.AdversarialKinds.ToDictionary(k => k.Code, k => k.Count),
            cases.GroupBy(c => c.Candidates[0].Tags[0][4..]).ToDictionary(g => g.Key, g => g.Count()));
        // Realistic 260 (115 customers, 145 strangers), theoretical limit 75 (strangers only).
        Assert.Equal((260, 115, 75), (Rows.Value.Count(r => r.Realistic), Rows.Value.Count(r => r.Realistic && r.Same), Rows.Value.Count(r => !r.Realistic && !r.Same)));
    }

    /// <summary>The national IDs are of valid form: every customer's, every stranger's other ID; a customer's typo fails the check digit.</summary>
    [Fact]
    public void National_ids_are_format_valid()
    {
        var rows = Rows.Value;
        var customerIds = rows.Select(r => r.Subject.Document).Where(d => d?.Type == "TW_NATIONAL_ID").Select(d => d!.Number).ToList();
        Assert.Equal((240, 240), (customerIds.Count, customerIds.Count(SyntheticCaseGenerator.IsValidNationalId)));
        string ListId(Row r) => r.Outcome.Hit!.Profile.Documents!.Single().Number;
        var others = rows.Where(r => r.Kind is "NUMBER_ONE_OFF" or "NUMBER_TRANSPOSED").ToList();
        Assert.Equal((30, 30), (others.Count(r => SyntheticCaseGenerator.IsValidNationalId(ListId(r))), others.Count(r => ListId(r) != r.Subject.Document!.Number)));
        Assert.Equal(0, rows.Where(r => r.Kind == "C_NUMBER_TYPO").Count(r => SyntheticCaseGenerator.IsValidNationalId(ListId(r))));
        Assert.True(SyntheticCaseGenerator.IsValidNationalId("A123456789"));
    }

    /// <summary>Every pair is read with every checked field right and found as one hit.</summary>
    [Fact]
    public void Every_pair_is_read_and_found()
    {
        var o = Rows.Value.Select(r => r.Outcome).ToList();
        Assert.Equal((335, 335), (o.Count(x => x.Hit is not null), o.Count(x => x.FieldErrors.Count == 0)));
        Assert.Equal(335, o.Count(x => x.ConditionRight));
    }

    /// <summary>What the product decides today, per kind: identity decision and its first code (hard conflict, downgrade, or the stage 3 row's code).</summary>
    [Fact]
    public void Product_today_per_kind()
    {
        const string Consistent = ReasonCodes.ConsistentAtAvailablePrecision, Unique = ReasonCodes.UniqueIdentifierAgreement,
            None = ReasonCodes.NoNonPrerequisiteSupport, Partial = ReasonCodes.PartialSupportOnly, DocConflict = ReasonCodes.DocumentIdentityConflict,
            Downgraded = ReasonCodes.FullDobConflict + "+" + ReasonCodes.SourceQualityDowngrade;
        var (p, r, n) = (IdentityDecision.ProbableMatch, IdentityDecision.Review, IdentityDecision.NoMatch);
        Assert.Equal(new Dictionary<(string, IdentityDecision, string), int>
        {
            [("SAME_NAME_FULL_DOB", p, Consistent)] = 15,
            [("SAME_NAME_BIRTH_YEAR", p, Consistent)] = 15,
            [("JAN1_LIST_SIDE", p, Consistent)] = 15,
            [("JAN1_BOTH_SIDES", p, Consistent)] = 15,
            [("NUMBER_OTHER_TYPE", r, None)] = 15,
            [("NUMBER_OTHER_COUNTRY", r, None)] = 15,
            [("NUMBER_ONE_OFF", r, DocConflict)] = 15,
            [("NUMBER_TRANSPOSED", r, DocConflict)] = 15,
            [("RECYCLED_WITH_DOB_GENDER", r, Downgraded)] = 10,
            [("RECYCLED_BARE", p, Unique)] = 10,
            [("RECYCLED_SAME_YEAR", p, Unique)] = 10,
            [("RELATIVE_FAR", n, ReasonCodes.FullDobConflict)] = 10,
            [("RELATIVE_NEAR", r, Partial)] = 10,
            [("TWIN", r, Partial)] = 15,
            [("SAME_NAME_DOB_ADDRESS", p, Consistent)] = 15,
            [("SAME_NAME_ADDRESS", r, None)] = 10,
            [("NUMBER_NO_COUNTRY", r, None)] = 10,
            [("C_NUMBER_TYPO", r, DocConflict)] = 15,
            [("C_NAME_FORM", p, Unique)] = 15,
            [("C_NO_LIST_DOB", p, Unique)] = 15,
            [("C_LIST_DOB_YEAR", p, Unique)] = 15,
            [("C_PASSPORT_RENEWED", r, DocConflict)] = 15,
            [("C_DOB_WITHIN_TWO", p, Unique)] = 15,
            [("C_DOB_BEYOND_TWO", r, Downgraded)] = 15,
            [("C_NUMBER_NO_COUNTRY", p, Consistent)] = 10,
        }, Rows.Value.GroupBy(x => (x.Kind, x.Outcome.Hit!.Identity.Decision, Code(x.Outcome.Hit.Identity))).ToDictionary(g => g.Key, g => g.Count()));

        // No wrong conclusion today: the customer is never NO_MATCH; the only NO_MATCH is the relatives born 3+ years apart.
        var hits = Rows.Value.Select(x => x.Outcome).ToList();
        Assert.Equal((0, 10), (hits.Count(x => x.Truth.SamePerson && x.Hit!.Identity.Decision == n), hits.Count(x => x.Hit!.PairResult == ScreeningAssessment.NoHit)));
        // PROBABLE_MATCH (REVIEW HIGH outside) for strangers: realistic, same name with an agreeing or tolerated birth date (60);
        // theoretical, same name, birth date and address (15) and the recycled passport bare (10) or with the same birth year (10).
        Assert.Equal((60, 35), (Rows.Value.Count(x => x.Realistic && !x.Same && x.Outcome.Hit!.Identity.Decision == p),
            Rows.Value.Count(x => !x.Realistic && x.Outcome.Hit!.Identity.Decision == p)));
        // The name forms: Wade-Giles alone reads EXACT, Pinyin alone the same reading, simplified alone EXACT with
        // VARIANT_CHARACTERS, except 1 of 5: 杰 for 傑 is not in the folded variant list, so that name is only similar (PARTIAL).
        Assert.Equal(new Dictionary<(string, ComparisonStatus, bool), int>
        {
            [("name:wade-giles-only", ComparisonStatus.Exact, false)] = 5,
            [("name:pinyin-only", ComparisonStatus.Partial, false)] = 5,
            [("name:simplified-only", ComparisonStatus.Exact, true)] = 4,
            [("name:simplified-only", ComparisonStatus.Partial, false)] = 1,
        }, hits.Where(x => x.Truth.Tags[0] == "adv:C_NAME_FORM").Select(x => (x.Truth.Tags[1], x.Hit!.Identity.Evidence[EvidenceGroup.Name]))
            .GroupBy(x => (x.Item1, x.Item2.Status, x.Item2.Detail == Findings.VariantCharacters)).ToDictionary(g => g.Key, g => g.Count()));
        Assert.Equal(5, hits.Count(x => x.Truth.Tags.Contains("name:pinyin-only") && x.Hit!.Identity.Evidence[EvidenceGroup.Name].Detail == Findings.RomanizationVariant));
        // Not a vacuous probe: the Wade-Giles list form is written otherwise than the bank's English name (syllables apart, not hyphenated).
        Assert.Equal(5, hits.Count(x => x.Truth.Tags.Contains("name:wade-giles-only") && !x.Case.Subject.Names.Contains(x.Truth.Fields.Names[0], StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// MATCH per kind under B, B′, B′∅, B″-exact, B″-tol (all wrong for a stranger kind, all right for a customer kind); kinds
    /// where every rule gives 0 are left out.
    /// </summary>
    [Fact]
    public void Rules_B_B_prime_and_B_double_prime_per_kind()
    {
        Assert.Equal(new Dictionary<string, int[]>
        {
            ["NUMBER_OTHER_TYPE"] = [15, 0, 0, 0, 0],
            ["NUMBER_OTHER_COUNTRY"] = [15, 0, 0, 0, 0],
            ["RECYCLED_BARE"] = [10, 10, 10, 0, 0],
            ["RECYCLED_SAME_YEAR"] = [10, 10, 10, 10, 10],
            ["NUMBER_NO_COUNTRY"] = [10, 0, 10, 0, 0],
            ["C_NAME_FORM"] = [14, 14, 14, 14, 14], // the 杰 name fails the name prerequisite
            ["C_NO_LIST_DOB"] = [15, 15, 15, 0, 0],
            ["C_LIST_DOB_YEAR"] = [15, 15, 15, 15, 15],
            ["C_DOB_WITHIN_TWO"] = [15, 15, 15, 0, 15],
            ["C_NUMBER_NO_COUNTRY"] = [10, 0, 10, 0, 0],
        }, Rows.Value.GroupBy(x => x.Kind).Select(g => (g.Key, Counts: AllRules.Select(rule => g.Count(r => r.Matches(rule))).ToArray()))
            .Where(x => x.Counts.Any(n => n > 0)).ToDictionary(x => x.Key, x => x.Counts));
    }

    private static (int Right, int Wrong) Totals(List<Row> rows, DocumentRule rule) => (rows.Count(r => r.Same && r.Matches(rule)), rows.Count(r => !r.Same && r.Matches(rule)));

    // Pairs a rule would conclude that the product leaves undecided today (its decided pairs are HIT, NO_HIT or NOT_APPLICABLE).
    private static int Added(List<Row> rows, DocumentRule rule) => rows.Count(r => r.Matches(rule) && !SyntheticMeasure.IsAutomatic(r.Outcome.Hit!.PairResult));

    /// <summary>
    /// MATCH right / wrong per rule, and the pairs each adds to today's decided ones, on the realistic group and on the
    /// end-to-end set (seed 20261126). The theoretical-limit group is reported apart, as what the rules cannot see: its
    /// MATCHes are never counted into a wrong-MATCH rate.
    /// </summary>
    [Fact]
    public void Totals_on_both_sets()
    {
        var realistic = Rows.Value.Where(r => r.Realistic).ToList();
        // The 10 wrong under B and B′∅: another holder of the same passport number, no issuing country on either side; why a
        // missing country must not count as agreement.
        Assert.Equal(new (int, int)[] { (69, 10), (59, 0), (69, 10), (29, 0), (44, 0) }, AllRules.Select(rule => Totals(realistic, rule)));
        Assert.Equal((10, "79 59 79 29 44"), (realistic.Count(r => SyntheticMeasure.IsAutomatic(r.Outcome.Hit!.PairResult)), string.Join(' ', AllRules.Select(rule => Added(realistic, rule)))));
        var limit = Rows.Value.Where(r => !r.Realistic).ToList();
        Assert.Equal(new (int, int)[] { (0, 50), (0, 20), (0, 20), (0, 10), (0, 10) }, AllRules.Select(rule => Totals(limit, rule)));

        // The end-to-end set: no stranger's number ever equals the customer's, and every list issues its documents with a
        // country, so B, B′ and B′∅ agree; they conclude exactly the customers whose list record holds their own document, and
        // B″ those of them whose list birth date also agrees (exactly at its precision, or within two years).
        var existing = SyntheticSetTests.Set.Value.Outcomes.Where(o => o.Hit is not null).Select(o => ToRow(o, o.Truth.Kind.ToString())).ToList();
        var own = existing.Where(r => r.Same && r.Outcome.Case.Subject.DocumentNumber is { } n && r.Outcome.Truth.Fields.DocumentNumbers.Contains(n)).ToList();
        Assert.Equal(new (int, int)[] { (7, 0), (7, 0), (7, 0), (5, 0), (5, 0) }, AllRules.Select(rule => Totals(existing, rule)));
        Assert.Equal((7, 5, 5), (own.Count, own.Count(r => DocumentMatch.AgreesExactly(r.Outcome.Case.Subject.Dob, r.Outcome.Truth.Fields.Dobs)),
            own.Count(r => r.Outcome.Case.Subject.Dob is { } d && r.Outcome.Truth.Fields.Dobs.Any(x => !d.IsMoreThanYearsApart(x, EvidenceBuilder.DobToleranceYears)))));
        Assert.Equal((118, "7 7 7 5 5"), (existing.Count(r => SyntheticMeasure.IsAutomatic(r.Outcome.Hit!.PairResult)), string.Join(' ', AllRules.Select(rule => Added(existing, rule)))));
    }
}
