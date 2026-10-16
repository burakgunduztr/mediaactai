---
trigger: manual
description: Build one TempScore pipeline stage end-to-end - architect confirms the contract, senior-dev implements, review-stage runs, pm scope-adjusts and owns ticket status. Takes a stage name or a Linear ticket ID (TMT-N / MED-N). @-mention to invoke.
---

Ship one stage end-to-end.

0. **Resolve the target** — the given name is either a bare stage name (e.g. "FrameExtractor") or a Linear ticket ID: `TMT-N` is the epic's plan-local candidate ID, `MED-N` is its real Linear identifier (see `epic/Epic-TempScore-Mac-ThinkPad.md` for the mapping, workspace `mediaactai`, team `MED`, project `TempScore`).
   - **Ticket ID given**: have `pm` fetch the issue from Linear and move it to **In Progress**. Use its Objective, Files, Behavior, Acceptance criteria, and Draft `/goal` as the spec for steps 2–5 below.
   - **Bare stage name given**: proceed as before, using `10hoursPlan.md` and `TempScore-Implementation-Guide.md` as the spec.
1. **Confirm the contract** — if `architect` hasn't already frozen this stage's input/output shape, do that first.
2. **Implement** — `senior-dev` builds the stage against that contract and spec.
3. **Review** — run the `review-stage` rule on the result.
4. **Iterate** — `senior-dev` fixes every blocking finding; re-run `review-stage` until the board is clean. `security`'s sign-off is never skipped.
5. **Scope-adjust** — `pm` decides: fix now, or add to `10hoursPlan.md`'s cut list (or a new/linked Linear ticket) with a reason.
6. **Close out** — if a Linear ticket was given, `pm` moves it to **Done** and posts a completion comment (outcome, verdict-table summary, anything deferred). If the comment call fails, say so explicitly.
7. **Report** — stage name, time spent vs. the window's budget, final verdict table, anything deferred.

No PR, CI, or auto-merge — solo/local build, no git remote yet. "Done" means the working tree has the change, the board is clean, and (if a ticket) Linear reflects it.
