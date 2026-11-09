using TeamBeats.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamBeats.Persistence;

namespace TeamBeats.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public static readonly MigrationSource Migrations = new("messaging", typeof(Outbox).Assembly);

    /// <summary>Outbox writer (API and workers) and the consumer pipeline.</summary>
    public static IServiceCollection AddTeamBeatsMessaging(this IServiceCollection services)
    {
        services.TryAddScoped<IOutbox, Outbox>();
        services.TryAddSingleton<MessageProcessor>();
        services.TryAddSingleton<OutboxDispatcher>();
        return services;
    }

    public static IServiceCollection AddMessageHandler<TEvent, THandler>(this IServiceCollection services, string consumerName)
        where TEvent : IIntegrationEvent
        where THandler : class, IMessageHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new ConsumerRegistration(consumerName, Envelope.TypeName(typeof(TEvent)), typeof(TEvent), typeof(THandler)));
        return services;
    }

    /// <summary>Sets how soon consumers named with this prefix should handle their messages (see <see cref="ConsumerLagObjective"/>).</summary>
    public static IServiceCollection AddConsumerLagObjective(this IServiceCollection services, string prefix, TimeSpan objective)
    {
        services.AddSingleton(new ConsumerLagObjective(prefix, objective));
        return services;
    }

    /// <summary>Selects the transport from <c>Messaging:Transport</c> (RabbitMq or InMemory).</summary>
    public static IServiceCollection AddMessageTransport(this IServiceCollection services, IConfiguration configuration)
    {
        var transport = configuration["Messaging:Transport"] ?? "InMemory";
        if (string.Equals(transport, "RabbitMq", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.Section));
            services.TryAddSingleton<RabbitMqConnection>();
            services.TryAddSingleton<IMessageBroker, RabbitMqBroker>();
        }
        else
        {
            services.TryAddSingleton<IMessageBroker, InMemoryBroker>();
        }
        return services;
    }

    public static bool UsesRabbitMq(IConfiguration configuration) =>
        string.Equals(configuration["Messaging:Transport"], "RabbitMq", StringComparison.OrdinalIgnoreCase);
}
