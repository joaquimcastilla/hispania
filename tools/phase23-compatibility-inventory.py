#!/usr/bin/env python3
"""Read-only provenance and upgrade-manifest validator for the Phase 23 gate."""
from __future__ import annotations

import hashlib
import json
import pathlib
import sys


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    root = pathlib.Path(__file__).resolve().parents[1]
    matrix_path = root / "compatibility" / "phase23-upgrade-matrix.json"
    contract_path = root / "compatibility" / "baseline-0.7.json"
    data_path = root / "compatibility" / "data-baseline-0.7.json"
    matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
    contract_manifest = json.loads(contract_path.read_text(encoding="utf-8"))
    data_manifest = json.loads(data_path.read_text(encoding="utf-8"))

    errors: list[str] = []
    checked_contracts = checked_data = 0
    seen_ids: set[str] = set()
    for row in contract_manifest["artifacts"]:
        artifact_id = row["id"]
        if artifact_id in seen_ids:
            errors.append(f"duplicate compatibility artifact id: {artifact_id}")
        seen_ids.add(artifact_id)
        artifact = root / "compatibility" / row["path"]
        if not artifact.is_file():
            errors.append(f"missing compatibility artifact: {row['path']}")
        elif sha256(artifact) != row["sha256"]:
            errors.append(f"compatibility artifact hash mismatch: {row['path']}")
        checked_contracts += 1

    seen_data_paths: set[str] = set()
    for row in data_manifest["artifacts"]:
        relative = row["path"]
        if relative in seen_data_paths:
            errors.append(f"duplicate historical data artifact: {relative}")
        seen_data_paths.add(relative)
        artifact = root / "compatibility" / relative
        if not artifact.is_file():
            errors.append(f"missing historical data artifact: {relative}")
        elif sha256(artifact) != row["sha256"]:
            errors.append(f"historical data artifact hash mismatch: {relative}")
        if row.get("immutable") is not True or not row.get("owner"):
            errors.append(f"historical data artifact lacks immutable owner metadata: {relative}")
        checked_data += 1

    if contract_manifest.get("sourceSha") != "78f291fdacf9442c6e2d8e23094dc0e309828693":
        errors.append("Phase 15 baseline provenance SHA changed")
    if data_manifest.get("sourceSha") != "78f291fdacf9442c6e2d8e23094dc0e309828693":
        errors.append("Phase 16 data corpus provenance SHA changed")
    if len(contract_manifest["artifacts"]) != 27:
        errors.append("Phase 15 corpus must retain exactly 27 authenticated artifacts")
    if len(data_manifest["artifacts"]) != 15:
        errors.append("Phase 16 data corpus must retain exactly 15 authenticated artifacts")

    target_sha = matrix["target"]["sourceSha"]
    for scenario in matrix["scenarios"]:
        for key in ("sourceVersion", "targetVersion", "sourceSha", "targetSha", "storageEngines", "enabledModules", "packageSet", "expectedSchemaTransitions", "preservedData", "warnings", "unsupportedConditions", "execution"):
            if key not in scenario:
                errors.append(f"upgrade scenario {scenario.get('id', '<unknown>')} lacks {key}")
        if scenario.get("targetSha") != target_sha:
            errors.append(f"upgrade scenario {scenario['id']} target SHA disagrees with matrix target")

    report = {
        "schemaVersion": 1,
        "matrix": str(matrix_path.relative_to(root)).replace("\\", "/"),
        "targetSha": "recorded-by-gate-from-current-worktree",
        "phase15CompatibilityArtifactsVerified": checked_contracts,
        "phase16HistoricalDataArtifactsVerified": checked_data,
        "upgradeScenarios": len(matrix["scenarios"]),
        "historicalCompleteReleaseSnapshots": 0,
        "errors": errors,
    }
    rendered = json.dumps(report, sort_keys=True, indent=2) + "\n"
    print(rendered, end="")
    if len(sys.argv) == 2:
        destination = pathlib.Path(sys.argv[1]).resolve()
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(rendered, encoding="utf-8", newline="\n")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
