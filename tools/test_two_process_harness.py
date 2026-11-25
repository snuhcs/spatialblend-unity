#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Tests for the process handling of the two-process harness. No Unity, no network.

The harness runs on a machine that other runs share. It must end only the processes it started: a kill by name
(pkill -f / killall) ends every other run's Unity as well (their exit code 143)."""
from __future__ import annotations

import os
from pathlib import Path
import re
import subprocess
import sys
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
LIB = TOOLS / "lib" / "scoped-kill.sh"
HARNESS = TOOLS / "run-two-process-cloud-test.sh"
BY_NAME = re.compile(r"\b(pkill|killall|pgrep\s+-f)\b")


def alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    try:  # a zombie is dead: it only waits to be reaped
        state = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    except OSError:
        state = ""
    return bool(state) and not state.startswith("Z")


def wait_dead(pid: int, seconds: float = 10.0) -> bool:
    end = time.time() + seconds
    while time.time() < end:
        if not alive(pid):
            return True
        time.sleep(0.1)
    return not alive(pid)


class NoKillByName(unittest.TestCase):
    def test_no_script_under_tools_kills_by_name(self) -> None:
        offenders = []
        for path in sorted(TOOLS.rglob("*.sh")) + sorted(TOOLS.rglob("*.py")):
            if path.name == Path(__file__).name:
                continue
            for number, line in enumerate(path.read_text(errors="replace").splitlines(), 1):
                code = line.split("#", 1)[0] if path.suffix == ".sh" else line
                if path.suffix == ".py" and line.lstrip().startswith("#"):
                    continue
                if BY_NAME.search(code):
                    offenders.append(f"{path.relative_to(ROOT)}:{number}: {line.strip()}")
        self.assertEqual(offenders, [], "kill only pids the script started (tools/lib/scoped-kill.sh)")

    def test_harness_uses_the_scoped_kill_on_its_own_jobs(self) -> None:
        text = HARNESS.read_text()
        self.assertIn("lib/scoped-kill.sh", text)
        self.assertRegex(text, r"(?m)^set -m$")
        self.assertIn('kill_process_groups "$host_pid" "$joiner_pid"', text)

    def test_a_failing_role_still_writes_its_rc(self) -> None:
        # set -e ends run_role at a failing Unity before `echo $? > rc` unless the status is captured: the wait would then
        # run to the timeout, and the timeout used to kill by name.
        text = HARNESS.read_text()
        self.assertIn("|| rc=$?", text)
        self.assertIn('echo "$rc" > "$work/$1.rc"', text)


@unittest.skipUnless(sys.platform == "darwin", "the harness is the Mac Unity driver (cp -c clones); Linux CI covers the other tests")
class HarnessRun(unittest.TestCase):
    """The harness itself, on a fake repo and a stand-in Unity (UNITY_BIN): the timeout path and a failing role."""

    def setUp(self) -> None:
        import shutil, tempfile
        self.tmp = Path(tempfile.mkdtemp(prefix="harness-test."))
        self.token = f"{os.getpid()}-{int(time.time())}"  # names of this run's processes: another copy of the suite must not see them
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.repo = self.tmp / "repo"
        for d in ("Assets", "Packages", "ProjectSettings", "Library"):
            (self.repo / d).mkdir(parents=True)
        shutil.copytree(TOOLS, self.repo / "tools")
        (self.tmp / "tmpdir").mkdir()

    def run_harness(self, unity_body: str, timeout_s: int) -> subprocess.CompletedProcess:
        unity = self.tmp / "fake-unity.sh"
        unity.write_text("#!/bin/bash\n" + unity_body + "\n")
        unity.chmod(0o755)
        env = {**os.environ, "SPATIALBLEND_PHOTON_CLOUD": "1", "UNITY_BIN": str(unity), "SPATIALBLEND_TP_TIMEOUT_S": str(timeout_s),
               "SCOPED_KILL_GRACE_S": "1", "TMPDIR": str(self.tmp / "tmpdir"), "PYTHON_BIN": "/usr/bin/python3"}
        return subprocess.run(["bash", str(self.repo / "tools" / "run-two-process-cloud-test.sh"), "Photon"],
                              capture_output=True, text=True, timeout=400, env=env)

    def test_a_timeout_ends_the_harness_s_own_unity_and_no_other_process(self) -> None:
        other = subprocess.Popen(["bash", "-c", f"exec -a spatialblend-two-process-bystander-{self.token} sleep 306"], start_new_session=True)
        try:
            result = self.run_harness(f"exec -a unity-standin-{self.token} sleep 307", timeout_s=3)
            self.assertEqual(result.returncode, 3, result.stderr)
            self.assertIn("TIMEOUT", result.stderr)
            def standins() -> list:
                return [l for l in subprocess.run(["ps", "-axo", "pid,stat,command"], capture_output=True, text=True).stdout.splitlines()
                        if f"unity-standin-{self.token}" in l and " Z" not in l[:20]]
            end = time.time() + 15  # the signal is sent before the harness returns; a loaded machine reaps late
            while standins() and time.time() < end:
                time.sleep(0.2)
            self.assertEqual(standins(), [], "the harness's own Unity stand-ins must be gone")
            self.assertTrue(alive(other.pid), "a process whose name contains spatialblend-two-process must not be touched")
        finally:
            other.kill()
            other.wait()

    def test_a_failing_role_does_not_wait_for_the_timeout(self) -> None:
        start = time.time()
        result = self.run_harness('exit 7', timeout_s=300)
        self.assertLess(time.time() - start, 150, "both rc files are written at once; the wait must not run to the 300 s timeout")
        self.assertIn("host_rc=7 joiner_rc=7", result.stdout)


class ScopedKill(unittest.TestCase):
    def run_bash(self, script: str) -> subprocess.CompletedProcess:
        return subprocess.run(["bash", "-c", script], capture_output=True, text=True, timeout=60,
                              env={**os.environ, "SCOPED_KILL_GRACE_S": "1"})

    def test_ends_the_given_jobs_and_their_children_and_nothing_else(self) -> None:
        # An unrelated process with the SAME command line, in another session: a kill by name would end it too.
        other = subprocess.Popen(["sleep", "301"], start_new_session=True)
        try:
            script = f'''
            set -m
            source "{LIB}"
            bash -c 'sleep 301 & wait' & a=$!
            bash -c 'sleep 301 & wait' & b=$!
            set +m
            a_child=
            for _ in $(seq 1 100); do a_child=$(pgrep -P "$a" | head -1); [ -n "$a_child" ] && break; sleep 0.1; done
            echo "$a $b $a_child"
            kill_process_groups "$a" "$b"
            '''
            result = self.run_bash(script)
            self.assertEqual(result.returncode, 0, result.stderr)
            a, b, child = (int(x) for x in result.stdout.split())
            for pid in (a, b, child):
                self.assertTrue(wait_dead(pid), f"pid {pid} of the harness's own job must be gone")
            self.assertTrue(alive(other.pid), "an unrelated process must not be touched")
        finally:
            other.kill()
            other.wait()

    def test_kills_a_job_that_ignores_term(self) -> None:
        script = f'''
        set -m
        source "{LIB}"
        bash -c 'trap "" TERM; while true; do sleep 1; done' & a=$!
        set +m
        sleep 1
        echo "$a"
        kill_process_groups "$a"
        '''
        result = self.run_bash(script)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(wait_dead(int(result.stdout.split()[0])))

    def test_ignores_pids_that_are_not_a_real_group(self) -> None:
        other = subprocess.Popen(["sleep", "302"], start_new_session=True)
        try:
            # "", 0, 1 and text must never become `kill -- -1` or `kill -- -0` (every process of the user, or of the group).
            result = self.run_bash(f'source "{LIB}"; kill_process_groups "" 0 1 abc -5 "1 2"; echo survived')
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("survived", result.stdout)
            self.assertTrue(alive(other.pid))
        finally:
            other.kill()
            other.wait()


if __name__ == "__main__":
    unittest.main()
