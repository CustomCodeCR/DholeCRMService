# DholeCRM — deployment and rollback (Phase 1)

## Scope and dependencies

This release has **zero CRM EF migrations and zero business tables**. It verifies PostgreSQL and Redis using live SELECT 1 / PING. It does not publish or consume business events until the relevant later phases. Credentials must stay in `/opt/dhole/.env[.staging]`; GitHub token is passed only as a Docker BuildKit secret to private NuGet restore.

Runtime Compose is provided by `CustomCodeCR/DholeSystem/deploy/docker-compose.services.yml`, which **includes** `/opt/dhole/docker-compose.services.yml`; never copy/replace it. An effective Compose preflight must pass; unmerged `DholeSystem` overrides or duplicate `api-gateway` cause a safe failure.

Set `CRM_STAGING_DEPLOY_ENABLED=true` in GitHub repository variables to enable staging deployment after PR-validated `develop` pushes. Provide a production GitHub environment with mandatory reviewer approval and separately enable `CRM_PRODUCTION_DEPLOY_APPROVED=true`. Production refuses to run unless the successful staging SHA is an ancestor of the promoted `master` commit. Both gates are **off by default**.

The self-hosted runner needs dotnet 10, Docker Compose, Docker BuildKit, outbound NuGet/package and container-image access, and permissions for protected runtime files. Provision the `dhole_crm` database on the target PostgreSQL instance **before deploying**; the phase 1 workflows never create/alter DB schemas. Keep existing services running.

## Manual preflight

- Confirm `/opt/dhole/.env.staging` references isolated staging DB and Redis; do not print its values.
- Confirm `/opt/dhole/docker-compose.services.yml` has no duplicate `crm-api` or `crm-workers` and the effective `api-gateway` topology is valid.
- Run `docker compose --env-file /opt/dhole/.env.staging -f _dhole-system/deploy/docker-compose.services.yml -f _dhole-system/deploy/docker-compose.staging.yml -p dhole-staging config -q` on the runner.
- Ensure `dhole-staging` and `dhole` networks, DNS and Traefik are preconfigured. There is no public CRM route until phase 4.
- Review the existing Gateway's checked-in JWT signing key exposure separately: rotate if active; never copy it to CRM.

## Image versioning and data safety

Staging tag `staging`; production tag `latest` plus immutable commit SHA. The successful SHA is recorded under `/opt/dhole/releases/crm/{staging,production}-success.sha`; previous SHA preserved. Commands only recreate `crm-api` and `crm-workers` with `--no-deps`; production promotion requires staging success.

Any migration file appearing under `*/Migrations/*` **fails closed** in phase 1. Before phase 2, replace this safety gate with versioned, separately reviewed migration preflight, verified backups and guarded migrations; never drop tables or rollback user data automatically.

## Rollback (images only)

1. Stop ongoing deployment, investigate health and ensure no incompatible migration was applied.
2. Read `/opt/dhole/releases/crm/production-previous.sha`. Confirm both `dhole/crm-api:$SHA` and `dhole/crm-workers:$SHA` images are retained.
3. Tag those two immutable images back to `latest` and `docker compose ... up -d --no-deps --no-build crm-api crm-workers` using the **same verified production Compose and env**.
4. Recheck actual readiness and observability; do not change or restore the database automatically.

The first-ever release has no previous image and cannot perform image rollback until an earlier healthy SHA has been recorded. CI, readiness, staging gate and approval status must be reported rather than assumed.
