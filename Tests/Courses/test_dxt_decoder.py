"""Compile and run the production DXT decoder (Idas3DxtDecoder.cs) without Unity.

Checks hand-built BC1/BC3 blocks and mip chains, then decodes every DXT texture
found under RuntimeAssets (or --packs) to confirm each authored chain is accepted.
Run: python Tests/Courses/test_dxt_decoder.py
"""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile


REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "Assets/Scripts/Idas3DxtDecoder.cs"
DRIVER = Path(__file__).with_name("DxtDecoderDriver.cs")


def compiler_path(explicit):
    if explicit:
        return Path(explicit).resolve(strict=True)
    visual_studio = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Microsoft Visual Studio"
    candidates = sorted(visual_studio.glob("*/*/MSBuild/Current/Bin/Roslyn/csc.exe"))
    if not candidates:
        raise RuntimeError("Pass --compiler with a C# 7.3+ compiler.")
    return candidates[-1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--compiler")
    parser.add_argument("--packs", default=str(REPO / "RuntimeAssets"))
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as directory:
        executable = Path(directory) / "DxtDecoderDriver.exe"
        subprocess.run([str(compiler_path(args.compiler)), "/nologo", "/optimize+", "/target:exe",
                        "/out:" + str(executable), str(SOURCE), str(DRIVER)], check=True)
        completed = subprocess.run([str(executable), args.packs], capture_output=True, text=True, timeout=600)
        print(completed.stdout.strip())
        if completed.returncode:
            raise SystemExit(completed.stderr.strip() or "DXT decoder checks failed")


if __name__ == "__main__":
    main()
