using System.Data;
using Weir.Contracts;

namespace Weir.Connectors.SqlServer;

/// <summary>Maps Weir's provider-agnostic types to SQL Server <see cref="SqlDbType"/>.</summary>
internal static class SqlDbTypeMapper
{
    /// <summary>Maps a provider-agnostic <see cref="WeirDbType"/> to a SQL Server <see cref="SqlDbType"/>.</summary>
    /// <param name="type">The provider-agnostic type.</param>
    /// <returns>The SQL Server type.</returns>
    public static SqlDbType Map(WeirDbType type) => type switch
    {
        WeirDbType.String => SqlDbType.NVarChar,
        WeirDbType.AnsiString => SqlDbType.VarChar,
        WeirDbType.Boolean => SqlDbType.Bit,
        WeirDbType.Byte => SqlDbType.TinyInt,
        WeirDbType.Int16 => SqlDbType.SmallInt,
        WeirDbType.Int32 => SqlDbType.Int,
        WeirDbType.Int64 => SqlDbType.BigInt,
        WeirDbType.Decimal => SqlDbType.Decimal,
        WeirDbType.Double => SqlDbType.Float,
        WeirDbType.Single => SqlDbType.Real,
        WeirDbType.DateTime => SqlDbType.DateTime2,
        WeirDbType.DateTimeOffset => SqlDbType.DateTimeOffset,
        WeirDbType.Date => SqlDbType.Date,
        WeirDbType.Time => SqlDbType.Time,
        WeirDbType.Guid => SqlDbType.UniqueIdentifier,
        WeirDbType.Binary => SqlDbType.VarBinary,
        WeirDbType.Json => SqlDbType.NVarChar,
        WeirDbType.Xml => SqlDbType.Xml,
        WeirDbType.Structured => SqlDbType.Structured,
        _ => SqlDbType.NVarChar,
    };

    /// <summary>Maps a SQL Server type name (from introspection) to a provider-agnostic type.</summary>
    /// <param name="sqlTypeName">The SQL type name, e.g. "nvarchar" or "uniqueidentifier".</param>
    /// <returns>The closest <see cref="WeirDbType"/>.</returns>
    public static WeirDbType FromSqlTypeName(string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "int" => WeirDbType.Int32,
        "bigint" => WeirDbType.Int64,
        "smallint" => WeirDbType.Int16,
        "tinyint" => WeirDbType.Byte,
        "bit" => WeirDbType.Boolean,
        "decimal" or "numeric" or "money" or "smallmoney" => WeirDbType.Decimal,
        "float" => WeirDbType.Double,
        "real" => WeirDbType.Single,
        "nvarchar" or "nchar" or "ntext" or "sysname" => WeirDbType.String,
        "varchar" or "char" or "text" => WeirDbType.AnsiString,
        "datetime" or "datetime2" or "smalldatetime" => WeirDbType.DateTime,
        "datetimeoffset" => WeirDbType.DateTimeOffset,
        "date" => WeirDbType.Date,
        "time" => WeirDbType.Time,
        "uniqueidentifier" => WeirDbType.Guid,
        "varbinary" or "binary" or "image" or "timestamp" or "rowversion" => WeirDbType.Binary,
        "xml" => WeirDbType.Xml,
        "json" => WeirDbType.Json,
        _ => WeirDbType.String,
    };
}
