"""The three helpers of prep.py that the stage-model scripts use, behaviour unchanged."""
import unicodedata


def normalise(s):
    """NFKC per character, kept one to one so offsets stay valid (CJK compatibility ideographs, full-width forms)."""
    out = []
    for c in s:
        n = unicodedata.normalize("NFKC", c)
        out.append(n if len(n) == 1 else c)
    return "".join(out)


def occurrences(text, s):
    i = text.find(s)
    while i >= 0 and s:
        yield i, i + len(s)
        i = text.find(s, i + 1)


def event_label(e):
    lab = e["type"] if e["certainty"] == "Stated" else f'{e["type"]}-{e["certainty"]}'
    return lab
