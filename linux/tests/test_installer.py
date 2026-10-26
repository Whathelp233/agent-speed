#!/usr/bin/env python3
"""Installer boundary checks: real loopback downloads, isolated fake apt/sudo."""
import hashlib
import http.server
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[2]


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="agent-speed-installer-")
        self.root = Path(self.temp.name)
        self.release = self.root / "release"
        self.release.mkdir()
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "apt.log"
        for name, body in {
            "sudo": '#!/bin/sh\nexec "$@"\n',
            "apt-get": '#!/bin/sh\nprintf "%s\\n" "$@" >> "$AGENT_SPEED_TEST_APT_LOG"\n',
            "agent-speed": '#!/bin/sh\nexit 0\n',
        }.items():
            path = self.bin / name
            path.write_text(body)
            path.chmod(0o755)

        directory = str(self.release)

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=directory, **kwargs)

            def log_message(self, *args):
                pass

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        AGENT_SPEED_TEST_APT_LOG=str(self.log),
                        AGENT_SPEED_DOWNLOAD_BASE=f"http://127.0.0.1:{self.server.server_port}",
                        XDG_CONFIG_HOME=str(self.root / "config"), XDG_DATA_HOME=str(self.root / "data"),
                        XDG_CACHE_HOME=str(self.root / "cache"))

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.temp.cleanup()

    def run_installer(self, *args):
        return subprocess.run(["sh", str(ROOT / "install-linux.sh"), *args], env=self.env,
                              capture_output=True, text=True, timeout=15)

    def publish(self, correct=True, duplicate=False):
        body = b"installer-test-only, never passed to the real package manager"
        asset = "AgentSpeed-Linux-x64.deb"
        (self.release / asset).write_bytes(body)
        digest = hashlib.sha256(body if correct else b"corrupted").hexdigest()
        line = f"{digest}  {asset}\n"
        (self.release / "SHA256SUMS.txt").write_text(line * (2 if duplicate else 1))

    def test_verified_download_reaches_package_manager(self):
        self.publish()
        result = self.run_installer("--no-launch")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("install\n-y\n", self.log.read_text())

    def test_common_entrypoint_dispatches_to_local_linux_installer(self):
        result = subprocess.run(["sh", "install.sh", "--help"], cwd=ROOT, env=self.env,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("install-linux.sh", result.stdout)

    def test_tampering_and_duplicate_checksums_never_reach_apt(self):
        for correct, duplicate in [(False, False), (True, True)]:
            self.publish(correct, duplicate)
            self.assertNotEqual(0, self.run_installer("--no-launch").returncode)
            self.assertFalse(self.log.exists())

    def test_uninstall_retains_data_unless_purged(self):
        data = Path(self.env["XDG_DATA_HOME"]) / "agent-speed"
        data.mkdir(parents=True)
        (data / "history.sqlite").write_bytes(b"retained")
        self.assertEqual(0, self.run_installer("--uninstall").returncode)
        self.assertTrue(data.exists())
        self.assertEqual(0, self.run_installer("--uninstall", "--purge").returncode)
        self.assertFalse(data.exists())


if __name__ == "__main__":
    unittest.main()
