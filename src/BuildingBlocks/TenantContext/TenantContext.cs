namespace TeamBeats.TenantContext;

public enum ActorType
{
    Anonymous,
    User,
    PlatformOperator,
    System,

    /// <summary>An application app calling the public API as itself (spec 035 FR-011); its ID is the app's ID.</summary>
    App,
}

/// <summary>
/// The execution context for one request or one message. Tenant identity is only ever set from the
/// authenticated session (HTTP) or from message headers (workers), never from client input
/// (constitution I, spec 001 FR-006).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? ActorId { get; }
    ActorType ActorType { get; }
    Guid? SessionId { get; }
    HostScope Host { get; }

    /// <summary>Client IP for audit records, when the work came from HTTP.</summary>
    string? SourceIp { get; }

    /// <summary>Set when the session was opened on a shared kiosk device (spec 030 US4): only its features are reachable.</summary>
    SharedDeviceSession? SharedDevice => null;

    /// <summary>The tenant ID or a fail-closed exception when missing.</summary>
    Guid RequireTenant();

    Guid RequireUser();
}

/// <summary>Mutable, scoped implementation. Only infrastructure code sets values.</summary>
public sealed class TenantContextAccessor : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? ActorId { get; private set; }
    public ActorType ActorType { get; private set; } = ActorType.Anonymous;
    public Guid? SessionId { get; private set; }
    public HostScope Host { get; private set; } = HostScope.Unknown;
    public string? SourceIp { get; set; }
    public SharedDeviceSession? SharedDevice { get; set; }

    /// <summary>Raised when the tenant changes so the open database transaction can re-apply context.</summary>
    public event Action<Guid?>? TenantChanged;

    public void SetHost(HostScope host) => Host = host;

    public void SetTenant(Guid? tenantId)
    {
        if (TenantId == tenantId)
        {
            return;
        }
        TenantId = tenantId;
        TenantChanged?.Invoke(tenantId);
    }

    public void SetActor(ActorType type, Guid? actorId, Guid? sessionId = null)
    {
        ActorType = type;
        ActorId = actorId;
        SessionId = sessionId;
    }

    public Guid RequireTenant() =>
        TenantId ?? throw new InvalidOperationException("Tenant context is missing. Failing closed.");

    public Guid RequireUser() =>
        ActorType == ActorType.User && ActorId is { } id
            ? id
            : throw new InvalidOperationException("No authenticated tenant user in context.");
}
