using System.ComponentModel;
using Aml.Core.Shared;

namespace Aml.Core.Identity;

public enum IdentityDecision { NoMatch, Review, ProbableMatch }

public enum Assumption { Agree, Conflict }

/// <summary>
/// What stage 3 would decide if one direct identifier became comparable on both sides. It names the row and code only,
/// never the external result, and holds every other piece of evidence fixed.
/// </summary>
public sealed record WouldChangeIf(
    [property: Description("假設的項目：DOCUMENT 或 DATE_OF_BIRTH")] EvidenceGroup Field,
    [property: Description("假設相符（AGREE）或衝突（CONFLICT）")] Assumption Assumption,
    [property: Description("假設成立時的第三階段列號")] int Stage3Row,
    [property: Description("假設成立時的身分判斷（不是對外結果）")] IdentityDecision Decision,
    [property: Description("假設成立時的理由碼")] string ReasonCode,
    [property: Description("中文說明")] string Text);

public sealed record IdentityResult(
    [property: Description("各證據群組的比對結果")] IReadOnlyDictionary<EvidenceGroup, Evidence> Evidence,
    [property: Description("第一階段硬衝突代碼")] string? HardConflict,
    [property: Description("第三階段決策表列號（1～6）；硬衝突或非自然人規則時可能為 null")] int? Stage3Row,
    [property: Description("身分判斷")] IdentityDecision Decision,
    [property: Description("身分判斷的理由碼，第一個是決定的理由")] IReadOnlyList<string> ReasonCodes);
