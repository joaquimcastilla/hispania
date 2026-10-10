"""Exercise the Python analyzer against isolated copies; never mutate historical evidence."""
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
manifest = json.loads((ROOT / "compatibility/baseline-0.7.json").read_text(encoding="utf-8"))

with tempfile.TemporaryDirectory(prefix="hispania-corpus-negative-") as directory:
    staging = Path(directory)
    (staging / "tools").mkdir()
    shutil.copyfile(ROOT / "tools/compatibility-manifest.py", staging / "tools/compatibility-manifest.py")
    def restore():
        for relative in ["baseline-0.7.json", "manifests/0.7/frozen-index.json"] + [row["path"] for row in manifest["artifacts"]]:
            source = ROOT / "compatibility" / relative
            target = staging / "compatibility" / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    def run():
        return subprocess.run([sys.executable, str(staging / "tools/compatibility-manifest.py")], capture_output=True, text=True)
    restore()
    first, second = run(), run()
    assert first.returncode == second.returncode == 0 and first.stdout == second.stdout, "Analysis must be deterministic"
    path = staging / "compatibility" / manifest["artifacts"][0]["path"]
    manifest_path = staging / "compatibility/baseline-0.7.json"
    cases = ["modified", "missing", "wrong-hash", "omitted", "unregistered", "malformed", "wrong-source", "duplicate", "unsafe-path", "index-tamper"]
    for case in cases:
        restore()
        changed = json.loads(manifest_path.read_text(encoding="utf-8"))
        if case == "modified": path.write_bytes(b"TEST-ONLY-modified")
        elif case == "missing": path.unlink()
        elif case == "wrong-hash": changed["artifacts"][0]["sha256"] = "0" * 64
        elif case == "omitted": changed["artifacts"].pop(0)
        elif case == "unregistered": (path.parent / "TEST-ONLY-unregistered.json").write_text("{}", encoding="utf-8")
        elif case == "malformed": manifest_path.write_text("{", encoding="utf-8")
        elif case == "wrong-source": changed["sourceSha"] = "TEST-ONLY-wrong-source"
        elif case == "duplicate": changed["artifacts"].append(changed["artifacts"][0])
        elif case == "unsafe-path": changed["artifacts"][0]["path"] = "../TEST-ONLY-outside"
        elif case == "index-tamper": (staging / "compatibility/manifests/0.7/frozen-index.json").write_text("[]", encoding="utf-8")
        if case in {"wrong-hash", "omitted", "wrong-source", "duplicate", "unsafe-path"}:
            manifest_path.write_text(json.dumps(changed), encoding="utf-8")
        result = run()
        assert result.returncode != 0, "Analyzer ignored " + case
        diagnostic = {
            "modified": "missing/changed fixture", "missing": "missing/changed fixture",
            "wrong-hash": "fixture", "omitted": "frozen entry removed/changed",
            "unregistered": "fixture omitted from manifest", "malformed": "JSONDecodeError",
            "wrong-source": "baseline source identity changed", "duplicate": "duplicate id",
            "unsafe-path": "unsafe fixture path", "index-tamper": "immutable index changed"
        }[case]
        assert diagnostic in result.stdout + result.stderr, "Wrong rejection reason for " + case
        if case == "unregistered": (path.parent / "TEST-ONLY-unregistered.json").unlink()
    print("Python manifest: deterministic analysis; 10/10 isolated negative cases rejected.")
