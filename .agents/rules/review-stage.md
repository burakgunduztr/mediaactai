---
trigger: manual
description: Run the TempScore review board (security mandatory, architect, code-reviewer, qa, pm) against a finished stage or the full pipeline. Never ships anything, only reports. @-mention to invoke.
---

Run the review board against the named stage (or the whole working tree since the last review, if "full pipeline" or nothing specific was named).

1. Identify the exact files that changed for this stage — don't review the whole repo if the stage is narrow.
2. Run these checks, in parallel where independent (`invoke_subagent` for each):
   - `security` — mandatory, never skipped, never downgraded tier. Offline boundary, subprocess argument safety, content-safety filters, dependency pinning per its own agent file.
   - `architect` — contract conformance against the frozen Frame/Scene/MusicPlan/Segment/run-folder/HTTP shapes.
   - `code-reviewer` — correctness, resource lifecycle, the fallback-is-real check, ffmpeg/ffprobe invocation safety.
   - `qa` — the pass criteria for this specific stage from its own agent file.
3. Each posts **APPROVE** or **REQUEST CHANGES** with concrete findings: file/line and the exact failure scenario.
4. `pm` scope-adjusts every REQUEST CHANGES finding: fix now, or defer with a one-line reason, weighed against the 10-hour clock and the cut order in its own agent file. `pm` can never overrule a `security` REQUEST CHANGES on the offline boundary.
5. Output a verdict table: `Agent | Verdict | Key finding`. End with a one-line overall call.

Never merges, commits, or ships anything — only produces verdicts for `ship-stage` or a human to act on.
