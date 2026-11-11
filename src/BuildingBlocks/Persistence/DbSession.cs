using System.Data;
using Npgsql;
using TeamBeats.TenantContext;

namespace TeamBeats.Persistence;

/// <summary>
/// Unit of work for one request or message: one connection, one transaction, and tenant context applied
/// transaction-locally with <c>set_config('app.tenant_id', ..., true)</c> so it cannot leak across pooled
/// connections (constitution I, r0-solution-structure section 3). Every module shares the session, so a
/// business change, its audit record and its outbox message commit or roll back together.
/// There is no way to run a query outside a transaction through this type.
/// </summary>
public interface IDbSession : IAsyncDisposable
{
    /// <summary>Opened connection inside the transaction, with the current tenant context applied.</summary>
    Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Runs a callback after a successful commit (for example cache eviction).</summary>
    void OnCommitted(Action callback);

    bool IsCompleted { get; }
}

public sealed class DbSession : IDbSession
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TenantContextAccessor _context;
    private readonly List<Action> _afterCommit = [];
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private Guid? _appliedTenant;
    private bool _applied;

    public DbSession(AppDataSource dataSource, TenantContextAccessor context)
    {
        _dataSource = dataSource.DataSource;
        _context = context;
    }

    public bool IsCompleted { get; private set; }

    public async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct = default)
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("The database session has already completed.");
        }

        if (_connection is null)
        {
            _connection = await _dataSource.OpenConnectionAsync(ct);
            _transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        if (!_applied || _appliedTenant != _context.TenantId)
        {
            await using var set = new NpgsqlCommand("select set_config('app.tenant_id', @tid, true)", _connection, _transaction);
            set.Parameters.AddWithValue("tid", _context.TenantId?.ToString() ?? string.Empty);
            await set.ExecuteNonQueryAsync(ct);
            _appliedTenant = _context.TenantId;
            _applied = true;
        }

        return new NpgsqlCommand(sql, _connection, _transaction);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("The database session has already completed.");
        }

        if (_transaction is not null)
        {
            await _transaction.CommitAsync(ct);
        }

        IsCompleted = true;
        foreach (var callback in _afterCommit)
        {
            callback();
        }
    }

    public void OnCommitted(Action callback) => _afterCommit.Add(callback);

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            if (!IsCompleted)
            {
                try
                {
                    await _transaction.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException)
                {
                    // The connection already ended the transaction.
                }
            }
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        IsCompleted = true;
    }
}

/// <summary>
/// Wraps the application data source, which connects as <c>tb_app</c> (no ownership, no BYPASSRLS). It is disposed with
/// its host, which closes the pool; otherwise every stopped host (tests start several) keeps its idle connections open.
/// </summary>
public sealed class AppDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    public void Dispose() => DataSource.Dispose();
}
