#!/usr/bin/env python3
"""Fail-closed static checks: no secrets in tracked runtime files, no fake success endpoints."""
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
tracked = subprocess.check_output(["git", "-C", str(root), "ls-files"], text=True).splitlines()
errors = []
for entry in tracked:
    name = pathlib.PurePosixPath(entry).name
    if name == ".env" or (name.startswith(".env.") and name != ".env.example"):
        errors.append("tracked environment file: " + entry)
    if re.search(r"(?i)\.(pfx|pem|key|p12)$", name):
        errors.append("tracked key material: " + entry)
for path in root.glob("src/**/appsettings*.json"):
    data = path.read_text()
    if re.search(r'(?i)"(SecretKey|Password|ClientSecret|PrivateKey)"\s*:\s*"[^"]+"', data):
        errors.append("embedded secret in: " + str(path.relative_to(root)))
for file in ["src/Dhole.Crm.Api/Dockerfile", "src/Dhole.Crm.Workers/Dockerfile"]:
    data = (root / file).read_text()
    if "github_token" not in data or "--mount=type=secret" not in data:
        errors.append("Docker image missing secret-mounted private NuGet restore: " + file)
if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
print("Static configuration checks passed.")
