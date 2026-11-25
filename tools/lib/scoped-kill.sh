# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

# Source this file. kill_process_groups PID...: end the process groups LED BY the given pids, and nothing else.
#
# The pids are the ones a script started itself as background jobs under `set -m` (job control gives every background job its own
# process group whose id is the job's pid), so the group is exactly that job and the Unity it launched. Never kill by name
# (pkill -f / killall): a name matches every other agent's and every other run's processes on the same machine.
# TERM first, KILL after SCOPED_KILL_GRACE_S (default 5) for whatever is left. A pid that is not a number above 1 is ignored:
# `kill -- -1` would signal every process the user owns.
kill_process_groups() {
    local pid valid=()
    for pid in "$@"; do
        if [[ "$pid" =~ ^[0-9]+$ ]] && (( 10#$pid > 1 )); then valid+=("$((10#$pid))"); fi
    done
    (( ${#valid[@]} )) || return 0
    for pid in "${valid[@]}"; do kill -TERM -- "-$pid" 2>/dev/null || true; done
    sleep "${SCOPED_KILL_GRACE_S:-5}"
    for pid in "${valid[@]}"; do kill -KILL -- "-$pid" 2>/dev/null || true; done
}
