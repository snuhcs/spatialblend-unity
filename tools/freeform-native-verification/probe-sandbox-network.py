# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Run inside the gate's sandbox: loopback must work, every external connect must be denied."""
import socket
import sys

TCP, UDP = socket.SOCK_STREAM, socket.SOCK_DGRAM
EXTERNAL = (
    ("tcp4 1.1.1.1:53", socket.AF_INET, TCP, ("1.1.1.1", 53)),
    ("udp4 1.1.1.1:53", socket.AF_INET, UDP, ("1.1.1.1", 53)),
    ("tcp4 8.8.8.8:443", socket.AF_INET, TCP, ("8.8.8.8", 443)),
    ("tcp6 2606:4700:4700::1111:53", socket.AF_INET6, TCP, ("2606:4700:4700::1111", 53)),
    ("udp6 2606:4700:4700::1111:53", socket.AF_INET6, UDP, ("2606:4700:4700::1111", 53)),
)


def attempt(family, kind, address):
    with socket.socket(family, kind) as sock:
        sock.settimeout(2)
        if kind == UDP:
            sock.sendto(b"x", address)
        else:
            sock.connect(address)


def listener(family, kind, host):
    sock = socket.socket(family, kind)
    sock.bind((host, 0))
    if kind == TCP:
        sock.listen()
    return sock


def main():
    failures = []
    for name, family, kind, host in (("tcp4", socket.AF_INET, TCP, "127.0.0.1"),
                                     ("udp4", socket.AF_INET, UDP, "127.0.0.1"),
                                     ("tcp6", socket.AF_INET6, TCP, "::1"),
                                     ("udp6", socket.AF_INET6, UDP, "::1")):
        with listener(family, kind, host) as server:
            try:
                attempt(family, kind, (host, server.getsockname()[1]))
                print(f"loopback {name} {host}: allowed")
            except OSError as error:
                failures.append(f"loopback {name} {host} was denied: {error}")
    for name, family, kind, address in EXTERNAL:
        try:
            attempt(family, kind, address)
            failures.append(f"external {name} was NOT denied")
        except PermissionError as error:
            print(f"external {name}: denied ({error})")
        except OSError as error:
            # Unreachable networks do not prove the sandbox denied it.
            failures.append(f"external {name} failed for a non-sandbox reason: {error}")
    if failures:
        print("\n".join(failures), file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
