#!/usr/bin/env python3
"""
Dynamic Service Discovery Test

Validates that Consul-based service discovery, consul-template,
and Nginx hot-reload work correctly end-to-end.

Usage:
    python performance/service_discovery_test.py
    python performance/service_discovery_test.py --service matchmaking
    python performance/service_discovery_test.py --scale-to 5 --skip-failure
"""

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.request
import urllib.error
import ssl

CONSUL_API = os.getenv("CONSUL_HTTP_ADDR", "http://localhost:8500")

SERVICE_MAP = {
    "support":     {"consul_name": "support-service",    "port": 8000, "compose": "support",     "initial_replicas": 3},
    "matchmaking": {"consul_name": "matchmaking-service", "port": 8001, "compose": "matchmaking", "initial_replicas": 2},
}


def _req(method: str, url: str, timeout: int = 10):
    ctx = ssl._create_unverified_context()
    req = urllib.request.Request(url, method=method)
    with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
        return resp.status, json.loads(resp.read().decode())


def _get(url: str, timeout: int = 10):
    return _req("GET", url, timeout)


def _run(cmd: str, capture: bool = True):
    result = subprocess.run(cmd, shell=True, capture_output=capture, text=True)
    return result.returncode, result.stdout, result.stderr


def test_registration(consul_name: str, expected_replicas: int):
    print(f"\n[1/6] Registration: expecting {expected_replicas} healthy '{consul_name}' instances ...")
    url = f"{CONSUL_API}/v1/health/service/{consul_name}?passing=true"
    deadline = time.time() + 20
    last_data = []
    while time.time() < deadline:
        _, data = _get(url)
        if len(data) >= expected_replicas:
            break
        time.sleep(2)
    actual = len(data)
    assert actual >= expected_replicas, \
        f"Expected at least {expected_replicas} healthy instances, got {actual}"
    for entry in data:
        inst = entry["Service"]
        addr = inst.get("Address", "?")
        port = inst.get("Port", "?")
        sid = inst["ID"]
        print(f"  * {sid}  →  {addr}:{port}")
    print(f"  PASS  PASS ({actual}/{expected_replicas})")


def test_nginx_config(expected_replicas: int, port: int, timeout: int = 20):
    print(f"\n[2/6] Nginx config: checking upstream servers through port {port} ...")
    deadline = time.time() + timeout
    last_lines = []
    while time.time() < deadline:
        rc, stdout, _ = _run("docker compose exec reverse-proxy cat /etc/nginx/conf.d/default.conf")
        if rc == 0:
            server_lines = [
                l.strip() for l in stdout.splitlines()
                if l.strip().startswith("server") and f":{port}" in l.strip().rstrip(";")
            ]
            last_lines = server_lines
            if len(server_lines) >= expected_replicas:
                break
        time.sleep(3)
    actual = len(last_lines)
    assert actual >= expected_replicas, \
        f"nginx.conf has {actual} servers for port {port}, expected at least {expected_replicas}"
    for line in last_lines:
        print(f"     {line}")
    print(f"  PASS  PASS ({actual}/{expected_replicas})")


def test_traffic_routing(port: int, min_expected: int, min_unique: int = 2):
    print(f"\n[3/6] Traffic routing: sending requests through port {port} ...")
    instances_seen = {}
    ctx = ssl._create_unverified_context()
    url = f"https://localhost:{port}/health"
    for i in range(max(min_expected * 4, 20)):
        req = urllib.request.Request(url)
        req.add_header("Connection", "close")
        try:
            with urllib.request.urlopen(req, timeout=5, context=ctx) as resp:
                body = json.loads(resp.read().decode())
                hostname = body.get("instance", "unknown")
                instances_seen[hostname] = instances_seen.get(hostname, 0) + 1
        except Exception:
            pass
    print(f"  Instances hit: {dict(instances_seen)}")
    expect = min(min_unique, min_expected)
    assert len(instances_seen) >= expect, \
        f"Traffic only reached {len(instances_seen)} unique instance(s); expected at least {expect}"
    print(f"  PASS  PASS ({len(instances_seen)} unique instances)")


def test_scale_up(compose_name: str, consul_name: str, current: int, target: int):
    print(f"\n[4/6] Scale up: {compose_name} {current} → {target} replicas ...")
    rc, _, _ = _run(f"docker compose up -d --scale {compose_name}={target} --no-recreate")
    assert rc == 0, f"docker compose scale up failed (rc={rc})"
    deadline = time.time() + 45
    last_count = 0
    while time.time() < deadline:
        _, data = _get(f"{CONSUL_API}/v1/health/service/{consul_name}?passing=true")
        last_count = len(data)
        if last_count >= target:
            break
        time.sleep(3)
    assert last_count >= target, \
        f"Expected at least {target} after scale up, got {last_count}"
    print(f"  PASS  PASS (scaled to {last_count} instances)")


def test_scale_down(compose_name: str, consul_name: str, current: int, target: int, port: int):
    print(f"\n[5/6] Scale down: {compose_name} {current} → {target} replicas ...")
    rc, _, _ = _run(f"docker compose up -d --scale {compose_name}={target} --no-recreate")
    assert rc == 0, f"docker compose scale down failed (rc={rc})"
    deadline = time.time() + 45
    while time.time() < deadline:
        _, data = _get(f"{CONSUL_API}/v1/health/service/{consul_name}?passing=true")
        if len(data) <= target:
            break
        time.sleep(3)
    healthy = len(data)
    assert healthy <= target, \
        f"Expected at most {target} after scale down, got {healthy}"
    deadline = time.time() + 15
    while time.time() < deadline:
        _, stdout, _ = _run("docker compose exec reverse-proxy cat /etc/nginx/conf.d/default.conf")
        server_lines = [
            l.strip() for l in stdout.splitlines()
            if l.strip().startswith("server") and f":{port}" in l.strip().rstrip(";")
        ]
        if len(server_lines) <= target:
            break
        time.sleep(3)
    nginx_count = len(server_lines)
    assert nginx_count <= target, \
        f"nginx.conf has {nginx_count} servers after scale down, expected at most {target}"
    print(f"  PASS  PASS ({healthy} healthy, {nginx_count} in nginx)")


def test_failure_recovery(consul_name: str, expected_healthy: int):
    print(f"\n[6/6] Failure recovery: killing one instance of '{consul_name}' ...")
    rc, stdout, _ = _run("docker compose ps -q " + consul_name.replace("-service", ""))
    container_ids = [c for c in stdout.strip().split("\n") if c.strip()]
    assert len(container_ids) > 1, "Need more than 1 instance for failure test"
    target = container_ids[0]
    cinfo = subprocess.run(
        ["docker", "inspect", "--format", "{{.Name}}", target],
        capture_output=True, text=True,
    )
    cname = cinfo.stdout.strip("/ \n")
    print(f"  Killing container: {cname}")
    _run(f"docker kill {target}")
    deadline = time.time() + 15
    found_critical = False
    while time.time() < deadline:
        _, data = _get(f"{CONSUL_API}/v1/health/service/{consul_name}")
        for entry in data:
            for check in entry.get("Checks", []):
                if "DeregisterCriticalServiceAfter" not in check.get("CheckID", "") and check.get("Status") == "critical":
                    found_critical = True
                    break
        if found_critical:
            break
        time.sleep(2)
    assert found_critical, "Consul did not mark killed instance as critical within 15s"
    print(f"  PASS  Consul detected critical status")
    deadline = time.time() + 40
    while time.time() < deadline:
        _, data = _get(f"{CONSUL_API}/v1/health/service/{consul_name}?passing=true")
        if len(data) <= expected_healthy - 1:
            break
        time.sleep(3)
    current_healthy = len(data)
    assert current_healthy <= expected_healthy - 1, \
        f"Killed instance not deregistered; {current_healthy} still healthy"
    print(f"  PASS  PASS (deregistered, {current_healthy} healthy remain)")


def main():
    parser = argparse.ArgumentParser(description="Dynamic Service Discovery Test")
    parser.add_argument("--service", choices=list(SERVICE_MAP.keys()), default="support")
    parser.add_argument("--scale-to", type=int, default=5, help="Target replicas for scale-up test")
    parser.add_argument("--skip-scale-down", action="store_true", help="Skip scale-down")
    parser.add_argument("--skip-failure", action="store_true", help="Skip failure recovery test")
    args = parser.parse_args()
    svc = SERVICE_MAP[args.service]
    init = svc["initial_replicas"]
    consul_name = svc["consul_name"]
    port = svc["port"]
    compose_name = svc["compose"]
    print(f"Testing service discovery for: {consul_name}")
    print(f"  Initial replicas: {init}, scale target: {args.scale_to}, port: {port}")
    test_registration(consul_name, init)
    test_nginx_config(init, port)
    test_traffic_routing(port, init)
    test_scale_up(compose_name, consul_name, init, args.scale_to)
    test_nginx_config(args.scale_to, port)
    test_traffic_routing(port, args.scale_to)
    if not args.skip_failure:
        test_failure_recovery(consul_name, args.scale_to)
    if not args.skip_scale_down:
        test_scale_down(compose_name, consul_name, args.scale_to, init, port)
    print(f"\n{'=' * 50}")
    print("  ALL SERVICE DISCOVERY TESTS PASSED")
    print(f"{'=' * 50}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
