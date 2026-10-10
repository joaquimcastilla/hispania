"""Add Phase 22 CLR declarations to the pre-1.0 contract inventory."""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1]
catalog_path = root / "docs/compatibility/contracts.json"
markdown_path = root / "docs/compatibility/CONTRACT-CATALOG.md"
catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
known = {row["id"] for row in catalog["contracts"] if row["kind"] == "CLR"}
pattern = re.compile(r"^\s*public\s+(?:(?:sealed|abstract|static|readonly|partial)\s+)*(?:record(?:\s+(?:struct|class))?|class|struct|interface|enum)\s+(\w+)", re.M)
versioned = re.compile(r"(?:V[12]|Version[12])(?:$|[^0-9])")
additions = []
for folder in ("contracts", "core", "apps", "application", "infrastructure", "modules"):
    for file in (root / folder).rglob("*.cs"):
        relative = file.relative_to(root).as_posix()
        if any(part in ("bin", "obj", "node_modules") for part in file.parts):
            continue
        for match in pattern.finditer(file.read_text(encoding="utf-8")):
            name = match.group(1)
            if not (relative.startswith("contracts/") or relative.startswith("core/domain/") or versioned.search(name)):
                continue
            identifier = relative + "#" + name
            if identifier in known:
                continue
            public = relative.startswith("contracts/")
            row = {
                "id": identifier,
                "contract": name,
                "source": relative,
                "kind": "CLR",
                "owner": "Platform",
                "technicalVersion": "Technical suffix V1" if versioned.search(name) else "Unversioned CLR declaration",
                "visibility": "Public experimental contract" if public else "Internal boundary (CLR public)",
                "stability": "Public Experimental" if public else "Internal",
                "consumers": "Platform owners, host and adapters",
                "serialization": "Owner-controlled CLR boundary; persisted forms are versioned independently",
                "compatibilityCommitment": "No cross-minor guarantee; preserve documented data/security invariants",
                "breakingChangePolicy": "0.x: deliberate owner review + notes/migration; no intentional compatible-line patch break",
                "interpretation": "A: technical CLR generation; D: experimental contract",
            }
            additions.append(row)
            known.add(identifier)
catalog["contracts"].extend(sorted(additions, key=lambda row: row["id"]))
catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
if additions:
    heading_exists = "## Phase 22 declarations" in markdown_path.read_text(encoding="utf-8")
    with markdown_path.open("a", encoding="utf-8") as out:
        if not heading_exists:
            out.write("\n## Phase 22 declarations\n\n")
        for row in sorted(additions, key=lambda row: row["id"]):
            out.write(f"- `{row['id']}` — {row['stability']}; {row['technicalVersion']}.\n")
print(f"Added {len(additions)} declarations")
