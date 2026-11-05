# Production API image. Built from src/Api only, so the development sign-in assembly cannot be included (ADR-0004).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Api/TeamBeats.Api.csproj -c Release -o /out \
 && test ! -f /out/TeamBeats.Modules.Identity.DevAuth.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "TeamBeats.Api.dll"]
