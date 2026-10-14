#!/usr/bin/env bash
# Deploy ONLY CRM using the existing DholeSystem compose and the protected runtime env.
set -euo pipefail
umask 077
mode="${1:?staging or production}"
[[ "$mode" == staging || "$mode" == production ]] || exit 2
[[ -r "${DHOLE_ENV_FILE:?}" && -r /opt/dhole/docker-compose.services.yml ]] || { echo "Protected runtime Compose/env unavailable" >&2; exit 1; }
[[ -n "${GITHUB_SHA:-}" && -n "${GITHUB_TOKEN:-}" ]] || { echo "Build provenance missing" >&2; exit 1; }
[[ -f _dhole-system/deploy/docker-compose.services.yml ]] || exit 1
if find src -type f -path '*/Migrations/*' | grep -q . ; then
  # Phase 1 contains no migrations. A later phase must supply validated SQL,
  # a tested full backup and migration approval before modifying this gate.
  echo "Unreviewed database migrations: refusing automatic deployment" >&2
  exit 1
fi
export DHOLE_ENV_FILE DHOLE_NETWORK DHOLE_IMAGE_TAG
compose=(-f _dhole-system/deploy/docker-compose.services.yml)
if [[ "$mode" == staging ]]; then compose+=(-f _dhole-system/deploy/docker-compose.staging.yml); fi
# Do not print the effective Compose: it contains rendered credentials.
docker compose --env-file "$DHOLE_ENV_FILE" "${compose[@]}" -p "$COMPOSE_PROJECT" config -q
# Check the *effective*, not a guessed, definition.
docker compose --env-file "$DHOLE_ENV_FILE" "${compose[@]}" -p "$COMPOSE_PROJECT" config --format json | \
  python3 scripts/validate-compose.py "$DHOLE_NETWORK"
# Never source the protected env file or print rendered secrets.
# DB must already be provisioned in staging/production. Failure remains a blocker.
docker build --secret id=github_token,env=GITHUB_TOKEN -f src/Dhole.Crm.Api/Dockerfile \
  -t "dhole/crm-api:${GITHUB_SHA}" .
docker build --secret id=github_token,env=GITHUB_TOKEN -f src/Dhole.Crm.Workers/Dockerfile \
  -t "dhole/crm-workers:${GITHUB_SHA}" .
release_dir=/opt/dhole/releases/crm
mkdir -p "$release_dir"
previous=""
if [[ -r "$release_dir/${mode}-success.sha" ]]; then previous="$(cat "$release_dir/${mode}-success.sha")"; fi
# The staging Compose points at :staging; production Compose at :latest.
docker tag "dhole/crm-api:${GITHUB_SHA}" "dhole/crm-api:${DHOLE_IMAGE_TAG}"
docker tag "dhole/crm-workers:${GITHUB_SHA}" "dhole/crm-workers:${DHOLE_IMAGE_TAG}"
# Never start / restart unrelated microservices.
docker compose --env-file "$DHOLE_ENV_FILE" "${compose[@]}" -p "$COMPOSE_PROJECT" \
  up -d --no-deps --no-build crm-api crm-workers
for attempt in $(seq 1 45); do
  api_id="$(docker compose --env-file "$DHOLE_ENV_FILE" "${compose[@]}" -p "$COMPOSE_PROJECT" ps -q crm-api)"
  worker_id="$(docker compose --env-file "$DHOLE_ENV_FILE" "${compose[@]}" -p "$COMPOSE_PROJECT" ps -q crm-workers)"
  if [[ -n "$api_id" && -n "$worker_id" ]] && \
     docker exec "$api_id" curl -fsS http://127.0.0.1:8080/health/ready >/dev/null 2>&1 && \
     docker exec "$worker_id" curl -fsS http://127.0.0.1:8082/health/ready >/dev/null 2>&1; then
    break
  fi
  if [[ "$attempt" -eq 45 ]]; then
    echo "CRM readiness failed; old image SHA available for operator rollback" >&2
    exit 1
  fi
  sleep 2
done
# Independent networking smoke: HTTP and gRPC executed from matching Docker network.
docker run --rm --network "$DHOLE_NETWORK" curlimages/curl:8.15.0 \
  -fsS --max-time 10 http://crm-api:8080/health/ready >/dev/null
docker run --rm --network "$DHOLE_NETWORK" curlimages/curl:8.15.0 \
  -fsS --max-time 10 http://crm-workers:8082/health/ready >/dev/null
# Verify gRPC from internal network using the pinned image and reflection-free proto.
docker run --rm --network "$DHOLE_NETWORK" \
  -v "$PWD/src/Dhole.Crm.Contracts/Protos/crm_diagnostics.proto:/crm.proto:ro" \
  fullstorydev/grpcurl:v1.9.3 -plaintext -import-path / -proto crm.proto \
  -d '{}' crm-api:8081 dhole.crm.diagnostics.v1.CrmDiagnostics/Check | \
  python3 scripts/validate-grpc-result.py
if [[ -n "$previous" && "$previous" != "$GITHUB_SHA" ]]; then
  printf '%s\n' "$previous" > "$release_dir/${mode}-previous.sha"
fi
printf '%s\n' "$GITHUB_SHA" > "$release_dir/${mode}-success.sha"
echo "CRM ${mode} deployed and real readiness checks passed: ${GITHUB_SHA}"
