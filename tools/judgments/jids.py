"""Candidate judgment ids (jid) for the court releases of the court sets, from the release's title (court and case number)
and dates (the release date in the court news list, and dates written next to 宣判 in the text). Writes jids.jsonl:
{doc, jids: [...]} in try order. Counts only. jid = court code + M, ROC year, case word, number, yyyymmdd, check number.
Reads AML_DATA court/raw/index.jsonl and court/list.jsonl and the court sets in AML_TRAIN measurements/court; writes
AML_JUDGMENTS/jids.jsonl (paths.py). Development sets only.
usage: jids.py [<set> ...]   (default accept accept3 accept4; lower-case set names)
       jids.py --selfcheck   the guards refuse Accept5, V11, accept5 before reading or writing"""
import json, os, re, sys, collections
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "stage-model"))
import paths

COURTS = {  # Judicial Yuan court codes (criminal judgments add "M")
    "最高法院": "TPS", "臺灣高等法院臺中分院": "TCH", "臺灣高等法院臺南分院": "TNH", "臺灣高等法院高雄分院": "KSH",
    "臺灣高等法院花蓮分院": "HLH", "福建高等法院金門分院": "KMH", "臺灣高等法院": "TPH", "智慧財產及商業法院": "IPC",
    "臺灣臺北地方法院": "TPD", "臺灣新北地方法院": "PCD", "臺灣士林地方法院": "SLD", "臺灣桃園地方法院": "TYD",
    "臺灣新竹地方法院": "SCD", "臺灣苗栗地方法院": "MLD", "臺灣臺中地方法院": "TCD", "臺灣彰化地方法院": "CHD",
    "臺灣南投地方法院": "NTD", "臺灣雲林地方法院": "ULD", "臺灣嘉義地方法院": "CYD", "臺灣臺南地方法院": "TND",
    "臺灣高雄地方法院": "KSD", "臺灣橋頭地方法院": "CTD", "臺灣屏東地方法院": "PTD", "臺灣臺東地方法院": "TTD",
    "臺灣花蓮地方法院": "HLD", "臺灣宜蘭地方法院": "ILD", "臺灣基隆地方法院": "KLD", "臺灣澎湖地方法院": "PHD",
    "福建金門地方法院": "KMD", "福建連江地方法院": "LCD", "臺灣高雄少年及家事法院": "KSY",
}
CASE = re.compile(r"(\d{2,3})\s*年度\s*([\u4e00-\u9fff]{1,8}?)\s*字\s*第\s*(\d+)\s*號")
DATE = re.compile(r"(\d{2,3})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日")


def ymd(roc, m, d): return "%04d%02d%02d" % (int(roc) + 1911, int(m), int(d))


def main(sets):
    if bad := [s for s in sets if not re.fullmatch(r"[a-z0-9_-]+", s)]: raise SystemExit("not a set name: %d argument(s)" % len(bad))
    if held := paths.held(sets): raise SystemExit("held back: " + ", ".join(held))
    index = {r["id"]: r for r in map(json.loads, open(paths.COURT_RAW + "index.jsonl", encoding="utf-8"))}
    listed = {r["url"]: r for r in map(json.loads, open(paths.DATA + "court/list.jsonl", encoding="utf-8"))}
    c, out = collections.Counter(), []
    for s in sets:
        for line in open(paths.COURT + "%s.jsonl" % s, encoding="utf-8"):
            d = json.loads(line); c["docs"] += 1
            row = index.get(d["doc"], {}); title = row.get("title", "").replace("台灣", "臺灣")
            court = next((k for k in sorted(COURTS, key=len, reverse=True) if title.startswith(k) or k in title[:30]), None)
            m = CASE.search(title) or CASE.search(d["text"])
            if not court: c["no court in title"] += 1; continue
            if not m: c["no case number"] += 1; continue
            dates = []
            if (lr := listed.get(row.get("url"))) and (dm := re.match(r"(\d{2,3})-(\d{1,2})-(\d{1,2})", lr.get("date", ""))): dates.append(ymd(*dm.groups()))
            for dm in DATE.finditer(d["text"]):
                if "宣判" in d["text"][max(0, dm.start() - 15):dm.end() + 15]: dates.append(ymd(*dm.groups()))
            dates = list(dict.fromkeys(dates))
            if not dates: c["no date"] += 1; continue
            jids = ["%sM,%s,%s,%s,%s,%d" % (COURTS[court], m.group(1), m.group(2), m.group(3), dt, k) for dt in dates for k in (1, 2)]
            out.append({"doc": d["doc"], "set": s, "jids": jids}); c["with candidates"] += 1
    os.makedirs(paths.JUDGMENTS, exist_ok=True)
    with open(paths.JUDGMENTS + "jids.jsonl", "w", encoding="utf-8") as w:
        for r in out: w.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(dict(c), "| candidate jids", sum(len(r["jids"]) for r in out))


def selfcheck():
    """The guards refuse before anything is read or written."""
    for sets in (["Accept5"], ["V11"], ["accept5"], ["accept", "v11"]):
        try: main(sets); raise AssertionError(sets)
        except SystemExit as e: assert str(e).startswith(("not a set name", "held back")), e
    print("selfcheck OK")


if __name__ == "__main__": selfcheck() if sys.argv[1:] == ["--selfcheck"] else main(sys.argv[1:] or ["accept", "accept3", "accept4"])
