#!/usr/bin/env python3
"""Bounded, opt-in CRM staging performance gate. Never logs JWT or CRM response bodies."""
import argparse
import concurrent.futures
import math
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


def measure(url, bearer, timeout):
    request = urllib.request.Request(
        url, headers={"Authorization": "Bearer " + bearer, "Accept": "application/json"}
    )
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            response.read(1)  # Do not collect or print customer data.
            status = response.status
    except urllib.error.HTTPError as exc:
        status = exc.code
    except (OSError, TimeoutError):
        status = 0
    return (time.monotonic() - started) * 1000, status


def main():
    args_parser = argparse.ArgumentParser(
        description="CRM search performance gate: authorized staging environment only"
    )
    args_parser.add_argument("--base-url", required=True)
    args_parser.add_argument("--requests", type=int, default=60)
    args_parser.add_argument("--concurrency", type=int, default=4)
    args_parser.add_argument("--timeout", type=float, default=8)
    args_parser.add_argument("--p95-budget-ms", type=float, default=1500)
    args = args_parser.parse_args()
    if os.environ.get("CRM_PERF_STAGING_CONFIRMED") != "true":
        args_parser.error("explicit CRM_PERF_STAGING_CONFIRMED=true required")
    token = os.environ.get("CRM_PERF_TOKEN", "")
    if not token or len(token) > 8192:
        args_parser.error("a staging-only CRM_PERF_TOKEN must be provided securely")
    if not 1 <= args.requests <= 100 or not 1 <= args.concurrency <= 10:
        args_parser.error("max 100 requests, concurrency <=10 (below CRM rate limit)")
    if not 1 <= args.timeout <= 30 or not 100 <= args.p95_budget_ms <= 30000:
        args_parser.error("timeout or p95 budget out of safe bounds")
    parsed = urllib.parse.urlparse(args.base_url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password:
        args_parser.error("base URL must be valid HTTP(S), without credentials")
    if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
        args_parser.error("base URL must be origin-only")
    url = args.base_url.rstrip("/") + "/api/crm/search?q=CrmLoadTestNoRecords&pageSize=10"
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        measures = list(pool.map(lambda _: measure(url, token, args.timeout), range(args.requests)))
    latencies = sorted(duration for duration, _ in measures)
    statuses = [status for _, status in measures]
    errors = sum(status != 200 for status in statuses)
    p95 = latencies[math.ceil(0.95 * len(latencies)) - 1]
    error_ratio = errors / len(statuses)
    print(f"CRM staging search n={len(statuses)} p95_ms={p95:.1f} "
          f"error_ratio={error_ratio:.3f} (no response payloads logged)")
    if p95 > args.p95_budget_ms or error_ratio > 0.01:
        print("CRM staging performance gate failed", file=sys.stderr)
        return 1
    print("CRM staging performance gate passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
