#!/usr/bin/env python3
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run(cmd: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(cmd, cwd=ROOT, text=True, capture_output=True)


def main() -> int:
    gen = run([sys.executable, "scripts/generate_third_party_notices.py"])
    sys.stdout.write(gen.stdout)
    sys.stderr.write(gen.stderr)
    if gen.returncode != 0:
        return gen.returncode

    diff = run(["git", "diff", "--exit-code", "--", "THIRD_PARTY_NOTICES.md", "LICENSES"])
    if diff.returncode != 0:
        print("[ERROR] THIRD_PARTY_NOTICES.md and/or LICENSES are out of date. Run generator and commit changes.")
        sys.stdout.write(diff.stdout)
        sys.stderr.write(diff.stderr)
        return 1

    others = run(["git", "ls-files", "--others", "--exclude-standard", "THIRD_PARTY_NOTICES.md", "LICENSES"])
    if others.returncode == 0 and others.stdout.strip():
        print("[ERROR] Untracked notices/license files detected:")
        print(others.stdout.strip())
        return 1

    print("[OK] THIRD_PARTY_NOTICES and LICENSES are present and up-to-date.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
