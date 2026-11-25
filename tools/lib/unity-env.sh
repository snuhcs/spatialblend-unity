# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

# Source this file, then run Unity (or a wrapper around it) as: unity_clean_env COMMAND ARGS...
#
# Unity batchmode writes the WHOLE environment of the process into its log, so any API key exported in the caller's shell
# would land in a log file. unity_clean_env starts the command with an empty environment plus an explicit allowlist:
#   HOME PATH USER LOGNAME TMPDIR LANG LC_ALL SHELL      the process basics
#   ANDROID_HOME ANDROID_SDK_ROOT ANDROID_NDK_ROOT JAVA_HOME   what an Android build looks for, when set
#   SPATIALBLEND_*                                         this project's own test knobs, except any name containing SECRET, TOKEN,
#                                                       PASSWORD or KEY (a secret is passed by file: SPATIALBLEND_*_FILE is allowed)
# Variables set as a prefix of the call (VAR=x unity_clean_env cmd) are exported inside the function, so they pass the same filter.
unity_clean_env() {
    local keep=() name
    for name in HOME PATH USER LOGNAME TMPDIR LANG LC_ALL SHELL ANDROID_HOME ANDROID_SDK_ROOT ANDROID_NDK_ROOT JAVA_HOME; do
        if [[ -n "${!name:-}" ]]; then keep+=("$name=${!name}"); fi
    done
    while IFS= read -r name; do
        case "$name" in
            SPATIALBLEND_*_FILE) ;;
            SPATIALBLEND_*SECRET*|SPATIALBLEND_*TOKEN*|SPATIALBLEND_*PASSWORD*|SPATIALBLEND_*KEY*) continue ;;
            SPATIALBLEND_*) ;;
            *) continue ;;
        esac
        keep+=("$name=${!name}")
    done < <(compgen -e)
    env -i "${keep[@]}" "$@"
}
