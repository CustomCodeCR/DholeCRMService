# DholeCRMService

Dhole commercial CRM microservice — **Phase 2 data foundation (dependent on unmerged Phase 1)**. .NET 10 LTS, Minimal API, Clean Architecture, PostgreSQL, Redis, gRPC and a dependency heartbeat worker. Includes PostgreSQL CRM accounts, leads, lead conversions, assignments, contacts, account-contact links, addresses, preferences, consent and business-activity records. User identities and business catalogs remain owned by DholeAuth and DholeConfig. This phase does not implement opportunities, workflows, Pricing linkage, Dynamics, Hacienda or the DholeWeb CRM UI. The diagnostic gRPC contract is internal and **reports actual** PostgreSQL/Redis connectivity.

## Projects

- `Domain`: domain boundary; references existing `CustomCodeFramework.Core`.
- `Application`: readiness abstraction and type.
- `Infrastructure`: real PostgreSQL `SELECT 1` and Redis `PING`.
- `Contracts`: versioned protobuf for internal diagnostics.
- `Api`: HTTP liveness/readiness (8080) and HTTP/2-only gRPC diagnostics (8081).
- `Workers`: independent background dependency verifier with liveness/readiness (8082).
- `UnitTests`, `IntegrationTests` and `Smoke`: code, actual external dependencies and running-process contract tests.

## Local setup

Install .NET SDK 10, Docker and Compose. Private NuGet source `CustomCodeCR` is configured in `NuGet.Config`; provide the package read token securely as `NuGetPackageSourceCredentials_github` (do not commit token). For Docker build set `GITHUB_TOKEN` in your shell and pass as BuildKit secret, never build arg:

```bash
dotnet restore DholeCRMService.slnx --configfile NuGet.Config
dotnet build DholeCRMService.slnx -c Release --no-restore
dotnet test DholeCRMService.slnx -c Release --no-build

docker build --secret id=github_token,env=GITHUB_TOKEN -f src/Dhole.Crm.Api/Dockerfile -t dhole/crm-api:local .
docker build --secret id=github_token,env=GITHUB_TOKEN -f src/Dhole.Crm.Workers/Dockerfile -t dhole/crm-workers:local .
docker compose -f deploy/docker-compose.local.yml up -d
dotnet run --project tests/Dhole.Crm.Smoke -- http://127.0.0.1:18083 http://127.0.0.1:18084 http://127.0.0.1:18085
```

Local Compose uses *explicitly non-production* credentials only, bound to loopback. Integration tests require `CRM_TEST_POSTGRES` and `CRM_TEST_REDIS`; without them the live test is inconclusive, never falsely passing. CI provides both test services.

## Environment keys

`ConnectionStrings__Postgres` (or `Postgres__ConnectionString`), `Redis__ConnectionString`; the existing DholeSystem Compose supplies both. `CRM_DB` is `dhole_crm`; `CRM_HTTP_URL` is `http://crm-api:8080`; `CRM_GRPC_URL` is `http://crm-api:8081`. No credentials are shipped in appsettings or published from health responses.

See [deployment and rollback](docs/DEPLOYMENT.md). Never auto-promote to production without successful staging, security review and an approved change.

## Phase 2 API and restrictions

CRM data endpoints under `/api/crm/*` are JWT protected; each request validates the active session owner via Auth's internal user lookup and evaluates effective CRM scopes from Auth **plus the account/lead assignment**. The backend feature flag `Features__CrmEnabled` is disabled by default and must only be activated after Auth/Config PRs, schema migration and gateway security review are promoted. Auth uses `Auth__Client__BaseAddress` and `Auth__Client__InternalServiceKey` (from protected host configuration), Config uses `Grpc__Clients__Config__Address`. Never enable this flag for direct public CRM API exposure while Gateway route/auth is pending in phase 4.

Phase 2 scopes: seven `crm.account.*`, seven `crm.lead.*`, five `crm.contact.*`; **19 total** seeded idempotently by DholeAuth PR #12, not assigned to roles automatically. Config PR #7 creates six CRM base catalog groups with 24 entries and preserves corporate catalogs.

The SQL migration `20261014193000_InitialCrmData` creates 10 tables under schema `crm`; `crm.lead_conversions.lead_id` and active fiscal identity have unique constraints. Internal migration executable: `dotnet run --project src/Dhole.Crm.Migrator`, requiring explicit `CRM_SCHEMA_MIGRATION_APPROVED=true` and protected database variables. The server deployment workflow verifies a pg_dump archive and schema approval before running it. Schema `Down` deliberately refuses destructive rollback.

The strict behavior is deliberate: an explicit account match must be selected to link an existing customer on lead conversion, and tax/owner authorization checked; otherwise duplicate identity returns HTTP 409 for human resolution. Account status, customer category and currencies are stored as codes, not externally duplicated entities.

**Known dependencies:** Fase 1 PR #1 has not been merged; Fase 0 DholeSystem PR #103 also open; Auth #12 and Config #7 must be verified and deployed in staging before CRM data handlers can be enabled. No staging or production deployment has occurred in this branch.
