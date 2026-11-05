FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Workers/TeamBeats.Workers.csproj -c Release -o /out \
 && test ! -f /out/TeamBeats.Modules.Identity.DevAuth.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER app
ENTRYPOINT ["dotnet", "TeamBeats.Workers.dll"]
