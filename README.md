# DholeCRMService

Dhole commercial CRM microservice — **Phase 1 bootstrap**. .NET 10 LTS, Minimal API, Clean Architecture, PostgreSQL, Redis, gRPC and a dependency heartbeat worker. No accounts, leads, opportunities, commercial endpoints, scopes, data migrations, Outbox/Inbox events or Dynamics operations yet; those belong to subsequent phases. The diagnostic gRPC contract is internal and **reports actual** PostgreSQL/Redis connectivity.

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
