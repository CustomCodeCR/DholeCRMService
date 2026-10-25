#!/usr/bin/env python3
import json
import sys

try:
    obj = json.load(sys.stdin)
except (ValueError, EOFError):
    sys.exit("No valid gRPC diagnostics response")
if not (obj.get("ready") and obj.get("postgresReachable") and obj.get("redisReachable")):
    sys.exit("gRPC readiness failed")
print("CRM internal gRPC diagnosis passed.")
