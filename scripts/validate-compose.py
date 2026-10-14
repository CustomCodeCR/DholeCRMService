#!/usr/bin/env python3
"""Consume sensitive Compose JSON from stdin WITHOUT logging it."""
import json
import sys

expected_network = sys.argv[1]
data = json.load(sys.stdin)
services = data.get("services", {})
required = ("crm-api", "crm-workers")
errors = []
for name in required:
    info = services.get(name)
    if not info:
        errors.append("missing " + name)
        continue
    image = info.get("image", "")
    if not image.startswith("dhole/" + name + ":"):
        errors.append("unexpected image for " + name)
    network_names = info.get("networks", {})
    assigned = [data.get("networks", {}).get(k, {}).get("name", k) for k in network_names]
    if expected_network not in assigned:
        errors.append("wrong network for " + name)
gateway = services.get("api-gateway", {})
# Do not modify the existing gateway during phase 1.
if not gateway:
    errors.append("missing existing api-gateway")
# Prevent an empty runtime configuration being treated as successful readiness.
if errors:
    print("CRM compose blocked: " + ", ".join(errors), file=sys.stderr)
    sys.exit(1)
print("CRM Compose topology validated without revealing configuration.")
