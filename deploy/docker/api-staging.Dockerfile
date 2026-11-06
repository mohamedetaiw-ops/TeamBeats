# Staging API image (spec 003 US7, ADR-0004). Built from src/Api.DevHost, which adds development sign-in in
# test account mode so testers can sign in as the staging test admin with one click. Never use this image for production: tools/deploy-lint.sh
# fails CI if a production manifest references it. Production uses api.Dockerfile, which cannot contain the
# development sign-in assembly.
#
# Runtime settings supplied by the deployment, never by source control:
#   ConnectionStrings__App, ConnectionStrings__Owner                   (secrets)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Api.DevHost/TeamBeats.Api.DevHost.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Staging \
    Storage__Root=/tmp/teambeats-storage
ENTRYPOINT ["dotnet", "TeamBeats.Api.DevHost.dll"]
