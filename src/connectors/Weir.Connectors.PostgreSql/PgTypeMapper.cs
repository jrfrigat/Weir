using NpgsqlTypes;
using Weir.Contracts;

namespace Weir.Connectors.PostgreSql;

/// <summary>Maps Weir's provider-agnostic types to Npgsql <see cref="NpgsqlDbType"/> and back.</summary>
internal static class PgTypeMapper
{
    /// <summary>Maps a provider-agnostic type to the Npgsql database type.</summary>
    /// <param name="type">The provider-agnostic type.</param>
    /// <returns>The matching <see cref="NpgsqlDbType"/>.</returns>
    public static NpgsqlDbType Map(WeirDbType type) => type switch
    {
        WeirDbType.String => NpgsqlDbType.Text,
        WeirDbType.AnsiString => NpgsqlDbType.Varchar,
        WeirDbType.Boolean => NpgsqlDbType.Boolean,
        WeirDbType.Byte => NpgsqlDbType.Smallint,
        WeirDbType.Int16 => NpgsqlDbType.Smallint,
        WeirDbType.Int32 => NpgsqlDbType.Integer,
        WeirDbType.Int64 => NpgsqlDbType.Bigint,
        WeirDbType.Decimal => NpgsqlDbType.Numeric,
        WeirDbType.Double => NpgsqlDbType.Double,
        WeirDbType.Single => NpgsqlDbType.Real,
        WeirDbType.DateTime => NpgsqlDbType.Timestamp,
        WeirDbType.DateTimeOffset => NpgsqlDbType.TimestampTz,
        WeirDbType.Date => NpgsqlDbType.Date,
        WeirDbType.Time => NpgsqlDbType.Time,
        WeirDbType.Guid => NpgsqlDbType.Uuid,
        WeirDbType.Binary => NpgsqlDbType.Bytea,
        WeirDbType.Json => NpgsqlDbType.Jsonb,
        WeirDbType.Xml => NpgsqlDbType.Xml,
        _ => NpgsqlDbType.Text,
    };

    /// <summary>Maps a PostgreSQL type name (from introspection) to a provider-agnostic type.</summary>
    /// <param name="pgTypeName">The PostgreSQL type name, e.g. "integer" or "timestamp with time zone".</param>
    /// <returns>The closest <see cref="WeirDbType"/>.</returns>
    public static WeirDbType FromPgTypeName(string pgTypeName) => pgTypeName.ToLowerInvariant() switch
    {
        "integer" or "int" or "int4" or "serial" => WeirDbType.Int32,
        "bigint" or "int8" or "bigserial" => WeirDbType.Int64,
        "smallint" or "int2" or "smallserial" => WeirDbType.Int16,
        "boolean" or "bool" => WeirDbType.Boolean,
        "numeric" or "decimal" or "money" => WeirDbType.Decimal,
        "double precision" or "float8" => WeirDbType.Double,
        "real" or "float4" => WeirDbType.Single,
        "character varying" or "varchar" or "character" or "char" or "text" or "name" or "citext" => WeirDbType.String,
        "timestamp without time zone" or "timestamp" => WeirDbType.DateTime,
        "timestamp with time zone" or "timestamptz" => WeirDbType.DateTimeOffset,
        "date" => WeirDbType.Date,
        "time without time zone" or "time" or "time with time zone" or "timetz" => WeirDbType.Time,
        "uuid" => WeirDbType.Guid,
        "bytea" => WeirDbType.Binary,
        "json" or "jsonb" => WeirDbType.Json,
        "xml" => WeirDbType.Xml,
        _ => WeirDbType.String,
    };
}
