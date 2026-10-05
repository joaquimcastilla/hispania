"""Analyze immutable 0.7 corpus. Initial freeze is explicit and refuses overwrites."""
import hashlib
import json
import sys
from pathlib import Path
from collections import Counter

ROOT = Path(__file__).resolve().parent.parent
CORPUS = ROOT / "compatibility"
SOURCE = "78f291fdacf9442c6e2d8e23094dc0e309828693"
INDEX = CORPUS / "manifests/0.7/frozen-index.json"
MANIFEST = CORPUS / "baseline-0.7.json"

def hash_file(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def paths():
    return sorted((p for p in CORPUS.rglob("*") if p.is_file() and "0.7" in p.parts
                   and p.relative_to(CORPUS).parts[0] != "data"
                   and not {"bin", "obj"}.intersection(p.parts) and p != INDEX),
                  key=lambda p: p.relative_to(CORPUS).as_posix())

def rows():
    result = []
    for path in paths():
        relative = path.relative_to(CORPUS).as_posix()
        category = relative.split("/")[0]
        owner = "Platform" if category in {"api", "contracts", "clients", "serialization"} else "Module runtime"
        if "http-range" in relative:
            owner = "Media Playback"
        expectations = {
            "api": "Selected source signatures and public enum numeric values remain present; additive diff is reported.",
            "contracts": "Historical SDK provenance only; runtime gates must use current contracts, never load these copies.",
            "clients": "Frozen binary runs against current Platform contracts; frozen source compiles separately against current contracts.",
            "modules": "Historical greeting binary executes against current host contracts without recompilation.",
            "packages": "Historical signed format-1 package installs, validates, loads, executes and disables.",
            "protocols": "Generation-1 historical bytes decode with current parser; selected results preserve meaning.",
            "serialization": "Historical JSON decodes; selected semantic writer shape and observable outcomes remain compatible.",
            "manifests": "Historical manifest signature verifies using TEST ONLY public key."
        }
        result.append(dict(id=relative, axis=category, path=relative, sha256=hash_file(path), owner=owner,
            origin=SOURCE, version="0.7", expectation=expectations[category],
            regenerationPolicy="Immutable. Capture a new versioned corpus; never overwrite or rehash 0.7 to waive a failure."))
    return result

if "--initial-freeze" in sys.argv:
    if INDEX.exists() or MANIFEST.exists():
        raise SystemExit("Refusing to overwrite a frozen baseline")
    entries = rows()
    INDEX.write_text(json.dumps(entries, indent=2) + "\n", encoding="utf-8", newline="\n")
    MANIFEST.write_text(json.dumps(dict(baseline="0.7", sourceSha=SOURCE,
        designation="Technical compatibility baseline; not a published release", artifacts=entries), indent=2) + "\n",
        encoding="utf-8", newline="\n")
else:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entries = manifest["artifacts"]
    frozen = json.loads(INDEX.read_text(encoding="utf-8"))
    by_id = {row["id"]: row for row in entries}
    errors = ["BREAKING: frozen entry removed/changed " + row["id"] for row in frozen if by_id.get(row["id"]) != row]
    if manifest.get("baseline") != "0.7" or manifest.get("sourceSha") != SOURCE:
        errors.append("BREAKING: baseline source identity changed")
    if hash_file(INDEX) != "de8431a7f42e4a36979cc372a574cb62094f37399e9ee88a167d17f3a2bb005b":
        errors.append("BREAKING: immutable index changed")
    for key in ("id", "path"):
        errors += ["INVALID: duplicate " + key + " " + value for value, count in Counter(row[key] for row in entries).items() if count != 1]
    for row in entries:
        for key in ("id", "axis", "path", "sha256", "owner", "origin", "version", "expectation", "regenerationPolicy"):
            if not isinstance(row.get(key), str) or not row[key].strip():
                errors.append("INVALID: missing " + key)
        relative = row["path"]
        if "\\" in relative or ":" in relative or any(part in ("", ".", "..") for part in relative.split("/")):
            errors.append("INVALID: unsafe fixture path " + relative)
            continue
        if row["origin"] != SOURCE or row["version"] != "0.7":
            errors.append("INVALID: artifact provenance " + row["id"])
        path = CORPUS / row["path"]
        if not path.is_file() or hash_file(path) != row["sha256"]:
            errors.append("BREAKING: missing/changed fixture " + row["path"])
    listed = {row["path"] for row in entries}
    errors += ["UNREVIEWED: fixture omitted from manifest " + p.relative_to(CORPUS).as_posix() for p in paths() if p.relative_to(CORPUS).as_posix() not in listed]
    print(json.dumps(dict(fixtures=len(entries), manifestSha256=hash_file(MANIFEST), freezeSha256=hash_file(INDEX), errors=sorted(errors)), indent=2))
    sys.exit(bool(errors))
