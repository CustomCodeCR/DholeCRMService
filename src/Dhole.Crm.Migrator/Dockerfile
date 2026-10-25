# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src
COPY NuGet.Config ./
COPY src/ ./src/
ARG GITHUB_ACTOR=github-actions
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet restore src/Dhole.Crm.Migrator/Dhole.Crm.Migrator.csproj --configfile NuGet.Config && \
    dotnet publish src/Dhole.Crm.Migrator/Dhole.Crm.Migrator.csproj -c Release --no-restore -o /publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=publish /publish ./
USER $APP_UID
ENTRYPOINT ["dotnet","Dhole.Crm.Migrator.dll"]
