"""Score labeled fixtures against the rules, so every number in the README is reproducible.

    export TYPESAFE_API_KEY=...
    python calibrate.py                    # score every case, compare to its expectation
    python calibrate.py --include-withdrawn   # also the cases showing why two rules were dropped
    python calibrate.py --record           # store today's scores, so later runs can report drift
    python calibrate.py --case silent_guard_flag

Fixtures carry their state inline, so there is no extractor here and nothing to point at a codebase.
Stdlib only.
"""
import argparse
import json
import os
import statistics as st
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor

API_URL = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-latest"  # resolves forward; re-record baselines when it moves
HERE = os.path.dirname(os.path.abspath(__file__))
REPEATS = 3  # a rule sitting on its threshold answers bimodally; one run cannot tell that from noise

USAGE = {"input": 0, "requests": 0}


def post(state, questions, key):
    body = json.dumps({"model": MODEL, "state": state, "questions": questions}).encode()
    req = urllib.request.Request(API_URL, body, {"Authorization": f"Bearer {key}",
                                                 "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        answer = json.load(resp)
    USAGE["input"] += answer.get("usage", {}).get("input_tokens", 0)
    USAGE["requests"] += 1
    return {q: a["noul"] for q, a in answer["answers"].items()}


def load_rules(path):
    """A rule's context describes the state it asked for, and nothing else.

    Code rules declare `state`, so each gets the descriptions for its own fields plus the facts it
    names. Comment rules all read the same two fields and share one context. A field a rule never
    asked for still sways it, so rules are only asked together when they want the same fields.
    """
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    questions = {}
    for rid, r in data["rules"].items():
        if "state" in r:
            parts = [data["state_docs"][f] for f in r["state"]]
            parts.append(data["judge_only"])
            parts += [data["facts"][f] for f in r.get("facts", ())]
            context = " ".join(parts)
        else:
            context = data["context"]
        q = {"type": "noul", "instructions": f"{context}\n\n{r['instructions']}"}
        if "criteria" in r:
            q["criteria"] = r["criteria"]
        questions[rid] = q
    return data["rules"], questions


def state_group(rule):
    """Rules sharing a request share its state, so they must want exactly the same fields."""
    return tuple(rule["state"]) if "state" in rule else ("*",)


def median_scores(state, questions, key, rules=None):
    """One request per group of rules wanting the same fields, so none sees state it did not ask for."""
    groups = {}
    for q, v in questions.items():
        groups.setdefault(state_group(rules[q]) if rules else ("*",), {})[q] = v
    out = {}
    for fields, mine in groups.items():
        sent = state if fields == ("*",) else {f: state[f] for f in fields if f in state}
        runs = [post({"language": "C#", **sent}, mine, key) for _ in range(REPEATS)]
        out.update({q: st.median(r[q] for r in runs) for q in mine})
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rules", default=os.path.join(HERE, "rules.json"))
    ap.add_argument("--fixtures", default=os.path.join(HERE, "fixtures.json"))
    ap.add_argument("--record", action="store_true",
                    help="overwrite each case's stored score with today's, so drift is measured from here")
    ap.add_argument("--case", action="append", help="only this case; repeatable")
    ap.add_argument("--include-withdrawn", action="store_true",
                    help="also run the cases kept as evidence for withdrawn rules")
    args = ap.parse_args()

    key = os.environ.get("TYPESAFE_API_KEY")
    if not key:
        sys.exit("set TYPESAFE_API_KEY")

    rules, questions = load_rules(args.rules)
    doc = json.load(open(args.fixtures, encoding="utf-8"))
    cases = [c for c in doc["cases"]
             if (not args.case or c["name"] in args.case)
             and (args.case or args.include_withdrawn or not c.get("withdrawn_only"))]
    cases.sort(key=lambda c: bool(c.get("withdrawn_only")))  # withdrawn rules last, under their own heading

    def ask(case):
        asked = {r: questions[r] for r in case["rules"] if r in questions}
        missing = [r for r in case["rules"] if r not in questions]
        return case, median_scores(case["state"], asked, key, rules) if asked else {}, missing

    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(ask, cases))

    failures = 0
    heading_shown = False
    for case, scores, missing in results:
        if case.get("withdrawn_only") and not heading_shown:
            heading_shown = True
            print("\n-- rules withdrawn; kept as evidence, not in use --")
        for rule in missing:
            print(f"FAIL  {case['name']:34} {rule:24} rule no longer exists")
            failures += 1
        for rule, score in sorted(scores.items()):
            flagged = score >= rules[rule]["threshold"]
            expect = case.get("expect_by_rule", {}).get(rule, case["expect"])
            straddles = expect == "straddles"  # on the line: reported, never failed
            ok = straddles or flagged == (expect == "flag")
            # these cases document why a rule was withdrawn, so their red lines are the point
            failures += not ok and not case.get("withdrawn_only")
            drift = ""
            if rule in case.get("baseline", {}):
                delta = score - case["baseline"][rule]
                drift = f"  ({delta:+.2f} vs baseline)" if abs(delta) >= 0.05 else ""
            mark = "~~~ " if straddles else ("ok  " if ok else "FAIL")
            print(f"{mark}  {case['name']:34} {rule:24}"
                  f" {score:.2f} vs {rules[rule]['threshold']:.2f} want={expect}{drift}")
        if args.record:
            case["baseline"] = {**case.get("baseline", {}), **scores}

    if args.record:
        with open(args.fixtures, "w", encoding="utf-8", newline="\n") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print("baselines recorded")

    print(f"[{USAGE['requests']} requests, {USAGE['input']} input tokens]")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
