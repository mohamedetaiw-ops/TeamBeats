using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TeamBeats.TenantContext;

namespace TeamBeats.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddTeamBeatsPersistence(this IServiceCollection services, string appConnectionString)
    {
        services.TryAddSingleton(_ => new AppDataSource(NpgsqlDataSource.Create(appConnectionString)));
        services.TryAddScoped<TenantContextAccessor>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>());
        services.TryAddScoped<IDbSession, DbSession>();
        services.TryAddScoped<IdempotencyStore>();
        return services;
    }
}
