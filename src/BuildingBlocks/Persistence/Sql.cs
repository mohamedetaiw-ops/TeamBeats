using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TeamBeats.Persistence;

/// <summary>JSON value passed as a jsonb parameter.</summary>
public sealed record Jsonb(string Json)
{
    public static Jsonb From<T>(T value) => new(JsonSerializer.Serialize(value, JsonDefaults.Options));
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Small explicit SQL helpers over Npgsql. Parameters come from an anonymous object; property names
/// become <c>@name</c> parameters. Values are always parameterized.
/// </summary>
public static class Sql
{
    public static async Task<int> ExecAsync(this IDbSession session, string sql, object? args = null, CancellationToken ct = default)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<T>> QueryAsync<T>(this IDbSession session, string sql, object? args, Func<NpgsqlDataReader, T> map, CancellationToken ct = default)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }
        return list;
    }

    public static async Task<T?> QuerySingleOrDefaultAsync<T>(this IDbSession session, string sql, object? args, Func<NpgsqlDataReader, T> map, CancellationToken ct = default)
    {
        var list = await session.QueryAsync(sql, args, map, ct);
        return list.Count switch
        {
            0 => default,
            1 => list[0],
            _ => throw new InvalidOperationException("Query returned more than one row."),
        };
    }

    public static async Task<T?> ScalarAsync<T>(this IDbSession session, string sql, object? args = null, CancellationToken ct = default)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, args);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }

    public static void Bind(NpgsqlCommand cmd, object? args)
    {
        if (args is null)
        {
            return;
        }

        foreach (var prop in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = prop.GetValue(args);
            var name = prop.Name;
            switch (value)
            {
                case null:
                    cmd.Parameters.Add(new NpgsqlParameter(name, ParameterType(prop.PropertyType)) { Value = DBNull.Value });
                    break;
                case Jsonb json:
                    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = json.Json });
                    break;
                case Enum e:
                    cmd.Parameters.AddWithValue(name, ToSnake(e.ToString()));
                    break;
                default:
                    cmd.Parameters.AddWithValue(name, value);
                    break;
            }
        }
    }

    private static NpgsqlDbType ParameterType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(Guid)) return NpgsqlDbType.Uuid;
        if (u == typeof(int)) return NpgsqlDbType.Integer;
        if (u == typeof(long)) return NpgsqlDbType.Bigint;
        if (u == typeof(bool)) return NpgsqlDbType.Boolean;
        if (u == typeof(DateTimeOffset) || u == typeof(DateTime)) return NpgsqlDbType.TimestampTz;
        if (u == typeof(Jsonb)) return NpgsqlDbType.Jsonb;
        if (u == typeof(string[])) return NpgsqlDbType.Array | NpgsqlDbType.Text;
        if (u == typeof(Guid[])) return NpgsqlDbType.Array | NpgsqlDbType.Uuid;
        return NpgsqlDbType.Text;
    }

    /// <summary>Enum values are stored as snake_case text.</summary>
    public static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static TEnum ParseEnum<TEnum>(string snake) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(snake.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);

    public static T? Get<T>(this NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        if (r.IsDBNull(ordinal))
        {
            return default;
        }

        var underlying = Nullable.GetUnderlyingType(typeof(T));
        if (underlying is null)
        {
            return r.GetFieldValue<T>(ordinal);
        }

        object value = underlying == typeof(DateTimeOffset) ? r.GetFieldValue<DateTimeOffset>(ordinal)
            : underlying == typeof(Guid) ? r.GetGuid(ordinal)
            : underlying == typeof(int) ? r.GetInt32(ordinal)
            : underlying == typeof(long) ? r.GetInt64(ordinal)
            : underlying == typeof(bool) ? r.GetBoolean(ordinal)
            : r.GetValue(ordinal);
        return (T)value;
    }

    public static bool IsUniqueViolation(this PostgresException ex) => ex.SqlState == PostgresErrorCodes.UniqueViolation;
}
