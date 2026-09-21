"""Does the ruleset rank a real improvement as an improvement?

    export TYPESAFE_API_KEY=...
    python ordering.py
    python ordering.py v0 v1 --rules comment-rules.json

Every case in fixtures.json is judged alone, against a threshold. That cannot catch a rule which is
simply backwards, because backwards rules still put easy cases on the right side of the line. This
script scores two versions of the same real file — before and after a change made to satisfy the
rules — and checks the later one flags less. Two rules were withdrawn on this evidence; see their
`withdrawn` field in rules.json.

With no arguments it runs the comparisons recorded in ordering.json. Blocks there are precomputed,
so there is no parser here; samples/ holds the source they came from, for reading.
"""
import argparse
import json
import os
import statistics as st
import sys
from concurrent.futures import ThreadPoolExecutor

from calibrate import (API_URL, HERE, REPEATS, USAGE,  # noqa: F401  (API_URL used by post)
                       load_rules, median_scores, post, state_group)


def blocks_for(group):
    """Which precomputed blocks carry the fields this group of rules asked for."""
    if group == ("*",):
        return "comment_blocks"
    return "types" if group[0] == "signatures" else "declarations"


def score_blocks(blocks, questions, key, rules, workers=8):
    """Median score per rule per block, one request per state group."""
    with ThreadPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(lambda b: median_scores(b["state"], questions, key, rules), blocks))


def compare(doc, before, after, change, rules_path, key, include_withdrawn):
    rules, questions = load_rules(rules_path)
    if not include_withdrawn:
        questions = {q: v for q, v in questions.items() if not rules[q].get("withdrawn")}

    for version in (before, after):
        if version not in doc["versions"]:
            sys.exit(f"unknown version {version!r}; have: {', '.join(doc['versions'])}")

    by_group = {}
    for g in {state_group(rules[q]) for q in questions}:
        asked = {q: v for q, v in questions.items() if state_group(rules[q]) == g}
        field = blocks_for(g)
        by_group[g] = {v: (score_blocks(doc["versions"][v][field], asked, key, rules),
                           doc["versions"][v][field])
                       for v in (before, after)}

    # several groups read the same blocks, so count each block set once
    per_field = {blocks_for(g): by_group[g] for g in by_group}
    counts = {v: sum(len(p[v][1]) for p in per_field.values()) for v in (before, after)}
    print(f"\n=== {before} ({counts[before]} blocks)  ->  {after} ({counts[after]} blocks)")
    if change:
        print(f"    {change}")
    print(f"\n    {'rule':30} {'before':>8} {'after':>8} {'change':>8}   blocks flagged")

    failures = 0
    for rule in sorted(questions):
        per_version = by_group[state_group(rules[rule])]
        cells, flags = [], []
        for version in (before, after):
            scored, _ = per_version[version]
            vals = [s[rule] for s in scored]
            cells.append(st.mean(vals))
            flags.append(sum(1 for v in vals if v >= rules[rule]["threshold"]))
        delta = cells[1] - cells[0]
        # A mean can rise harmlessly when the surviving blocks are denser, so the failure is a rule
        # that flags MORE after the fix. A rise with no flags either side is noted, not failed.
        bad = flags[1] > flags[0] and rules[rule].get("withdrawn") is None
        failures += bad
        mark = ("  <-- ranks the fix worse" if bad else
                "  (mean up, flags none)" if delta > 0.02 and not any(flags) else "")
        print(f"    {rule:30} {cells[0]:8.2f} {cells[1]:8.2f} {delta:+8.2f}"
              f"   {flags[0]:2} -> {flags[1]:<2}{mark}")

    # every group over all its blocks: rules are disjoint across groups, so nothing is counted twice
    totals = [sum(1 for per in by_group.values() for s in per[v][0]
                  for r, score in s.items() if score >= rules[r]["threshold"])
              for v in (before, after)]
    print(f"\n    total flags over every rule: {totals[0]} -> {totals[1]}")
    return failures


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("before", nargs="?", help="version to score first; omit to run the recorded comparisons")
    ap.add_argument("after", nargs="?", help="version it was changed into")
    ap.add_argument("--rules", default=os.path.join(HERE, "rules.json"),
                    help="ruleset for an explicit comparison (default: the code rules)")
    ap.add_argument("--include-withdrawn", action="store_true",
                    help="also score the withdrawn rules, to reproduce why they were withdrawn")
    args = ap.parse_args()

    key = os.environ.get("TYPESAFE_API_KEY")
    if not key:
        sys.exit("set TYPESAFE_API_KEY")
    if bool(args.before) != bool(args.after):
        sys.exit("give both versions, or neither")

    doc = json.load(open(os.path.join(HERE, "ordering.json"), encoding="utf-8"))
    if args.before:
        todo = [(args.before, args.after, "", args.rules)]
    else:
        todo = [(c["before"], c["after"], c["change"], os.path.join(HERE, c["rules"]))
                for c in doc["comparisons"]]

    failures = sum(compare(doc, *t, key, args.include_withdrawn) for t in todo)
    print(f"\n[{USAGE['requests']} requests, {USAGE['input']} input tokens]")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
