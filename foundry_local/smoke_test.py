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

Usage:
    python smoke_test.py                              # uses DEFAULT_BASE (this Mac's port)
    python smoke_test.py --base http://127.0.0.1:<port>  # real ThinkPad finding: the port is
                                                           # assigned fresh per `foundry server
                                                           # status` daemon start and is NOT
                                                           # stable across machines -- confirmed
                                                           # different on the real ThinkPad --
                                                           # get the real port from `foundry
                                                           # server status` first (plain `foundry
                                                           # status` doesn't work there).
    FOUNDRY_LOCAL_BASE_URL=http://127.0.0.1:<port> python smoke_test.py   # equivalent env var form
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_BASE = "http://127.0.0.1:56343"  # this Mac's port only -- see Usage above, don't hand-edit this for another machine
ALIAS_PREFIX = "phi-4-mini-instruct"  # case-insensitive match against /v1/models ids


def resolve_model_id(base: str) -> str:
    """The chat/completions endpoint wants the exact catalog id (e.g.
    "Phi-4-mini-instruct-generic-gpu:5"), not the short CLI alias
    ("phi-4-mini") used by `foundry model download/load` — confirmed by
    hand against the real local server; passing the alias 404s with
    "Model not found". Resolve it from /v1/models instead of hardcoding
    the version suffix, since that number can change between catalog
    updates.
    """
    with urllib.request.urlopen(f"{base}/v1/models", timeout=5) as resp:
        models = json.loads(resp.read())["data"]
    for m in models:
        if m["id"].lower().startswith(ALIAS_PREFIX):
            return m["id"]
    raise RuntimeError(
        f"No loaded model id starts with {ALIAS_PREFIX!r} — run "
        f"`foundry model load phi-4-mini` first. Loaded models: "
        f"{[m['id'] for m in models]}"
    )


def chat(base: str, model_id: str, system: str, user: str) -> str:
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
        f"{base}/v1/chat/completions",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        payload = json.loads(resp.read())
    return payload["choices"][0]["message"]["content"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--base",
        default=os.environ.get("FOUNDRY_LOCAL_BASE_URL", DEFAULT_BASE),
        help="Foundry Local base URL, e.g. http://127.0.0.1:<port>. Get <port> from "
             "`foundry server status` (not `foundry status` -- confirmed not to work on "
             "the real ThinkPad). Defaults to $FOUNDRY_LOCAL_BASE_URL if set, else this "
             "Mac's own port -- almost certainly wrong on any other machine.",
    )
    args = parser.parse_args()
    base = args.base

    try:
        model_id = resolve_model_id(base)
    except (urllib.error.URLError, ConnectionRefusedError) as exc:
        print(f"FAIL: Foundry Local service not reachable at {base} ({exc}). "
              f"Run `foundry server status` to find the real port and pass it with "
              f"--base (or set FOUNDRY_LOCAL_BASE_URL), or `foundry server restart` "
              f"if the service seems unresponsive.", file=sys.stderr)
        return 1
    except RuntimeError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1

    print(f"Resolved model id: {model_id}")

    content = chat(
        base,
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
