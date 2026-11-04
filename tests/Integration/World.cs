using TeamBeats.Testing;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TeamBeats.Integration.Tests;

[CollectionDefinition(WorldCollection.Name)]
public sealed class World : ICollectionFixture<TenantWorld>;
