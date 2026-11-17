# Builds the API, worker and migrator from one restore. Select with --target api|worker|migrator.
# Base images are pinned to exact patch tags (ARC-001); runtime stages run as the non-root 'app' user.

FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig TrustedInventory.slnx ./
COPY src/ src/
COPY db/ db/
# The migrator embeds the fixture plan catalog (DEC-062).
COPY docs/entitlement-catalog.json docs/
ENV CI=true
RUN dotnet restore src/TrustedInventory.Api/TrustedInventory.Api.csproj --locked-mode \
 && dotnet restore src/TrustedInventory.Worker/TrustedInventory.Worker.csproj --locked-mode \
 && dotnet restore src/TrustedInventory.Migrator/TrustedInventory.Migrator.csproj --locked-mode
RUN dotnet publish src/TrustedInventory.Api/TrustedInventory.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/TrustedInventory.Worker/TrustedInventory.Worker.csproj -c Release --no-restore -o /out/worker \
 && dotnet publish src/TrustedInventory.Migrator/TrustedInventory.Migrator.csproj -c Release --no-restore -o /out/migrator

FROM mcr.microsoft.com/dotnet/aspnet:10.0.5 AS api
WORKDIR /app
COPY --from=build /out/api ./
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TrustedInventory.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0.5 AS worker
WORKDIR /app
COPY --from=build /out/worker ./
USER $APP_UID
ENTRYPOINT ["dotnet", "TrustedInventory.Worker.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0.5 AS migrator
WORKDIR /app
COPY --from=build /out/migrator ./
USER $APP_UID
ENTRYPOINT ["dotnet", "TrustedInventory.Migrator.dll"]
