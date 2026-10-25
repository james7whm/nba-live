import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("sync", Path(__file__).resolve().parents[1] / "scripts/sync_gitee_release.py")
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class ReleaseSyncTests(unittest.TestCase):
    def test_withdrawal_only_changes_metadata_after_github_withdrawal(self):
        class Fake:
            def __init__(self): self.changes = []
            def api(self, path, method="GET", body=None):
                if method == "PATCH": self.changes.append(body)
                return {"id": 1}
        client = Fake()
        with self.assertRaises(ValueError):
            sync.withdraw(client, "v1.2.3", {"prerelease": False})
        self.assertFalse(client.changes)
        sync.withdraw(client, "v1.2.3", {"prerelease": True, "name": "withdrawn"})
        self.assertEqual(client.changes, [{"tag_name": "v1.2.3", "target_commitish": "v1.2.3", "name": "withdrawn", "body": "", "prerelease": True}])

    def test_excludes_full_and_includes_installer_checksums(self):
        names = sync.asset_names("v1.2.3")
        self.assertEqual(len(names), 6)
        self.assertNotIn("NbaLive-1.2.3-win-x64.zip", names)
        self.assertIn("NbaLive-1.2.3-online-setup-win-x64.exe.sha256", names)
        self.assertIn("NbaLive-1.2.3-lite-win-x64.zip", names)

    def test_rejects_invalid_tags_and_download_origins(self):
        for tag in ("v1.2.3-beta", "v1.2.3;echo", "../main", "v01.2.3"):
            with self.assertRaises(ValueError):
                sync.asset_names(tag)
        for url in ("http://github.com/x", "https://gitee.com.evil/x", "https://a:secret@gitee.com/x"):
            with self.assertRaises(ValueError):
                sync.check_public_url(url)

    def test_corruption_fails_before_upload(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload, checksum = Path(temporary) / "test.zip", Path(temporary) / "test.zip.sha256"
            payload.write_bytes(b"sample")
            checksum.write_text(sync.digest(payload) + "  test.zip\n")
            files = {payload.name: payload, checksum.name: checksum}
            sync.verify_local(files)
            payload.write_bytes(b"changed")
            with self.assertRaises(ValueError):
                sync.verify_local(files)

    def test_failed_verification_keeps_release_unpublished(self):
        class Fake:
            def __init__(self):
                self.attachments, self.published = [], False
            def api(self, path, method="GET", body=None):
                if method == "PATCH":
                    self.published = True
                return self.attachments if "attach_files" in path else {"id": 1, "prerelease": True}
            def upload(self, release_id, name, path):
                self.attachments.append({"name": name})
            def verify_remote(self, asset, name, path, tag):
                if name == "bad":
                    raise ValueError("bad hash")
        good = Fake()
        sync.mirror(good, "v1.2.3", {"name": "release"}, {"ok": "unused"})
        self.assertTrue(good.published)
        bad = Fake()
        with self.assertRaises(ValueError):
            sync.mirror(bad, "v1.2.3", {"name": "release"}, {"ok": "unused", "bad": "unused"})
        self.assertFalse(bad.published)

    def test_stable_existing_release_is_not_overwritten(self):
        class Existing:
            def api(self, path, method="GET", body=None):
                if method != "GET":
                    raise AssertionError("stable release mutated")
                return [] if "attach_files" in path else {"id": 1, "prerelease": False}
            def upload(self, *args):
                raise AssertionError("stable release overwritten")
        with self.assertRaises(ValueError):
            sync.mirror(Existing(), "v1.2.3", {"name": "release"}, {"missing": "unused"})


if __name__ == "__main__":
    unittest.main()
