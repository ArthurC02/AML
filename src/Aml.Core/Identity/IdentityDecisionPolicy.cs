using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>Identity decision: entity records, then stage 1 hard conflicts, then the stage 3 table (<see cref="Stage3Rows"/>).</summary>
public static class IdentityDecisionPolicy
{
    private const int StrongSupportMinimumCount = 2;

    // A DOB conflict never reaches stage 3: the gate closes it (HardConflictGate.Check).
    private static readonly EvidenceGroup[] ProfileGroupsWithConflictCode = [EvidenceGroup.Document];
    private static readonly EvidenceGroup[] NewsGroupsWithConflictCode = [EvidenceGroup.StatedAge, EvidenceGroup.StatedBirthDate];

    public static IdentityResult Decide(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = EvidenceBuilder.Build(s, p, news);
        if (!IsPersonRecord(p))
            return EntityDecision(s, p, new Dictionary<EvidenceGroup, Evidence>(e));
        if (HardConflictGate.Check(s, p) is { } hard)
            return HardConflictDecision(e, p, hard);

        var (row, decision, code) = Stage3(e);
        var reasons = new List<string> { code };
        if (e[EvidenceGroup.Gender].Status == ComparisonStatus.Conflict) reasons.Add(ReasonCodes.GenderConflict);
        if (e.GetValueOrDefault(EvidenceGroup.StatedGender)?.Status == ComparisonStatus.Conflict) reasons.Add(ReasonCodes.NewsGenderDifference);
        if (row == Stage3Rows.UniqueIdentifier)
            reasons.AddRange(NewsGroupsWithConflictCode.Select(g => e.GetValueOrDefault(g)).Where(v => v?.Status == ComparisonStatus.Conflict).Select(v => v!.Detail!));
        if (NoBilateralDirectIdentifier(s, p)) reasons.Add(ReasonCodes.NoBilateralDirectIdentifier);
        return new IdentityResult(e, null, row, decision, reasons);
    }

    internal static bool IsPersonRecord(SourceProfile p) => p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase);

    private static IdentityResult HardConflictDecision(IReadOnlyDictionary<EvidenceGroup, Evidence> e, SourceProfile p, string hard)
    {
        if (!HardConflictGate.ValidatedReasonCodes.Contains(hard)) return new IdentityResult(e, hard, null, IdentityDecision.Review, [hard]);
        return HardConflictGate.SourceQualityConcern(p, e, hard)
            ? new IdentityResult(e, hard, null, IdentityDecision.Review, [hard, ReasonCodes.SourceQualityDowngrade])
            : new IdentityResult(e, hard, null, IdentityDecision.NoMatch, [hard]);
    }

    /// <summary>
    /// Entity evidence groups: a registration number with its jurisdiction, or an IMO number for a vessel. The same key on
    /// both sides is UNIQUE support (PROBABLE_MATCH, externally REVIEW); a different number under the same key is a conflict.
    /// A natural-person subject is never compared with an entity record. Nothing here is automatic MATCH or NO_MATCH.
    /// ponytail: entity groups pending validation with an entity Golden Case (system-overview.md, excluded for Phase 1).
    /// </summary>
    private static IdentityResult EntityDecision(ScreenedSubject s, SourceProfile p, Dictionary<EvidenceGroup, Evidence> e)
    {
        const EvidenceGroup g = EvidenceGroup.EntityIdentifier;
        if (!s.IsEntity) return new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        var subject = SubjectEntityIdentifiers(s);
        var profile = (p.Documents ?? []).Where(IsEntityIdentifier).ToList();
        var comparable = subject.SelectMany(a => profile.Where(b => EvidenceBuilder.AreComparable(a, b)), (a, b) => (a, b)).ToList();
        if (comparable.Count == 0)
        {
            e[g] = new(g, Direction.Neutral, subject.Count == 0 ? ComparisonStatus.SubjectMissing : profile.Count == 0 ? ComparisonStatus.ProfileMissing : ComparisonStatus.Uncertain);
            // A senior manager of the same name on both sides is partial support: a person still decides, but sooner.
            return e.GetValueOrDefault(EvidenceGroup.Relationship)?.Detail == EvidenceDetails.SameNamedSeniorManager
                ? new IdentityResult(e, null, Stage3Rows.PartialSupport, IdentityDecision.Review, [ReasonCodes.EntitySeniorManagerAgreement])
                : new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        }
        if (comparable.Any(x => EvidenceBuilder.DocumentNumber(x.a.Number) == EvidenceBuilder.DocumentNumber(x.b.Number)))
        {
            e[g] = new(g, Direction.Support, ComparisonStatus.Exact, Strength.Unique);
            return new IdentityResult(e, null, Stage3Rows.UniqueIdentifier, IdentityDecision.ProbableMatch, [ReasonCodes.EntityIdentifierAgreement]);
        }
        e[g] = new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.EntityIdentifierConflict);
        return new IdentityResult(e, null, Stage3Rows.Conflict, IdentityDecision.Review, [ReasonCodes.EntityIdentifierConflict]);
    }

    private static List<Document> SubjectEntityIdentifiers(ScreenedSubject s)
    {
        var taxRegistration = s.TaxId is { State: ValueState.Present, Value: { } tax } ? new Document("REGISTRATION_NUMBER", tax, "TW") : null;
        return new[] { s.Document, taxRegistration }.OfType<Document>().Where(IsEntityIdentifier).ToList();
    }

    private static bool IsEntityIdentifier(Document d) => d.Type is "REGISTRATION_NUMBER" or "IMO_NUMBER";

    /// <summary>
    /// Contrastive explanation: the same document type and a full DOB, each assumed to agree and to conflict, re-run through
    /// stage 3 only (a full DOB would also reach the stage 1 checks, which are not re-run). Outcomes on the current row are
    /// left out. Empty outside rows 3 to 6 (which also leaves out hard conflicts and non-Person records) and for Profiles
    /// with scattered DOBs. On rows 3 to 6 a document is never comparable (agreeing is row 2, conflicting row 1), and a full
    /// DOB is comparable only when it agrees.
    /// </summary>
    public static IReadOnlyList<WouldChangeIf> Contrast(IdentityResult r)
    {
        if (r.Stage3Row is not (Stage3Rows.StrongSupport or Stage3Rows.ConsistentAtPrecision or Stage3Rows.PartialSupport or Stage3Rows.NoSupport)) return [];
        if (r.Evidence.ContainsKey(EvidenceGroup.EntityIdentifier)) return [];
        var dob = r.Evidence.GetValueOrDefault(EvidenceGroup.DateOfBirth);
        if (dob?.Detail == Findings.ProfileMultipleDob) return [];

        var result = new List<WouldChangeIf>();
        foreach (var (evidence, assumption, label) in Assumptions(dob))
        {
            // A conflicting full DOB is the validated stage 1 rule, not a stage 3 row.
            if (evidence is { Group: EvidenceGroup.DateOfBirth, Direction: Direction.Conflict })
            {
                result.Add(new(evidence.Group, assumption, null, IdentityDecision.NoMatch, evidence.Detail!, $"若{label}：身分判斷為第一階段硬衝突 {evidence.Detail}"));
                continue;
            }
            var e = new Dictionary<EvidenceGroup, Evidence>(r.Evidence) { [evidence.Group] = evidence };
            var (row, decision, code) = Stage3(e);
            if (row != r.Stage3Row)
                result.Add(new(evidence.Group, assumption, row, decision, code, $"若{label}：身分判斷移至第三階段第 {row} 列 {code}"));
        }
        return result;
    }

    private static List<(Evidence Evidence, Assumption Assumption, string Label)> Assumptions(Evidence? dob)
    {
        var assumptions = new List<(Evidence Evidence, Assumption Assumption, string Label)>
        {
            (new(EvidenceGroup.Document, Direction.Support, ComparisonStatus.Exact, Strength.Unique), Assumption.Agree, "同類證件相符"),
            (new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict), Assumption.Conflict, "同類證件不符"),
        };
        if (dob?.Status != ComparisonStatus.Exact)
        {
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Support, ComparisonStatus.Exact, Strength.High), Assumption.Agree, "完整出生日期相符"));
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.FullDobConflict), Assumption.Conflict,
                $"完整出生日期相差逾 {EvidenceBuilder.DobToleranceYears} 年"));
        }
        return assumptions;
    }

    private static (int, IdentityDecision, string) Stage3(IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus StatusOf(EvidenceGroup g) => e.GetValueOrDefault(g)?.Status ?? ComparisonStatus.ProfileMissing;
        var supports = e.Values.Where(v => v.IsSupport).ToArray();

        // Every DOB, document, stated-age and news birth date conflict carries its code.
        Evidence? ConflictIn(EvidenceGroup[] groups) => groups.Select(g => e.GetValueOrDefault(g)).FirstOrDefault(v => v?.Status == ComparisonStatus.Conflict);
        if (ConflictIn(ProfileGroupsWithConflictCode) is { } conflict)
            return (Stage3Rows.Conflict, IdentityDecision.Review, conflict.Detail!);

        // An agreeing document outranks what news states next to a name: that conflict is recorded beside row 2.
        if (supports.Any(v => v.Strength == Strength.Unique))
            return (Stage3Rows.UniqueIdentifier, IdentityDecision.ProbableMatch, ReasonCodes.UniqueIdentifierAgreement);

        if (ConflictIn(NewsGroupsWithConflictCode) is { } stated)
            return (Stage3Rows.Conflict, IdentityDecision.Review, stated.Detail!);

        // A gender attached news states for the Profile person that differs from the customer's counts as the Profile's own.
        var oppositeGenders = StatusOf(EvidenceGroup.Gender) == ComparisonStatus.Conflict || e.GetValueOrDefault(EvidenceGroup.StatedGender)?.Status == ComparisonStatus.Conflict;
        if (!oppositeGenders && supports.Any(v => v.Strength == Strength.High) && supports.Length >= StrongSupportMinimumCount)
            return (Stage3Rows.StrongSupport, IdentityDecision.ProbableMatch, ReasonCodes.StrongSupportCombination);

        var prerequisitesAgree = StatusOf(EvidenceGroup.Name) == ComparisonStatus.Exact
            && StatusOf(EvidenceGroup.Gender) == ComparisonStatus.Exact
            && StatusOf(EvidenceGroup.Nationality) == ComparisonStatus.Exact;
        var dobAgrees = e.GetValueOrDefault(EvidenceGroup.DateOfBirth)?.IsSupport == true;
        var docOrEmploymentUnsettled = new[] { EvidenceGroup.Document, EvidenceGroup.Employment }
            .Select(g => e.GetValueOrDefault(g)).Any(IsUnsettled);
        if (prerequisitesAgree && !oppositeGenders && dobAgrees && !docOrEmploymentUnsettled)
            return (Stage3Rows.ConsistentAtPrecision, IdentityDecision.ProbableMatch, ReasonCodes.ConsistentAtAvailablePrecision);

        return supports.Length > 0
            ? (Stage3Rows.PartialSupport, IdentityDecision.Review, ReasonCodes.PartialSupportOnly)
            : (Stage3Rows.NoSupport, IdentityDecision.Review, ReasonCodes.NoNonPrerequisiteSupport);
    }

    internal static bool IsUnsettled(Evidence? v) =>
        v?.Status is ComparisonStatus.Conflict or ComparisonStatus.Uncertain && v.Detail != EvidenceBuilder.NotComparable;

    private static bool NoBilateralDirectIdentifier(ScreenedSubject s, SourceProfile p) =>
        !BothHaveFullDob(s, p) && !BothHaveComparableDocument(s, p);

    private static bool BothHaveFullDob(ScreenedSubject s, SourceProfile p) =>
        s.DateOfBirth?.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day);

    private static bool BothHaveComparableDocument(ScreenedSubject s, SourceProfile p) =>
        s.Document is { } d && (p.Documents ?? []).Any(x => EvidenceBuilder.AreComparable(x, d));
}
