using Aml.Core.Retrieval;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Retrieval;

/// <summary>Candidate retrieval routes and fusion, Recall@10 on a labelled set, and Profile merging.</summary>
public class ProfileIndexTests
{
    private static SourceProfile Profile(string key, string[] names, Gender? gender = Gender.Male, IReadOnlyList<Document>? docs = null,
        IReadOnlyList<ProfileRelationship>? links = null, IReadOnlyList<PartialDate>? dobs = null) =>
        new("DJ:" + key, gender, names, dobs ?? [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], links ?? [], null, [], [], docs);

    private static readonly Document Id = new("TW_NATIONAL_ID", "A123456789", "TW");

    // ---- merging ----

    [Fact]
    public void Placeholder_short_repeated_or_unqualified_document_numbers_never_merge()
    {
        foreach (var doc in new Document[]
                 {
                     new("PASSPORT", "-", "TW"), new("PASSPORT", "－", "TW"), new("PASSPORT", "", "TW"), new("PASSPORT", "A12", "TW"),
                     new("PASSPORT", "00000000", "TW"), new("PASSPORT", "1234567", null), new(null, "1234567", "US"),
                 })
            Assert.Empty(ProfileMerge.Group([Profile("1", ["甲"], docs: [doc]), Profile("2", ["乙"], docs: [doc]), Profile("3", ["丙"], docs: [doc])]));
    }

    [Fact]
    public void A_conflicting_member_reached_through_another_profile_is_refused_and_recorded()
    {
        var result = ProfileMerge.Resolve(
        [
            Profile("A", ["王建宏"], Gender.Male, docs: [Id]),
            Profile("B", ["Wang Chien-Hung"], null, docs: [Id], links: [new ProfileRelationship("C", "王建宏", "Same As")]),
            Profile("C", ["王建紅"], Gender.Female),
        ]);
        var group = Assert.Single(result.Groups);
        Assert.Equal(["DJ:A", "DJ:B"], group.ProfileKeys); // C stays out: A and C have opposite genders
        var edge = Assert.Single(group.Edges);
        Assert.Equal(new MergeEdge("DJ:A", "DJ:B", ProfileMerge.SharedDocument, "TW_NATIONAL_ID|TW|A123456789"), edge);
        var refused = Assert.Single(result.Rejected);
        Assert.Equal(("DJ:B", "DJ:C", ProfileMerge.SourceLink), (refused.A, refused.B, refused.Basis));
        Assert.Equal($"{ProfileMerge.GenderConflict}(DJ:A,DJ:C)", refused.Conflict);
    }

    [Fact]
    public void Contradicting_dobs_refuse_a_merge_while_a_coarser_agreeing_dob_does_not()
    {
        var refused = ProfileMerge.Resolve([Profile("1", ["甲"], docs: [Id], dobs: [new(1970)]), Profile("2", ["乙"], docs: [Id], dobs: [new(1980, 1, 2)])]);
        Assert.Empty(refused.Groups);
        Assert.StartsWith(ProfileMerge.DobConflict, Assert.Single(refused.Rejected).Conflict);

        var merged = ProfileMerge.Group([Profile("1", ["甲"], docs: [Id], dobs: [new(1970)]), Profile("2", ["乙"], docs: [Id], dobs: [new(1970, 5, 1)])]);
        Assert.Equal(["DJ:1", "DJ:2"], Assert.Single(merged).ProfileKeys);
        Assert.Equal(["1", "2"], merged[0].ProfileIds);
    }

    [Fact]
    public void A_group_over_ten_members_is_flagged_and_self_links_record_no_basis()
    {
        Assert.Empty(ProfileMerge.Group([Profile("1", ["甲"], links: [new ProfileRelationship("1", "甲", "Same As")])]));

        var ten = Assert.Single(ProfileMerge.Group(Enumerable.Range(1, 10).Select(i => Profile($"{i:D2}", ["甲"], docs: [Id]))));
        Assert.Empty(ten.Findings);
        Assert.Equal(9, ten.Edges.Count); // one per merge, none for a Profile with itself
        var eleven = Assert.Single(ProfileMerge.Group(Enumerable.Range(1, 11).Select(i => Profile($"{i:D2}", ["甲"], docs: [Id]))));
        Assert.Equal([ProfileMerge.OversizedGroup], eleven.Findings);
    }

    // ---- tiers and fusion ----

    [Fact]
    public void A_variant_found_only_by_ngram_is_not_pushed_out_by_ten_bm25_hits_on_a_shared_given_name()
    {
        string[] others =
        [
            "Mohammed Hassan", "Mohammed Ali", "Mohammed Karim", "Mohammed Saleh", "Mohammed Yousef",
            "Mohammed bin Abdulrahman Al Saud", "Mohammed Abdulaziz Al Thani", "Mohammed Ibrahim Al Mansouri",
            "Mohammed Khalifa bin Zayed Al Nahyan", "Mohammed Abdullah Al Faisal",
        ];
        var index = new ProfileIndex(others.Select((n, i) => Profile($"M{i}", [n])).Append(Profile("T", ["Mohamed Alrashid"])));
        var hits = index.Search("Mohammed Al-Rashid");
        Assert.Contains(hits, h => h.ProfileKey == "DJ:T");
        Assert.Equal(10, hits.Count);
        Assert.All(hits, h => Assert.True(h.Truncated)); // eleven Profiles matched, ten returned
    }

    [Fact]
    public void Exact_matches_are_never_cut_and_the_overflow_is_marked()
    {
        var index = new ProfileIndex(Enumerable.Range(1, 12).Select(i => Profile($"{i:D2}", ["陳志明"])).Append(Profile("X", ["陳志成"])));
        var hits = index.Search("陳志明", top: 10);
        Assert.Equal(12, hits.Count);
        Assert.All(hits, h => Assert.Equal((ProfileIndex.ExactAlias, true), (h.Route, h.Truncated)));

        var few = index.Search("陳志成", top: 20);
        Assert.Equal("DJ:X", few[0].ProfileKey);
        Assert.All(few, h => Assert.False(h.Truncated));
    }

    // ---- empty keys ----

    [Fact]
    public void An_empty_query_key_returns_nothing_and_empty_aliases_are_not_indexed()
    {
        var index = new ProfileIndex([Profile("1", ["-", "陳志明"]), Profile("2", ["—"])]);
        Assert.Empty(index.Search("---"));
        Assert.Empty(index.Search("  "));
        Assert.DoesNotContain(index.Search("陳志成"), h => h.MatchedName is "-" or "—");
    }

    // ---- n-grams ----

    [Theory]
    [InlineData("王偉", "王瑋")]
    [InlineData("歐陽娜娜", "歐陽那娜")]
    [InlineData("黃明", "黃𥕢明")]
    public void One_differing_character_in_a_short_chinese_name_still_reaches_the_ngram_floor(string query, string alias)
    {
        Assert.True(ProfileIndex.NGramScore(query, alias) >= ProfileIndex.NGramFloorCjk);
        var index = new ProfileIndex([Profile("1", [alias]), Profile("2", ["林美玲"])]);
        Assert.Equal("DJ:1", Assert.Single(index.Search(query)).ProfileKey);
    }

    // ---- deterministic order ----

    [Fact]
    public void Output_order_does_not_depend_on_input_row_order()
    {
        var profiles = LabelledIndexProfiles();
        var forward = new ProfileIndex(profiles);
        var backward = new ProfileIndex(Enumerable.Reverse(profiles));
        foreach (var query in new[] { "陳志明", "Chen Wei", "王偉", "Mohammed", "林" })
            Assert.Equal(forward.Search(query), backward.Search(query));

        // Equal scores tie-break on alias text, not on row order.
        var tie = new ProfileIndex([Profile("9", ["Lee Bo"]), Profile("1", ["Lee Al"])]).Search("Lee Xu");
        Assert.Equal(["Lee Al", "Lee Bo"], tie.Select(h => h.MatchedName));
    }

    // ---- reading keys ----

    [Theory]
    [InlineData("陳水扁", "Chen Shui-bian")]
    [InlineData("Jiang Jingguo", "Chiang Ching-kuo")]
    public void A_shared_reading_key_retrieves_across_scripts_and_romanizations_but_never_as_exact(string query, string alias)
    {
        var index = new ProfileIndex([Profile("1", [alias]), Profile("2", ["林美玲", "Lin Mei-ling"])]);
        var hit = Assert.Single(index.Search(query));
        Assert.Equal(("DJ:1", ProfileIndex.Reading), (hit.ProfileKey, hit.Route));
    }

    [Fact]
    public void Exact_and_ngram_hits_rank_above_a_profile_reached_only_by_reading_key()
    {
        // The romanized alias reaches the query only through its reading key; the exact alias keeps its own tier on top.
        var index = new ProfileIndex([Profile("E", ["陳志明"]), Profile("G", ["陳志民"]), Profile("R", ["Chen Chih-Ming"])]);
        // 陳志民 is reached by BM25 and n-gram, each at rank 1, and fuses to 2 / (60 + 1); the reading key alone gives 1 / (60 + 1).
        var hits = index.Search("陳志明");
        Assert.Equal(
            [("DJ:E", ProfileIndex.ExactAlias, 1.0), ("DJ:G", ProfileIndex.Bm25, 2.0 / 61), ("DJ:R", ProfileIndex.Reading, 1.0 / 61)],
            hits.Select(h => (h.ProfileKey, h.Route, h.Score)));
    }

    [Fact]
    public void Each_profile_is_ranked_by_its_closest_alias()
    {
        var hits = new ProfileIndex([Profile("1", ["Lee Xu Zed Qa", "Lee Al Bo"]), Profile("2", ["Lee Mo"])]).Search("Lee Al Bo Cy");
        Assert.Equal([("DJ:1", "Lee Al Bo", ProfileIndex.Bm25), ("DJ:2", "Lee Mo", ProfileIndex.Bm25)], hits.Select(h => (h.ProfileKey, h.MatchedName, h.Route)));
    }

    // Latin trigram Dice: 12 / 26 sits on the floor 2 × 0.3 / 1.3, 10 / 26 below it. No token or reading key is shared.
    [Theory]
    [InlineData("Vandersmoulij", 12.0 / 26, true)]
    [InlineData("Vandermoulijx", 10.0 / 26, false)]
    public void A_latin_alias_on_the_ngram_floor_is_retrieved_and_one_below_is_not(string alias, double dice, bool retrieved)
    {
        Assert.Equal(dice, ProfileIndex.NGramScore("Vandersteenko", alias), 12);
        var hits = new ProfileIndex([Profile("1", [alias])]).Search("Vandersteenko");
        var expected = retrieved ? new[] { ("DJ:1", ProfileIndex.NGram) } : [];
        Assert.Equal(expected, hits.Select(h => (h.ProfileKey, h.Route)));
    }

    // CJK bigram Dice exactly 0.3 (6 / 20) still counts as an n-gram route next to BM25; 0.2 does not.
    [Theory]
    [InlineData("歐陽明星大海藍天水", 0.3, 2.0 / 61)]
    [InlineData("歐陽春星大海藍天水", 0.2, 1.0 / 61)]
    public void An_ngram_score_exactly_on_the_cjk_floor_counts_as_a_route(string alias, double dice, double fused)
    {
        Assert.Equal(dice, ProfileIndex.NGramScore("歐陽明月清風白雲山", alias));
        var hit = Assert.Single(new ProfileIndex([Profile("1", [alias])]).Search("歐陽明月清風白雲山"));
        Assert.Equal(fused, hit.Score);
    }

    [Fact]
    public void Top_k_returns_k_profiles_and_marks_truncation_only_beyond_k()
    {
        var index = new ProfileIndex([Profile("1", ["陳志明"]), Profile("2", ["陳志成"]), Profile("3", ["王志成"])]);
        var exactlyK = index.Search("陳志強", top: 3);
        Assert.Equal(["DJ:2", "DJ:1", "DJ:3"], exactlyK.Select(h => h.ProfileKey));
        Assert.All(exactlyK, h => Assert.False(h.Truncated));

        var cut = index.Search("陳志強", top: 2);
        Assert.Equal(["DJ:2", "DJ:1"], cut.Select(h => h.ProfileKey));
        Assert.All(cut, h => Assert.True(h.Truncated));
    }

    // ---- labelled Recall@10 ----

    private static List<SourceProfile> LabelledIndexProfiles()
    {
        var list = new List<SourceProfile>
        {
            Profile("T01", ["陳志明", "Chen Chih-Ming"]),
            Profile("T02", ["歐陽娜娜", "Ou-Yang Na-Na"]),
            Profile("T03", ["王瑋", "Wang Wei"]),
            Profile("T04", ["黃𥕢明"]),
            Profile("T05", ["Mohamed Alrashid"]),
            Profile("T06", ["Nguyễn Thị Hồng Nhung"]),
            Profile("T07", ["蔣經國", "Chiang Ching-kuo"]),
            Profile("T08", ["李明輝", "Lee Ming-Hui"]),
            Profile("T09", ["Chen Shui-bian"]),
        };
        // Distractors sharing surnames, given names and characters with the targets.
        string[] surnames = ["陳", "王", "黃", "李", "林", "歐陽"];
        string[] given = ["志成", "志強", "明德", "偉", "娜", "建宏", "美玲", "明"];
        var n = 0;
        foreach (var s in surnames)
            foreach (var g in given)
                if (!list.Any(p => p.Names.Contains(s + g))) list.Add(Profile($"D{n++:D3}", [s + g]));
        foreach (var name in new[] { "Chen Wei-Ming", "Chen Chih-Cheng", "Wang Wei-Lin", "Lee Ming-Hsien", "Nguyen Van Hung",
                     "Nguyen Thi Lan", "Mohammed Hassan", "Mohammed Ali", "Mohammed Rashidi", "Chiang Wei-kuo" })
            list.Add(Profile($"D{n++:D3}", [name]));
        return list;
    }

    // Kind, query, target and the lowest rank the target may take in the top 10.
    public static readonly TheoryData<string, string, string, int> Labelled = new()
    {
        { "romanization", "Chen Zhiming", "DJ:T01", 1 },
        { "romanization", "Chen Jhih-Ming", "DJ:T01", 1 }, // Tongyong
        { "romanization", "Jiang Jingguo", "DJ:T07", 1 }, // Pinyin vs Wade-Giles: reading-key route
        { "cross-script", "陳水扁", "DJ:T09", 4 }, // Chinese query, Latin-only Profile: reading-key route
        { "romanization", "Li Minghui", "DJ:T08", 1 },
        { "simplified", "陈志明", "DJ:T01", 1 },
        { "simplified", "蒋经国", "DJ:T07", 1 },
        { "typo", "歐陽那娜", "DJ:T02", 1 },
        { "typo", "王偉", "DJ:T03", 2 },
        { "typo", "Mohammed Al-Rashid", "DJ:T05", 4 },
        { "reordered", "Chih-Ming Chen", "DJ:T01", 1 },
        { "reordered", "Na-Na Ou-Yang", "DJ:T02", 1 },
        { "diacritics", "Nguyen Thi Hong Nhung", "DJ:T06", 1 },
        { "rare character", "黃明", "DJ:T04", 3 },
        { "rare character", "黄𥕢明", "DJ:T04", 1 },
    };

    [Fact]
    public void Recall_at_10_on_the_labelled_variant_set_is_one_and_each_target_keeps_its_rank()
    {
        var index = new ProfileIndex(LabelledIndexProfiles());
        var (misses, ranked) = (new List<string>(), new List<string>());
        foreach (var row in Labelled)
        {
            var (kind, query, target, worstRank) = row.Data;
            var rank = index.Search(query, top: 10).Select(h => h.ProfileKey).ToList().IndexOf(target) + 1;
            if (rank == 0) misses.Add($"{kind}: {query} → {target}");
            else if (rank > worstRank) ranked.Add($"{kind}: {query} → {target} at rank {rank}, expected {worstRank} or better");
        }
        var recall = 1 - (double)misses.Count / Labelled.Count;
        TestContext.Current.TestOutputHelper?.WriteLine($"Recall@10 = {recall:F3} over {Labelled.Count} labelled queries");
        foreach (var m in misses) TestContext.Current.TestOutputHelper?.WriteLine("miss " + m);
        Assert.Empty(misses);
        Assert.Equal(1.0, recall);
        Assert.Empty(ranked);
    }

    [Fact]
    public void A_profile_key_given_twice_keeps_its_last_profile_only()
    {
        var index = new ProfileIndex([Profile("7", ["王建宏"]), Profile("7", ["李大華"])]);

        Assert.Empty(index.Search("王建宏"));
        var hit = Assert.Single(index.Search("李大華"));
        Assert.Equal(("DJ:7", "EXACT_ALIAS"), (hit.ProfileKey, hit.Route));
    }
}
