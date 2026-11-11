"""Verify Docker ignores private environment files in both component contexts."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
for tier in ("backend", "frontend"):
    context = root / tier
    sentinel = context / ".env.regression.local"
    assert not sentinel.exists(), "Do not overwrite an existing input"
    sentinel.write_text("BUILD_CONTEXT_SENTINEL=not-a-secret\n")
    try:
        subprocess.run(["docker", "build", "--platform", "linux/amd64", "-f", "-", str(context)],
            input=b"FROM alpine:3.22\nCOPY . /context\nRUN test ! -e /context/.env.regression.local && test ! -e /context/.env.local && test ! -e /context/.env.prod\n", check=True)
        result = subprocess.run(["git", "check-ignore", "--quiet", f"{tier}/.env.prod"], cwd=root)
        assert result.returncode == 0, "Production environment must be untracked"
    finally:
        sentinel.unlink()
print("PASS: private environment files excluded from both build contexts and Git")
