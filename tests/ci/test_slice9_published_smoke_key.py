import hashlib
import importlib.util
import os
import pathlib
import tempfile
import unittest


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


if __name__ == "__main__":
    unittest.main()
