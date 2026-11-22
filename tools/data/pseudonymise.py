"""A pseudonymised copy of a data bundle: every person name the labels know replaced, consistently in every file, by a
generated name of the same length, so character offsets in labels and rows stay valid. Prints counts only, never a
name, a text or a real -> pseudonym pair.

usage: pseudonymise.py <original root> <dest root> --map <map file> [--ner <NER model dir> [--sample N]] [--extend] [--seed HEX]

Inventory: every person of the labels and measured sets (persons[].mentions / forms, people[] with role PER-*), the
NER person spans of the rows (ner-*.jsonl PER-* spans), the classifier rows' names and the recheck lists' forms; with
--extend also the names the cue detector finds in the original raw texts (a surname and two characters right after a
role word: 被告, 證人, 法官 ...), and the residual is printed before and after.
Map (forms of 2+ characters; one-character forms are never replaced):
  surname   each real surname -> one pseudonym surname everywhere: a common surname (the stage rules' list,
            StageGlossary.CommonSurnames) -> another common one, a rarer one -> a rarer one, compound -> compound,
            so the rules' "is this a surname" reading is unchanged; surname + mask (○ 〇 Ｏ ◯ Ｘ, and 某 姓 after
            a common or compound surname) is mapped for every known surname, so masked forms stay consistent with
            the full names
  given     each given name -> one generated given name (common given-name characters in the tokenizer's vocabulary)
  full      surname + given = pseudonym surname + pseudonym given; a form inside a full name maps to the same slice
  masked    surname + mask/suffix (王○○, 王某, 王姓, 王男) -> pseudonym surname + the same suffix; a form masking some
            characters of the full name (王○明) maps position by position
  other     aliases: a generated string of the same length; Latin names token by token (case kept); role words and
            forms several different people share (被告, 車手 ...) are not names and are left
Pseudonyms come from a secret seed (random, or --seed) through HMAC, so they cannot be recomputed from the code; no
pseudonym is, or contains, a form, full name or given name of the inventory, and no two full names, given names or
aliases share one. Replacement is one pass, longest form first, over every text file; JSON files string literal by
string literal (escapes decoded; a changed literal re-encoded); a label's event sentence, trigger and also-sentences
are then cut from its document's pseudonymised text where the original stood, so they stay substrings of it.
Limit: names are mapped whole, so a form that occurs only inside ANOTHER person's longer name (陳文 inside 陳文雄)
no longer occurs there; a character-level cipher would keep that but lets one known name decode others.
The map (seed, surname / given / form tables) goes ONLY to --map, which must lie inside the original root; it is
never written into the destination.
Checks: occurrences of inventory forms left unreplaced in the destination as a reader sees it, JSON escapes decoded
(must be 0; a real form string that is pseudonym text, such as a pseudonym surname + ○○ equal to another person's
masked form, or a coincidence beside a pseudonym, is counted apart); person candidates in the destination's raw
texts that no pseudonym covers (unlabelled people), as counts: the cue detector over every raw text and, with --ner, the
deployed NER model (ONNX, read only) over a seeded sample of --sample texts (first 3,000 characters each; the whole
corpus would take hours on a CPU).
"""
import argparse, collections, hashlib, hmac, json, os, pathlib, random, re, secrets, sys, unicodedata

sys.dont_write_bytecode = True
HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import bundle  # noqa: E402

REPO = HERE.parents[1]
GLOSSARY = REPO / "src" / "Aml.Core" / "Screening" / "Stage" / "StageGlossary.cs"


def common_surnames():
    m = re.search(r'CommonSurnames\s*=\s*"([^"]+)"', GLOSSARY.read_text(encoding="utf-8")) if GLOSSARY.exists() else None
    return m.group(1) if m else "陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮馬董溫唐藍蔣石古紀姚連馮歐程湯田康姜白汪鄒尤巫黎涂龔嚴韓袁金童陸夏柳邵錢伍倪于譚駱熊任甘秦顧毛章史官萬俞雷粘饒"


COMMON = common_surnames()
RARE = "".join(dict.fromkeys(c for c in "闕凌崔尹孔辛武辜陶段龍韋葛池孟褚殷麥賀賈莫文管關向包丘梅華利裴樊房全佘左花魯安鮑郝穆塗邢蒲成谷常閻練盛鄔耿聶符申祝繆陽解曲岳齊籃應單舒畢喬龎翟牛鄞留季覃卜項凃喻商滕焦車虞苗戚牟雲巴艾樂臧封豐景尚靳路易蘭鄺冉宗談楚匡柴鞠鞏桂温黄郁竺栗屈晏邴計伏湛鄢冷寇卞蒙査荊幸冼" if c not in COMMON))
COMPOUND = ["歐陽", "司馬", "諸葛", "上官", "東方", "夏侯", "司徒", "皇甫", "公孫", "端木", "張簡", "范姜", "鍾離", "慕容", "長孫", "宇文", "令狐", "西門", "南宮", "呼延", "澹臺", "左丘"]
GIVEN_POOL = "".join(dict.fromkeys("志明俊傑宏偉家豪建華文雄淑芬美玲惠君怡婷雅佳穎冠宇承恩柏翰軒子涵詠晴品妤宜蓁欣德仁義忠信和平安康寧靜秀英珍珠蘭菊梅桂鳳麗娟萍琪瑜瑩瑋璇琳慧敏嘉凱翔鴻鵬飛龍虎彥昌盛興隆旺福祿壽喜春夏秋冬晨曦光輝煌耀榮富貴金銀玉石山川河海松柏樺楓宗祖孟仲叔季世維崇哲誠勇毅剛強立廷堯舜禹湯昇恆永泰晉逸皓澤霖雨雲霞虹雪霜竹蓮荷芳蕙茹萱"))
MASKS = "○〇Ｏ◯●＊*□ＸX"
GENERIC_MASK = "○〇Ｏ◯Ｘ某姓"   # surname + one of these is mapped for every known surname
SUFFIX = set(MASKS) | set("某姓男女嫌君員婦翁氏童伯叔姨嫂哥姐弟妹父母妻夫子")
GENERIC = ("被告", "嫌", "犯", "男", "女", "先生", "小姐", "該", "警", "法官", "檢察", "律師", "車手", "人頭", "負責人", "董事", "老闆",
           "主任", "經理", "院長", "部長", "局長", "少年", "證人", "告訴", "被害", "民眾", "網友", "某", "甲", "乙", "丙", "丁", "己", "庚")
LATIN_STOP = {"mr", "mrs", "ms", "dr", "jr", "sr", "ltd", "co", "inc", "the", "and", "of", "limited", "company", "corp",
              "per", "org", "suspect", "victim", "official", "other", "witness", "conviction", "acquittal", "nonindictment",
              "indictment", "wantednotice", "investigation", "prior", "stated", "unknown", "alleged", "wanted", "indicted",
              "dismissed", "convicted", "acquitted", "missing", "true", "false", "null", "http", "https", "www", "com", "gov"}
CJK = re.compile(r"[㐀-䶿一-鿿豈-﫿]")
TEXT_SUFFIXES = {".txt", ".json", ".jsonl", ".tsv", ".md", ".csv"}


def normalise(s):
    """As tools/stage-model/textutil.normalise: NFKC per character, one to one."""
    return "".join(n if len(n := unicodedata.normalize("NFKC", c)) == 1 else c for c in s)


def trie_regex(words):
    """One regex matching any of words, longest first at each position."""
    trie = {}
    for w in words:
        node = trie
        for c in w: node = node.setdefault(c, {})
        node[""] = True

    def build(node):
        alts = [re.escape(c) + build(node[c]) for c in sorted(k for k in node if k)]
        if not alts: return ""
        body = alts[0] if len(alts) == 1 else "(?:" + "|".join(alts) + ")"
        return "(?:" + body + ")?" if "" in node else body
    return re.compile(build(trie)) if trie else re.compile(r"(?!x)x")


# ---------------------------------------------------------------- inventory

def person_forms(x, out, parent=None):
    """Collect the forms of every person entry anywhere in a JSON value."""
    if isinstance(x, dict):
        fs = x.get("mentions", x.get("forms"))
        if parent in ("persons", "people") and fs is not None and (parent == "persons" or str(x.get("role", "")).startswith("PER-")):
            fs = [fs] if isinstance(fs, str) else [f for f in fs if isinstance(f, str)]
            if fs: out.append(fs)
        for k, v in x.items(): person_forms(v, out, k)
    elif isinstance(x, list):
        for v in x: person_forms(v, out, parent)


def inventory(root, files):
    """Person entries (lists of forms) from every data file of the bundle."""
    entries = []
    for p in files:
        if p.suffix == ".json":
            try: person_forms(json.loads(p.read_text(encoding="utf-8-sig")), entries)
            except ValueError: pass
        elif p.suffix == ".jsonl":
            for l in open(p, encoding="utf-8"):
                if not l.strip(): continue
                try: r = json.loads(l)
                except ValueError: continue
                person_forms(r, entries)
                if isinstance(r, dict) and isinstance(r.get("spans"), list) and isinstance(r.get("text"), str):
                    entries += [[r["text"][s:e]] for s, e, lab in r["spans"] if str(lab).startswith("PER")]
                if isinstance(r, dict) and "person" in r and isinstance(r.get("name"), str): entries.append([r["name"]])
        elif p.name == "persons.tsv":
            entries += [l.rstrip("\n").split("\t")[2].split("|") for l in open(p, encoding="utf-8") if l.count("\t") >= 2]
    return entries


# ---------------------------------------------------------------- the map

class Namer:
    def __init__(self, secret, vocab, banned):
        self.secret, self.banned = secret, banned
        self.pool = "".join(c for c in GIVEN_POOL if not vocab or c in vocab)
        self.ban_rx = trie_regex(banned)

    def rng(self, real, k): return random.Random(hmac.new(self.secret, ("%s#%d" % (real, k)).encode(), hashlib.sha256).digest())

    def clean(self, s): return s not in self.banned and not self.ban_rx.search(s)

    def draw(self, real, ok=lambda s: True):
        """A same-length string for real: CJK characters from the pool, Latin letters with the same case, others kept."""
        for k in range(500):
            r = self.rng(real, k)
            s = "".join(r.choice(self.pool) if CJK.match(c) else
                        (r.choice("ABCDEFGHIJKLMNOPRSTUVWY") if c.isupper() else r.choice("abcdefghiklmnoprstuvwy")) if c.isascii() and c.isalpha()
                        else c for c in real)
            if s != real and self.clean(s) and ok(s): return s
        raise SystemExit("no pseudonym found within 500 draws (counts only: one form)")


def surname_of(b):
    return b[:2] if b[:2] in COMPOUND and len(b) >= 3 else b[0]


def lead(f):
    """The known surname a form opens with, else ''."""
    return f[:2] if f[:2] in COMPOUND else f[0] if f and (f[0] in COMMON or f[0] in RARE) else ""


def is_name_like(f):
    return 2 <= len(f) <= 4 and all(CJK.match(c) for c in f) and (f[:2] in COMPOUND or f[0] in COMMON or f[0] in RARE)


def build_map(entries, secret, vocab):
    c = collections.Counter()
    entries = sorted({tuple(dict.fromkeys(g for f in e for g in (f, normalise(f)) if len(g) >= 2)) for e in entries} - {()})
    # bases: full names; a two-character form that is someone's given name is a given name, not a base
    long_b = {f for e in entries for f in e if is_name_like(f) and len(f) >= 3 and not all(ch in SUFFIX for ch in f[len(surname_of(f)):])}
    givens = {b[len(surname_of(b)):] for b in long_b}
    two_b = {f for e in entries for f in e if is_name_like(f) and len(f) == 2 and f[1] not in SUFFIX and f not in givens}
    bases = long_b | two_b
    # no pseudonym may be or hold a real form, full name or given name of 2+ characters
    banned = {f for e in entries for f in e} | bases | {b[len(surname_of(b)):] for b in bases if len(b) - len(surname_of(b)) >= 2}
    nm = Namer(secret, vocab, banned)
    surn = {surname_of(b) for b in bases} | {s for e in entries for f in e if (s := lead(f)) and len(f) > len(s) and all(ch in SUFFIX for ch in f[len(s):])}
    S = {}
    for group in (list(COMMON), list(RARE), COMPOUND):  # a common surname maps to a common one, rare to rare, compound to compound
        real = sorted(s for s in surn if s in group)
        if not real: continue
        r = random.Random(hmac.new(secret, ("surnames %d" % len(group)).encode(), hashlib.sha256).digest())
        free = [s for s in group if s not in real]; used = list(real); r.shuffle(free); r.shuffle(used)
        img = (free + used)[:len(real)]  # images that are no real surname of the data first
        for _ in range(100):
            fixed = [i for i in range(len(real)) if img[i] == real[i]]
            if not fixed or len(real) == 1: break
            for i in fixed: j = (i + 1) % len(real); img[i], img[j] = img[j], img[i]
        S.update(zip(real, img))  # a fixed point left (a one-surname group) keeps its surname; the given name still changes
    c["surnames"] = len(S)
    users = collections.defaultdict(set)
    for b in bases: users[b[len(surname_of(b)):]].add(surname_of(b))
    G, used = {}, set()  # used: images already given out, so no two real names share a pseudonym
    for g in sorted(users):
        G[g] = nm.draw(g, lambda s, g=g: (len(s) < 2 or s not in used) and all(nm.clean(S[x] + s) and S[x] + s not in used for x in users[g]))
        used.update([G[g]] * (len(g) >= 2) + [S[x] + G[g] for x in users[g]])
    c["given names"] = len(G); c["full names"] = len(bases)
    M = {b: S[surname_of(b)] + G[b[len(surname_of(b)):]] for b in bases}
    for g, v in G.items():
        if len(g) >= 2: M.setdefault(g, v)
    structural = set()  # surname-level forms: their images may equal another person's real masked form
    for s, v in S.items():
        if v == s: continue
        if len(s) >= 2: M.setdefault(s, v); structural.add(s)
        for m in GENERIC_MASK if s in COMMON or len(s) == 2 else GENERIC_MASK[:5]:  # 向某, 成姓: words; ○ masks only
            M.setdefault(s + m, v + m); structural.add(s + m)
    known = trie_regex(M)
    owners = collections.defaultdict(list)  # form -> the base sets of the entries that list it
    for e in entries:
        for f in e: owners[f].append(frozenset(x for x in e if x in bases))
    shared = lambda f: len(set(owners[f])) > 1 or (len(owners[f]) >= 4 and not any(owners[f]))
    latin = {}
    for e in entries:
        eb = [x for x in e if x in bases]
        for f in e:
            if f in M: continue
            b = next((b for b in eb if f in b), None)
            sur = lead(f)
            if b: M[f] = M[b][b.find(f):b.find(f) + len(f)]; c["form inside a full name"] += 1
            elif sur and S.get(sur, sur) != sur and len(f) > len(sur) and all(ch in SUFFIX for ch in f[len(sur):]): M[f] = S[sur] + f[len(sur):]; structural.add(f); c["surname + suffix"] += 1
            elif (b := next((b for b in eb if len(b) == len(f) and all(x == y or x in MASKS for x, y in zip(f, b))), None)):
                M[f] = "".join(x if x in MASKS else M[b][i] for i, (x, y) in enumerate(zip(f, b))); c["partly masked"] += 1
            elif known.search(f): c["holds a mapped form (replaced inside)"] += 1
            elif re.search(r"[A-Za-z]", f):
                for t in re.findall(r"[A-Za-z]+", f):
                    if len(t) >= 3 and t.lower() not in LATIN_STOP and not t.islower(): latin.setdefault(t, None)
                c["latin"] += 1
            elif any(w in f for w in GENERIC) or shared(f) or len(CJK.findall(f)) < 2: c["left: role word, shared or too short"] += 1
            else: M[f] = nm.draw(f, lambda s: s not in used); used.add(M[f]); c["alias"] += 1
    toks = set(latin)
    for t in sorted(latin): latin[t] = nm.draw(t, lambda s: s not in toks and s.lower() not in LATIN_STOP and s not in latin.values())
    c["latin tokens"] = len(latin)
    for k in list(M):
        if normalise(k) != k:
            M.setdefault(normalise(k), M[k])
            if k in structural: structural.add(normalise(k))
    assert all(len(k) == len(v) and k != v for k, v in M.items()), "a pseudonym of another length"
    c["forms mapped"] = len(M)
    c["non-surname pseudonyms equal to a real form"] = sum(v in banned for k, v in M.items() if k not in structural)
    owner = collections.defaultdict(set)
    for k, v in M.items(): owner[v].add(normalise(k))
    c["pseudonyms shared by two real forms"] = sum(len(o) > 1 for o in owner.values())
    return {"S": S, "G": G, "map": M, "latin": latin, "structural": structural}, c


# ---------------------------------------------------------------- replacing

STR = re.compile(r'"(?:[^"\\\n]|\\.)*"')  # a JSON string literal


class Replacer:
    """Replaces in what a reader sees: a text file whole, a JSON file string literal by string literal (escapes decoded,
    a changed literal re-encoded with ensure_ascii as it was). check=True also counts, in each replaced unit, the real
    forms (and Latin tokens) that touch no replacement: left unreplaced, the residual that must be 0."""

    def __init__(self, m, check=False):
        self.M, self.L, self.structural = m["map"], m["latin"], m.get("structural", set())
        self.rx = trie_regex(self.M)
        self.lrx = re.compile(r"(?<![A-Za-z])(" + "|".join(sorted(map(re.escape, self.L), key=len, reverse=True)) + r")(?![A-Za-z])") if self.L else None
        self.check, self.res = check, collections.Counter()

    def plain(self, t, spans=None):
        def f(m):
            if spans is not None: spans.append(m.span())
            return self.M[m.group()]
        t = self.rx.sub(f, t)
        if self.lrx:
            def g(m):
                if spans is not None: spans.append(m.span())
                return self.L[m.group()]
            t = self.lrx.sub(g, t)
        return t

    def unit(self, s):
        if not self.check: return self.plain(s)
        spans = []; s2 = self.plain(s, spans)
        hit = bytearray(len(s2) + 1)
        for a, b in spans: hit[a:b] = bytes([1]) * (b - a)
        for y in [*self.rx.finditer(s2), *(self.lrx.finditer(s2) if self.lrx else [])]:
            self.res["left unreplaced" if not any(hit[y.start():y.end()]) else
                     "masked surname form (a pseudonym image)" if y.group() in self.structural else "beside or inside a pseudonym"] += 1
        return s2

    def text(self, t, json_like):
        if not json_like: return self.unit(t)

        def lit(m):
            g = m.group()
            try: s = json.loads(g)
            except ValueError: self.res["unparsable string literals (left)"] += 1; return g
            s2 = self.unit(s)
            return g if s2 == s else json.dumps(s2, ensure_ascii="\\u" in g)
        return STR.sub(lit, t)


# ---------------------------------------------------------------- NER detector

class Ner:
    """The deployed NER model (ONNX), characters as tokens (bert-base-chinese is per character for CJK); person spans."""

    def __init__(self, d):
        import numpy, onnxruntime
        self.np = numpy
        self.s = onnxruntime.InferenceSession(str(pathlib.Path(d) / "model.onnx"), providers=["CPUExecutionProvider"])
        self.vocab = {w: i for i, w in enumerate(pathlib.Path(d, "vocab.txt").read_text(encoding="utf-8").split("\n"))}
        cfg = json.loads(pathlib.Path(d, "config.json").read_text(encoding="utf-8"))
        self.labels = {int(k): v for k, v in cfg["id2label"].items()}
        self.unk, self.cls, self.sep = self.vocab["[UNK]"], self.vocab["[CLS]"], self.vocab["[SEP]"]

    def spans(self, text, win=510, step=448, batch=8, min_p=0.0):
        np = self.np
        pos = [i for i, ch in enumerate(text) if not ch.isspace()]
        ids = [self.vocab.get(text[i].lower() if text[i].isascii() else text[i], self.unk) for i in pos]
        starts = list(range(0, max(1, len(ids) - win + step), step)) or [0]
        lab = [0] * len(ids); prob = [0.0] * len(ids)
        for b in range(0, len(starts), batch):
            chunk = [ids[s:s + win] for s in starts[b:b + batch]]; L = max(len(x) for x in chunk) + 2
            inp = np.zeros((len(chunk), L), dtype=np.int64); att = np.zeros_like(inp)
            for k, x in enumerate(chunk): inp[k, :len(x) + 2] = [self.cls] + x + [self.sep]; att[k, :len(x) + 2] = 1
            lo = self.s.run(["logits"], {"input_ids": inp, "attention_mask": att, "token_type_ids": np.zeros_like(inp)})[0]
            e = np.exp(lo - lo.max(-1, keepdims=True)); pr = e / e.sum(-1, keepdims=True)
            for k, s in enumerate(starts[b:b + batch]):
                lo_off = 0 if s == 0 else (win - step) // 2   # overlapping windows: keep the middle part
                for j in range(lo_off, len(chunk[k])):
                    lab[s + j] = int(pr[k, j + 1].argmax()); prob[s + j] = float(pr[k, j + 1].max())
        out, i = [], 0
        while i < len(ids):
            name = self.labels[lab[i]]
            if name.startswith("B-PER"):
                j = i + 1
                while j < len(ids) and self.labels[lab[j]] == "I" + name[1:]: j += 1
                ps = prob[i:j]
                if min(ps) >= min_p: out.append((pos[i], pos[j - 1] + 1))
                i = j
            else: i += 1
        return out


RAW_DIRS = ("aml-data/court/raw/", "aml-data/judgments/", "aml-data/press/", "aml-data/news/")


def raw_files(root, m):
    return [root / f["path"] for f in m["files"] if f["path"].startswith(RAW_DIRS) and f["path"].endswith(".txt")]


CUE = re.compile(r"(?:被告|嫌犯|犯嫌|嫌疑人|嫌疑犯|證人|告訴人|被害人|法官|檢察官|書記官|律師|辯護人|負責人|董事長|總經理|"
                 r"議員|委員|上訴人|聲請人|抗告人|共犯|同案被告)\s*(?:即\s*)?([" + re.escape(COMMON + RARE) + r"][一-鿿]{2})"
                 r"(?=[、，。；：（(等涉於因與及和之的係為則並亦均\s]|$)")


def cue_names(text):
    """A simple detector: a common or rarer surname and two characters right after a role word (被告王大明涉嫌 ...)."""
    return [m.span(1) for m in CUE.finditer(text)]


def uncovered(text, found, ps):
    """Found spans that no pseudonym occurrence overlaps: (count, sha256 of each string)."""
    cover = set()
    for x in ps.finditer(text): cover.update(range(*x.span()))
    hits = [(s, e) for s, e in found if len(CJK.findall(text[s:e])) >= 2 and not cover & set(range(s, e))]
    return len(hits), {hashlib.sha256(text[s:e].encode()).hexdigest() for s, e in hits}


def residual_raw(texts, tables, ner=None, sample=0):
    """Person candidates in pseudonymised raw texts that no pseudonym covers: the cue detector over all texts, the NER
    model over a seeded sample (first 3,000 characters of each). Counts only."""
    ps = trie_regex({v for v in tables["map"].values() if len(v) >= 2} | set(tables["latin"].values()))
    out = {}
    d = n = 0; dist = set()
    for t in texts:
        k, h = uncovered(t, cue_names(t), ps); d += k > 0; n += k; dist |= h
    out["cue detector"] = "texts %d, with candidates %d, candidates %d, distinct %d" % (len(texts), d, n, len(dist))
    if ner and sample:
        pick = random.Random(1).sample(range(len(texts)), min(sample, len(texts)))
        d = n = 0; dist = set()
        for i in pick:
            t = texts[i][:3000]; k, h = uncovered(t, ner.spans(t), ps); d += k > 0; n += k; dist |= h
        out["NER sample"] = "texts %d, with candidates %d, candidates %d, distinct %d" % (len(pick), d, n, len(dist))
    return out


# ---------------------------------------------------------------- main

def raw_index(root, m):
    """doc id -> its raw text file, as tools/news-model/prep.py raw_text picks it (.para.txt before .main.txt)."""
    out = {}
    for f in sorted(raw_files(root, m), key=lambda p: not p.name.endswith(".para.txt")):
        out.setdefault(p_doc(f), f)
    return out


def p_doc(p): return p.name.split(".")[0]


def fix_events(t, r, raw, rep, c):
    """A label file's event sentence, trigger and also-sentences cut from the pseudonymised text of its document at the
    place the original stood, so they stay substrings of it (prep.py finds them there): replacing a short string on its
    own can differ from replacing it inside its sentence when it starts or ends inside a name."""
    try: o, n = json.loads(t), json.loads(r)
    except ValueError: return r
    if not (isinstance(o, dict) and isinstance(o.get("events"), list) and o.get("doc") in raw): return r
    R = raw[o["doc"]].read_text(encoding="utf-8-sig"); R2 = rep.plain(R); ch = False
    for eo, en in zip(o["events"], n["events"]):
        s = eo.get("sentence")
        if isinstance(s, str) and s and s in R:
            i = R.find(s); s2 = R2[i:i + len(s)]
            if en.get("sentence") != s2: en["sentence"] = s2; ch = True; c["sentence"] += 1
            tr = eo.get("trigger")
            if isinstance(tr, str) and tr and tr in s:
                j = s.find(tr); t2 = s2[j:j + len(tr)]
                if en.get("trigger") != t2: en["trigger"] = t2; ch = True; c["trigger"] += 1
        if isinstance(eo.get("also"), list):
            for k, x in enumerate(eo["also"]):
                if isinstance(x, str) and x and x in R and k < len(en.get("also", [])):
                    i = R.find(x); x2 = R2[i:i + len(x)]
                    if en["also"][k] != x2: en["also"][k] = x2; ch = True; c["also"] += 1
    if not ch: return r
    c["files"] += 1
    lines = t.split("\n"); ind = len(lines[1]) - len(lines[1].lstrip(" ")) if len(lines) > 1 and lines[1].startswith(" ") else None
    return json.dumps(n, ensure_ascii="\\u" in t, indent=ind) + ("\n" if t.endswith("\n") else "")


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument("original"); a.add_argument("dest"); a.add_argument("--map", required=True)
    a.add_argument("--ner"); a.add_argument("--sample", type=int, default=120); a.add_argument("--extend", action="store_true"); a.add_argument("--seed")
    x = a.parse_args()
    root, dest, mp = (pathlib.Path(v).resolve() for v in (x.original, x.dest, x.map))
    if root not in mp.parents: sys.exit("--map must lie inside the original root (it is encrypted with it)")
    if dest == root or root in dest.parents or dest in root.parents: sys.exit("dest and original must be separate folders")
    if dest.exists(): sys.exit("dest exists")
    m = json.load(open(root / "MANIFEST.json", encoding="utf-8"))
    if m.get("pseudonymised"): sys.exit("the source is already pseudonymised")
    files = [root / f["path"] for f in m["files"]]
    entries = inventory(root, files); print("inventory: person entries %d" % len(entries))
    ner = Ner(x.ner) if x.ner else None
    vf = root / "aml-train/models/bert-base-chinese/vocab.txt"
    vocab = set(vf.read_text(encoding="utf-8").split("\n")) if vf.exists() else set()
    secret = bytes.fromhex(x.seed) if x.seed else secrets.token_bytes(32)
    if x.extend:  # the residual of the labels' inventory alone, then the cue detector's names added
        raws = [p.read_text(encoding="utf-8-sig") for p in raw_files(root, m)]
        base, _ = build_map(entries, secret, vocab); rp = Replacer(base)
        for k, v in residual_raw([rp.plain(r) for r in raws], base, ner, x.sample).items(): print("residual before --extend, %s: %s" % (k, v))
        extra = [[r[s:e]] for r in raws for s, e in cue_names(r)]
        entries += extra; print("inventory: cue-detector names added %d (distinct %d)" % (len(extra), len({e[0] for e in extra})))
    tables, c = build_map(entries, secret, vocab)
    print("map:", dict(c))
    rep = Replacer(tables, check=True); changed = 0; fixed = collections.Counter()
    raw = raw_index(root, m)
    for f in m["files"]:
        p = root / f["path"]; out = dest / f["path"]; out.parent.mkdir(parents=True, exist_ok=True)
        if p.suffix not in TEXT_SUFFIXES: out.write_bytes(p.read_bytes()); continue
        t = p.read_bytes().decode("utf-8"); js = p.suffix in (".json", ".jsonl")
        r = rep.text(t, js)
        assert len(r) == len(t) or js, "length changed"
        if p.suffix == ".json" and f["path"].startswith("aml-data/labels/") and '"events"' in t: r = fix_events(t, r, raw, rep, fixed)
        changed += r != t
        out.write_bytes(r.encode("utf-8"))
    res = rep.res
    print("label events re-cut from their document's pseudonymised text:", dict(fixed))
    m2 = bundle.manifest(dest, pseudonymised=True, note="pseudonymised copy of a bundle (pseudonymise.py)")
    (dest / "README.txt").write_text(bundle.README.format(kind="pseudonymised", warning=PSEUDO), encoding="utf-8")
    mp.write_text(json.dumps({"format": 1, "warning": "real names: keep inside the encrypted original only", "secret": secret.hex(),
                              "surnames": tables["S"], "given": tables["G"], "map": tables["map"], "latin": tables["latin"]},
                             ensure_ascii=False, indent=1), encoding="utf-8")
    assert not (dest / mp.name).exists()
    print("files %d, changed %d" % (len(m2["files"]), changed))
    print("residual: inventory forms left unreplaced %d | real-form strings that are pseudonym text: %s" % (res.pop("left unreplaced", 0), dict(res)))
    for k, v in residual_raw([p.read_text(encoding="utf-8-sig") for p in raw_files(dest, m2)], tables, ner, x.sample).items():
        print("residual%s, %s: %s" % (" after --extend" if x.extend else "", k, v))
    print("map written inside the original root; dest holds no map")


PSEUDO = """PSEUDONYMISED, NOT ANONYMOUS. Every person name the labels know is replaced by a generated name (consistently, same
length), but the texts are public reports: case numbers, dates, places, courts, amounts and quotes can still identify
the people, and people the labels do not list keep their names. An NER sample of 120 texts still found person spans in
27 (an upper bound: NER also flags non-names), so this copy is not name-free. Treat this copy as personal data too: keep it
encrypted at rest, record every copy, and delete a removed judgment from it as well (bundle.py remove). The map back to
the real names exists only inside the encrypted original (PSEUDONYM-MAP.json) and must never be copied here."""


if __name__ == "__main__":
    main()
