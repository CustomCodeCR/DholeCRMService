# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src
COPY NuGet.Config ./
COPY src/ ./src/
ARG GITHUB_ACTOR=github-actions
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet restore src/Dhole.Crm.Api/Dhole.Crm.Api.csproj --configfile NuGet.Config && \
    dotnet publish src/Dhole.Crm.Api/Dhole.Crm.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish /publish ./
USER $APP_UID
EXPOSE 8080 8081
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s CMD curl -fsS http://127.0.0.1:8080/health/ready >/dev/null || exit 1
ENTRYPOINT ["dotnet","Dhole.Crm.Api.dll"]
