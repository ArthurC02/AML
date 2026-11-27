// Evaluates the news readers on the annotated test documents. Prints rates only, never text.
// Usage: Aml.NewsEval <models dir> [old] [out=<dir>]    (old = only the 49 documents of the v4 test split)
// The working folder is out=<dir>, else AML_EVAL, outside the repository: it holds dev.jsonl / train.jsonl /
// test.jsonl and evids-dev.jsonl, and every relative path argument (dump=, stageout=, rowdump=, …) and net-ids.json
// are written there. Pass absolute paths for the models dir and caches.
using System.Text.Json;
using Aml.Core.Explanation;
using Aml.Core.Ports;
using Aml.Core.Subjects;
using Aml.Infra.Models;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var work = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? Environment.GetEnvironmentVariable("AML_EVAL");
if (string.IsNullOrEmpty(work) || !Directory.Exists(work))
{
    Console.Error.WriteLine("Set AML_EVAL or pass out=<dir>: the evaluator's working folder outside the repository (documents in, rows out).");
    Environment.Exit(64);
}
Directory.SetCurrentDirectory(work);
var cacheRoot = args.FirstOrDefault(a => a.StartsWith("cache="))?[6..] ?? "cache";
Directory.CreateDirectory(cacheRoot);
var docs = File.ReadLines(args.FirstOrDefault(a => a.StartsWith("docs="))?[5..] ?? (args.Contains("dev") ? "dev.jsonl" : args.Contains("train") ? "train.jsonl" : "test.jsonl")).Select(l => JsonSerializer.Deserialize<Doc>(l, Json.Options)!).ToList();
if (args.Contains("old")) docs = docs.Where(d => d.Old).ToList();
if (args.Contains("unwrap")) docs = docs.Select(d => d with { Text = Aml.Infra.Web.PdfText.Unwrap(d.Text) }).ToList();
var idsReferenceFile = args.FirstOrDefault(a => a.StartsWith("idsref=", StringComparison.Ordinal))?[7..];
var idsReference = idsReferenceFile is null ? null : File.ReadLines(idsReferenceFile).Select(line =>
{
    using var reference = JsonDocument.Parse(line);
    return (Doc: reference.RootElement.GetProperty("doc").GetString()!,
            Ids: reference.RootElement.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()).ToArray());
}).ToDictionary(value => value.Doc, value => value.Ids);
if (idsReference is not null && (idsReference.Count != docs.Count || docs.Any(doc => !idsReference.ContainsKey(doc.Name))))
    throw new InvalidDataException("Token-reference documents differ from the selected split");
if (args.Contains("stated"))
{
    // The news-stated birth date and gender check, on each annotated suspect: the customer is that person (gold value),
    // then someone else with the same name (birth year + 7, the other gender). Counts only, never text.
    var c = new SortedDictionary<string, int>();
    void Count(string k) => c[k] = c.GetValueOrDefault(k) + 1;
    static Aml.Core.Subjects.ScreenedSubject Subj(IReadOnlyList<string> names, Aml.Core.Shared.Gender? g, Aml.Core.Shared.PartialDate? dob) =>
        new(Aml.Core.Subjects.SubjectRole.Customer, null, names, g, dob, ["TW"], null, null, null, null, [], []);
    foreach (var d in docs)
        foreach (var p in d.People.Where(p => p.Role.StartsWith("PER-") && p.Party == "Suspect"))
        {
            var profile = new SourceProfile("DJ:1", null, p.Mentions.ToList(), [], ["TW"], [], [], [], [], [], null, [], []);
            var news = new NewsItem("1", null, "", null, null, d.Text);
            string Show(Aml.Core.Shared.Evidence? e) => e is null ? "no reading" : $"{e.Direction} {e.Detail ?? e.Status.ToString()} {e.Strength}";
            var births = (p.Descriptors ?? []).Where(x => x.Kind == "BIRTH_DATE").Select(x => Aml.Core.Identity.NewsStatedCheck.Parse(x.Text)).OfType<Aml.Core.Shared.PartialDate>().OrderByDescending(x => x.Precision).ToList();
            if ((p.Descriptors ?? []).Any(x => x.Kind == "BIRTH_DATE") && births.Count == 0) Count("birth: gold text not parsed");
            if (births.Count > 0)
            {
                var b = births[0];
                Count("birth, same person:  " + Show(Aml.Core.Identity.NewsStatedCheck.CheckBirthDate(Subj(profile.Names, null, b), profile, news, null)));
                Count("birth, other person: " + Show(Aml.Core.Identity.NewsStatedCheck.CheckBirthDate(Subj(profile.Names, null, b with { Year = b.Year + 7 }), profile, news, null)));
            }
            var gs = (p.Descriptors ?? []).Where(x => x.Kind == "GENDER").Select(x => x.Text.Contains('男') ? Aml.Core.Shared.Gender.Male : x.Text.Contains('女') ? Aml.Core.Shared.Gender.Female : (Aml.Core.Shared.Gender?)null).OfType<Aml.Core.Shared.Gender>().Distinct().ToList();
            if (gs.Count == 1)
            {
                Count("gender, same person:  " + Show(Aml.Core.Identity.NewsStatedCheck.CheckGender(Subj(profile.Names, gs[0], null), profile, news)));
                Count("gender, other person: " + Show(Aml.Core.Identity.NewsStatedCheck.CheckGender(Subj(profile.Names, gs[0] == Aml.Core.Shared.Gender.Male ? Aml.Core.Shared.Gender.Female : Aml.Core.Shared.Gender.Male, null), profile, news)));
            }
        }
    foreach (var (k, v) in c) Console.WriteLine($"  {k}: {v}");
    return;
}
if (args.Contains("stage"))
{
    // The rules' ADVERSE_MEDIA legal stage for each annotated suspect taken as the Profile person, against the stage the
    // annotated events of that person imply (prior records, negated and requested steps left out). Counts only.
    static string Gold(IEnumerable<string> labels)
    {
        var l = labels.Where(x => !x.Contains('-') && x != "prior").ToHashSet();
        return l.Contains("Conviction") ? "Convicted" : l.Contains("Acquittal") ? "Acquitted" : l.Contains("NonIndictment") ? "Dismissed"
            : l.Contains("Indictment") ? "Indicted" : l.Contains("WantedNotice") ? "Wanted" : l.Count > 0 ? "Alleged" : "Unknown";
    }
    static string Diag(string text, IEnumerable<string> ms)
    {
        var lines = text.Split((char)10); var o = Array.FindIndex(lines, l => string.Concat(l.Where(ch => !char.IsWhiteSpace(ch))) == "主文");
        var listed = o >= 0 && lines.Take(o).Any(l => string.Concat(l.Where(ch => !char.IsWhiteSpace(ch))) is var k && (k.StartsWith("被告") || k.StartsWith("上訴人")) && ms.Any(k.Contains));
        var sents = text.Split('。', '！', '？', (char)10, '；');
        int ownInd = sents.Count(x => x.Contains("起訴") && ms.Any(x.Contains)), anyInd = sents.Count(x => x.Contains("起訴")), ownSent = sents.Count(x => ms.Any(x.Contains));
        return $"order={(o >= 0 ? 1 : 0)} listed={(listed ? 1 : 0)} ownInd={ownInd} anyInd={anyInd} ownSent={ownSent}";
    }
    var c = new SortedDictionary<(string, string), int>(); var outFile = args.FirstOrDefault(a => a.StartsWith("stageout="))?[9..];
    // Which rule gave the stage, and what every ruling reader says (reflection: RulingReading is internal). Counts only.
    var trace = typeof(Aml.Core.Screening.SourceConditions).Assembly.GetType("Aml.Core.Screening.Stage.RulingReading", true)!
        .GetMethod("Trace", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
    var basisCounts = new Dictionary<string, (int N, int Correct)>();
    var readerCounts = new Dictionary<string, (int N, int Agrees)>(); var readerOrder = new List<string>();
    var conflicts = new Dictionary<(string, string, string, string), int>(); var conflictPersons = 0;
    if (outFile is not null) File.WriteAllText(outFile, "");
    foreach (var d in docs)
        foreach (var p in d.People.Where(p => p.Role.StartsWith("PER-") && p.Party == "Suspect"))
        {
            var mine = d.Events.Where(e => p.Mentions.Contains(e.Mention)).ToList();
            var gold = Gold(mine.Where(e => !e.Labels.Contains("prior")).SelectMany(e => e.Labels));
            var profile = new SourceProfile("DJ:1", null, p.Mentions.ToList(), [], ["TW"], [], [new SourceCategory("Adverse Media", null)], [], [], [], null, [], []);
            var condition = Aml.Core.Screening.SourceConditions.Determine(profile, false, d.Text).Single();
            var rule = condition.LegalStage.ToString(); var basis = condition.LegalStageBasis ?? "(none)";
            c[(gold, rule)] = c.GetValueOrDefault((gold, rule)) + 1;
            var bc = basisCounts.GetValueOrDefault(basis); basisCounts[basis] = (bc.N + 1, bc.Correct + (gold == rule ? 1 : 0));
            var said = ((IEnumerable<(string Code, Aml.Core.Screening.LegalStage? Stage)>)trace.Invoke(null, [d.Text, (IReadOnlyList<string>)profile.Names])!).ToList();
            foreach (var (code, _) in said) if (!readerOrder.Contains(code)) readerOrder.Add(code);
            var speaking = said.Where(x => x.Stage is not null).ToList();
            foreach (var (code, stage) in speaking) { var rc = readerCounts.GetValueOrDefault(code); readerCounts[code] = (rc.N + 1, rc.Agrees + (stage.ToString() == gold ? 1 : 0)); }
            var anyConflict = false;
            for (var i = 0; i < speaking.Count; i++)
                for (var j = i + 1; j < speaking.Count; j++)
                    if (speaking[i].Stage != speaking[j].Stage)
                    {
                        var key = (speaking[i].Code, speaking[i].Stage.ToString()!, speaking[j].Code, speaking[j].Stage.ToString()!);
                        conflicts[key] = conflicts.GetValueOrDefault(key) + 1; anyConflict = true;
                    }
            if (anyConflict) conflictPersons++;
            if (outFile is not null && gold != rule) File.AppendAllText(outFile, $"{d.Name}\t{p.Id}\t{gold}\t{rule}\t{(d.Text.Contains("主文") || d.Text.Contains("主　文") ? "has-order-word" : "no-order-word")}\t{Diag(d.Text, p.Mentions)}\tevents={mine.Count}\tlabels={string.Join("|", mine.SelectMany(e => e.Labels).Distinct().Order())}\tmentions={string.Join(",", p.Mentions.Select(m => m.Length))}\tforms={string.Join("|", p.Mentions)}\tbasis={basis}\n");
        }
    var total = c.Values.Sum(); var agree = c.Where(k => k.Key.Item1 == k.Key.Item2).Sum(k => k.Value);
    Console.WriteLine($"STAGE suspects {total}, same {agree} ({agree / (double)Math.Max(1, total):F3})");
    foreach (var ((g, r), n) in c) Console.WriteLine($"    gold {g,-10} rule {r,-10} {n}");
    foreach (var (b, (n, ok)) in basisCounts.Where(x => x.Key != "(none)").OrderByDescending(x => x.Value.N).ThenBy(x => x.Key, StringComparer.Ordinal))
        Console.WriteLine($"BASIS {b} decided {n} correct {ok}");
    var none = basisCounts.GetValueOrDefault("(none)"); Console.WriteLine($"BASIS (none) {none.N} correct {none.Correct}");
    foreach (var code in readerOrder.Where(readerCounts.ContainsKey))
        Console.WriteLine($"READER {code} speaks {readerCounts[code].N} agrees_with_gold {readerCounts[code].Agrees}");
    foreach (var ((a, sa, b, sb), n) in conflicts.OrderBy(x => readerOrder.IndexOf(x.Key.Item1)).ThenBy(x => readerOrder.IndexOf(x.Key.Item3)))
        Console.WriteLine($"CONFLICT {a} {sa} vs {b} {sb} {n}");
    Console.WriteLine($"CONFLICT total persons {conflictPersons}");
    return;
}
if (args.Contains("idents"))
{
    // Identifiers read next to the annotated people's own forms, against their annotated descriptors (typed by rule).
    // A found item is right when one of the same kind overlaps it (one text holds the other). Rates only, never text.
    static string K(NewsIdentifierKind k) => k switch { NewsIdentifierKind.BirthDate => "BIRTH_DATE", NewsIdentifierKind.IdNumber => "ID_NUMBER", _ => k.ToString().ToUpperInvariant() };
    static bool Same(string a, string b) => a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    foreach (var only in new[] { "all", "Suspect" })
    {
        var c = new SortedDictionary<string, int[]>(); var wrong = new Dictionary<(string, string), int>();
        int[] Of(string k) => c.TryGetValue(k, out var v) ? v : c[k] = new int[3];
        foreach (var d in docs)
            foreach (var p in d.People.Where(p => p.Role.StartsWith("PER-") && (only == "all" || p.Party == only)))
            {
                var got = NewsIdentifiers.Read(d.Text, p.Mentions).Select(i => (Kind: K(i.Kind), Text: i.Text ?? i.Number!)).ToList();
                var gold = (p.Descriptors ?? []).Select(x => (x.Kind, x.Text)).Distinct().ToList();
                foreach (var g in got) { var ok = gold.Any(x => x.Kind == g.Kind && Same(x.Text, g.Text)); Of(g.Kind)[ok ? 0 : 1]++; if (!ok && only == "all" && args.FirstOrDefault(a => a.StartsWith("fpout=")) is { } fpo) File.AppendAllText(fpo[6..], $"{d.Name}\t{p.Id}\t{g.Kind}\t{g.Text}\n"); if (!ok) wrong[(g.Kind, g.Text)] = wrong.GetValueOrDefault((g.Kind, g.Text)) + 1; }
                foreach (var x in gold) if (!got.Any(g => g.Kind == x.Kind && Same(x.Text, g.Text))) { Of(x.Kind)[2]++; if (only == "all" && args.FirstOrDefault(a => a.StartsWith("fnout=")) is { } fo) File.AppendAllText(fo[6..], $"{d.Name}\t{p.Id}\t{x.Kind}\t{gold.IndexOf(x)}\n"); }
            }
        Console.WriteLine($"IDENTIFIERS ({only} people): P {c.Values.Sum(v => v[0]) / (double)Math.Max(1, c.Values.Sum(v => v[0] + v[1])):F3} R {c.Values.Sum(v => v[0]) / (double)Math.Max(1, c.Values.Sum(v => v[0] + v[2])):F3}");
        foreach (var (k, v) in c) Console.WriteLine($"    {k}: tp {v[0]} fp {v[1]} fn {v[2]}");
        if (args.Contains("wrong")) foreach (var ((k, t), n) in wrong.Where(w => w.Value >= 2).OrderByDescending(w => w.Value).Take(40)) Console.WriteLine($"      wrong {n} {k} {t}");
    }
    return;
}
if (args.Contains("frag"))
{
    // Unmatched model entities (no form equals an annotated mention) by how they relate to the annotated people of the
    // document, from cached tagger spans. Counts only, never text.
    var split = args.Contains("dev") ? "dev" : args.Contains("train") ? "train" : "test";
    var cache = File.ReadLines(Path.Combine(cacheRoot, $"tags-{split}.jsonl")).Select(l => JsonSerializer.Deserialize<TagCache>(l, Json.Options)!).ToDictionary(t => t.Doc);
    var entitiesOf = typeof(NewsModelReading).GetMethod("Entities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var c = new SortedDictionary<string, int>();
    void Count(string k) => c[k] = c.GetValueOrDefault(k) + 1;
    foreach (var d in docs)
    {
        var spans = cache[d.Name].Spans.Select(s => new Aml.Core.Ports.TaggedSpan(s[0], s[1], cache[d.Name].Roles[s[2]])).ToList();
        var ents = ((System.Collections.IEnumerable)entitiesOf.Invoke(null, [d.Text, spans])!).Cast<object>()
            .Select(e => (Name: (string)e.GetType().GetProperty("Name")!.GetValue(e)!, Forms: (IReadOnlyList<string>)e.GetType().GetProperty("Forms")!.GetValue(e)!, Role: (string)e.GetType().GetProperty("Role")!.GetValue(e)!)).ToList();
        var who = d.People.SelectMany(p => p.Mentions.Select(m => (m, p))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().p);
        var matched = ents.Select(e => e.Forms.Select(f => who.GetValueOrDefault(f)).FirstOrDefault(x => x is not null)).ToList();
        var matchedIds = matched.OfType<Person>().Select(p => p.Id).ToHashSet();
        foreach (var (e, m) in ents.Zip(matched))
        {
            var kind = e.Role.StartsWith("ORG-") ? "ORG" : "PER";
            if (m is not null) { Count($"{kind} matched"); continue; }
            var rel = d.People.Where(p => p.Mentions.Any(g => e.Forms.Any(f => f.Contains(g) || g.Contains(f)))).ToList();
            if (rel.Count == 0) { Count($"{kind} unmatched, overlaps no annotated person"); continue; }
            var p0 = rel[0];
            var shape = e.Forms.Any(f => p0.Mentions.Any(g => g.Contains(f))) ? "inside a mention" : "holds a mention";
            if (args.Contains("detail"))
            {
                var f0 = e.Name; var g0 = p0.Mentions.Where(g => g.Contains(f0) || f0.Contains(g)).OrderBy(g => g.Length).First();
                var where = g0.Contains(f0) ? (g0.StartsWith(f0) ? "prefix" : g0.EndsWith(f0) ? "suffix" : "middle") : (f0.StartsWith(g0) ? "mention+extra after" : f0.EndsWith(g0) ? "extra before+mention" : "around");
                var wf = f0.Length is >= 2 and <= 4 && f0.All(Aml.Core.Normalization.CjkText.IsCjk) && ((string)typeof(Aml.Core.Screening.SourceConditions).Assembly.GetType("Aml.Core.Screening.Stage.StageGlossary")!.GetMember("CommonSurnames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).Select(m => m is System.Reflection.FieldInfo fi ? fi.GetValue(null) : ((System.Reflection.PropertyInfo)m).GetValue(null)).First()!).Contains(f0[0]);
                // Do the fragment's tagged spans all lie inside a text occurrence of a longer form of another entity?
                var mySpans = spans.Where(sp => e.Forms.Contains(d.Text.Substring(sp.Start, sp.Length))).ToList();
                var longer = ents.Where(o => o.Name != e.Name).SelectMany(o => o.Forms).Where(o => o.Length > f0.Length && o.Contains(f0)).ToList();
                bool Inside(Aml.Core.Ports.TaggedSpan sp) => longer.Any(o => { for (var a = Math.Max(0, sp.Start - o.Length); a <= sp.Start; a++) if (a + o.Length <= d.Text.Length && a + o.Length >= sp.Start + sp.Length && string.CompareOrdinal(d.Text, a, o, 0, o.Length) == 0) return true; return false; });
                var inside = mySpans.Count > 0 && mySpans.All(Inside) ? "all spans inside a longer entity form" : longer.Count > 0 ? "some spans standalone, a longer entity form exists" : "no longer entity form";
                static string M(string x) => new(x.Select(ch => Aml.Core.Normalization.CjkText.IsCjk(ch) ? 'X' : char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? 'A' : ch).ToArray());
                if (args.Contains("shapes")) { Count($"   shape: {kind} entity [{M(f0)}] vs mention [{M(g0)}]"); continue; }
                Count($"   detail: {kind} {shape}, {where}, len {f0.Length} vs mention {g0.Length}, {(wf ? "well-formed" : "not well-formed")}, {inside}");
            }
            Count($"{kind} unmatched, {shape} of {(rel.Count == 1 ? "one" : "several")} person(s); that person {(matchedIds.Contains(p0.Id) ? "IS" : "is NOT")} matched by another entity; gold role {p0.Role[..3]}");
        }
    }
    foreach (var (k, n) in c) Console.WriteLine($"{n,5} {k}");
    return;
}
if (args.Contains("droprole")) typeof(NewsModelReading).GetProperty("BareRoleMerge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, false);
using var model = new OnnxNewsModel(args[0]);
CachedModel? cached = null;
if (args.Contains("cached"))
{
    var sp = args.Contains("dev") ? "dev" : args.Contains("train") ? "train" : "test";
    cached = new CachedModel(model, docs.ToDictionary(d => d.Text, d => d.Name), Path.Combine(cacheRoot, $"tags-{sp}.jsonl"), Path.Combine(cacheRoot, $"evcache-{sp}.tsv"));
}
if (args.Contains("tagcache"))
{
    var split0 = args.Contains("dev") ? "dev" : args.Contains("train") ? "train" : "test";
    var roles = new List<string>();
    using var w = new StreamWriter(Path.Combine(cacheRoot, "tags-" + split0 + ".jsonl"));
    foreach (var d in docs)
    {
        var spans = model.Tag(d.Text).Select(s => { var i = roles.IndexOf(s.Role); if (i < 0) { roles.Add(s.Role); i = roles.Count - 1; } return new[] { s.Start, s.Length, i }; }).ToList();
        w.WriteLine(JsonSerializer.Serialize(new TagCache(d.Name, roles.ToList(), spans), Json.Options));
    }
    return;
}

Console.WriteLine($"{docs.Count} documents, model {model.Version}");

// 1. Token ids must equal the Python tokenizer's.
var tok = typeof(OnnxNewsModel).GetMethod("Tokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
var same = 0; var firstDiff = new List<string>(); var netIds = new Dictionary<string, List<int>>();
foreach (var d in docs)
{
    var ids = ((System.Collections.IEnumerable)tok.Invoke(model, [d.Text])!).Cast<object>().Select(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)!).ToList();
    IReadOnlyList<int> expected = idsReference is null ? d.Ids : idsReference[d.Name];
    if (ids.SequenceEqual(expected)) { same++; continue; }
    netIds[d.Name] = ids;
    firstDiff.Add($"len {ids.Count} vs {expected.Count}, first diff at {Enumerable.Range(0, Math.Min(ids.Count, expected.Count)).FirstOrDefault(i => ids[i] != expected[i], -1)}");
}
Console.WriteLine($"token ids identical: {same}/{docs.Count}");
foreach (var f in firstDiff.Take(5)) Console.WriteLine("  " + f);
File.WriteAllText("net-ids.json", JsonSerializer.Serialize(netIds));
if (args.Contains("ids")) return;
if (args.Contains("evids"))
{
    // The event input (ids and segment boundary) must equal train_ev.pair_ids.
    var ei = typeof(OnnxNewsModel).GetMethod("EventInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    int ok = 0, bad = 0;
    foreach (var line in File.ReadLines("evids-dev.jsonl"))
    {
        using var j = JsonDocument.Parse(line);
        var want = j.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var (ids, types) = ((long[], long[]))ei.Invoke(model, [j.RootElement.GetProperty("mention").GetString()!, j.RootElement.GetProperty("sentence").GetString()!])!;
        if (ids.SequenceEqual(want) && Array.IndexOf(types, 1L) == j.RootElement.GetProperty("seg").GetInt32()) ok++;
        else if (bad++ < 3) Console.WriteLine($"  differs: len {ids.Length} vs {want.Length}, first diff at {Enumerable.Range(0, Math.Min(ids.Length, want.Length)).FirstOrDefault(i => ids[i] != want[i], -1)}");
    }
    Console.WriteLine($"event inputs identical: {ok}/{ok + bad}");
    return;
}
if (args.FirstOrDefault(a => a.StartsWith("rowdump=")) is { } rowdump)
{
    // Every annotated row whose sentence names the person: the model's probability for each gold label, with how many
    // labelled people the sentence names and this person's place among them. No text.
    var outRows = new List<string>();
    foreach (var d in docs)
    {
        var who = d.People.SelectMany(p => p.Mentions.Select(m => (m, p.Id))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Id);
        var isPerson = d.People.Where(p => p.Id.StartsWith("P")).Select(p => p.Id).ToHashSet();
        foreach (var e in d.Events.Where(e => e.Sentence.Contains(e.Mention) && who.ContainsKey(e.Mention)))
        {
            var first = who.Where(w => isPerson.Contains(w.Value) && e.Sentence.Contains(w.Key)).GroupBy(w => w.Value).Select(g => (g.Key, At: g.Min(w => e.Sentence.IndexOf(w.Key, StringComparison.Ordinal)))).OrderBy(x => x.At).ToList();
            var rank = first.FindIndex(x => x.Key == who[e.Mention]);
            var pr = ((Aml.Core.Ports.INewsModel?)cached ?? model).Events(d.Text, [(e.Mention, e.Sentence)])[0];
            foreach (var l in e.Labels)
                outRows.Add($"{d.Name}	{who[e.Mention]}	{l}	{pr.Where(x => x.Key == l).Select(x => (double)x.Value).DefaultIfEmpty(-1).Max().ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}	{first.Count}	{rank}	{e.Sentence.Length}	{e.Sentence.IndexOf(e.Mention, StringComparison.Ordinal)}");
        }
    }
    File.WriteAllLines(rowdump[8..], outRows); Console.WriteLine($"rows {outRows.Count}");
    return;
}
if (args.Contains("profile"))
{
    // As the system uses the reading: one known person at a time. Each labelled suspect person in turn is the Profile
    // person (all their written forms are the Profile's names); only the entities the reader marks ProfileName count.
    // Persons sharing a written form are one. Events per (document, person, label), and the legal stage they imply.
    var min = double.Parse(args.FirstOrDefault(a => a.StartsWith("min="))?[4..] ?? "0.3", System.Globalization.CultureInfo.InvariantCulture);
    static int Stage(IEnumerable<string> ls) => ls.Where(l => l != "prior" && !l.Contains('-')).Select(l => l switch { "Conviction" => 6, "Acquittal" => 5, "NonIndictment" => 4, "Indictment" => 3, "WantedNotice" => 2, _ => 1 }).DefaultIfEmpty(0).Max();
    int tp = 0, fp = 0, fn = 0, n = 0, sameStage = 0, unfound = 0, anyBoth = 0, goldNone = 0; var conf = new Dictionary<string, int>(); var rows = new List<string>();
    foreach (var d in docs)
    {
        var done = new HashSet<string>();
        foreach (var p in d.People.Where(p => p.Role == "PER-Suspect"))
        {
            if (done.Contains(p.Id)) continue;
            var group = new List<Person> { p };
            for (var grew = true; grew;) { grew = false; foreach (var q in d.People.Where(q => q.Role.StartsWith("PER-") && !group.Contains(q) && group.Any(g => g.Mentions.Intersect(q.Mentions).Any()))) { group.Add(q); grew = true; } }
            foreach (var g in group) done.Add(g.Id);
            var names = group.SelectMany(g => g.Mentions).Where(m => m.Length > 1).Distinct().ToList();
            if (!names.Any(m => d.Text.Contains(m))) continue;
            var ids = group.Select(g => g.Id).ToHashSet();
            var who = d.People.SelectMany(x => x.Mentions.Select(m => (m, x.Id))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Id);
            var gold = d.Events.Where(e => ids.Contains(who.GetValueOrDefault(e.Mention, "?"))).SelectMany(e => e.Labels).ToHashSet();
            var r = NewsModelReading.Read(d.Text, names, cached ?? (Aml.Core.Ports.INewsModel)model);
            var mine = r.Entities.Where(en => en.ProfileName).ToList();
            var pred = mine.SelectMany(en => en.Events.Where(v => v.Probability >= min)).Select(v => v.Type == NewsEventType.PriorRecord ? "prior"
                : v.Type + (v.Certainty == NewsCertainty.Negated ? "-Negated" : v.Certainty == NewsCertainty.Requested ? "-Requested" : "")).ToHashSet();
            n++; if (mine.Count == 0) unfound++; if (gold.Count == 0) goldNone++;
            tp += gold.Intersect(pred).Count(); fp += pred.Except(gold).Count(); fn += gold.Except(pred).Count();
            int a = Stage(gold), b = Stage(pred); if (a == b) sameStage++; else conf[$"{a}->{b}"] = conf.GetValueOrDefault($"{a}->{b}") + 1;
            if ((a > 0) == (b > 0)) anyBoth++;
            rows.Add($"{d.Name}\t{p.Id}\t{mine.Count}\t{a}\t{b}\t{string.Join(",", gold.Order())}\t{string.Join(",", pred.Order())}");
        }
    }
    Console.WriteLine($"PROFILE: persons {n} (no gold event {goldNone}; reader gave no Profile entity {unfound}) | events P {tp / (double)Math.Max(tp + fp, 1):F3} R {tp / (double)Math.Max(tp + fn, 1):F3} F1 {2.0 * tp / Math.Max(2 * tp + fp + fn, 1):F4} (tp {tp} fp {fp} fn {fn}) | stage same {sameStage} ({sameStage / (double)Math.Max(n, 1):F3}) | any step agrees {anyBoth} ({anyBoth / (double)Math.Max(n, 1):F3})");
    Console.WriteLine("   stage errors gold->model (6 convicted 5 acquitted 4 dismissed 3 indicted 2 wanted 1 alleged 0 none): " + string.Join(" ", conf.OrderByDescending(x => x.Value).Select(x => $"{x.Key}:{x.Value}")));
    if (args.FirstOrDefault(a => a.StartsWith("dump=")) is { } pdump) File.WriteAllLines(pdump[5..], rows);
    return;
}
if (args.Contains("e2e"))
{
    // End to end, as the system runs: NewsModelReading.Read on the whole text, entities matched to labelled people by
    // their forms, events compared per (document, person, label). Unmatched entities' events are false positives.
    int tp = 0, fp = 0, fn = 0, fpUnlabelled = 0, fnUnfound = 0;
    var perLabel = new Dictionary<string, int[]>();
    var rows = new List<string>(); var ents = new List<string>(); var fnRows = new List<string>(); var pvs = new List<string>();
    foreach (var d in docs)
    {
        var r = NewsModelReading.Read(d.Text, [], cached ?? (Aml.Core.Ports.INewsModel)model);
        var who = d.People.SelectMany(p => p.Mentions.Select(m => (m, p.Id))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Id);
        var gold = new Dictionary<string, HashSet<string>>();
        foreach (var e in d.Events)
            (gold.TryGetValue(who.GetValueOrDefault(e.Mention, "?" + e.Mention), out var g) ? g : gold[who.GetValueOrDefault(e.Mention, "?" + e.Mention)] = []).UnionWith(e.Labels);
        var pred = new Dictionary<string, HashSet<string>>();
        var prob = new Dictionary<(string Id, string Label), double>();
        var filter = args.FirstOrDefault(a => a.StartsWith("filter="))?[7..];
        static bool OrgType(NewsEventType t) => t is NewsEventType.Investigation or NewsEventType.Search or NewsEventType.Indictment
            or NewsEventType.RegulatoryPenalty or NewsEventType.AssetRestraint or NewsEventType.Confiscation or NewsEventType.Fine or NewsEventType.Conviction;
        foreach (var en in r.Entities.Select(en => args.Contains("orgtypes") && en.Organisation ? en with { Events = en.Events.Where(v => OrgType(v.Type)).ToList() } : en).Where(en => filter switch
        {
            "suspect" => en.Party == NewsParty.Suspect,
            "notofficial" => en.Party != NewsParty.Official,
            "mentions2" => en.Mentions >= 2,
            _ => true,
        }))
        {
            var id = new[] { en.Name }.Concat(en.OtherForms).Select(f => who.GetValueOrDefault(f)).FirstOrDefault(x => x is not null) ?? "x:" + en.Name;
            var min = double.Parse(args.FirstOrDefault(a => a.StartsWith("min="))?[4..] ?? "0.3", System.Globalization.CultureInfo.InvariantCulture);
            ents.Add($"{d.Name}	{(id.StartsWith("x:") ? "x:" + string.Join(",", who.Where(w => w.Key.Contains(en.Name) || en.Name.Contains(w.Key)).Select(w => w.Value + (w.Key.Contains(en.Name) ? "<" : ">") + en.Name.Length).Distinct()) : id)}	{en.Party}	{en.Organisation}	{en.Mentions}	{en.Events.Count}");
            foreach (var v in en.Events)
            {
                if (args.Contains("kw") && Keywords.TryGetValue(v.Type, out var kw) && !System.Text.RegularExpressions.Regex.IsMatch(v.Quote, kw)) { Dropped++; continue; }
                var l0 = v.Type == NewsEventType.PriorRecord ? "prior" : v.Type + (v.Certainty == NewsCertainty.Negated ? "-Negated" : v.Certainty == NewsCertainty.Requested ? "-Requested" : "");
                prob[(id, l0)] = Math.Max(prob.GetValueOrDefault((id, l0)), v.Probability);
                {
                    var forms0 = new[] { en.Name }.Concat(en.OtherForms).ToList(); var qt = v.Quote;
                    bool Same(string gs) => gs.Contains(qt) || qt.Contains(gs.Trim()) || (qt.Length >= 200 && gs.Trim().StartsWith(qt));
                    var hid = id.StartsWith("x:", StringComparison.Ordinal) ? "x:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.Name + " " + id))) : id;
                    var gl = d.Events.Where(e => e.Labels.Contains(l0)).ToList();
                    pvs.Add(string.Join("	", d.Name, hid, l0, v.Probability.ToString(System.Globalization.CultureInfo.InvariantCulture), forms0.Any(qt.Contains) ? 1 : 0, qt.Length,
                        r.Entities.Count(x => !x.Organisation && x.Party == NewsParty.Suspect && new[] { x.Name }.Concat(x.OtherForms).Any(qt.Contains)),
                        gl.Any(e => who.GetValueOrDefault(e.Mention) != id && Same(e.Sentence)) ? 1 : 0,
                        gl.Any(e => who.GetValueOrDefault(e.Mention) != id) ? 1 : 0,
                        d.Events.Any(e => who.GetValueOrDefault(e.Mention) == id && Same(e.Sentence)) ? 1 : 0,
                        d.Events.Any(e => Same(e.Sentence)) ? 1 : 0, r.Entities.Count(x => !x.Organisation && x.Party == NewsParty.Suspect), en.Party, d.Text.IndexOf(qt, StringComparison.Ordinal)));
                }
            }
            if (args.Contains("boiler") && !en.Organisation && en.Party == NewsParty.Suspect)
            {
                // Judgment boilerplate (案經…移送…偵查起訴) names no one or the defendants: it is each named suspect's, else every suspect's.
                var forms = new[] { en.Name }.Concat(en.OtherForms).ToList();
                var suspects = r.Entities.Where(x => !x.Organisation && x.Party == NewsParty.Suspect).Select(x => new[] { x.Name }.Concat(x.OtherForms).ToList()).ToList();
                foreach (var s in NewsModelReadingSentences(d.Text).Where(s => Boiler.IsMatch(s)))
                {
                    var named = suspects.Where(f => f.Any(x => s.Contains(x))).ToList();
                    if ((named.Count > 0 || args.Contains("named")) && !forms.Any(x => s.Contains(x))) continue;
                    var set = pred.TryGetValue(id, out var q0) ? q0 : pred[id] = [];
                    if (Regex2.IsMatch(s, "移送|函送|報告|告發")) set.Add("Referral");
                    if (Regex2.IsMatch(s, "偵查起訴|提起公訴|起訴|聲請簡易判決處刑")) set.Add("Indictment");
                }
            }
            (pred.TryGetValue(id, out var q) ? q : pred[id] = []).UnionWith(en.Events.Where(v => v.Probability >= min && (!args.Contains("kw") || !Keywords.TryGetValue(v.Type, out var kw2) || System.Text.RegularExpressions.Regex.IsMatch(v.Quote, kw2))).Select(v => v.Type == NewsEventType.PriorRecord ? "prior"
                : v.Type + (v.Certainty == NewsCertainty.Negated ? "-Negated" : v.Certainty == NewsCertainty.Requested ? "-Requested" : "")));
        }
        foreach (var k in gold.Keys.Union(pred.Keys))
        {
            var g = gold.GetValueOrDefault(k) ?? []; var q = pred.GetValueOrDefault(k) ?? [];
            var dumpId = k.StartsWith("x:", StringComparison.Ordinal)
                ? "x:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.Name + "\0" + k))) : k;
            foreach (var l in g.Union(prob.Keys.Where(x => x.Id == k).Select(x => x.Label)))
                rows.Add($"{d.Name}\t{dumpId}\t{l}\t{prob.GetValueOrDefault((k, l)).ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{(g.Contains(l) ? 1 : 0)}\t");
            foreach (var l in g.Except(q))
            {
                var sents = NewsModelReadingSentences(d.Text).ToList();
                var mine = d.Events.Where(e => who.GetValueOrDefault(e.Mention) == k && e.Labels.Contains(l)).ToList();
                var namedRows = mine.Where(e => e.Sentence.Contains(e.Mention)).ToList();
                double best = -1; var split = "none";
                foreach (var e in namedRows)
                {
                    var pr = ((Aml.Core.Ports.INewsModel?)cached ?? model).Events(d.Text, [(e.Mention, e.Sentence)])[0];
                    var pv = pr.Where(x => x.Key == l).Select(x => (double)x.Value).DefaultIfEmpty(0).Max();
                    if (pv > best) { best = pv; split = sents.Contains(e.Sentence.Trim()) ? "same" : sents.Any(x => x.Contains(e.Sentence.Trim())) ? "reader-longer" : sents.Any(x => x.Length > 3 && e.Sentence.Contains(x)) ? "reader-shorter" : "other"; }
                }
                fnRows.Add($"{d.Name}	{(k.StartsWith("x:") || k.StartsWith("?") ? "?" : k)}	{l}	{mine.Count}	{namedRows.Count}	{best.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}	{split}	{pred.ContainsKey(k)}");
            }
            foreach (var l in g.Union(q))
            {
                var c = perLabel.TryGetValue(l, out var a) ? a : perLabel[l] = new int[3];
                if (g.Contains(l) && q.Contains(l)) { tp++; c[0]++; } else if (q.Contains(l)) { fp++; c[1]++; if (k.StartsWith("x:")) fpUnlabelled++; } else { fn++; c[2]++; if (!pred.ContainsKey(k)) fnUnfound++; }
            }
        }
    }
    Console.WriteLine($"dropped {Dropped}"); Console.WriteLine($"E2E EVENTS: P {tp / (double)Math.Max(tp + fp, 1):F3} R {tp / (double)Math.Max(tp + fn, 1):F3} F1 {2.0 * tp / Math.Max(2 * tp + fp + fn, 1):F3} (tp {tp} fp {fp}, of which on unlabelled names {fpUnlabelled}; fn {fn}, of which the person has no entity {fnUnfound})");
    if (args.FirstOrDefault(a => a.StartsWith("dump=")) is { } dump) { File.WriteAllLines(dump[5..], rows); File.WriteAllLines(dump[5..] + ".ent", ents); File.WriteAllLines(dump[5..] + ".pv", pvs); File.WriteAllLines(dump[5..] + ".fn", fnRows); }
    if (args.Contains("perlabel")) foreach (var (l, c) in perLabel.OrderByDescending(x => x.Value[1] + x.Value[2])) Console.WriteLine($"    {l}: tp {c[0]} fp {c[1]} fn {c[2]}");
    return;
}
if (args.Contains("bench"))
{
    var pairs = docs.Take(3).SelectMany(d => d.Events.Select(e => (e.Mention, e.Sentence))).Take(64).ToList();
    model.Events(pairs.Take(2).ToList());
    for (var round = 0; round < 3; round++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var q in pairs) model.Events([q]);
        var one = sw.ElapsedMilliseconds; sw.Restart();
        model.Events(pairs);
        Console.WriteLine($"{pairs.Count} pairs: one at a time {one} ms, batched {sw.ElapsedMilliseconds} ms");
    }
    return;
}
if (args.Contains("read"))
{
    foreach (var d in docs.Take(5))
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        model.Tag(d.Text); var tagMs = sw.ElapsedMilliseconds; sw.Restart();
        var r = NewsModelReading.Read(d.Text, [], model);
        Console.Write($"tag {tagMs} ms; ");
        Console.WriteLine($"{d.Text.Length} chars: {r.Entities.Count} entities, {r.Entities.Sum(e => e.Events.Count)} events, {sw.ElapsedMilliseconds} ms");
    }
    return;
}

// 2. People: model, rules, merged.
var tagged = docs.ToDictionary(d => d.Name, d => (cached ?? (Aml.Core.Ports.INewsModel)model).Tag(d.Text));
Report("MODEL raw", docs, d => tagged[d.Name].Select(s => (d.Text.Substring(s.Start, s.Length), s.Role)));
Report("MODEL drop 1-char", docs, d => tagged[d.Name].Where(s => s.Length > 1).Select(s => (d.Text.Substring(s.Start, s.Length), s.Role)));
Report("MODEL join+drop", docs, d => Clean(tagged[d.Name]).Select(s => (d.Text.Substring(s.Start, s.Length), s.Role)));
Errors(docs, d => tagged[d.Name].Where(s => s.Length > 1).Select(s => (d.Text.Substring(s.Start, s.Length), s.Role)));
Report("MERGED no rule roles", docs, d => Merged(d.Text, tagged[d.Name]));
var rulePeople = docs.ToDictionary(d => d.Name, d => NewsDigests.Build(new NewsItem("p", null, "", null, null, d.Text), [], "S1")!.People);
// Report("MERGED + rule roles", docs, d => Merged(d.Text, tagged[d.Name]));
Errors(docs, d => Merged(d.Text, tagged[d.Name]));
if (args.Contains("tagger")) return;
Report("RULES", docs, d => Rules(d));

// 3. Events per (document, person, label).
var owner = docs.SelectMany(d => d.People.SelectMany(p => p.Mentions.Select(m => (d.Name, m, p.Id)))).GroupBy(x => (x.Name, x.m)).ToDictionary(g => g.Key, g => g.First().Id);
var probs = docs.SelectMany(d => d.Events.Select(e => (d.Name, e, p: model.Events([(e.Mention, e.Sentence)])[0]))).ToList();
var ruleEvents = new Dictionary<(string, string), HashSet<string>>();
foreach (var d in docs)
    foreach (var e in NewsDigests.Build(new NewsItem("p", null, "", null, null, d.Text), [], "S1")!.Events)
    {
        if (e.Actor is null || e.Certainty == NewsCertainty.Hedged) continue;
        var label = e.Type + e.Certainty switch { NewsCertainty.Negated => "-Negated", NewsCertainty.Requested => "-Requested", _ => "" };
        var key = (d.Name, owner.GetValueOrDefault((d.Name, e.Actor), e.Actor)!);
        (ruleEvents.TryGetValue(key, out var r) ? r : ruleEvents[key] = []).Add(label);
    }
var labels = probs.SelectMany(x => x.p.Keys).ToHashSet();
var priorCue = new System.Text.RegularExpressions.Regex(args.FirstOrDefault(a => a.StartsWith("prior="))?[6..] ?? "前科|累犯|曾因");
foreach (var thr0 in new[] { 0.3 })
{
    var none = new Dictionary<(string, string), Dictionary<string, double>>(); var goldN = new Dictionary<(string, string), HashSet<string>>();
    foreach (var (doc, e, p) in probs)
    {
        var key = (doc, owner.GetValueOrDefault((doc, e.Mention), e.Mention)!);
        (goldN.TryGetValue(key, out var g) ? g : goldN[key] = []).UnionWith(e.Labels.Where(p.ContainsKey));
        var n = none.TryGetValue(key, out var d) ? d : none[key] = [];
        foreach (var (l, q) in p) n[l] = n.GetValueOrDefault(l, 1.0) * (1 - q);
    }
    int tp = 0, fp = 0, fn = 0;
    foreach (var k in goldN.Keys)
    {
        var g = goldN[k]; var q = none[k].Where(x => 1 - x.Value >= thr0).Select(x => x.Key).ToHashSet();
        tp += q.Intersect(g).Count(); fp += q.Except(g).Count(); fn += g.Except(q).Count();
    }
    Console.WriteLine($"EVENTS noisy-or {thr0}: P {tp / (double)Math.Max(tp + fp, 1):F3} R {tp / (double)Math.Max(tp + fn, 1):F3} F1 {2.0 * tp / Math.Max(2 * tp + fp + fn, 1):F3}");
}
foreach (var (title, thr, useModel, useRules) in new[] { ("model", 0.3f, true, false) })
{
    var gold = new Dictionary<(string, string), HashSet<string>>(); var pred = new Dictionary<(string, string), HashSet<string>>();
    foreach (var (doc, e, p) in probs)
    {
        var key = (doc, owner.GetValueOrDefault((doc, e.Mention), e.Mention)!);
        (gold.TryGetValue(key, out var g) ? g : gold[key] = []).UnionWith(e.Labels.Where(p.ContainsKey));
        if (!pred.ContainsKey(key)) pred[key] = [];
        if (useModel) pred[key].UnionWith(p.Where(x => x.Value > thr).Select(x => x.Key));
        if (title == "model+prior" && priorCue.IsMatch(e.Sentence)) pred[key].Add("prior");
    }
    if (useRules) foreach (var (k, ls) in ruleEvents) (pred.TryGetValue(k, out var q) ? q : pred[k] = []).UnionWith(ls.Where(labels.Contains));
    int tp = 0, fp = 0, fn = 0;
    var per = new Dictionary<string, int[]>();
    foreach (var k in gold.Keys.Union(pred.Keys))
    {
        var g = gold.GetValueOrDefault(k) ?? []; var q = pred.GetValueOrDefault(k) ?? [];
        tp += q.Intersect(g).Count(); fp += q.Except(g).Count(); fn += g.Except(q).Count();
        foreach (var l in g.Union(q)) { var c = per.TryGetValue(l, out var a) ? a : per[l] = new int[3]; if (g.Contains(l)) c[0]++; if (g.Contains(l) && q.Contains(l)) c[1]++; if (q.Contains(l) && !g.Contains(l)) c[2]++; }
    }
    Console.WriteLine($"EVENTS {title} {thr}: P {tp / (double)Math.Max(tp + fp, 1):F2} R {tp / (double)Math.Max(tp + fn, 1):F2} F1 {2.0 * tp / Math.Max(2 * tp + fp + fn, 1):F2}");
    if (args.Contains("perlabel")) foreach (var (l, c) in per.OrderByDescending(x => x.Value[0])) Console.WriteLine($"    {l}: gold {c[0]}, found {c[1]}, extra {c[2]}");
}

static IEnumerable<(string Name, string Role)> Rules(Doc d)
{
    var digest = NewsDigests.Build(new NewsItem("p", null, "", null, null, d.Text), [], "S1");
    return digest!.People.Where(p => !p.Name.Contains('姓') && p.Name.Length > 1).Select(p => (p.Name, p.Party switch
    {
        NewsParty.Suspect => "PER-Suspect",
        NewsParty.Victim => "PER-Victim",
        NewsParty.Official => "PER-Official",
        _ => "PER-Other",
    }));
}

// Fragment fixes, as eval_person.py --clean: join touching spans (role of the longer), drop single characters.
static List<TaggedSpan> Clean(IReadOnlyList<TaggedSpan> spans)
{
    var o = new List<TaggedSpan>();
    foreach (var s in spans)
    {
        if (o.Count > 0 && o[^1].Start + o[^1].Length == s.Start)
        {
            var p = o[^1]; o.RemoveAt(o.Count - 1);
            o.Add(new(p.Start, p.Length + s.Length, p.Length >= s.Length ? p.Role : s.Role));
        }
        else o.Add(s);
    }
    return o.Where(x => x.Length > 1).ToList();
}

static void Errors(List<Doc> docs, Func<Doc, IEnumerable<(string Name, string Role)>> read)
{
    var kinds = new Dictionary<string, int>();
    void Add(string k) => kinds[k] = kinds.GetValueOrDefault(k) + 1;
    foreach (var d in docs)
    {
        var pred = read(d).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.GroupBy(x => x.Role).OrderByDescending(r => r.Count()).First().Key);
        var all = d.People.SelectMany(p => p.Mentions.Select(m => (m, p.Role))).ToList();
        foreach (var p in d.People)
        {
            var hit = pred.Keys.Where(p.Mentions.Contains).ToList();
            if (hit.Count == 0) { Add($"missed {p.Role}"); continue; }
            var got = hit.GroupBy(h => pred[h]).OrderByDescending(g => g.Count()).First().Key;
            if (got != p.Role) Add($"role {p.Role} -> {got}");
        }
        foreach (var (name, role) in pred.Where(x => !all.Any(a => a.m == x.Key)))
        {
            var longer = all.Where(a => name.Contains(a.m)).ToList(); var shorter = all.Where(a => a.m.Contains(name)).ToList();
            var shape = name.Contains('姓') ? "X姓" : name.Any(c => c == '某' || c == '○' || c == 'O') ? "masked" : name.Length switch { 2 => "len2", 3 => "len3", 4 => "len4", _ => "len5+" };
            if (longer.Count > 0) Add($"extra too long (+{name.Length - longer.Max(a => a.m.Length)}) {role[..3]}");
            else if (shorter.Count > 0) Add($"extra too short {role[..3]} {shape}" + (pred.Keys.Any(k => k != name && k.Contains(name)) ? " inside-pred" : "") + (shorter.All(a => a.m.StartsWith(name)) ? " prefix" : shorter.All(a => a.m.EndsWith(name)) ? " suffix" : " middle") + (name.Length >= 2 && SurnameProbe.Common(name[0]) ? " surname" : ""));
            else Add($"extra unrelated {role} {shape}" + (role.StartsWith("ORG") ? " ..." + name[^1] : ""));
        }
    }
    foreach (var (k, n) in kinds.OrderBy(x => x.Key)) Console.WriteLine($"  {n,4} {k}");
}

static void Report(string title, List<Doc> docs, Func<Doc, IEnumerable<(string Name, string Role)>> read)
{
    int gold = 0, found = 0, roleOk = 0, predCount = 0, extra = 0, missed = 0;
    var perRole = new Dictionary<string, int[]>();
    foreach (var d in docs)
    {
        var pred = read(d).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.GroupBy(x => x.Role).OrderByDescending(r => r.Count()).First().Key);
        var used = new HashSet<string>();
        foreach (var p in d.People)
        {
            var c = perRole.TryGetValue(p.Role, out var a) ? a : perRole[p.Role] = new int[3];
            gold++; c[0]++;
            var hit = pred.Keys.Where(p.Mentions.Contains).ToList();
            if (hit.Count == 0) { missed++; continue; }
            used.UnionWith(hit); found++; c[1]++;
            var got = hit.GroupBy(h => pred[h]).OrderByDescending(g => g.Count()).First().Key;
            if (got == p.Role) { roleOk++; c[2]++; }
        }
        predCount += pred.Count; extra += pred.Keys.Count(k => !used.Contains(k));
    }
    Console.WriteLine($"{title}: found {found}/{gold} = {found / (double)gold:P0} | role right {roleOk}/{found} = {roleOk / (double)Math.Max(found, 1):P0} | extra {extra} of {predCount} | missed {missed}");
    foreach (var (r, c) in perRole.OrderBy(x => x.Key)) Console.WriteLine($"  {r}: found {c[1]}/{c[0]}, role right {c[2]}/{c[1]}");
}

record Desc(string Text, string Kind);
record Person(string Id, string Role, List<string> Mentions, string? Party = null, List<Desc>? Descriptors = null);
record Ev(string Mention, string Sentence, List<string> Labels);
record Doc([property: System.Text.Json.Serialization.JsonPropertyName("doc")] string Name, bool Old, string Text, List<int> Ids, List<Person> People, List<Ev> Events);
static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

static class SurnameProbe
{
    static readonly string S = (string)typeof(Aml.Core.Screening.SourceConditions).Assembly.GetType("Aml.Core.Screening.Stage.StageGlossary")!.GetField("CommonSurnames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    public static bool Common(char c) => S.Contains(c);
}

static partial class Program
{
    static readonly System.Reflection.MethodInfo EntitiesMethod = typeof(NewsModelReading).GetMethod("Entities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    static IEnumerable<(string, string)> Merged(string text, IReadOnlyList<TaggedSpan> spans) =>
        ((System.Collections.IEnumerable)EntitiesMethod.Invoke(null, [text, spans])!).Cast<object>()
            .Select(e => ((string)e.GetType().GetProperty("Name")!.GetValue(e)!, (string)e.GetType().GetProperty("Role")!.GetValue(e)!));
}

record TagCache(string Doc, List<string> Roles, List<int[]> Spans);

sealed class CachedModel(OnnxNewsModel inner, Dictionary<string, string> docOfText, string tags, string events) : Aml.Core.Ports.INewsModel
{
    // Tagger spans from tagcache; event answers memoised on disk by (mention, sentence), so a rerun asks only new pairs.
    private readonly Dictionary<string, TagCache> tagged = File.ReadLines(tags).Select(l => JsonSerializer.Deserialize<TagCache>(l, Json.Options)!).ToDictionary(t => t.Doc);
    private readonly Dictionary<string, Dictionary<string, float>> memo = File.Exists(events)
        ? File.ReadLines(events).Select(l => l.Split('\t')).ToDictionary(x => x[0] + "\u0001" + x[1], x => JsonSerializer.Deserialize<Dictionary<string, float>>(x[2])!)
        : [];
    public string Version => inner.Version;
    public IReadOnlyList<Aml.Core.Ports.TaggedSpan> Tag(string text)
    {
        var t = tagged[docOfText[text]];
        return t.Spans.Select(s => new Aml.Core.Ports.TaggedSpan(s[0], s[1], t.Roles[s[2]])).ToList();
    }
    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(IReadOnlyList<(string Mention, string Sentence)> pairs)
    {
        var missing = pairs.Where(p => !memo.ContainsKey(p.Mention + "\u0001" + p.Sentence)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var got = inner.Events(missing);
            using var w = File.AppendText(events);
            foreach (var (p, a) in missing.Zip(got))
            {
                var d = a.ToDictionary(x => x.Key, x => x.Value);
                memo[p.Mention + "\u0001" + p.Sentence] = d;
                w.WriteLine($"{p.Mention.Replace('\t', ' ').Replace('\n', ' ')}\t{p.Sentence.Replace('\t', ' ').Replace('\n', ' ')}\t{JsonSerializer.Serialize(d)}");
            }
        }
        return pairs.Select(p => (IReadOnlyDictionary<string, float>)memo[p.Mention + "\u0001" + p.Sentence]).ToList();
    }
    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(string text, IReadOnlyList<(string Mention, string Sentence)> pairs)
    {
        if (!inner.RequiresDocumentContext) return Events(pairs);
        var prefix = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inner.Version + "\u0002" + text))) + "\u0002";
        var missing = pairs.Where(p => !memo.ContainsKey(prefix + p.Mention + "\u0001" + p.Sentence)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var got = inner.Events(text, missing);
            using var writer = File.AppendText(events);
            foreach (var (pair, answer) in missing.Zip(got))
            {
                var copy = answer.ToDictionary(x => x.Key, x => x.Value);
                memo[prefix + pair.Mention + "\u0001" + pair.Sentence] = copy;
                writer.WriteLine($"{prefix}{pair.Mention.Replace('\t', ' ').Replace('\n', ' ')}\t{pair.Sentence.Replace('\t', ' ').Replace('\n', ' ')}\t{JsonSerializer.Serialize(copy)}");
            }
        }
        return pairs.Select(p => (IReadOnlyDictionary<string, float>)memo[prefix + p.Mention + "\u0001" + p.Sentence]).ToList();
    }
}

static partial class Program
{
    internal static int Dropped;
    // Evidence words per event type (fixed lists, not tuned): a model event is kept only when its best sentence holds one.
    internal static readonly Dictionary<NewsEventType, string> Keywords = new()
    {
        [NewsEventType.Indictment] = "起訴|公訴|簡易判決處刑",
        [NewsEventType.Referral] = "移送|函送|移請",
        [NewsEventType.Investigation] = "偵辦|偵查|調查|偵訊|追查|查緝|專案|搜索|指揮",
        [NewsEventType.Arrest] = "逮捕|拘提|拘捕|緝獲|查獲|到案|落網|現行犯",
        [NewsEventType.Detention] = "羈押|收押|押",
        [NewsEventType.Questioning] = "訊問|詢問|約談|到案說明|複訊|訊後",
        [NewsEventType.Conviction] = "判處|判刑|判決|有罪|定讞|處有期徒刑|處拘役|宣判|應執行|犯.{0,20}罪",
        [NewsEventType.Search] = "搜索|搜查|搜出",
        [NewsEventType.Confiscation] = "沒收|追徵",
        [NewsEventType.AssetRestraint] = "扣押|凍結|查扣|警示",
        [NewsEventType.PriorRecord] = "前科|累犯|曾因|曾犯|曾經|前案",
        [NewsEventType.Bail] = "交保|具保|停止羈押|釋放|請回|限制住居|限制出境|責付",
        [NewsEventType.Appeal] = "上訴|抗告|更審|再審|更一審|二審",
        [NewsEventType.Acquittal] = "無罪",
        [NewsEventType.NonIndictment] = "不起訴|緩起訴",
        [NewsEventType.Fine] = "罰金",
        [NewsEventType.RegulatoryPenalty] = "罰鍰|裁罰|停止買賣|處分|糾正|警告",
        [NewsEventType.WantedNotice] = "通緝",
        [NewsEventType.Extradition] = "引渡|遣返|遣送",
    };
}

static partial class Program
{
    internal static readonly System.Text.RegularExpressions.Regex Boiler = new("案經.{0,80}(移送|函送|報告|告發|告訴).{0,80}(偵查起訴|提起公訴|聲請簡易判決處刑|起訴)");
    internal static List<string> NewsModelReadingSentences(string t) => (List<string>)typeof(NewsModelReading).GetMethod("Sentences", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [t])!;
}
static class Regex2 { public static bool IsMatch(string s, string p) => System.Text.RegularExpressions.Regex.IsMatch(s, p); }
