"""Fetch the judgments behind the court releases from the Judicial Yuan judgment API (data.judicial.gov.tw/jdg/api).
RUN THIS YOURSELF, in your own terminal: it asks for your open-data account and password with getpass (nothing is
echoed, written to disk or logged), keeps the token in memory only, and reads AML_JUDGMENTS/jids.jsonl (from jids.py).
For each release it tries the candidate jids in order and saves the first judgment found as AML_JUDGMENTS/docs/<doc>.json.
A judgment saved before is asked for again, and deleted when the service says it has been removed (a removed judgment
may not be kept). The service only answers between 00:00 and 06:00; one request a second. A release whose requests
failed is counted as a request error, not as none found; 5 failed requests in a row stop the run. Prints counts only.
usage: python -I fetch.py   (there is deliberately no way to pass the password but typing it)"""
import datetime, getpass, json, os, ssl, sys, time, urllib.request
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "stage-model"))
import paths

# Python 3.13+ verifies certificates in strict RFC 5280 mode, and the site's certificate chain lacks a Subject Key
# Identifier. The chain and the host name are still verified; only that strict extra check is turned off.
CTX = ssl.create_default_context()
CTX.verify_flags &= ~ssl.VERIFY_X509_STRICT

API = "https://data.judicial.gov.tw/jdg/api/"
UA = "Mozilla/5.0 (compatible; research-client)"
GONE = "查無資料"  # the service's answer for a judgment it does not (or no longer) hold


def post(path, body):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json", "User-Agent": UA}, method="POST")
    with urllib.request.urlopen(req, timeout=60, context=CTX) as r: return json.loads(r.read().decode("utf-8"))


def gone(answer):
    """The answer holds no judgment and says there is none."""
    return not (isinstance(answer, dict) and answer.get("JID")) and GONE in json.dumps(answer, ensure_ascii=False)


LIMIT = 5  # failed requests in a row that stop the run (service hours, network or an expired token)


class Stop(SystemExit):
    """Not an Exception: the per-request handlers let it through."""


class Api:
    """send(path, body) one request a second, counting calls and failed requests; Stop after LIMIT failures in a row."""
    def __init__(self, send, pause=1.0): self.send, self.pause = send, pause; self.calls = self.failed = self.run = 0

    def __call__(self, path, body):
        time.sleep(self.pause); self.calls += 1
        try: answer = self.send(path, body)
        except Exception:
            self.failed += 1; self.run += 1
            if self.run >= LIMIT: raise Stop("stopped: %d requests in a row failed (service hours, network or token); the counts above are the releases done so far" % LIMIT)
            raise
        self.run = 0; return answer


def first(jids, ask):
    """(jid, judgment) of the first jid the service holds, else (None, whether a request failed: then 'none' is not known)."""
    failed = False
    for j in jids:
        try: doc = ask(j)
        except Exception: failed = True; continue
        if isinstance(doc, dict) and doc.get("JID"): return j, doc
    return None, failed


def main():
    if not 0 <= datetime.datetime.now().hour < 6: raise SystemExit("The API answers only from 00:00 to 06:00; run it then.")
    user = input("司法院資料開放平臺帳號: ").strip(); password = getpass.getpass("密碼（不會顯示）: ")
    auth = post("Auth", {"user": user, "password": password}); del password
    token = auth.get("Token") or raise_(auth)
    docs = paths.JUDGMENTS + "docs/"
    os.makedirs(docs, exist_ok=True)
    api = Api(post); ask = lambda j: api("JDoc", {"token": token, "j": j})
    found = missing = errors = kept = removed = 0
    try:
        for line in open(paths.JUDGMENTS + "jids.jsonl", encoding="utf-8"):
            r = json.loads(line); path = docs + r["doc"] + ".json"
            if os.path.exists(path):
                try: answer = ask(json.load(open(path, encoding="utf-8"))["jid"])
                except Exception: errors += 1; continue  # no answer is no evidence of removal: the file stays
                if gone(answer): os.remove(path); removed += 1
                else: kept += 1
                continue
            j, doc = first(r["jids"], ask)
            if j: json.dump({"doc": r["doc"], "jid": j, "judgment": doc}, open(path, "w", encoding="utf-8"), ensure_ascii=False); found += 1
            elif doc: errors += 1
            else: missing += 1
    finally:
        print("releases with a judgment %d, none found %d, request errors %d (not known; saved files kept), already had %d, "
              "removed by the service and deleted %d | API calls %d, failed requests %d"
              % (found, missing, errors, kept, removed, api.calls, api.failed))


def raise_(answer): raise SystemExit("驗證失敗: " + str(answer.get("error", answer)))


if __name__ == "__main__":
    if sys.argv[1:] == ["--selfcheck"]:
        assert gone({"error": GONE}) and gone(GONE) and not gone({"JID": "X", "JFULLX": {"JFULLCONTENT": GONE}}) and not gone({"error": "timeout"})
        api = Api(lambda path, j: {"JID": j} if j == "y" else {"error": GONE} if j == "n" else 1 / 0, pause=0)
        ask = lambda j: api("JDoc", j)
        assert first(["x", "y"], ask) == ("y", {"JID": "y"}) and (api.calls, api.failed, api.run) == (2, 1, 0)
        assert first(["n"], ask) == (None, False) and first(["x", "n"], ask) == (None, True) and api.run == 0
        for k in range(1, LIMIT + 1):
            try: ask("x"); assert False
            except ZeroDivisionError: assert k < LIMIT
            except Stop: assert k == LIMIT and api.run == LIMIT
        print("selfcheck OK")
    else: main()
