"""TMT-4 smoke test C: MusicGen small, dry-run on Mac CPU.

Identical to the guide's own Step 1 Test C script, with `.to("cuda")` changed
to `.to("cpu")` in both places (the Mac has no CUDA GPU). Timing here is
*not* representative of the RTX PRO 500 — this only proves the model loads,
the prompt format works, and a playable WAV comes out. The real timing
decision (MusicGen vs. ACE-Step) happens on the ThinkPad in smoke tests C/D.
"""

import os

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

proc = AutoProcessor.from_pretrained("facebook/musicgen-small", local_files_only=True)
model = MusicgenForConditionalGeneration.from_pretrained(
    "facebook/musicgen-small", local_files_only=True
).to("cpu")
inputs = proc(text=["slow melancholic piano and strings, 70 bpm, A minor"], return_tensors="pt").to("cpu")
audio = model.generate(**inputs, max_new_tokens=500)  # about 50 tokens per second of audio
rate = model.config.audio_encoder.sampling_rate
scipy.io.wavfile.write("test.wav", rate, audio[0, 0].cpu().numpy())
print(f"wrote test.wav at {rate} Hz")
