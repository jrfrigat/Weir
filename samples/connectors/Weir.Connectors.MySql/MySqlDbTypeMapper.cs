using MySqlConnector;
using Weir.Contracts;

namespace Weir.Connectors.MySql;

/// <summary>Maps Weir's provider-agnostic types to MySQL <see cref="MySqlDbType"/> and back.</summary>
internal static class MySqlDbTypeMapper
{
    /// <summary>Maps a provider-agnostic type to the MySQL database type.</summary>
    /// <param name="type">The provider-agnostic type.</param>
    /// <returns>The matching <see cref="MySqlDbType"/>.</returns>
    public static MySqlDbType Map(WeirDbType type) => type switch
    {
        WeirDbType.String => MySqlDbType.VarChar,
        WeirDbType.AnsiString => MySqlDbType.VarChar,
        WeirDbType.Boolean => MySqlDbType.Bool,
        WeirDbType.Byte => MySqlDbType.Byte,
        WeirDbType.Int16 => MySqlDbType.Int16,
        WeirDbType.Int32 => MySqlDbType.Int32,
        WeirDbType.Int64 => MySqlDbType.Int64,
        WeirDbType.Decimal => MySqlDbType.Decimal,
        WeirDbType.Double => MySqlDbType.Double,
        WeirDbType.Single => MySqlDbType.Float,
        WeirDbType.DateTime => MySqlDbType.DateTime,
        WeirDbType.DateTimeOffset => MySqlDbType.DateTime,
        WeirDbType.Date => MySqlDbType.Date,
        WeirDbType.Time => MySqlDbType.Time,
        WeirDbType.Guid => MySqlDbType.Guid,
        WeirDbType.Binary => MySqlDbType.VarBinary,
        WeirDbType.Json => MySqlDbType.JSON,
        WeirDbType.Xml => MySqlDbType.Text,
        _ => MySqlDbType.VarChar,
    };

    /// <summary>Maps a MySQL type name (from information_schema) to a provider-agnostic type.</summary>
    /// <param name="mysqlTypeName">The MySQL type name, e.g. "int" or "varchar".</param>
    /// <returns>The closest <see cref="WeirDbType"/>.</returns>
    public static WeirDbType FromMySqlTypeName(string mysqlTypeName) => mysqlTypeName.ToLowerInvariant() switch
    {
        "int" or "integer" or "mediumint" => WeirDbType.Int32,
        "bigint" => WeirDbType.Int64,
        "smallint" or "year" => WeirDbType.Int16,
        "tinyint" => WeirDbType.Byte,
        "bit" or "bool" or "boolean" => WeirDbType.Boolean,
        "decimal" or "numeric" or "dec" => WeirDbType.Decimal,
        "double" or "real" => WeirDbType.Double,
        "float" => WeirDbType.Single,
        "varchar" or "char" or "text" or "tinytext" or "mediumtext" or "longtext" or "enum" or "set" => WeirDbType.String,
        "datetime" or "timestamp" => WeirDbType.DateTime,
        "date" => WeirDbType.Date,
        "time" => WeirDbType.Time,
        "binary" or "varbinary" or "blob" or "tinyblob" or "mediumblob" or "longblob" => WeirDbType.Binary,
        "json" => WeirDbType.Json,
        _ => WeirDbType.String,
    };
}
