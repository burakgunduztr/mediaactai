"""TMT-7 smoke test C: MusicGen small, real GPU run on the ThinkPad.

Identical to the guide's own Step 1 Test C script (`.to("cuda")` in both
places) — the counterpart to smoke_test_c.py's Mac CPU dry run (TMT-4),
which only proved the model loads and the prompt format works. This one
is the real thing: run it on the ThinkPad's RTX PRO 2000, not the Mac.

Unlike the guide's original snippet, this prints wall-clock generation
time — the engine decision in notes/testModels.md and notes/switchingToFoundryLocal.md
already locked MusicGen as the engine (ACE-Step dropped from scope), so
this number isn't deciding MusicGen-vs-ACE-Step anymore. It's the baseline
timing for a 10s clip, useful for estimating real per-segment generation
time once TMT-9 wires this into music_service for real.

Requires the CUDA 12.8 torch/torchaudio build, not the Mac CPU wheels —
see notes/installForWindows.md Step 3 and music_service/requirements.txt.
"""

import os
import time

# Same offline fix as music_service/server.py (MED-19/TMT-15 security review):
# from_pretrained() phones home to huggingface.co for a revision check unless
# told not to -- a real finding there, and this standalone smoke test has the
# identical unprotected call, just never carried the fix since it's a
# separate file. Must be set before transformers is imported.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

from transformers import AutoProcessor, MusicgenForConditionalGeneration
import scipy.io.wavfile
import torch

if not torch.cuda.is_available():
    raise SystemExit(
        "torch.cuda.is_available() is False — this script needs the CUDA 12.8 "
        "build on the ThinkPad, not the Mac CPU wheels. See notes/installForWindows.md."
    )

proc = AutoProcessor.from_pretrained("facebook/musicgen-small", local_files_only=True)
model = MusicgenForConditionalGeneration.from_pretrained(
    "facebook/musicgen-small", local_files_only=True
).to("cuda")
inputs = proc(text=["slow melancholic piano and strings, 70 bpm, A minor"], return_tensors="pt").to("cuda")

start = time.perf_counter()
audio = model.generate(**inputs, max_new_tokens=500)  # about 50 tokens per second of audio
elapsed = time.perf_counter() - start

rate = model.config.audio_encoder.sampling_rate
scipy.io.wavfile.write("test.wav", rate, audio[0, 0].cpu().numpy())
print(f"wrote test.wav at {rate} Hz")
print(f"generation time: {elapsed:.1f}s for 10s of audio on {torch.cuda.get_device_name(0)}")
