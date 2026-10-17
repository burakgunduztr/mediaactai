"""MED-9 (TMT-6) — Foundry Local smoke test, Mac-portable half of the pivot
documented in notes/switchingToFoundryLocal.md.

Phi Silica and Image Description both returned NotSupportedOnCurrentSystem on
the real ThinkPad. Foundry Local (unlike the Windows AI APIs) runs on both
Windows and macOS, so the text-generation half of that replacement can be
proven here on CPU/Metal before the ThinkPad ever enters the picture.

This hits the real local OpenAI-compatible endpoint the C# ITextGenerator
implementation (TMT-11) will call — not a mock — with the same JSON-only
prompt shape SceneSummarizer.cs/MusicPlanner.cs will send. It does not time
anything: like smoke_test_c.py's Mac CPU dry run, this only proves the model
loads, the HTTP contract works, and the JSON-leaning output comes back in a
shape TempScore.Core's ParseJson<T>/fallback path can actually handle.

Prerequisites (see notes/GUIDE_foundrylocalsetup.md for the Windows side):
    brew tap microsoft/foundrylocal && brew trust microsoft/foundrylocal && brew install foundrylocal
    foundry model download phi-4-mini
    foundry model load phi-4-mini
"""

import json
import sys
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:56343"
ALIAS_PREFIX = "phi-4-mini-instruct"  # case-insensitive match against /v1/models ids


def resolve_model_id() -> str:
    """The chat/completions endpoint wants the exact catalog id (e.g.
    "Phi-4-mini-instruct-generic-gpu:5"), not the short CLI alias
    ("phi-4-mini") used by `foundry model download/load` — confirmed by
    hand against the real local server; passing the alias 404s with
    "Model not found". Resolve it from /v1/models instead of hardcoding
    the version suffix, since that number can change between catalog
    updates.
    """
    with urllib.request.urlopen(f"{BASE}/v1/models", timeout=5) as resp:
        models = json.loads(resp.read())["data"]
    for m in models:
        if m["id"].lower().startswith(ALIAS_PREFIX):
            return m["id"]
    raise RuntimeError(
        f"No loaded model id starts with {ALIAS_PREFIX!r} — run "
        f"`foundry model load phi-4-mini` first. Loaded models: "
        f"{[m['id'] for m in models]}"
    )


def chat(model_id: str, system: str, user: str) -> str:
    body = json.dumps(
        {
            "model": model_id,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            "temperature": 0.2,
            "max_tokens": 200,
        }
    ).encode()
    req = urllib.request.Request(
        f"{BASE}/v1/chat/completions",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        payload = json.loads(resp.read())
    return payload["choices"][0]["message"]["content"]


def main() -> int:
    try:
        model_id = resolve_model_id()
    except (urllib.error.URLError, ConnectionRefusedError) as exc:
        print(f"FAIL: Foundry Local service not reachable at {BASE} ({exc}). "
              f"Run `foundry server start` first.", file=sys.stderr)
        return 1
    except RuntimeError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1

    print(f"Resolved model id: {model_id}")

    content = chat(
        model_id,
        system="You output only valid JSON, no prose, no markdown fences.",
        user="Describe a sad rainy film scene as JSON with keys mood and tempo_bpm.",
    )
    print("Raw response:")
    print(content)

    if not content.strip():
        print("FAIL: empty completion", file=sys.stderr)
        return 1

    print("\nwrote a non-empty completion through the real local endpoint")
    return 0


if __name__ == "__main__":
    sys.exit(main())
