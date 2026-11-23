// The legal-stage vote measured through the real system entry (SourceConditions.Determine on an adverse-media Profile
// with the news attached), readers = [OnnxStageClassifier, replayed language model answers]. Prints counts only, never text.
// Usage: Aml.StageEval <llm answers for report sets> <llm answers for court sets> [--heldback] [onnx=<dir>] [out=<dir>]
//        [judgments=<dir>] [sets=<dir with report/ and court/>]
using System.Text.Json;
using Aml.Core.Screening;
using Aml.Core.Subjects;
using Aml.Infra.Models;
using Aml.StageEval;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string Arg(string k, string d) => args.FirstOrDefault(a => a.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..] ?? d;
string[] keys = ["onnx", "out", "judgments", "sets"];
// An option is --name or key=value (key letters only); anything else is a positional path.
static bool IsKey(string a) => a.IndexOf('=', StringComparison.Ordinal) is > 0 and var i && a[..i].All(char.IsAsciiLetter);
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && !IsKey(a)).ToList();
var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal) ? a != "--heldback" : IsKey(a) && !keys.Contains(a[..a.IndexOf('=', StringComparison.Ordinal)])).ToList();
if (positional.Count != 2 || unknown.Count > 0)
{
    if (unknown.Count > 0) Console.Error.WriteLine($"unknown option {unknown[0]}");
    Console.Error.WriteLine("usage: Aml.StageEval <llm answers for report sets> <llm answers for court sets> [--heldback] [onnx=<dir>] [out=<dir>] [judgments=<dir>] [sets=<dir>]");
    return 2;
}
var (reportAnswers, courtAnswers) = (positional[0], positional[1]);
var heldback = args.Contains("--heldback");
var onnx = Arg("onnx", @"C:\tmp\aml-train\downloads\aml47\onnx");
var outDir = Arg("out", @"C:\tmp\eval-sys\out");
var setDir = Arg("sets", @"C:\tmp\aml-train\measurements");
// judgments=<dir>: <doc>.json with the judgment's full text; the rules read it for that doc (column "judgment").
var judgmentDir = Arg("judgments", "");
foreach (var (what, dir) in new[] { ("sets", setDir), ("onnx", onnx), ("report answers", reportAnswers), ("court answers", courtAnswers), ("judgments", judgmentDir) })
{
    if (dir.Length > 0 && !Directory.Exists(dir)) { Console.Error.WriteLine($"no {what} directory {dir}"); return 2; }
}
Console.WriteLine($"sets {setDir}\nonnx {onnx}\nreport answers {reportAnswers}\ncourt answers {courtAnswers}\nout {outDir}\njudgments {(judgmentDir.Length == 0 ? "-" : judgmentDir)}");
Directory.CreateDirectory(outDir);

string Report(string f) => Path.Combine(setDir, "report", f);
string Court(string f) => Path.Combine(setDir, "court", f);
var sets = new List<(string Name, string File, string Answers, bool Held)>
{
    ("dev", Report("dev-stage.jsonl"), reportAnswers, false), ("dev2", Report("dev2-stage.jsonl"), reportAnswers, false),
    ("v9", Report("v9-stage.jsonl"), reportAnswers, false), ("accept", Court("accept.jsonl"), courtAnswers, false),
    ("accept3", Court("accept3.jsonl"), courtAnswers, false), ("accept4", Court("accept4.jsonl"), courtAnswers, false),
};
if (heldback) { sets.Add(("v11", Report("v11-stage.jsonl"), reportAnswers, true)); sets.Add(("accept5", Court("accept5.jsonl"), courtAnswers, true)); }
// A set whose file is absent is skipped (a measurement root may hold only some sets); its answers are then not needed.
foreach (var s in sets.Where(s => !File.Exists(s.File))) Console.WriteLine($"skipped {s.Name}: no file {s.File}");
sets.RemoveAll(s => !File.Exists(s.File));
if (sets.Count == 0) { Console.Error.WriteLine($"no set file under {setDir}"); return 2; }
foreach (var f in sets.Select(s => Path.Combine(s.Answers, s.Name + ".jsonl")))
{
    if (!File.Exists(f)) { Console.Error.WriteLine($"no file {f}"); return 2; }
}

using var classifier = new OnnxStageClassifier(onnx);
string? JudgmentText(string doc)
{
    var f = Path.Combine(judgmentDir, doc + ".json");
    if (judgmentDir.Length == 0 || !File.Exists(f)) return null;
    using var j = JsonDocument.Parse(File.ReadAllText(f));
    return j.RootElement.GetProperty("judgment").GetProperty("JFULLX").GetProperty("JFULLCONTENT").GetString()?.Replace("\r\n", "\n", StringComparison.Ordinal);
}

var clock = System.Diagnostics.Stopwatch.StartNew();
Console.WriteLine($"{"set",-8} {"n",5} {"RULES",6} {"CLASSIF",7} {"LLM",5} {"VOTE",5} {"SPLIT",5} {"llm-none",8} {"clf-none",8} {"DECIDED",7} {"RIGHT",5}");
foreach (var (name, file, answersDir, held) in sets)
{
    var llm = new ReplayStageReader(Path.Combine(answersDir, name + ".jsonl"));
    var docs = File.ReadLines(file).Select(l => JsonSerializer.Deserialize<Doc>(l, Doc.Options)!).ToList();
    int n = 0, rules = 0, clf = 0, lm = 0, vote = 0, split = 0, llmNone = 0, clfNone = 0, decided = 0, decidedRight = 0;
    var rows = new List<string> { "doc\tperson\treference\trules\tclassifier\tllm\tvote\tbasis\tjudgment" };
    foreach (var d in docs)
    {
        var jtext = JudgmentText(d.Name);
        foreach (var p in d.People.Where(p => p.Role.StartsWith("PER-", StringComparison.Ordinal) && p.Party == "Suspect"))
        {
            var gold = ReferenceStage.Of(d.Events.Where(e => p.Mentions.Contains(e.Mention) && !e.Labels.Contains("prior")).SelectMany(e => e.Labels));
            var two = p.Mentions.Where(m => m.EnumerateRunes().Count() >= 2).ToList();
            var names = two.Count > 0 ? two : p.Mentions.ToList();
            var profile = new SourceProfile("DJ:1", null, names, [], ["TW"], [], [new SourceCategory("Adverse Media", null)], [], [], [], null, [], []);
            llm.Key = (d.Name, p.Id);
            var c = SourceConditions.Determine(profile, false, d.Text, stageReaders: [classifier, llm]).Single();
            var readers = c.StageReaders ?? [];
            string Of(string r) => readers.FirstOrDefault(x => x.Reader == r)?.Stage?.ToString() ?? "-";
            string ru = Of("rules"), cl = Of("classifier"), ll = Of("llm"), vo = c.LegalStage.ToString();
            n++; rules += ru == gold ? 1 : 0; clf += cl == gold ? 1 : 0; lm += ll == gold ? 1 : 0; vote += vo == gold ? 1 : 0;
            if (c.LegalStageBasis == "STAGE_VOTE_UNANIMOUS") { decided++; decidedRight += vo == gold ? 1 : 0; }
            split += c.LegalStageBasis == "STAGE_VOTE_SPLIT" ? 1 : 0; llmNone += ll == "-" ? 1 : 0; clfNone += cl == "-" ? 1 : 0;
            var ju = jtext is null ? "-" : SourceConditions.Determine(profile, false, jtext, stageReaders: []).Single().LegalStage.ToString();
            // Held-back sets: aggregate counts only, no per-person rows.
            if (!held) rows.Add($"{d.Name}\t{p.Id}\t{gold}\t{ru}\t{cl}\t{ll}\t{vo}\t{c.LegalStageBasis ?? "-"}\t{ju}");
        }
    }
    if (!held) File.WriteAllLines(Path.Combine(outDir, name + ".tsv"), rows);
    Console.WriteLine($"{name,-8} {n,5} {rules,6} {clf,7} {lm,5} {vote,5} {split,5} {llmNone,8} {clfNone,8} {decided,7} {decidedRight,5}");
}
Console.WriteLine($"elapsed {clock.Elapsed.TotalSeconds:F0} s");
return 0;
