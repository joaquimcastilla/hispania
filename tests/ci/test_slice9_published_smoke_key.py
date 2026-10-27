import contextlib
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import sqlite3
import tempfile
import unittest
from unittest import mock


SCRIPT = pathlib.Path(__file__).resolve().parents[2] / ".github" / "scripts" / "slice9-core-published-smoke.py"
SPEC = importlib.util.spec_from_file_location("slice9_core_published_smoke", SCRIPT)
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class PublisherPublicKeyTransferTests(unittest.TestCase):
    def test_loads_verified_pem_contents_instead_of_integrity_digest(self):
        pem = "-----BEGIN PUBLIC KEY-----\nAQID\n-----END PUBLIC KEY-----"
        key_bytes = pem.encode("ascii")
        expected_hash = hashlib.sha256(key_bytes).hexdigest().upper()
        with tempfile.TemporaryDirectory(prefix="hispania-s9-key-test-") as directory:
            key_path = pathlib.Path(directory) / "publisher-public.pem"
            key_path.write_bytes(key_bytes)

            result = SMOKE.load_verified_publisher_public_key(key_path, expected_hash)

        self.assertEqual(pem, result)
        self.assertNotEqual(expected_hash, result)

    def test_rejects_public_key_hash_mismatch(self):
        pem = "-----BEGIN PUBLIC KEY-----\nAQID\n-----END PUBLIC KEY-----"
        with tempfile.TemporaryDirectory(prefix="hispania-s9-key-test-") as directory:
            key_path = pathlib.Path(directory) / "publisher-public.pem"
            key_path.write_text(pem, encoding="ascii", newline="")

            with self.assertRaisesRegex(ValueError, "public key hash mismatch"):
                SMOKE.load_verified_publisher_public_key(key_path, "0" * 64)

    def test_normalizes_common_windows_and_linux_architecture_names(self):
        self.assertEqual("x86_64", SMOKE.normalized_machine("AMD64"))
        self.assertEqual("x86_64", SMOKE.normalized_machine("x86_64"))
        self.assertEqual("aarch64", SMOKE.normalized_machine("ARM64"))
        self.assertEqual("aarch64", SMOKE.normalized_machine("aarch64"))

    @unittest.skipUnless(os.name == "nt", "Windows certificate helper is platform-specific")
    def test_windows_certificate_helper_creates_ip_san_pfx_and_public_pem(self):
        with tempfile.TemporaryDirectory(prefix="hispania-s9-cert-test-") as directory:
            root = pathlib.Path(directory)
            pfx_path = root / "tls.pfx"
            cert_path = root / "tls-cert.pem"

            SMOKE.create_windows_test_certificate(pfx_path, cert_path, "synthetic-test-password")

            self.assertTrue(pfx_path.is_file())
            self.assertTrue(cert_path.read_text(encoding="ascii").startswith("-----BEGIN CERTIFICATE-----"))


class OwnedTempCleanupTests(unittest.TestCase):
    def setUp(self):
        self.parent = pathlib.Path(tempfile.mkdtemp(prefix="hispania-s9-cleanup-test-"))
        self.root = self.parent / "hispania-slice9-win-x64-owned"
        self.root.mkdir()

    def tearDown(self):
        if self.parent.exists():
            shutil.rmtree(self.parent)

    def test_owned_tree_is_removed_and_reported(self):
        (self.root / "data.db").write_bytes(b"synthetic")
        passed, diagnostics = SMOKE.cleanup_owned_temp_root(self.root, self.parent, "win-x64")
        self.assertTrue(passed)
        self.assertFalse(self.root.exists())
        self.assertIn({"stage": "temp_root_delete", "resourceType": "directory", "result": "pass"}, diagnostics)

    def test_residual_modulehost_fails_even_when_temp_is_removed(self):
        passed, diagnostics = SMOKE.cleanup_owned_temp_root(self.root, self.parent, "win-x64", residual_modulehost_count=1)
        self.assertFalse(passed)
        self.assertFalse(self.root.exists())
        self.assertEqual("fail", diagnostics[0]["result"])
        self.assertEqual(1, diagnostics[0]["residualCount"])

    def test_persistent_delete_error_is_classified_and_not_hidden(self):
        def deny_delete(_path):
            raise PermissionError(13, "synthetic diagnostic secret", str(self.root / "payload.db"))

        passed, diagnostics = SMOKE.cleanup_owned_temp_root(
            self.root, self.parent, "win-x64", remove_tree=deny_delete
        )
        self.assertFalse(passed)
        self.assertTrue(self.root.exists())
        failure = next(item for item in diagnostics if item["stage"] == "temp_root_delete")
        self.assertEqual("access_denied", failure["errorCategory"])
        self.assertEqual("PermissionError", failure["exceptionType"])
        self.assertEqual("payload.db", failure["relativePath"])
        serialized = json.dumps(diagnostics)
        self.assertNotIn(str(self.parent), serialized)
        self.assertNotIn("synthetic diagnostic secret", serialized)

    def test_windows_sharing_violation_classification(self):
        error = PermissionError("synthetic")
        error.winerror = 32
        result = SMOKE._cleanup_error(error)
        self.assertEqual("sharing_violation", result["errorCategory"])
        self.assertNotIn("synthetic", json.dumps(result))

    def test_sqlite_quick_check_closes_connection_before_cleanup(self):
        database = self.root / "core.db"
        with contextlib.closing(sqlite3.connect(database)) as connection:
            connection.execute("CREATE TABLE marker (value TEXT)")
            connection.commit()
        self.assertEqual("ok", SMOKE.sqlite_quick_check(database))
        renamed = self.root / "core-renamed.db"
        database.rename(renamed)
        renamed.unlink()
        self.assertFalse(database.exists())
        self.assertFalse(renamed.exists())

    def test_wrong_parent_or_prefix_is_rejected_without_deletion(self):
        passed, diagnostics = SMOKE.cleanup_owned_temp_root(self.root, self.parent, "linux-x64")
        self.assertFalse(passed)
        self.assertTrue(self.root.exists())
        self.assertEqual("temp_root_preflight", diagnostics[0]["stage"])

    def test_reparse_point_blocks_recursive_deletion(self):
        if not hasattr(os, "symlink"):
            self.skipTest("symlink API unavailable")
        destination = self.parent / "outside"
        destination.mkdir()
        sentinel = destination / "sentinel.txt"
        sentinel.write_text("preserve", encoding="utf-8")
        link = self.root / "redirect"
        try:
            link.symlink_to(destination, target_is_directory=True)
        except (OSError, NotImplementedError) as exception:
            self.skipTest(f"symlink unavailable: {type(exception).__name__}")
        passed, diagnostics = SMOKE.cleanup_owned_temp_root(self.root, self.parent, "win-x64")
        self.assertFalse(passed)
        self.assertTrue(sentinel.is_file())
        self.assertTrue(any(item.get("resourceType") == "reparse_point" for item in diagnostics))

    @unittest.skipUnless(os.name == "nt", "open-file sharing behavior is Windows-specific")
    def test_open_file_reports_sharing_violation_without_deletion(self):
        import ctypes
        from ctypes import wintypes

        file_path = self.root / "locked.bin"
        file_path.write_bytes(b"synthetic")
        create_file = ctypes.windll.kernel32.CreateFileW
        create_file.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        create_file.restype = wintypes.HANDLE
        handle = create_file(str(file_path), 0x80000000, 0, None, 3, 0x80, None)
        invalid = wintypes.HANDLE(-1).value
        if handle == invalid:
            self.skipTest("could not create controlled exclusive file handle")
        try:
            passed, diagnostics = SMOKE.cleanup_owned_temp_root(self.root, self.parent, "win-x64")
            self.assertFalse(passed)
            failure = next(item for item in diagnostics if item["stage"] == "temp_root_delete")
            self.assertEqual("sharing_violation", failure["errorCategory"])
            self.assertTrue(file_path.exists())
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)

    def test_github_outputs_include_only_gate_metadata(self):
        output = self.parent / "github-output.txt"
        summary = {
            "sourceCommit": "a" * 40,
            "runtimeIdentifier": "win-x64",
            "architecture": "x86_64",
            "aspnetCoreRuntime": "Microsoft.AspNetCore.App 10.0.12 [runtime-root]",
            "contentId": "b" * 64,
            "manifestSha256": "c" * 64,
            "evidenceId": "hispania-slice9-evidence-win-x64-123-1",
            "runnerName": "hispania-win-ci",
            "checks": [{"status": "PASS"} for _ in range(38)],
        }
        with mock.patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}):
            SMOKE.write_github_smoke_outputs(summary)
        contents = output.read_text(encoding="utf-8")
        self.assertIn("source_commit=" + "a" * 40, contents)
        self.assertIn("check_count=38", contents)
        self.assertIn("pass_count=38", contents)
        self.assertIn("fail_count=0", contents)
        self.assertIn("skip_count=0", contents)
        self.assertIn("runner_name=hispania-win-ci", contents)
        self.assertIn("runtime_version=10.0.12", contents)
        self.assertNotIn("password", contents.lower())

    def test_github_outputs_reject_newline_metadata(self):
        output = self.parent / "github-output-invalid.txt"
        summary = {
            "sourceCommit": "a" * 40,
            "runtimeIdentifier": "win-x64",
            "architecture": "x86_64",
            "aspnetCoreRuntime": "Microsoft.AspNetCore.App 10.0.12 [runtime-root]",
            "contentId": "b" * 64,
            "manifestSha256": "c" * 64,
            "evidenceId": "safe\nINJECTED=value",
            "runnerName": "hispania-win-ci",
            "checks": [],
        }
        with mock.patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}):
            with self.assertRaisesRegex(ValueError, "safe gate metadata"):
                SMOKE.write_github_smoke_outputs(summary)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
