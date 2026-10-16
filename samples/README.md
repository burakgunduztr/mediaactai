# Sample clips

These three clips are **synthetic placeholders generated via ffmpeg**, not real footage or licensed stock video. `notes/EnvironmentSetupPreMac.md` / the guide's Step 0 call for "your own footage or royalty-free stock," which wasn't available when this ticket (TMT-5 / MED-8) was shipped — so these stand in for it, built to match the *technical* properties the pipeline actually needs to validate against, not the literal semantic content:

| Clip | Duration | Audio track | Scene cuts | Why |
| --- | --- | --- | --- | --- |
| `dialogue.mp4` | 60.0s | Yes (sine tone) | 0 | Low-motion `testsrc2` pattern + an audio track — stands in for a static two-shot conversation. Exercises the "has audio, keep/duck it" path. |
| `action.mp4` | 60.0s | Yes (sine tone) | 4 | Five 12s solid-color segments concatenated with hard cuts between them — exercises `select='gt(scene,0.3)'` cut detection with real, non-zero cut counts. |
| `calm.mp4` | 60.0s | **No** audio stream at all | 0 | Single static color, no audio track — exercises the "clip has no audio track" path TMT-2's acceptance criteria explicitly call for. |

All three verified before shipping: `ffprobe` duration exactly 60.0s, `fps=1/3,scale=768:-2` frame extraction produces exactly 20 frames, and `ffprobe -select_streams a` confirms the audio presence/absence above.

**Swap these for real footage later without changing anything downstream** — same filenames, same ~60s duration, same audio-track pattern (two with audio, one without) is all any later stage depends on.
