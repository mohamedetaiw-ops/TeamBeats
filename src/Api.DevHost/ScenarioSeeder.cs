using TeamBeats.DevFakes;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Modules.Integrations;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// Connects the seeded test tenant to the scenario's fake directory, through each module's own use cases so the data is
/// audited and shaped exactly as in production: Entra ID single sign-on (spec 003 US2), the Microsoft 365 connection with
/// its features consented (spec 006 US5), directory sync settings and a first sync (spec 003 US6), which creates the
/// people, groups and organization attributes. Every step is skipped when it is already done, so restarts are safe.
/// </summary>
public sealed class ScenarioSeeder(FakeSystems fakes, IServiceScopeFactory scopes, ILogger<ScenarioSeeder> logger)
{
    public async Task SeedAsync(string tenantKey, CancellationToken ct)
    {
        if (fakes.Scenario.Tenant is not { } plan)
        {
            return;
        }
        var key = TenantRules.NormalizeKey(tenantKey);
        TenantInfo? tenant;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenant = (await list.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct)).FirstOrDefault(t => t.Key == key);
        }
        if (tenant is null)
        {
            logger.LogWarning("Scenario {Scenario}: tenant {Tenant} not found, nothing seeded", fakes.Scenario.Name, key);
            return;
        }
        var dir = fakes.Scenario.FindDirectory(plan.Directory)!;
        var directoryId = Guid.Parse(dir.Id);

        // Each step is its own transaction, in the tenant's context, as the system actor.
        async Task<T> Step<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            context.SetTenant(tenant.Id);
            context.SetActor(ActorType.System, null);
            var result = await work(scope.ServiceProvider);
            await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            return result;
        }

        if (plan.ConfigureSso && dir.SsoApp is { } app)
        {
            var configured = await Step(async sp =>
            {
                var providers = sp.GetRequiredService<IdentityProviderStore>();
                if (await providers.GetCurrentAsync(ct) is not null)
                {
                    return false;
                }
                await providers.SaveAsync(new IdentityProviderRequest(
                    IdentityProviderStore.KindEntra,
                    FakeIdentityProvider.IssuerFor(directoryId),
                    app.ClientId,
                    SecretReferences.For(tenant.Id, app.SecretName),
                    plan.SsoDisplayName ?? dir.DisplayName,
                    plan.JitDomains.Count > 0,
                    plan.JitDomains,
                    plan.GroupRoles.Select(m => new GroupRoleMapping(m.Key, m.Value, "tenant", null)).ToList(),
                    true), ct);
                return true;
            });
            if (configured)
            {
                logger.LogInformation("Scenario {Scenario}: single sign-on configured for {Tenant}", fakes.Scenario.Name, key);
            }
        }

        var connectionId = await Step(async sp =>
        {
            var connections = sp.GetRequiredService<ConnectionService>();
            var existing = (await connections.ListAsync(ct)).FirstOrDefault(c => c.ExternalTenantId == directoryId.ToString("D"));
            if (existing is not null)
            {
                return existing.Id;
            }
            var created = await connections.CreateAsync(new CreateConnectionRequest("microsoft365", dir.Id, plan.ConnectionName, plan.Features), ct);
            // Admin consent for each feature, as the fake consent page would give it.
            fakes.Graph.Consent(directoryId, dir.GrantedRoles, dir.Users.Count, dir.Groups.Count);
            fakes.Graph.AddDelegatedConsent(directoryId, plan.Features.Select(FeatureCatalogue.Find).OfType<FeatureDefinition>()
                .SelectMany(f => f.Permissions).Where(p => p.Type == PermissionType.Delegated).Select(p => p.Name).ToArray());
            foreach (var feature in plan.Features)
            {
                var (url, binding) = await connections.StartConsentAsync(created.Id, feature, key, $"https://{key}.invalid/consent", ct);
                var state = FakeIdentityProvider.ParseQuery(new Uri(url).Query)["state"];
                await connections.CompleteConsentAsync(state, binding, "True", dir.Id, null, ct);
            }
            logger.LogInformation("Scenario {Scenario}: Microsoft 365 connection created for {Tenant}", fakes.Scenario.Name, key);
            return created.Id;
        });

        if (!plan.Features.Contains("directory_sync"))
        {
            return;
        }
        var newSettings = await Step(async sp =>
        {
            var directory = sp.GetRequiredService<DirectorySyncService>();
            if (await directory.GetSettingsAsync(ct) is not null)
            {
                return false;
            }
            await directory.SaveSettingsAsync(new DirectorySettingsRequest(new UserScope(UserScope.All, []), [], 30, plan.DeletionThresholdPct), ct);
            return true;
        });
        if (newSettings && plan.RunInitialSync)
        {
            var run = await Step(sp => sp.GetRequiredService<DirectorySyncService>().RunAsync(connectionId, ct));
            // Deliver the sync's events here too, so local development (which has no workers) sees the result.
            await using (var scope = scopes.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(1000, ct);
            }
            logger.LogInformation("Scenario {Scenario}: first directory sync {Outcome}, {Created} people created",
                fakes.Scenario.Name, run?.Outcome, run?.Created);
        }
    }
}
