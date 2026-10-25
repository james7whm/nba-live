"""Copy a fixed set of verified public assets. Tokens are environment-only."""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = "james7whm/nba-live"
API = "https://gitee.com/api/v5/repos/" + REPO


def asset_names(tag):
    if not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag):
        raise ValueError("Use a stable vX.Y.Z tag")
    stem = "NbaLive-" + tag[1:]
    files = [stem + "-online-setup-win-x64.exe", stem + "-lite-win-x64.zip", stem + "-compatibility-preview-net48-win-x64.zip"]
    return [name for file in files for name in (file, file + ".sha256")]


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            h.update(block)
    return h.hexdigest()


def verify_local(files):
    for name, path in files.items():
        if not name.endswith(".sha256") and Path(files[name + ".sha256"]).read_text(encoding="ascii").strip() != digest(path) + "  " + name:
            raise ValueError("Checksum mismatch: " + name)


def check_public_url(url):
    p = urllib.parse.urlsplit(url)
    host = p.hostname or ""
    if p.scheme != "https" or p.port not in (None, 443) or p.username or (host not in ("github.com", "gitee.com", "foruda.gitee.com") and not host.endswith(".githubusercontent.com")):
        raise ValueError("Untrusted asset download host")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


class PublicRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, url):
        check_public_url(url)
        return super().redirect_request(req, fp, code, msg, headers, url)


def download(url, destination):
    check_public_url(url)
    total, h = 0, hashlib.sha256()
    with urllib.request.build_opener(PublicRedirect()).open(url, timeout=120) as response, open(destination, "wb") as output:
        while block := response.read(65536):
            total += len(block)
            if total > 100 * 1024 * 1024:
                raise ValueError("Asset exceeds domestic download limit")
            h.update(block)
            output.write(block)
    return h.hexdigest()


def request_json(url, token, method="GET", body=None):
    request = urllib.request.Request(url, method=method, data=json.dumps(body).encode("utf-8") if body is not None else None,
        headers={"Authorization": "Bearer " + token, "User-Agent": "NbaLive-Release-Sync", "Content-Type": "application/json"})
    try:
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=30) as response:
            data = response.read(2097153)
            if len(data) > 2097152:
                raise ValueError("API response too large")
            return json.loads(data)
    except urllib.error.HTTPError as error:
        if error.code == 404 and method == "GET":
            return None
        raise RuntimeError(f"Release API {method} failed: HTTP {error.code}") from None


class Client:
    def __init__(self, token):
        if not token:
            raise ValueError("Configure the GITEE_TOKEN Actions secret first")
        self.token = token

    def api(self, path, method="GET", body=None):
        return request_json(API + path, self.token, method, body)

    def upload(self, release_id, name, path):
        boundary = "NbaLiveUpload" + os.urandom(16).hex()
        before = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode("ascii")
        after = ("\r\n--" + boundary + "--\r\n").encode("ascii")
        connection = http.client.HTTPSConnection("gitee.com", timeout=180)
        try:
            connection.putrequest("POST", f"/api/v5/repos/{REPO}/releases/{release_id}/attach_files")
            connection.putheader("Authorization", "Bearer " + self.token)
            connection.putheader("Content-Type", "multipart/form-data; boundary=" + boundary)
            connection.putheader("Content-Length", str(len(before) + Path(path).stat().st_size + len(after)))
            connection.endheaders()
            connection.send(before)
            with open(path, "rb") as stream:
                for block in iter(lambda: stream.read(65536), b""):
                    connection.send(block)
            connection.send(after)
            response = connection.getresponse()
            if response.status not in (200, 201):
                raise RuntimeError(f"Gitee upload failed: HTTP {response.status}; rerun to resume")
            response.read(2097152)
        finally:
            connection.close()

    def verify_remote(self, asset, name, path, tag):
        expected = f"https://gitee.com/{REPO}/releases/download/{tag}/{name}"
        if asset.get("browser_download_url") != expected:
            raise ValueError("Unexpected Gitee asset URL: " + name)
        if download(expected, Path(str(path) + ".remote")) != digest(path):
            raise ValueError("Gitee attachment differs; refusing replacement: " + name)


def mirror(client, tag, release, files):
    existing = client.api("/releases/tags/" + tag)
    if existing is None:
        existing = client.api("/releases", "POST", {"tag_name": tag, "target_commitish": tag,
            "name": release["name"], "body": release.get("body") or "", "prerelease": True})
    release_id = existing["id"]
    attachments = client.api(f"/releases/{release_id}/attach_files?per_page=100") or []
    for name, path in files.items():
        matches = [a for a in attachments if a.get("name") == name]
        if len(matches) > 1:
            raise ValueError("Duplicate remote attachment: " + name)
        if not matches:
            if not existing.get("prerelease", False):
                raise ValueError("Existing stable release is incomplete; refusing modification")
            client.upload(release_id, name, path)
            attachments = client.api(f"/releases/{release_id}/attach_files?per_page=100") or []
            matches = [a for a in attachments if a.get("name") == name]
        if len(matches) != 1:
            raise ValueError("Attachment was not committed: " + name)
        client.verify_remote(matches[0], name, path, tag)
        print("Verified " + name)
    if existing.get("prerelease", False):
        client.api(f"/releases/{release_id}", "PATCH", {"tag_name": tag, "target_commitish": tag,
            "name": release["name"], "body": release.get("body") or "", "prerelease": False})


def main(tag):
    names = asset_names(tag)
    client = Client(os.environ.get("GITEE_TOKEN", ""))
    gh = lambda path: request_json("https://api.github.com/repos/" + REPO + path, os.environ.get("GH_TOKEN", ""))
    release = gh("/releases/tags/" + tag)
    if not release or release.get("draft") or release.get("prerelease"):
        raise ValueError("Only published stable releases can be mirrored")
    commit = gh("/commits/" + tag)["sha"]
    for attempt in range(21):
        tags = client.api("/tags?per_page=100") or []
        match = [t for t in tags if t.get("name") == tag]
        if match:
            if match[0].get("commit", {}).get("sha") != commit:
                raise ValueError("Gitee tag differs from GitHub")
            break
        if attempt == 20:
            raise ValueError("Gitee mirror has not copied this tag; rerun later")
        time.sleep(15)
    with tempfile.TemporaryDirectory(prefix="nba-release-") as temporary:
        files = {}
        for name in names:
            assets = [a for a in release["assets"] if a["name"] == name]
            url = f"https://github.com/{REPO}/releases/download/{tag}/{name}"
            if len(assets) != 1 or assets[0].get("browser_download_url") != url:
                raise ValueError("GitHub attachment missing: " + name)
            path = Path(temporary) / name
            actual = download(url, path)
            if assets[0].get("digest") and assets[0]["digest"] != "sha256:" + actual:
                raise ValueError("GitHub attachment digest mismatch")
            files[name] = path
        verify_local(files)
        mirror(client, tag, release, files)
    print(f"Published https://gitee.com/{REPO}/releases/tag/{tag}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    args = parser.parse_args()
    try:
        main(args.tag)
    except Exception as error:
        print("Release sync failed: " + str(error))
        raise SystemExit(1) from None
