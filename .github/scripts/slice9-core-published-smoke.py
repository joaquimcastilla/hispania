#!/usr/bin/env python3
"""Run an isolated HTTPS/auth/SQLite/OOP fixture smoke against a downloaded Core package."""

from __future__ import annotations

import argparse
import hashlib
import http.cookiejar
import json
import os
import pathlib
import platform
import re
import secrets
import shutil
import signal
import socket
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def safe_relative(value: str) -> pathlib.PurePosixPath:
    rel = pathlib.PurePosixPath(value)
    if rel.is_absolute() or not rel.parts or any(part in ("", ".", "..") for part in rel.parts):
        raise ValueError("unsafe manifest path")
    return rel


def verify_bundle(bundle: pathlib.Path, fixture: pathlib.Path, rid: str, arch: str) -> tuple[pathlib.Path, pathlib.Path, str]:
    if {entry.name for entry in bundle.iterdir()} != {"package", "integrity.json"}:
        raise ValueError("downloaded Core bundle has unexpected entries")
    integrity = json.loads((bundle / "integrity.json").read_text(encoding="utf-8-sig"))
    if sha256(bundle / "integrity.json") != os.environ["HISPANIA_EXPECTED_INTEGRITY_SHA256"].upper():
        raise ValueError("package integrity sidecar hash mismatch")
    if integrity.get("sourceCommit") != os.environ["HISPANIA_EXPECTED_SOURCE_SHA"]:
        raise ValueError("source commit mismatch")
    if integrity.get("runtimeIdentifier") != rid or integrity.get("architecture") != arch:
        raise ValueError("package RID or architecture metadata mismatch")
    if integrity.get("targetFramework") != "net10.0" or integrity.get("publishMode") != "framework-dependent":
        raise ValueError("package framework or deployment mode mismatch")
    package = bundle / "package"
    manifest_path = package / "core-artifact-manifest.json"
    if sha256(manifest_path) != os.environ["HISPANIA_EXPECTED_MANIFEST_SHA256"].upper():
        raise ValueError("manifest hash mismatch")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("sourceCommit") != os.environ["HISPANIA_EXPECTED_SOURCE_SHA"] or manifest.get("runtimeIdentifier") != rid:
        raise ValueError("published manifest provenance mismatch")
    if manifest.get("contentId", "").upper() != os.environ["HISPANIA_EXPECTED_CONTENT_ID"].upper():
        raise ValueError("published content ID mismatch")
    if manifest.get("targetFramework") != "net10.0" or manifest.get("publishMode") != "framework-dependent":
        raise ValueError("published manifest framework or mode mismatch")
    files = integrity.get("files")
    if not isinstance(files, dict) or not files:
        raise ValueError("integrity inventory missing")
    declared = manifest.get("files")
    if not isinstance(declared, list):
        raise ValueError("manifest file entries missing")
    declared_map = {entry["path"]: entry for entry in declared}
    actual_names = set()
    for relative, expected_hash in files.items():
        rel = safe_relative(relative)
        path = package.joinpath(*rel.parts)
        if path.is_symlink() or not path.is_file():
            raise ValueError("package contains missing or linked file")
        if sha256(path) != expected_hash.upper():
            raise ValueError("package file hash mismatch")
        actual_names.add(relative)
        if relative != "core-artifact-manifest.json":
            entry = declared_map.get(relative)
            if entry is None or entry.get("sha256", "").upper() != expected_hash.upper() or entry.get("size") != path.stat().st_size:
                raise ValueError("manifest payload entry mismatch")
    filesystem_names = {
        p.relative_to(package).as_posix() for p in package.rglob("*") if p.is_file()
    }
    if any(p.is_symlink() for p in package.rglob("*")) or filesystem_names != actual_names:
        raise ValueError("package inventory has links, additions, or omissions")
    if set(declared_map) != actual_names - {"core-artifact-manifest.json"}:
        raise ValueError("manifest and uploaded package inventories differ")
    if integrity.get("fileCountIncludingManifest") != len(actual_names):
        raise ValueError("integrity inventory count mismatch")

    if sha256(fixture / "integrity.json") != os.environ["HISPANIA_EXPECTED_FIXTURE_INTEGRITY_SHA256"].upper():
        raise ValueError("fixture integrity sidecar hash mismatch")
    if {entry.name for entry in fixture.iterdir()} != {"fixture.hispania", "publisher-public.pem", "integrity.json"}:
        raise ValueError("downloaded fixture bundle has unexpected entries")
    fixture_integrity = json.loads((fixture / "integrity.json").read_text(encoding="utf-8-sig"))
    if fixture_integrity.get("sourceCommit") != os.environ["HISPANIA_EXPECTED_SOURCE_SHA"] or fixture_integrity.get("publisherId") != "hispania.slice7.test":
        raise ValueError("fixture provenance mismatch")
    fixture_files = fixture_integrity.get("files")
    if not isinstance(fixture_files, dict) or set(fixture_files) != {"fixture.hispania", "publisher-public.pem"}:
        raise ValueError("fixture bundle inventory mismatch")
    for name, expected_hash in fixture_files.items():
        path = fixture / name
        if path.is_symlink() or not path.is_file() or sha256(path) != expected_hash.upper():
            raise ValueError("fixture bundle hash mismatch")
    return package, fixture / "fixture.hispania", fixture_integrity["files"]["publisher-public.pem"]


def get_free_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def wait_health(opener: urllib.request.OpenerDirector, url: str, process: subprocess.Popen[bytes]) -> None:
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Core exited before health check")
        try:
            with opener.open(url + "/health/live", timeout=2) as response:
                if response.status == 200 and json.loads(response.read()).get("status") == "healthy":
                    return
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
            time.sleep(0.25)
    raise RuntimeError("Core health timeout")


def request(opener: urllib.request.OpenerDirector, url: str, method: str = "GET", body: bytes | None = None,
            headers: dict[str, str] | None = None) -> tuple[int, bytes, dict[str, str]]:
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    try:
        with opener.open(req, timeout=10) as response:
            return response.status, response.read(), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())


def modulehost_processes(modulehost_path: pathlib.Path) -> list[int]:
    found: list[int] = []
    proc_root = pathlib.Path("/proc")
    needle = str(modulehost_path).encode()
    for entry in proc_root.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            command = (entry / "cmdline").read_bytes()
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        if needle in command:
            found.append(int(entry.name))
    return found


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--rid", required=True, choices=("linux-x64", "linux-arm64"))
    parser.add_argument("--architecture", required=True, choices=("x86_64", "aarch64"))
    args = parser.parse_args()
    evidence_dir = pathlib.Path(os.environ["HISPANIA_EVIDENCE_DIR"]).resolve()
    evidence_dir.mkdir(parents=True, exist_ok=False)
    checks: list[dict[str, str]] = []
    stage = "preflight"
    temp_root: pathlib.Path | None = None
    core: subprocess.Popen[bytes] | None = None
    raw_log = None
    modulehost_path: pathlib.Path | None = None
    modulehost_observed = False
    summary: dict[str, object] = {
        "schemaVersion": 1, "overall": "FAIL", "sourceCommit": os.environ.get("HISPANIA_EXPECTED_SOURCE_SHA"),
        "runtimeIdentifier": args.rid, "architecture": args.architecture, "checks": checks,
        "failureStage": None, "errorCode": None, "coreExitCode": None, "tempCleanup": "NOT_RUN",
        "secretsPrinted": False,
    }
    secrets_to_redact: list[str] = []

    def record(name: str, passed: bool) -> None:
        checks.append({"name": name, "status": "PASS" if passed else "FAIL"})
        if not passed:
            raise RuntimeError(name)

    def stop_core() -> int | None:
        nonlocal core
        if core is None or core.poll() is not None:
            return core.returncode if core is not None else None
        core.terminate()
        try:
            return core.wait(timeout=15)
        except subprocess.TimeoutExpired:
            core.kill()
            core.wait(timeout=5)
            raise RuntimeError("Core did not stop gracefully")

    try:
        expected_machine = args.architecture
        record("native_linux_architecture", platform.system() == "Linux" and platform.machine() == expected_machine)
        package, fixture_zip, public_key = verify_bundle(pathlib.Path(args.bundle).resolve(), pathlib.Path(args.fixture).resolve(), args.rid, args.architecture)
        record("artifact_integrity_manifest_allowlist", True)
        runtimes = subprocess.run(["dotnet", "--list-runtimes"], check=True, capture_output=True, text=True).stdout
        record("aspnetcore_runtime_10_available", bool(re.search(r"^Microsoft\.AspNetCore\.App 10\.", runtimes, re.MULTILINE)))
        summary["contentId"] = os.environ["HISPANIA_EXPECTED_CONTENT_ID"].upper()
        summary["manifestSha256"] = os.environ["HISPANIA_EXPECTED_MANIFEST_SHA256"].upper()
        summary["aspnetCoreRuntime"] = next((line.strip() for line in runtimes.splitlines() if line.startswith("Microsoft.AspNetCore.App 10.")), None)
        summary["executionSource"] = "downloaded workflow artifact; published package directory"
        summary["coreAssemblySha256"] = sha256(package / "Hispania.Core.dll")
        summary["moduleHostSha256"] = sha256(package / "module-host/Hispania.ModuleHost.dll")
        summary["fixturePackageSha256"] = sha256(fixture_zip)
        record("non_root_runner", os.geteuid() != 0)

        temp_parent = pathlib.Path(os.environ["RUNNER_TEMP"]).resolve()
        temp_root = pathlib.Path(tempfile.mkdtemp(prefix=f"hispania-slice9-{args.rid}-", dir=temp_parent)).resolve()
        if temp_root.parent != temp_parent or not temp_root.name.startswith(f"hispania-slice9-{args.rid}-"):
            raise RuntimeError("Temporary root escaped runner temp")
        home = temp_root / "home"
        data = temp_root / "data"
        home.mkdir()
        data.mkdir()
        cert_key = temp_root / "tls-key.pem"
        cert_pem = temp_root / "tls-cert.pem"
        pfx = temp_root / "tls-cert.pfx"
        raw_log = temp_root / "core.raw.log"
        port = get_free_loopback_port()
        pfx_password = secrets.token_urlsafe(30)
        admin_password = secrets.token_urlsafe(36) + "Aa1!"
        secrets_to_redact.extend([pfx_password, admin_password])
        record("exclusive_temp_sqlite_and_home_paths", data != package and home != package)

        openssl_env = os.environ.copy()
        openssl_env["HISPANIA_TEST_PFX_PASSWORD"] = pfx_password
        subprocess.run([
            "openssl", "req", "-x509", "-newkey", "rsa:2048", "-sha256", "-days", "1", "-nodes",
            "-keyout", str(cert_key), "-out", str(cert_pem), "-subj", "/CN=127.0.0.1",
            "-addext", "subjectAltName=IP:127.0.0.1", "-addext", "extendedKeyUsage=serverAuth",
        ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=openssl_env)
        subprocess.run([
            "openssl", "pkcs12", "-export", "-out", str(pfx), "-inkey", str(cert_key), "-in", str(cert_pem),
            "-passout", "env:HISPANIA_TEST_PFX_PASSWORD",
        ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=openssl_env)
        os.chmod(cert_key, 0o600)
        os.chmod(pfx, 0o600)
        cert_inspect = subprocess.run(["openssl", "x509", "-in", str(cert_pem), "-noout", "-ext", "subjectAltName"], check=True, capture_output=True, text=True).stdout
        record("https_certificate_ip_san", "IP Address:127.0.0.1" in cert_inspect)

        env = os.environ.copy()
        env.update({
            "HOME": str(home), "DOTNET_CLI_HOME": str(home / ".dotnet"),
            "XDG_CONFIG_HOME": str(home / ".config"), "XDG_DATA_HOME": str(home / ".local/share"),
            "HISPANIA_CORE_DATA_DIRECTORY": str(data), "HISPANIA_CORE_LISTEN_ADDRESS": "127.0.0.1",
            "HISPANIA_CORE_PORT": str(port), "HISPANIA_CORE_HTTPS_CERTIFICATE_PATH": str(pfx),
            "HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD": pfx_password,
            "HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__hispania.slice7.test": public_key,
        })
        env.pop("HISPANIA_CORE_DATA_PROTECTION_KEYS_DIRECTORY", None)
        core_dll = package / "Hispania.Core.dll"
        bootstrap_input = f"ci-admin\n{admin_password}\n{admin_password}\n"
        bootstrap = subprocess.run(["dotnet", str(core_dll), "bootstrap-admin"], input=bootstrap_input, text=True,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, timeout=60)
        record("local_admin_bootstrap", bootstrap.returncode == 0 and "Initial administrator created." in bootstrap.stdout)
        record("fresh_sqlite_created", (data / "core.db").is_file())

        opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(cert_pem))),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
        )
        base_url = f"https://127.0.0.1:{port}"
        stage = "start-core"
        with raw_log.open("wb") as log:
            core = subprocess.Popen(["dotnet", str(core_dll)], cwd=package, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
            wait_health(opener, base_url, core)
        record("published_core_https_health", True)

        stage = "auth"
        origin = base_url
        code, body, _ = request(opener, base_url + "/api/v1/auth/login", "POST",
                                json.dumps({"username": "ci-admin", "password": admin_password}).encode(),
                                {"Content-Type": "application/json", "Origin": origin})
        login = json.loads(body) if code == 200 else {}
        csrf = login.get("csrfToken", "")
        if csrf:
            secrets_to_redact.append(csrf)
        record("synthetic_admin_login", code == 200 and bool(csrf))
        code, _, _ = request(opener, base_url + "/api/v1/auth/me")
        record("authenticated_me", code == 200)
        code, _, _ = request(opener, base_url + "/api/v1/auth/modules")
        record("authenticated_module_listing", code == 200)

        stage = "fixture-install"
        mutating_headers = {"Origin": origin, "X-Hispania-CSRF": csrf}
        package_bytes = fixture_zip.read_bytes()
        secrets_to_redact.extend([])
        code, body, _ = request(opener, base_url + "/api/v1/auth/modules/install", "POST", package_bytes,
                                {**mutating_headers, "Content-Type": "application/zip"})
        installed = json.loads(body) if code == 201 else {}
        record("signed_fixture_installed", code == 201 and installed.get("id") == "hispania.slice7.fixture")
        code, body, _ = request(opener, base_url + "/api/v1/auth/modules")
        rows = json.loads(body) if code == 200 else []
        row = next((item for item in rows if item.get("packageId") == "hispania.slice7.fixture"), {})
        record("fixture_trusted_authorized_and_disabled", bool(row.get("trusted") and row.get("authorized") and row.get("installed") and not row.get("enabled")))

        code, _, _ = request(opener, base_url + "/api/v1/auth/modules/hispania.slice7.fixture/enable", "POST", b"", mutating_headers)
        record("fixture_activation", code == 200)
        modulehost_path = (package / "module-host/Hispania.ModuleHost.dll").resolve()
        deadline = time.monotonic() + 20
        healthy_row = {}
        while time.monotonic() < deadline:
            code, body, _ = request(opener, base_url + "/api/v1/auth/modules")
            rows = json.loads(body) if code == 200 else []
            healthy_row = next((item for item in rows if item.get("packageId") == "hispania.slice7.fixture"), {})
            if healthy_row.get("enabled") and healthy_row.get("healthy"):
                break
            time.sleep(0.25)
        record("fixture_oop_healthy", bool(healthy_row.get("enabled") and healthy_row.get("healthy")))
        modulehost_pids = modulehost_processes(modulehost_path)
        modulehost_observed = bool(modulehost_pids)
        record("published_modulehost_process_observed", bool(modulehost_pids))

        stage = "restart-persistence"
        first_pid = core.pid
        first_exit = stop_core()
        core = None
        summary["firstCoreExitCode"] = first_exit
        record("first_core_graceful_shutdown", first_exit == 0)
        db_path = data / "core.db"
        record("sqlite_persisted_across_restart", db_path.is_file() and db_path.stat().st_size > 0)
        if not db_path.is_file():
            raise RuntimeError("SQLite database missing after stop")
        with sqlite3.connect(f"file:{db_path.as_posix()}?mode=ro", uri=True) as connection:
            integrity_result = connection.execute("PRAGMA quick_check").fetchone()[0]
        record("sqlite_quick_check_after_shutdown", integrity_result == "ok")

        with raw_log.open("ab") as log:
            core = subprocess.Popen(["dotnet", str(core_dll)], cwd=package, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
            wait_health(opener, base_url, core)
        summary["secondCorePid"] = core.pid
        code, _, _ = request(opener, base_url + "/api/v1/auth/me")
        record("session_and_identity_survive_core_restart", code == 200)
        code, body, _ = request(opener, base_url + "/api/v1/auth/modules")
        rows = json.loads(body) if code == 200 else []
        row = next((item for item in rows if item.get("packageId") == "hispania.slice7.fixture"), {})
        record("enabled_fixture_state_survives_restart", bool(row.get("enabled") and row.get("healthy")))

        stage = "fixture-cleanup"
        code, _, _ = request(opener, base_url + "/api/v1/auth/modules/hispania.slice7.fixture/disable", "POST", b"", mutating_headers)
        record("fixture_deactivation", code == 200)
        code, _, _ = request(opener, base_url + "/api/v1/auth/modules/hispania.slice7.fixture", "DELETE", b"", mutating_headers)
        record("fixture_uninstall", code == 204)
        code, body, _ = request(opener, base_url + "/api/v1/auth/modules")
        rows = json.loads(body) if code == 200 else []
        record("fixture_absent_after_uninstall", not any(item.get("packageId") == "hispania.slice7.fixture" for item in rows))

        code, _, _ = request(opener, base_url + "/api/v1/auth/logout", "POST", b"", mutating_headers)
        record("csrf_logout", code == 204)
        code, _, _ = request(opener, base_url + "/api/v1/auth/me")
        record("session_revoked_after_logout", code == 401)

        stage = "shutdown"
        final_exit = stop_core()
        core = None
        summary["coreExitCode"] = final_exit
        record("final_core_graceful_shutdown", final_exit == 0)
        time.sleep(0.5)
        record("no_orphan_modulehost_process", not modulehost_processes(modulehost_path))
        summary["overall"] = "PASS"
    except Exception as exc:  # Report only a stable stage and exception class, never response bodies.
        summary["failureStage"] = stage
        summary["errorCode"] = f"S9_{stage.upper().replace('-', '_')}_{type(exc).__name__.upper()}"
    finally:
        cleanup_ok = True
        try:
            if core is not None and core.poll() is None:
                exit_code = stop_core()
                if summary.get("coreExitCode") is None:
                    summary["coreExitCode"] = exit_code
            if modulehost_path is not None and modulehost_processes(modulehost_path):
                cleanup_ok = False
            if raw_log and raw_log.exists():
                allowed = re.compile(r"Now listening on:|Core host started\.|Core host stopping\.|Core SQLite passive checkpoint completed\.|Core host stopped\.")
                safe_lines = []
                for line in raw_log.read_text(encoding="utf-8", errors="replace").splitlines():
                    if allowed.search(line):
                        for secret in secrets_to_redact:
                            if secret:
                                line = line.replace(secret, "[REDACTED]")
                        safe_lines.append(line[:500])
                (evidence_dir / "core-lifecycle-sanitized.log").write_text("\n".join(safe_lines) + "\n", encoding="utf-8")
            if temp_root and temp_root.exists():
                runner_temp = pathlib.Path(os.environ["RUNNER_TEMP"]).resolve()
                if temp_root.parent != runner_temp or not temp_root.name.startswith(f"hispania-slice9-{args.rid}-"):
                    cleanup_ok = False
                else:
                    if any(path.is_symlink() for path in temp_root.rglob("*")):
                        cleanup_ok = False
                    else:
                        shutil.rmtree(temp_root)
                    if temp_root.exists():
                        cleanup_ok = False
            summary["tempCleanup"] = "PASS" if cleanup_ok else "FAIL"
        except Exception:
            cleanup_ok = False
            summary["tempCleanup"] = "FAIL"
        summary["checks"].append({"name": "temporary_cleanup", "status": "PASS" if cleanup_ok else "FAIL"})
        if not cleanup_ok:
            summary["overall"] = "FAIL"
            if summary.get("failureStage") is None:
                summary["failureStage"] = "cleanup"
                summary["errorCode"] = "S9_CLEANUP_FAILED"
        summary["moduleHostObserved"] = modulehost_observed
        summary["checkCount"] = len(summary["checks"])
        summary_path = evidence_dir / "summary.json"
        summary_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        (evidence_dir / "summary.sha256").write_text(sha256(summary_path) + "  summary.json\n", encoding="ascii")
        print(f"HISPANIA_SMOKE_OVERALL={summary['overall']}")
        print(f"HISPANIA_SMOKE_CHECKS={len(checks)}")
        print(f"HISPANIA_SMOKE_EVIDENCE={evidence_dir}")
    return 0 if summary["overall"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
