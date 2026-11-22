"""Exercise the lifecycle hook with the published Linux server in isolated directories."""
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time

package = Path(sys.argv[1]).resolve()
hook = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else package / "cmd/main"

with tempfile.TemporaryDirectory(prefix="mimotts-startup-") as temporary:
    root = Path(temporary)
    app = root / "app"
    app.mkdir()
    (app / "server").symlink_to(package / "app/server")
    state = root / "var"
    state.mkdir()
    env = dict(os.environ, TRIM_APPDEST=str(app), TRIM_PKGVAR=str(state),
               TRIM_PKGETC=str(root / "etc"), TRIM_PKGTMP=str(root / "tmp"))
    log = state / "service.log"
    os.mkfifo(log)
    # Model an abrupt reboot: the socket path exists but has no listener.
    socket_path = app / "app.sock"
    with socket.socket(socket.AF_UNIX) as stale:
        stale.bind(str(socket_path))
    socket_path.chmod(0o600)
    (state / "server.ready").write_text("1")

    def run(action):
        return subprocess.run(["bash", hook, action], env=env, timeout=30).returncode

    start = subprocess.Popen(["bash", hook, "start"], env=env, start_new_session=True)
    try:
        # The child cannot exec until a reader opens the log FIFO.
        deadline = time.monotonic() + 5
        while not (state / "server.pid").exists() and start.poll() is None:
            assert time.monotonic() < deadline, "start never recorded its child"
            time.sleep(0.01)
        time.sleep(0.3)
        assert start.poll() is None, "start exited before its child could exec"
        reader = os.open(log, os.O_RDONLY | os.O_NONBLOCK)
        try:
            assert start.wait(timeout=25) == 0
            assert run("status") == 0
            pid = (state / "server.pid").read_text().split()[0]
            assert (state / "server.ready").read_text() == pid
            assert run("start") == 0
            assert (state / "server.pid").read_text().split()[0] == pid
            assert run("stop") == 0
            assert run("status") == 3
            assert not (state / "server.ready").exists()
        finally:
            os.close(reader)
    finally:
        log.unlink()
        try:
            os.killpg(start.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        start.wait(timeout=5)
        run("stop")
    assert run("start") == 0
    try:
        assert run("status") == 0
    finally:
        assert run("stop") == 0

print("PASS delayed exec, stale socket, repeated start, stop and restart")
