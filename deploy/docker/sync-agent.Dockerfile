# The on-premises sync agent (spec 017 US6) as a Linux container, for customers who run it on Docker or Kubernetes
# inside their network. libldap is the OpenLDAP client that System.DirectoryServices.Protocols uses on Linux.
# Mount a volume at /var/lib/teambeats-agent for its key and state, and pass the service account through
# TEAMBEATS_AGENT_Agent__LdapUser and TEAMBEATS_AGENT_Agent__LdapPassword from the customer's own secret store.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SyncAgent/TeamBeats.SyncAgent.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libldap2 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /var/lib/teambeats-agent && chown app /var/lib/teambeats-agent
WORKDIR /app
COPY --from=build /out .
USER app
VOLUME /var/lib/teambeats-agent
ENTRYPOINT ["dotnet", "TeamBeats.SyncAgent.dll"]
