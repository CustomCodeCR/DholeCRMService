# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src
COPY NuGet.Config ./
COPY src/ ./src/
ARG GITHUB_ACTOR=github-actions
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet restore src/Dhole.Crm.Workers/Dhole.Crm.Workers.csproj --configfile NuGet.Config && \
    dotnet publish src/Dhole.Crm.Workers/Dhole.Crm.Workers.csproj -c Release --no-restore -o /publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish /publish ./
USER $APP_UID
EXPOSE 8082
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s CMD curl -fsS http://127.0.0.1:8082/health/ready >/dev/null || exit 1
ENTRYPOINT ["dotnet","Dhole.Crm.Workers.dll"]
