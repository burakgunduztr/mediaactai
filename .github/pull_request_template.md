<!-- Title: <feat|fix|refactor|test|docs|chore>: <summary> — name the stage/window this belongs to, e.g. "feat: FrameExtractor (H1-3 scaffolding)" -->

## What & why
<!-- One or two sentences: what changed, and which window in notes/10hoursPlan.md it belongs to. -->

## Stage
<!-- Which pipeline stage(s) this touches — mirror the six stages in notes/TempScore-Implementation-Guide.md -->
- Stage: Extract frames | Describe frames | Summarize | Plan music | Generate music | Mux | UI | Other
- Touches the offline boundary (network bind, subprocess, content-safety filter)? **yes / no** — if yes, `security` review is a hard gate (see `.github/CODEOWNERS`)
- Changes a frozen contract (Frame/Scene/MusicPlan/Segment/run-folder/HTTP shape)? **yes / no** — if yes, `architect` sign-off is required

## Definition of done
- [ ] `dotnet test` passes for the pure-logic project, and/or `music_service` starts and answers `/health`
- [ ] Matches the architect-frozen contract — no silently renamed/reshaped field
- [ ] If this stage has a fallback path (JSON retry, rule-based plan, etc.), it was actually exercised with bad input, not just described
- [ ] Tested against at least one of the three sample clips (dialogue/action/calm) where applicable
- [ ] Every run still writes its artifact to `%LOCALAPPDATA%\TempScore\runs\<timestamp>\`
- [ ] No new outbound network call; `music_service` still binds `127.0.0.1` only
- [ ] No secrets/API keys committed

### Manual smoke steps
<!-- exactly what you ran and what you observed -->

## Review Log
<!-- each agent appends its verdict (APPROVE / REQUEST CHANGES + key finding); keep newest last -->
- security:
- architect:
- code-reviewer:
- qa:
- pm:
