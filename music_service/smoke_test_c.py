"""TMT-4 smoke test C: MusicGen small, dry-run on Mac CPU.

Identical to the guide's own Step 1 Test C script, with `.to("cuda")` changed
to `.to("cpu")` in both places (the Mac has no CUDA GPU). Timing here is
*not* representative of the RTX PRO 500 — this only proves the model loads,
the prompt format works, and a playable WAV comes out. The real timing
decision (MusicGen vs. ACE-Step) happens on the ThinkPad in smoke tests C/D.
"""

from transformers import AutoProcessor, MusicgenForConditionalGeneration
import scipy.io.wavfile
import torch

proc = AutoProcessor.from_pretrained("facebook/musicgen-small")
model = MusicgenForConditionalGeneration.from_pretrained("facebook/musicgen-small").to("cpu")
inputs = proc(text=["slow melancholic piano and strings, 70 bpm, A minor"], return_tensors="pt").to("cpu")
audio = model.generate(**inputs, max_new_tokens=500)  # about 50 tokens per second of audio
rate = model.config.audio_encoder.sampling_rate
scipy.io.wavfile.write("test.wav", rate, audio[0, 0].cpu().numpy())
print(f"wrote test.wav at {rate} Hz")
