---
trigger: manual
description: Build two file-disjoint TempScore lanes concurrently against a frozen shared contract (e.g. C# pipeline vs Python music service for H1.0-3.0). Reconciled locally by pm, no auto-merge. @-mention to invoke.
---

Run a parallel build wave the way `other-project/`'s `ship-parallel` does, minus the Linear/GitHub/auto-merge machinery this repo doesn't have.

1. **Freeze the shared contract first** — `architect` locks the JSON/HTTP shapes both lanes build against (Frame/Scene/MusicPlan/Segment, the `/health`+`/generate` contract, the run-folder layout) before either lane starts.
2. **Declare the lanes, file-disjoint** — state explicitly which files each lane owns, e.g. Lane A: `app/Services/*.cs` (TempScore.Core/Models/Services are already shipped — consumed via `ProjectReference`, not touched here); Lane B: `music_service/*.py`. If a lane would need to touch a file the other owns, stop and fall back to sequential `ship-stage` for the overlapping part.
3. **Build concurrently** — `invoke_subagent` for `senior-dev` once per lane, each scoped only to its own files, against the frozen contract, without coordinating mid-build.
4. **Review each lane independently** — run `review-stage` separately per lane as each finishes.
5. **Reconcile** — `pm` confirms both lanes still agree with the frozen contract, merges both into the working tree, and scope-adjusts any findings.
6. **Report** — table: lane, files touched, verdict, time vs. the wave's budget window.

No auto-merge or CI gate — step 5's reconciliation is a direct local merge, since there's no GitHub remote or CI pipeline wired up for this build.
