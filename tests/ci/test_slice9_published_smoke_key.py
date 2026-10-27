import hashlib
import importlib.util
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


if __name__ == "__main__":
    unittest.main()
