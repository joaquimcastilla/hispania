"""Refresh source inventory without overriding reviewed contract metadata.

Standard-library-only; run from any directory with Python 3. New entries receive
conservative defaults and MUST be reviewed. CI validates inventory in .NET tests.
This scanner handles the declaration syntax currently used, not arbitrary C#.
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "docs/compatibility/contracts.json"
TYPE = re.compile(r"^\s*public\s+(?:(?:sealed|abstract|static|readonly|partial)\s+)*(?:record(?:\s+(?:struct|class))?|class|struct|interface|enum)\s+(\w+)", re.M)
VERSION = re.compile(r"(?:V[12]|Version[12])(?:$|[^0-9])")
CANDIDATES = {
    "IIdentityQueryV1", "IIdentityCommandV1", "UserId", "SessionId", "UserStatus", "User", "Session",
    "AuthenticationResult", "IdentityFailure", "IdentityOperationResult", "IAuthorizationServiceV1",
    "AuthorizationDecision", "AssetId", "AssetReference", "DirectPlayPlan", "PlaybackPlanningResult",
    "DirectPlayPlanned", "PlaybackMediaNotFound", "DirectPlayUnavailable", "IPlaybackPlanner",
    "ByteRangeRequest", "Full", "From", "Closed", "Suffix", "SourceContentFacts", "ReadableMediaContent",
    "DirectPlayExecutionResult", "FullContent", "PartialContent", "RangeNotSatisfiable", "RangeNotSupported",
    "SourceUnavailable", "SourceChanged", "PlanNoLongerValid", "IDirectPlayExecutor",
}


def defaults(path, name):
    owner = "Platform"
    consumers = "Platform host, runtime and adapters"
    if "module-system" in path or "core/runtime" in path:
        owner, consumers = "Platform extensibility/distributed runtime", "ModuleHost, WorkerAgent, coordinator and module authors"
    elif path.startswith("contracts/platform/identity") or path.startswith("contracts/platform/authorization"):
        owner, consumers = "Platform security", "Identity/Auth adapters, server and suite permission consumers"
    elif path.startswith("contracts/platform/assets") or path.startswith("contracts/platform/storage"):
        owner, consumers = "Platform assets/storage", "Asset adapters, storage providers and domain reference consumers"
    elif "inventory" in path:
        owner, consumers = "ERP Inventory", "Inventory application, persistence and HTTP composition"
    elif any(x in path for x in ("library", "metadata", "playback", "transcoding", "core/domain")):
        owner, consumers = "Media " + path.split("/")[1], "Media application/adapters and server composition"
    internal = "architecture-test" in path or (not path.startswith("contracts/") and not path.startswith("core/domain/"))
    demo = "extension-demo" in path
    candidate = name in CANDIDATES and (path.startswith("contracts/platform/") or path == "contracts/playback/PlaybackContracts.cs")
    stability = "Stable Candidate" if candidate else "Internal" if internal else "Experimental" if demo else "Public Experimental"
    interpretation = "D: experimental contract; no encoded version"
    current = "Unversioned CLR declaration; no independently published SDK version"
    if VERSION.search(name):
        current = "Technical suffix " + ("V2" if "V2" in name else "V1" if "V1" in name else "Version2" if "Version2" in name else "Version1")
        interpretation = "A: technical CLR generation; D: experimental contract"
        if "Protocol" in path or "DistributedWorkerContracts" in path:
            interpretation = "C: wire protocol generation (state-store records also persisted); not SemVer"
        elif "Payload" in name or "StockChanged" in name:
            interpretation = "B: payload schema identity; D: experimental evolution (Jobs has no independent negotiation)"
        elif "Envelope" in name:
            interpretation = "A: envelope generation; payload Version is separate schema B"
        if candidate:
            interpretation = "A: current CLR generation; E: future public stable candidate, not stable today"
    wire = "Yes: JSON/wire or persisted DTO; see owning axis and owner codec" if any(x in path for x in ("Protocol", "DistributedWorkerContracts", "events", "jobs", "Manifest", "inventory")) else "CLR API; serialized use must be declared separately"
    if internal:
        wire = "Implementation boundary; may serialize owned state, no SDK commitment"
    return dict(id=path + "#" + name, contract=name, source=path, kind="CLR", owner=owner,
                technicalVersion=current, visibility="Internal boundary (CLR public)" if internal else "Public",
                stability=stability, consumers=consumers, serialization=wire,
                compatibilityCommitment="Current-checkpoint behavior only; candidate gates in PLATFORM-1.0-SCOPE.md" if candidate else "No cross-minor guarantee; preserve documented data/security invariants",
                breakingChangePolicy="0.x: deliberate owner review + notes/migration; no intentional compatible-line patch break",
                interpretation=interpretation)


def inventory():
    result = []
    for folder in ("contracts", "core", "apps", "application", "infrastructure", "modules"):
        for file in sorted((ROOT / folder).rglob("*.cs")):
            if set(file.parts) & {"bin", "obj", "node_modules"}:
                continue
            path = file.relative_to(ROOT).as_posix()
            for match in TYPE.finditer(file.read_text(encoding="utf-8-sig")):
                name = match[1]
                if path.startswith(("contracts/", "core/domain/")) or VERSION.search(name):
                    result.append(defaults(path, name))
    return sorted(result, key=lambda row: row["id"])


def render(rows):
    versioned = [r for r in rows if r["kind"] == "CLR" and VERSION.search(r["contract"])]
    head = """# Contract catalog

Reviewed machine source: [contracts.json](contracts.json). The CLR inventory covers all current public type declarations in contracts/core/domain and every production public type named V1/V2/Version1/Version2. Source IDs are path#declaration (nested records use the source-local name). Families below cover non-CLR HTTP/data/behavior surfaces. Public implementation classes are not automatically SDK contracts. Internal/demo declarations are retained to make exclusions visible.

Each machine entry records owner, technical version, exposure, stability, consumers, serialization, compatibility commitment, breaking policy and A–E interpretation. A=technical generation, B=schema, C=wire, D=experimental, E=future stable candidate. These categories may overlap. Zero Public Stable entries are permitted in this phase. Stable candidates and missing gates are defined in PLATFORM-1.0-SCOPE.md; a candidate does not freeze all adjacent DTOs.

The inventory includes `IndependentGreetingModuleV2`: it identifies the second demonstration implementation behind the same `IExtensionDemoV1`, not a V2 stable SDK. Demo manifest filenames and module implementation versions are separate from CLR contract identity. No Version1/Version2 declaration exists at this checkpoint. Tests/fixtures are excluded deliberately. Counts are generated from source, not the approximate audit total.

Known inconsistencies: Worker generation 1 is duplicated in coordinator and agent (tested equality); package format checks repeat literal 1 (preserved); host 1.0.0 is legacy package bounds; Manifest has no independent schema field; unversioned HTTP DTOs coexist with V1 CLR records; Jobs payload suffix has no JobType version negotiation; instrument 1.0.0 is scope identity. Do not infer stable SemVer from any of these. No mass renames are required.

"""
    head += f"Inventory: {len(rows)} entries; {len(versioned)} version-named CLR declarations; " + str(sum(r["stability"] == "Stable Candidate" for r in rows)) + " candidate entries.\n\n"
    head += "| ID / contract | Owner | Technical version | Exposure / stability | Consumers | Serialization | Commitment / break policy | Interpretation |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n"
    for r in rows:
        values = [r["id"], r["owner"], r["technicalVersion"], r["visibility"] + " / " + r["stability"], r["consumers"], r["serialization"], r["compatibilityCommitment"] + "; " + r["breakingChangePolicy"], r["interpretation"]]
        head += "| " + " | ".join(v.replace("|", "/").replace("\n", " ") for v in values) + " |\n"
    return head


if __name__ == "__main__":
    previous = json.loads(CATALOG.read_text(encoding="utf-8")) if CATALOG.exists() else {"contracts": []}
    reviewed = {r["id"]: r for r in previous["contracts"]}
    rows = [reviewed.get(r["id"], r) for r in inventory()]
    rows += [r for r in previous["contracts"] if r["kind"] != "CLR"]
    rows.sort(key=lambda r: r["id"])
    CATALOG.parent.mkdir(parents=True, exist_ok=True)
    CATALOG.write_text(json.dumps({"schemaVersion": 1, "distributionSource": "version.json", "contracts": rows}, indent=2) + "\n", encoding="utf-8")
    (CATALOG.parent / "CONTRACT-CATALOG.md").write_text(render(rows), encoding="utf-8")
    print(f"Wrote {len(rows)} entries; review newly defaulted metadata before commit.")
