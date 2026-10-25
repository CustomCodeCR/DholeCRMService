#!/usr/bin/env bash
# Explicit, image-only CRM rollback. Never rewinds PostgreSQL or deletes data.
# Prerequisites: approved operator, a locally retained immutable release and
# the protected DholeSystem Compose checked out under _dhole-system/.
set -euo pipefail
umask 077

mode="${1:?Usage: rollback-crm.sh staging|production}"
[[ "$mode" == staging || "$mode" == production ]] || exit 2
[[ "${CRM_ROLLBACK_APPROVED:-false}" == true ]] || {
  echo "CRM rollback requires explicit operator approval" >&2; exit 1;
}
if [[ "$mode" == staging ]]; then
  expected_env=/opt/dhole/.env.staging
  expected_network=dhole-staging
  expected_project=dhole-staging
  tag=staging
else
  expected_env=/opt/dhole/.env
  expected_network=dhole
  expected_project=dhole
  tag=latest
fi
# Ignore caller-controlled env overrides that could point to a different environment.
[[ -r "$expected_env" && -r /opt/dhole/docker-compose.services.yml ]] || {
  echo "Protected Dhole runtime configuration unavailable" >&2; exit 1;
}
[[ -f _dhole-system/deploy/docker-compose.services.yml ]] || exit 1
release_dir=/opt/dhole/releases/crm
current_file="$release_dir/${mode}-success.sha"
previous_file="$release_dir/${mode}-previous.sha"
[[ -r "$current_file" && -r "$previous_file" ]] || {
  echo "No recorded successful CRM release and predecessor" >&2; exit 1;
}
current_sha="$(cat "$current_file")"
rollback_sha="$(cat "$previous_file")"
[[ "$current_sha" =~ ^[a-f0-9]{40}$ &&
   "$rollback_sha" =~ ^[a-f0-9]{40}$ &&
   "$current_sha" != "$rollback_sha" ]] || {
  echo "Invalid or identical release identifiers" >&2; exit 1;
}

compose=(-f _dhole-system/deploy/docker-compose.services.yml)
if [[ "$mode" == staging ]]; then
  compose+=(-f _dhole-system/deploy/docker-compose.staging.yml)
fi
export DHOLE_ENV_FILE="$expected_env" DHOLE_NETWORK="$expected_network" DHOLE_IMAGE_TAG="$tag"
docker compose --env-file "$expected_env" "${compose[@]}" -p "$expected_project" config -q
docker compose --env-file "$expected_env" "${compose[@]}" -p "$expected_project" config --format json |
  python3 scripts/validate-compose.py "$expected_network"

# Fail before changing ANY image aliases if both old images were not retained.
for name in crm-api crm-workers; do
  docker image inspect "dhole/$name:$rollback_sha" >/dev/null
  docker image inspect "dhole/$name:$current_sha" >/dev/null
done

deploy_sha() {
  local image_sha="$1"
  docker tag "dhole/crm-api:$image_sha" "dhole/crm-api:$tag"
  docker tag "dhole/crm-workers:$image_sha" "dhole/crm-workers:$tag"
  docker compose --env-file "$expected_env" "${compose[@]}" -p "$expected_project" \
    up -d --no-deps --no-build crm-api crm-workers
}
ready() {
  local api_id worker_id
  api_id="$(docker compose --env-file "$expected_env" "${compose[@]}" -p "$expected_project" ps -q crm-api)"
  worker_id="$(docker compose --env-file "$expected_env" "${compose[@]}" -p "$expected_project" ps -q crm-workers)"
  [[ -n "$api_id" && -n "$worker_id" ]] &&
    docker exec "$api_id" curl -fsS --max-time 5 http://127.0.0.1:8080/health/ready >/dev/null 2>&1 &&
    docker exec "$worker_id" curl -fsS --max-time 5 http://127.0.0.1:8082/health/ready >/dev/null 2>&1
}
check_health() {
  local attempt
  for attempt in $(seq 1 45); do
    if ready; then return 0; fi
    sleep 2
  done
  return 1
}
echo "Rolling back CRM images only in $mode; database and outbox remain unchanged."
deploy_sha "$rollback_sha"
if ! check_health; then
  echo "Rollback readiness failed; restoring previously active CRM images" >&2
  deploy_sha "$current_sha"
  check_health || echo "Restored release also failed readiness: operator intervention required" >&2
  exit 1
fi

# Verify the worker and API from the actual target Docker network.
for upstream in crm-api:8080/health/ready crm-workers:8082/health/ready \
                auth-api:8080/health config-api:8080/health \
                pricing-api:8080/health api-gateway:8080/health; do
  if ! docker run --rm --network "$expected_network" curlimages/curl:8.15.0 \
    -fsS --max-time 12 "http://$upstream" >/dev/null; then
    echo "Rollback smoke failed; attempting to restore last active images" >&2
    deploy_sha "$current_sha"
    check_health || echo "Previous active image readiness also failed" >&2
    exit 1
  fi
done
# Success markers only change after all checks have passed.
printf '%s\n' "$rollback_sha" > "$current_file"
printf '%s\n' "$current_sha" > "$previous_file"
echo "CRM $mode image-only rollback succeeded: $rollback_sha (previous: $current_sha)"
