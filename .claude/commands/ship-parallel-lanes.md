---
description: Build two file-disjoint TempScore lanes concurrently against a frozen shared contract (e.g. the C# pipeline vs the Python music service for H1.0-3.0). Reconciled locally by pm, no auto-merge.
argument-hint: [optional — which two lanes, defaults to "C# pipeline services" and "Python music service"]
allowed-tools: Read, Grep, Glob, Edit, Write, Bash, Task
---

Run a parallel build wave the way `otherprojects/`'s `ship-parallel` does, minus the Linear-ticket-queue/GitHub-PR/auto-merge machinery that workflow assumed.

1. **Freeze the shared contract first** — launch `architect` to lock the JSON/HTTP shapes both lanes will build against (Frame/Scene/MusicPlan/Segment, the `/health`+`/generate` contract, the run-folder layout) before either lane starts. This is the one thing that must not drift mid-wave.
2. **Declare the lanes, file-disjoint** — state explicitly which files each lane owns, e.g.:
   - Lane A: `app/Services/*.cs`, `app/Models/*.cs`
   - Lane B: `music_service/*.py`
   If a lane would need to touch a file the other lane owns, stop — that work isn't parallel-safe, fall back to sequential `/ship-stage` for the overlapping part.
3. **Build concurrently** — launch `senior-dev` once per lane via `Task` (these run as independent subagent invocations), each scoped only to its own files, building against the frozen contract from step 1 without coordinating mid-build.
4. **Review each lane independently** — run `/review-stage` separately per lane as each finishes.
5. **Reconcile** — launch `pm` to confirm both lanes still agree with the frozen contract (no silent drift on either side), merge both into the working tree, and scope-adjust any findings from step 4.
6. **Report** — a table: lane, files touched, verdict, time vs. the H1.0–3.0 budget (or whichever window this wave covers).

No `gh`, no auto-merge, no CI gate — reconciliation in step 5 is a local merge `pm`/the human does directly, since there's no GitHub remote or CI pipeline wired up for this build.
