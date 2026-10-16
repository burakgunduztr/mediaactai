---
name: review-stage
description: Run the TempScore review board (security mandatory, architect, code-reviewer, qa, pm) against a finished stage or the full pipeline. Never ships anything, only reports.
agent: pm
argument-hint: <stage name, e.g. "FrameExtractor", "music_service", or "full pipeline">
---

Run the review board against the stage named in the argument (or the whole working tree since the last review, if "full pipeline" or nothing specific was named).

1. Identify the exact files that changed for this stage — don't review the whole repo if the stage is narrow.
2. Run these checks, in parallel where they're independent:
   - `security` — mandatory, never skipped, never downgraded tier. Check the offline boundary, subprocess argument safety, content-safety filters, dependency pinning per its own agent file.
   - `architect` — contract conformance against the frozen Frame/Scene/MusicPlan/Segment/run-folder/HTTP shapes.
   - `code-reviewer` — correctness, resource lifecycle, the fallback-is-real check, ffmpeg/ffprobe invocation safety.
   - `qa` — the pass criteria for this specific stage from its own agent file (smoke test timing, JSON fallback actually triggering, BPM/segment correction, etc.).
3. Each posts **APPROVE** or **REQUEST CHANGES** with concrete findings: file/line and the exact failure scenario, not a vague note.
4. `pm` scope-adjusts every REQUEST CHANGES finding: fix now, or defer with a one-line reason, weighed against the 10-hour clock and the cut order in its own agent file (stretch goals → mood slider → regenerate → UI polish — never the six-stage pipeline or the offline guarantee). `pm` can never overrule a `security` REQUEST CHANGES on the offline boundary; that one is fixed or the feature doesn't ship this build.
5. Output a verdict table: `Agent | Verdict | Key finding`. End with a one-line overall call: clean, or what's still blocking.

This prompt never merges, commits, or ships anything — it only produces verdicts for `/ship-stage` or a human to act on.
