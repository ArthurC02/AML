using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Retrieval;

/// <summary>One merge decision: the Profile pair, its basis, the document key behind it and, when refused, why.</summary>
public sealed record MergeEdge(string A, string B, string Basis, string? DocumentKey = null, string? Conflict = null);

/// <summary>A set of Profiles describing the same person. Each keeps its own ID and sources; every merge edge is kept.</summary>
public sealed record MergedProfile(IReadOnlyList<string> ProfileIds, IReadOnlyList<string> ProfileKeys, IReadOnlyList<MergeEdge> Edges, IReadOnlyList<string> Findings)
{
    public IReadOnlyList<string> Basis => Edges.Select(e => e.Basis).Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>Merged groups, and the merges refused because they would put conflicting Profiles in one group.</summary>
public sealed record MergeResult(IReadOnlyList<MergedProfile> Groups, IReadOnlyList<MergeEdge> Rejected);

/// <summary>
/// Merges Profiles across IDs or sources when the list database is built, never per Case. A merge needs a shared direct
/// identifier (comparable document key) or a recorded source link; name similarity alone never merges. A merge that would
/// put Profiles with contradicting DOBs or opposite genders in one group is refused and recorded.
/// </summary>
public static class ProfileMerge
{
    public const string SharedDocument = "SHARED_DOCUMENT_KEY";
    public const string SourceLink = "RECORDED_SOURCE_LINK";
    public const string OversizedGroup = "OVERSIZED_GROUP";

    // ponytail: group size and number length limits are judgement calls; revisit with real list volumes.
    private const int MaxGroupSize = 10;
    private const int MinDocumentLength = 4;

    // ponytail: relation labels that record "this is the same person" in the exports seen so far; extend with new sources.
    private static readonly HashSet<string> SameAs = new(StringComparer.OrdinalIgnoreCase) { "Same As", "Duplicate", "Duplicate Of", "Alias Profile", "同一人" };

    public static IReadOnlyList<MergedProfile> Group(IEnumerable<SourceProfile> profiles) => Resolve(profiles).Groups;

    public static MergeResult Resolve(IEnumerable<SourceProfile> profiles)
    {
        var list = profiles.ToList();
        var byKey = list.ToDictionary(p => p.ProfileKey, StringComparer.Ordinal);
        var parent = list.ToDictionary(p => p.ProfileKey, p => p.ProfileKey, StringComparer.Ordinal);
        var members = list.ToDictionary(p => p.ProfileKey, p => new List<string> { p.ProfileKey }, StringComparer.Ordinal);
        var (edges, rejected) = (new List<MergeEdge>(), new List<MergeEdge>());

        string Find(string k)
        {
            var root = k;
            while (parent[root] != root) root = parent[root];
            while (parent[k] != root)
            {
                var next = parent[k];
                parent[k] = root;
                k = next;
            }
            return root;
        }

        // Union by size; refused when any member of one group conflicts with any member of the other.
        void Union(string a, string b, string basis, string? documentKey = null)
        {
            if (a == b || !parent.ContainsKey(b)) return;
            var (ra, rb) = (Find(a), Find(b));
            var edge = new MergeEdge(a, b, basis, documentKey);
            if (ra != rb)
            {
                var conflict = members[ra].SelectMany(x => members[rb].Select(y => Conflict(byKey[x], byKey[y]) is { } c ? $"{c}({x},{y})" : null))
                    .FirstOrDefault(c => c is not null);
                if (conflict is not null)
                {
                    rejected.Add(edge with { Conflict = conflict });
                    return;
                }
                if (members[ra].Count < members[rb].Count) (ra, rb) = (rb, ra);
                parent[rb] = ra;
                members[ra].AddRange(members[rb]);
                members.Remove(rb);
            }
            edges.Add(edge);
        }

        foreach (var g in list.SelectMany(p => (p.Documents ?? []).Select(d => (Key: DocumentKey(d), p.ProfileKey)))
                     .Where(x => x.Key is not null).Distinct().GroupBy(x => x.Key!))
            foreach (var other in g.Skip(1)) Union(g.First().ProfileKey, other.ProfileKey, SharedDocument, g.Key);
        foreach (var p in list)
            foreach (var r in p.Relationships.Where(r => SameAs.Contains(r.Relation.Trim())))
                Union(p.ProfileKey, ProfileKeys.KeyOf(r.ProfileId), SourceLink);

        var groups = members.Values.Where(m => m.Count > 1)
            .Select(m => m.Order(StringComparer.Ordinal).ToList())
            .OrderBy(m => m[0], StringComparer.Ordinal)
            .Select(m => new MergedProfile(m.Select(ProfileKeys.IdOf).ToList(), m, edges.Where(e => m.Contains(e.A)).ToList(), m.Count > MaxGroupSize ? [OversizedGroup] : []))
            .ToList();
        return new MergeResult(groups, rejected);
    }

    /// <summary>
    /// Comparable document key, or null when the number is blank, a placeholder, shorter than four characters or one
    /// repeated character, or the type or issuing country is unknown.
    /// </summary>
    private static string? DocumentKey(Document d)
    {
        var (type, country) = (Normalize.NullIfPlaceholder(d.Type), Normalize.NullIfPlaceholder(d.Country));
        if (type is null || country is null || Normalize.IsPlaceholder(d.Number)) return null;
        var number = EvidenceBuilder.DocumentNumber(d.Number);
        return number.Length < MinDocumentLength || number.Distinct().Count() == 1 ? null : $"{type}|{country}|{number}";
    }

    private static string? Conflict(SourceProfile x, SourceProfile y) =>
        x.Gender is { } g && y.Gender is { } h && g != h ? ReasonCodes.GenderConflict
        : x.DatesOfBirth.Count > 0 && y.DatesOfBirth.Count > 0 && !x.DatesOfBirth.Any(d => y.DatesOfBirth.Any(d.AgreesWith)) ? ReasonCodes.DobConflict
        : null;
}
