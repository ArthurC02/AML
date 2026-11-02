namespace Aml.Core.Disposition;

public enum HumanDecision { Match, NoMatch, Unresolved }

public enum FinalDisposition { OnList, NotOnList }

public enum ApprovalRole { Approver, SeniorApprover }

/// <summary>
/// Final disposition is binary. UNRESOLVED identity (Article 22 ③) needs senior approval, and the senior approver chooses.
/// ponytail: Article 22 ① and ② recommendations land with GC-09/GC-10 (Gate C).
/// </summary>
public static class DispositionPolicy
{
    public static IReadOnlyList<ApprovalRole> RequiredApprovals(HumanDecision d) =>
        d == HumanDecision.Unresolved ? [ApprovalRole.Approver, ApprovalRole.SeniorApprover] : [ApprovalRole.Approver];

    public static FinalDisposition Decide(HumanDecision decision, IReadOnlyCollection<ApprovalRole> approvals, FinalDisposition? seniorChoice = null)
    {
        var missing = RequiredApprovals(decision).Except(approvals).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Missing approval: {string.Join(", ", missing)}");
        return decision switch
        {
            HumanDecision.Match => FinalDisposition.OnList,
            HumanDecision.NoMatch => FinalDisposition.NotOnList,
            _ => seniorChoice ?? throw new InvalidOperationException("UNRESOLVED needs the senior approver's choice"),
        };
    }
}
