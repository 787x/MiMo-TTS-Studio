"""Exercise installed fnOS long-work APIs through the real Unix socket.

Run as root on the test NAS. --cloud authorizes two MiMo calls for synthetic
text and retains the completed work for the desktop playback check.
"""

import argparse
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tarfile
import time
import uuid
import wave

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--cloud", action="store_true")
args = parser.parse_args()
assert os.getuid() == 0
app = Path("/var/apps/mimotts")
sock = app / "target/app.sock"
csrf = ""


def request(path, method="GET", data=None, extra=None, owner="1000"):
    connection = http.client.HTTPConnection("nas.test")
    connection.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    connection.sock.settimeout(30)
    connection.sock.connect(str(sock))
    headers = {
        "Host": "nas.test", "Origin": "http://nas.test:5666",
        "Referer": "http://nas.test:5666/app/mimotts/",
        "X-Studio-CSRF": csrf,
    }
    if owner:
        headers.update({"X-Trim-Userid": owner, "X-Trim-Username": "verification", "X-Trim-Isadmin": "true"})
    if data is not None and not isinstance(data, bytes):
        headers["Content-Type"] = "application/json"
        data = json.dumps(data).encode()
    headers.update(extra or {})
    try:
        connection.request(method, "/app/mimotts/api" + path, data, headers)
        response = connection.getresponse()
        return response.status, response.read(), dict(response.getheaders())
    finally:
        connection.close()


def payload(path, method="GET", data=None, extra=None):
    status, raw, _ = request(path, method, data, extra)
    assert 200 <= status < 300, (status, raw[:240])
    return json.loads(raw)


def wait_work(path, state, seconds=25):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        work = payload(path)
        if work["state"] == state:
            return work
        if work["state"] in ("invalid", "failed", "uncertain", "damaged"):
            raise RuntimeError((work["state"], work["completed"], work["error"]))
        time.sleep(.3)
    raise TimeoutError(state)


def attach(text, title, characters):
    source = text.encode()
    identifier = uuid.uuid4().hex
    path = "/works/" + identifier
    payload("/works/", "POST", {"id": identifier, "title": title})
    for offset in range(0, len(source), 65536):
        chunk = source[offset:offset + 65536]
        headers = {"X-Chunk-SHA256": hashlib.sha256(chunk).hexdigest()}
        work = payload(f"{path}/text?offset={offset}", "PUT", chunk, headers)
        assert work["bytes"] == offset + len(chunk)
        if offset == 0:
            assert payload(f"{path}/text?offset={offset}", "PUT", chunk, headers)["bytes"] == work["bytes"]
    payload(path + "/prepare", "POST", {
        "parameters": {"text": "", "voice": "冰糖", "segmentCharacters": characters},
        "bytes": len(source), "sha256": hashlib.sha256(source).hexdigest(),
    })
    work = wait_work(path, "ready")
    assert work["sha256"] == hashlib.sha256(source).hexdigest()
    return path, work


session = payload("/session")
csrf = session["csrf"]
assert request("/works/", owner=None)[0] == 403
offline, planned = attach("# 第一章\r\n" + "字" * 100_000, "长篇导入实机验收", 50)
assert planned["total"] > 80
assert request(offline, owner="1001")[0] == 400
assert len(payload(offline + "/preview")["items"]) == 12
assert request(offline, "DELETE")[0] == 204
print("PASS fnOS Unix gateway: resumable chunks and SHA-256, 100,000 characters, paged preview, owner and gateway checks", flush=True)

if args.cloud and session["configured"]:
    work_path, ready = attach(
        "第一章 晨光\r\n清晨，窗外传来几声鸟鸣。阳光照亮了书桌，我翻开手边的笔记本。\r\n"
        "第二章 晚风\r\n傍晚，河面泛起细细的波纹。我们沿着河岸散步，远远听见了归船的声音。",
        "长篇引擎实机验收", 150,
    )
    assert ready["total"] == 2 and ready["chapters"] == 2
    payload(work_path + "/control", "POST", {"action": "start"})
    final = wait_work(work_path, "succeeded", seconds=390)
    assert final["completed"] == 2 and final["volumes"] == 2
    page = payload(work_path + "/volumes")
    assert [v["chapter"] for v in page["items"]] == [0, 1]
    media = {}
    for volume in page["items"]:
        number = volume["number"]
        route = f"{work_path}/volumes/{number}"
        status, content, _ = request(route + "?download=1")
        assert status == 200 and len(content) == volume["bytes"]
        with wave.open(io.BytesIO(content), "rb") as audio:
            assert audio.getnframes() > 0 and audio.getnchannels() == 1 and audio.getsampwidth() == 2
        assert request(route, extra={"Range": "bytes=0-43"})[:2] == (206, content[:44])
        media[number] = hashlib.sha256(content).hexdigest()
    status, content, _ = request(work_path + "/chapters/1/download")
    assert status == 200
    with tarfile.open(fileobj=io.BytesIO(content)) as archive:
        entry, = archive.getmembers()
        assert hashlib.sha256(archive.extractfile(entry).read()).hexdigest() == media[1]
    print(f"PASS real MiMo: 2 requests, 2 WAV volumes, {sum(v['bytes'] for v in page['items'])} bytes; download, Range and ordered TAR", flush=True)
    targets = payload("/export/targets")
    candidates = [t for t in targets if t["writable"]]
    if candidates:
        target = candidates[0]
        name = "mimo-verification-" + uuid.uuid4().hex + ".wav"
        route = work_path + "/volumes/0/export"
        body = {"directoryId": target["id"], "fileName": name}
        receipt = payload(route, "POST", body)
        written = Path(receipt["directory"]) / name
        try:
            assert hashlib.file_digest(written.open("rb"), "sha256").hexdigest() == media[0]
            assert request(route, "POST", body)[0] == 400
            print("PASS fnOS authorized NAS export and no-overwrite protection", flush=True)
        finally:
            written.unlink()
    else:
        print("SKIP real NAS export: no writable authorized directory", flush=True)
    subprocess.run(["appcenter-cli", "stop", "mimotts"], check=True)
    subprocess.run(["appcenter-cli", "start", "mimotts"], check=True)
    csrf = payload("/session")["csrf"]
    assert wait_work(work_path, "succeeded")["sha256"] == final["sha256"]
    for number, expected in media.items():
        status, content, _ = request(f"{work_path}/volumes/{number}")
        assert status == 200 and hashlib.sha256(content).hexdigest() == expected
    print("PASS fnOS restart retains the original digest and both WAV outputs byte-for-byte", flush=True)
elif args.cloud:
    print("SKIP real MiMo: no configured Key", flush=True)
