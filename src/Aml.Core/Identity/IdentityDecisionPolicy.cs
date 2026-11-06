using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Identity;

/// <summary>Stage 3 decision table (identity-adjudication.md). The first matching row wins.</summary>
public static class IdentityDecisionPolicy
{
    public static IdentityResult Decide(ScreenedSubject s, SourceProfile p, NewsItem? news = null)
    {
        var e = EvidenceBuilder.Build(s, p, news);
        // The person rules do not apply to organizations, vessels and other records; entity identifiers decide instead.
        if (!p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase))
            return EntityDecision(s, p, new Dictionary<EvidenceGroup, Evidence>(e));
        if (HardConflictGate.Check(s, p) is { } hard)
        {
            if (!HardConflictGate.Validated.Contains(hard)) return new IdentityResult(e, hard, null, IdentityDecision.Review, [hard]);
            return HardConflictGate.SourceQualityConcern(p, e)
                ? new IdentityResult(e, hard, null, IdentityDecision.Review, [hard, ReasonCodes.SourceQualityDowngrade])
                : new IdentityResult(e, hard, null, IdentityDecision.NoMatch, [hard]);
        }

        var (row, decision, code) = Stage3(e);
        var reasons = new List<string> { code };
        if (e[EvidenceGroup.Gender].Status == ComparisonStatus.Conflict) reasons.Add(ReasonCodes.GenderConflict);
        if (NoBilateralDirectIdentifier(s, p)) reasons.Add(ReasonCodes.NoBilateralDirectIdentifier);
        return new IdentityResult(e, null, row, decision, reasons);
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
        var subject = new[] { s.Document, s.TaxId is { State: ValueState.Present, Value: { } tax } ? new Document("REGISTRATION_NUMBER", tax, "TW") : null }
            .OfType<Document>().Where(d => d.Type is "REGISTRATION_NUMBER" or "IMO_NUMBER").ToList();
        var profile = (p.Documents ?? []).Where(d => d.Type is "REGISTRATION_NUMBER" or "IMO_NUMBER").ToList();
        var comparable = subject.SelectMany(a => profile.Where(b => EvidenceBuilder.Comparable(a, b)), (a, b) => (a, b)).ToList();
        if (comparable.Count == 0)
        {
            e[g] = new(g, Direction.Neutral, subject.Count == 0 ? ComparisonStatus.SubjectMissing : profile.Count == 0 ? ComparisonStatus.ProfileMissing : ComparisonStatus.Uncertain);
            return new IdentityResult(e, null, null, IdentityDecision.Review, [ReasonCodes.NonPersonSubjectReview]);
        }
        if (comparable.Any(x => EvidenceBuilder.DocumentNumber(x.a.Number) == EvidenceBuilder.DocumentNumber(x.b.Number)))
        {
            e[g] = new(g, Direction.Support, ComparisonStatus.Exact, Strength.Unique);
            return new IdentityResult(e, null, 2, IdentityDecision.ProbableMatch, [ReasonCodes.EntityIdentifierAgreement]);
        }
        e[g] = new(g, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.EntityIdentifierConflict);
        return new IdentityResult(e, null, 1, IdentityDecision.Review, [ReasonCodes.EntityIdentifierConflict]);
    }

    /// <summary>
    /// Contrastive explanation: the same document type and a full DOB, each assumed to agree and to conflict, re-run through
    /// stage 3 only (a full DOB would also reach the stage 1 checks, which are not re-run). Outcomes on the current row are
    /// left out. Empty outside rows 3 to 6 (which also leaves out hard conflicts and non-Person records) and for Profiles
    /// with scattered DOBs. On rows 3 to 6 a document is never comparable (agreeing is row 2, conflicting row 1), and a full
    /// DOB is comparable only when it agrees.
    /// </summary>
    public static IReadOnlyList<WouldChangeIf> Contrast(IdentityResult r)
    {
        if (r.Stage3Row is not (3 or 4 or 5 or 6)) return [];
        var dob = r.Evidence.GetValueOrDefault(EvidenceGroup.DateOfBirth);
        if (dob?.Detail == Findings.ProfileMultipleDob) return [];

        var assumptions = new List<(Evidence Evidence, Assumption Assumption, string Label)>
        {
            (new(EvidenceGroup.Document, Direction.Support, ComparisonStatus.Exact, Strength.Unique), Assumption.Agree, "同類證件相符"),
            (new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict), Assumption.Conflict, "同類證件不符"),
        };
        if (dob?.Status != ComparisonStatus.Exact)
        {
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Support, ComparisonStatus.Exact, Strength.High), Assumption.Agree, "完整出生日期相符"));
            assumptions.Add((new(EvidenceGroup.DateOfBirth, Direction.Conflict, ComparisonStatus.Conflict, Strength.None, ReasonCodes.FullDobConflict), Assumption.Conflict, "完整出生日期不符"));
        }

        var result = new List<WouldChangeIf>();
        foreach (var (evidence, assumption, label) in assumptions)
        {
            var e = new Dictionary<EvidenceGroup, Evidence>(r.Evidence) { [evidence.Group] = evidence };
            var (row, decision, code) = Stage3(e);
            if (row != r.Stage3Row)
                result.Add(new(evidence.Group, assumption, row, decision, code, $"若{label}：身分判斷移至第三階段第 {row} 列 {code}"));
        }
        return result;
    }

    private static (int, IdentityDecision, string) Stage3(IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus StatusOf(EvidenceGroup g) => e.GetValueOrDefault(g)?.Status ?? ComparisonStatus.ProfileMissing;
        var supports = e.Values.Where(v => v.IsSupport).ToArray();

        // Employment is never a conflict (a different employer is not evidence against identity), so it has no row 1 code.
        var conflict = new[] { EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.StatedAge }
            .Select(g => e.GetValueOrDefault(g)).FirstOrDefault(v => v?.Status == ComparisonStatus.Conflict);
        if (conflict is not null)
            return (1, IdentityDecision.Review, conflict.Detail!); // every DOB, document and stated-age conflict carries its code

        if (supports.Any(v => v.Strength == Strength.Unique))
            return (2, IdentityDecision.ProbableMatch, ReasonCodes.UniqueIdentifierAgreement);

        // Opposite genders rule out rows 3 and 4; the pair falls to row 5 or 6 with GENDER_CONFLICT.
        var genderConflict = StatusOf(EvidenceGroup.Gender) == ComparisonStatus.Conflict;
        if (!genderConflict && supports.Any(v => v.Strength == Strength.High) && supports.Length >= 2)
            return (3, IdentityDecision.ProbableMatch, ReasonCodes.StrongSupportCombination);

        var prerequisitesAgree = StatusOf(EvidenceGroup.Name) == ComparisonStatus.Exact
            && StatusOf(EvidenceGroup.Gender) == ComparisonStatus.Exact
            && StatusOf(EvidenceGroup.Nationality) == ComparisonStatus.Exact;
        var dobAgrees = e.GetValueOrDefault(EvidenceGroup.DateOfBirth)?.IsSupport == true;
        // A document of another type or country is not comparable, which is not unsettled.
        var docOrEmploymentUnsettled = new[] { EvidenceGroup.Document, EvidenceGroup.Employment }
            .Select(g => e.GetValueOrDefault(g))
            .Any(v => v?.Status is ComparisonStatus.Conflict or ComparisonStatus.Uncertain && v.Detail != EvidenceBuilder.NotComparable);
        if (prerequisitesAgree && dobAgrees && !docOrEmploymentUnsettled)
            return (4, IdentityDecision.ProbableMatch, ReasonCodes.ConsistentAtAvailablePrecision);

        return supports.Length > 0
            ? (5, IdentityDecision.Review, ReasonCodes.PartialSupportOnly)
            : (6, IdentityDecision.Review, ReasonCodes.NoNonPrerequisiteSupport);
    }

    // Direct identifier: full DOB or a document key present on both sides.
    private static bool NoBilateralDirectIdentifier(ScreenedSubject s, SourceProfile p) =>
        !(s.DateOfBirth?.Precision == DatePrecision.Day && p.DatesOfBirth.Any(d => d.Precision == DatePrecision.Day))
        && !(s.Document is { } d && (p.Documents ?? []).Any(x => EvidenceBuilder.Comparable(x, d)));
}
