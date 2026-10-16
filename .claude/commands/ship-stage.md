---
description: Build one TempScore pipeline stage end-to-end — architect confirms the contract, senior-dev implements, review-stage runs, pm scope-adjusts and owns ticket status. Takes a stage name or a Linear ticket ID (TMT-N / MED-N).
argument-hint: [stage name ("FrameExtractor") or Linear ticket ID ("TMT-8" / "MED-12")]
allowed-tools: Read, Grep, Glob, Edit, Write, Bash, Task
---

Ship **$1** end-to-end.

0. **Resolve the target** — `$1` is either a bare stage name (e.g. "FrameExtractor") or a Linear ticket ID: `TMT-N` is the epic's plan-local candidate ID, `MED-N` is its real Linear identifier (see `epic/Epic-TempScore-Mac-ThinkPad.md` for the mapping, workspace `mediaactai`, team `MED`, project `TempScore`).
   - **Ticket ID given**: have `pm` fetch the issue from Linear and move it to **In Progress**. Use its Objective, Files, Behavior, Acceptance criteria, and Draft `/goal` as the spec for steps 2–5 below — not a re-derivation from prose.
   - **Bare stage name given**: proceed as before, using `10hoursPlan.md` and `TempScore-Implementation-Guide.md` as the spec.
1. **Confirm the contract** — if `architect` hasn't already frozen this stage's input/output shape, launch it first via `Task`. Don't let `senior-dev` start against an unfrozen contract.
2. **Implement** — launch `senior-dev` to build the stage against that contract and spec.
3. **Review** — run `/review-stage $1` on the result.
4. **Iterate** — `senior-dev` fixes every blocking finding; re-run `/review-stage $1` until the board is clean. `security`'s sign-off is never skipped, even under time pressure.
5. **Scope-adjust** — `pm` decides what to do with any non-blocking finding: fix now, or add it to `10hoursPlan.md`'s cut list (or a new/linked Linear ticket) with a reason.
6. **Close out** — if `$1` was a Linear ticket, `pm` moves it to **Done** and posts a completion comment (outcome, verdict-table summary, anything deferred). If the comment call fails, say so explicitly — don't silently skip it.
7. **Report** — stage name, time spent vs. the window's budget, the final verdict table, anything deferred.

There is no PR, CI, or auto-merge here — this is a solo/local build with no git remote wired up. "Done" means the working tree has the change, the board from step 3/4 is clean, and (if a ticket) Linear reflects it — not that something was merged.
