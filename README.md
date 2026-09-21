# Calibrating Jev as a code reviewer

TypeSafe Jev launched a week ago. There are some code-review tools built on it, and I've written one as well. There doesn't seem to be a lot of talk about what it takes to build tools where the output is worth reading.

Getting to something I'd run as part of a code review took me two days (though it's nowhere near perfect). Calibrating is most of the work, so I'm sharing this in the hope you will spend less time. I will show you the measurements, and something so you can re-run some of the numbers in this README instead of taking my word for it.

## The setup

I have a Unity game project, with a Claude.md containing conventions I care about. In particular, I have struggled to make comments concise and to the point; the AI really loves writing verbose comments that say very little.

From these conventions I wrote two Jev rulesets, one for comments and one for code. I also had the AI write me a small CLI tool to interface with Jev (around 400 lines of python) and I tested using 4 project files that I thought were representative of good and bad code and comments.

## Findings

### 1. Judgement scales with state

Adding questions to a request barely moves the answers, however adding *state* changes everything. A consequence of this is that you cannot batch multiple topics in a single request (or at the very least your scoring thresholds will not apply the same).

Here is a table, the result of sending a json with multiple comments + the code they annotate:
| state | questions | `restates_code` |
|---|---|---|
| 1 json comment/code pair referenced by index | 13 | 0.32 |
| 1 json comment/code pair referenced by index| 1 | 0.36 |
| 1 referenced pair + 14 unreferenced pairs | 1 | 0.75 |
| 1 referenced pair + 14 unreferenced pairs | 15 | 0.74 |

I tried various approaches: giving the entire file as context and referencing comment/code with line numbers, separating only comments from the file, giving the entire file as context to the pairs, and more. The conclusion is that context should only be added if the rule needs it to make an accurate judgement. This also led me to categorize rules based on the context they need, and judge rules together only if they share the context.

### 2. Textual tells beat inference

C# says a member with no access modifier is private. [Jev knows that](https://console.typesafe.ai/playground?share=shr_1c122c9a08033fe485eb82f5c3ee6906dac). But making it *derive* rather than *read* the fact costs accuracy.

| context | private | public | gap |
|---|---|---|--|
wording: "declaration carries the keyword public or internal…" | 0.16 | 0.62 | 0.46
wording: "the accessibility of declaration is public or internal…" | 0.43 | 0.61 | 0.18
context: `accessibility: "private"` / `"public"` | 0.07 | 0.63 | 0.56

I conclude that you'd best feed Jev extra information from the code analyzer (if relevant), which I haven't seen anyone do yet.

### 3. Thresholds: worth adjusting

There are cases where you *want* the false positives, because false negatives are worse. I've also found that, regularly, legitimate false positives still indicate a code smell.

The threshold is a result of your codebase, your rule wording, and how easily measurable the rule is to begin with. Most people seem to default the thresholds to 0.8, but I started mine at 0.6 (and many of them actually stayed there).

### 4. The noise is worst exactly where the threshold is

I stumbled upon this one while writing this. At first it seems confidence variance was uniformly tiny: values came back 0.21/0.21/0.22 and 0.92/0.92/0.91. Then a test alternated between passing and failing across twelve runs:

| case | mean | stdev | min-max |
|---|---|---|---|
| `verb_rules_pass_control` | 0.152 | 0.007 | 0.14-0.16 |
| `verb_rules_flag_real` | 0.587 | 0.037 | 0.53-0.65 |
| `unasserted_precondition_flag` | 0.674 | 0.012 | 0.66-0.70 |
| `magic_literal_flag` | 0.812 | 0.006 | 0.80-0.82 |
| `assert_message_flag` | 0.980 | 0.000 | 0.98-0.98 |

So, it seems that noise is larger mid-range than at the extremes. I suppose this is why higher thresholds are recommended. Confident answers are solid, uncertain ones wobble, and thresholds are at the bottom end of this.

## Where it ended up

The fixes it drove were real:
- a map named for its key instead of its value;
- a literal standing in for a rule at five call sites (became a named constant);
- a missing assertion at a public boundary;
- and more.

As it stands, Jev as a review tool for me (and the AI) is a helpful addition. It has clear limits, and works best in small measurable contexts, where it augments code analyzer results. Setting it up takes some time, and works best when curating the instructions yourself. I found the comment rules work the best so far, but I'm sure there's a lot more to be gained.

Both rule sets over my four test files cost ~1.4M input tokens, about six cents.

## Failures

I have also tried more all-encompassing rules. In particular, I spent some time on `verb_disagreement` and `verb_overload`: rules that check whether the same verb is used with different operations, or different verbs are used for identical operations.

`verb_disagreement` scores the class I fixed *worse* than the original, and it does so regardless of the amount of context I include. Splitting one `Declare*` family into `Intern`/`Resolve`/`Create` gives it more verbs, and it reads it as a disagreement.

`verb_overload` looked like it worked at some point, but it was simply because I'd put an example in the question that looks like the actual code. Removing it or using an analogy makes the judgements look like noise.

It's very much possible I have poorly phrased the questions. If anyone has got similar rules working, I'd be interested to know how!

The `verb_` failure cases are still here:

```
python calibrate.py --include-withdrawn
```
```
-- rules withdrawn; kept as evidence, not in use --
ok    verb_rules_pass_control            verb_disagreement        0.16 vs 0.60 want=pass
ok    verb_rules_pass_control            verb_overload            0.20 vs 0.60 want=pass
ok    verb_disagreement_flag_control     verb_disagreement        0.81 vs 0.60 want=flag
FAIL  verb_rules_flag_real               verb_disagreement        0.58 vs 0.60 want=flag
ok    verb_rules_flag_real               verb_overload            0.66 vs 0.60 want=flag
FAIL  verb_rules_pass_real               verb_disagreement        0.67 vs 0.60 want=pass
ok    verb_rules_pass_real               verb_overload            0.51 vs 0.60 want=pass
```

Note that `verb_overload` seems to get both right here, but this is only because the rule describes this very class:

> Their parameters and return types show the difference: one registers what it is given, another mints and returns a new identity, another only looks something up.

Without it, the rule stops telling the two versions apart at all: 0.48 against 0.50 on the signatures, 0.35 against 0.36 on the full method bodies.

## Tips

- Write control pairs before the rule. If they don't separate, the rule measures the wrong thing and no threshold saves it.
- Use the examples from your codebase to set the threshold. Not 0.8 because a README said so.
- Make sure the context is as close to what's needed to judge the rule, no less, no more. Coincidentally, applying this will also help you save tokens.
- Avoid requiring deductions where possible:
    - Ask about what's visible in the text, not what can be deduced from it.
    - Add analyzer information where appropriate.
- Expect it to find things a linter can't, and to be confidently wrong about things a linter can.
- Double- or triple-check that your extractor is doing a good job; wrong data sent to Jev will absolutely lead you astray, because it will not tell you.
- Be careful with AI results when iterating on the rules. It will try to optimize the rules for your code based on Jev's scores, and in doing so, bias it towards said code.

## Caveats

- It's only been one week since the Jev release.
- I'm human.
- The model may change.

## What's here

| file | |
|---|---|
| `calibrate.py` | Scores labeled states against the rules. <br>`--rules FILE` / `--fixtures FILE` pick a ruleset (default: the code one), <br>`--record` stores the run's scores so later runs can report drift (e.g. in case of model changing), <br>`--case NAME` isolates one, <br>`--include-withdrawn` adds the cases for the dropped rules |
| `ordering.py` | Score comparisons are useful for tweaking rules. This scores two versions of the same file and checks the later one flags less. <br>`BEFORE AFTER` compares any two versions (v0, v1, v2), <br>`--rules FILE` picks the ruleset, <br>`--include-withdrawn` also scores the withdrawn rules |
| `rules.json` | all code rules in use, plus some withdrawn rules |
| `comment-rules.json` | all comment rules |
| `fixtures.json` | test cases for the code rules |
| `comment-fixtures.json` | test cases for the comment rules |
| `ordering.json` | blocks precomputed from `samples/`, so this repo needs no parser |
| `samples/` | one real file at three stages, named `v0` `v1` `v2` — the version names `ordering.py` takes. <br>`v0` before any review, <br>`v1` after the comments were cut, <br>`v2` after the renames |

Fixtures carry their state inline, so there's nothing to point at a codebase. The C# test file is from my own project, mostly written by AI.

The comment suite runs against its own files:

```
python calibrate.py --rules comment-rules.json --fixtures comment-fixtures.json
```

### Outputs
```
export TYPESAFE_API_KEY=...
python calibrate.py
```
```
ok    hidden_side_effect_pass            hidden_side_effect       0.41 vs 0.70 want=pass
ok    hidden_side_effect_flag            hidden_side_effect       0.92 vs 0.70 want=flag
ok    unasserted_precondition_pass       unasserted_precondition  0.11 vs 0.60 want=pass
ok    unasserted_precondition_flag       unasserted_precondition  0.67 vs 0.60 want=flag
ok    magic_literal_pass                 magic_literal            0.08 vs 0.60 want=pass
ok    magic_literal_flag                 magic_literal            0.81 vs 0.60 want=flag
ok    assert_message_pass                assert_message           0.10 vs 0.60 want=pass
ok    assert_message_flag                assert_message           0.98 vs 0.60 want=flag
ok    subject_not_action_pass            subject_not_action       0.05 vs 0.60 want=pass
ok    subject_not_action_flag            subject_not_action       0.81 vs 0.60 want=flag
ok    predicate_reads_as_action_pass     predicate_reads_as_action 0.48 vs 0.60 want=pass
ok    predicate_reads_as_action_flag     predicate_reads_as_action 0.80 vs 0.60 want=flag
ok    name_is_implementation_pass        name_is_implementation   0.11 vs 0.60 want=pass
ok    name_is_implementation_flag        name_is_implementation   0.65 vs 0.60 want=flag
ok    assert_and_guard_pass              assert_and_guard         0.04 vs 0.60 want=pass
ok    assert_and_guard_flag              assert_and_guard         0.70 vs 0.60 want=flag
ok    silent_guard_pass                  silent_guard             0.40 vs 0.60 want=pass
ok    silent_guard_flag                  silent_guard             0.89 vs 0.60 want=flag
ok    index_as_field_pass                index_as_field           0.11 vs 0.60 want=pass
ok    index_as_field_flag                index_as_field           0.77 vs 0.60 want=flag
ok    name_is_implementation_flag_map    name_is_implementation   0.80 vs 0.60 want=flag
ok    name_is_implementation_flag_buffer name_is_implementation   0.89 vs 0.60 want=flag
ok    name_is_implementation_pass_role   name_is_implementation   0.16 vs 0.60 want=pass
ok    name_is_implementation_pass_unshared name_is_implementation   0.36 vs 0.60 want=pass
ok    predicate_reads_as_action_flag_select predicate_reads_as_action 0.86 vs 0.60 want=flag
ok    predicate_reads_as_action_flag_refresh predicate_reads_as_action 0.92 vs 0.60 want=flag
ok    predicate_reads_as_action_pass_isvisible predicate_reads_as_action 0.06 vs 0.60 want=pass
ok    predicate_reads_as_action_pass_nonbool predicate_reads_as_action 0.04 vs 0.60 want=pass
```

```
python ordering.py v0 v1 --rules comment-rules.json
```
```
=== v0 (25 blocks)  ->  v1 (15 blocks)

    rule                             before    after   change   blocks flagged
    caller_irrelevant_internals        0.44     0.29    -0.15    9 -> 0
    contradicts_code                   0.42     0.30    -0.12    2 -> 0
    explains_identifier                0.40     0.28    -0.12    2 -> 0
    framework_mechanism                0.07     0.10    +0.03    0 -> 0   (mean up, flags none)
    future_plan                        0.11     0.05    -0.06    1 -> 0
    history                            0.09     0.15    +0.06    0 -> 0   (mean up, flags none)
    not_owner                          0.52     0.32    -0.20    3 -> 0
    pointer_or_callers                 0.14     0.06    -0.08    1 -> 0
    rejected_alternative               0.23     0.12    -0.11    1 -> 0
    restates_code                      0.20     0.28    +0.09    0 -> 0   (mean up, flags none)
    unstructured                       0.50     0.09    -0.41   10 -> 0
    vague_referent                     0.40     0.32    -0.09    2 -> 0
    verbose_prose                      0.24     0.13    -0.10    0 -> 0

    total flags over every rule: 31 -> 0
```
