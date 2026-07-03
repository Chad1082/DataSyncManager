using DataSyncManager.Web.Models;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System.Data;
using System.Data.Odbc;

namespace DataSyncManager.Web.Services;

public class SchemaColumn
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int MaxLength { get; set; } = -1;
    public bool IsNullable { get; set; } = true;
}

public interface ISchemaService
{
    Task<List<string>> GetTablesAsync(SourceServer server);
    Task<List<SchemaColumn>> GetColumnsAsync(SourceServer server, string tableName);
    Task<List<string>> GetDestinationTablesAsync(DestinationServer server, string database);
    Task<List<SchemaColumn>> GetDestinationColumnsAsync(DestinationServer server, string database, string tableName);
    Task<bool> TestSourceConnectionAsync(SourceServer server);
    Task<bool> TestDestinationConnectionAsync(DestinationServer server);
    Task<List<string>> GetDatabasesAsync(string connectionString);
    Task<(bool ok, string message)> TestConnectionWithDatabaseAsync(string connectionString, string database);
    Task<List<SchemaColumn>> GetColumnsFromQueryAsync(SourceServer server, string query);
}

public class SchemaService : ISchemaService
{
    private readonly ILogger<SchemaService> _log;
    private readonly IHttpClientFactory _httpFactory;

    public SchemaService(ILogger<SchemaService> log, IHttpClientFactory httpFactory)
    {
        _log = log;
        _httpFactory = httpFactory;
    }

    // ────────────────────────────────────────────────────
    // Source: Tables
    // ────────────────────────────────────────────────────

    public async Task<List<string>> GetTablesAsync(SourceServer server) =>
        server.SourceType switch
        {
            SourceType.SqlServer => await GetSqlServerTablesAsync(server.ConnectionString!),
            SourceType.Odbc => await GetOdbcTablesAsync(server.ConnectionString!),
            SourceType.RestApi => await GetRestApiEndpointsAsync(server),
            _ => new List<string>()
        };

    private async Task<List<string>> GetSqlServerTablesAsync(string cs)
    {
        var tables = new List<string>();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var sql = """
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
        return tables;
    }

    private Task<List<string>> GetOdbcTablesAsync(string cs)
    {
        var tables = new List<string>();
        using var conn = new OdbcConnection(cs);
        conn.Open();
        var schema = conn.GetSchema("Tables");
        foreach (DataRow row in schema.Rows)
        {
            var tableName = row["TABLE_NAME"].ToString()!;
            var tableType = row["TABLE_TYPE"]?.ToString() ?? "";
            if (tableType.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
                tables.Add(tableName);
        }
        return Task.FromResult(tables);
    }

    private async Task<List<string>> GetRestApiEndpointsAsync(SourceServer server)
    {
        // Convention: the REST API exposes a /meta/tables endpoint that returns an array of endpoint names
        try
        {
            var client = _httpFactory.CreateClient();
            if (!string.IsNullOrEmpty(server.AuthHeader))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", server.AuthHeader);

            var url = server.BaseUrl!.TrimEnd('/') + "/meta/tables";
            var json = await client.GetStringAsync(url);
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not discover REST API tables from {Url}", server.BaseUrl);
            return new List<string>();
        }
    }

    // ────────────────────────────────────────────────────
    // Source: Columns
    // ────────────────────────────────────────────────────

    public async Task<List<SchemaColumn>> GetColumnsAsync(SourceServer server, string tableName) =>
        server.SourceType switch
        {
            SourceType.SqlServer => await GetSqlServerColumnsAsync(server.ConnectionString!, tableName),
            SourceType.Odbc => await GetOdbcColumnsAsync(server.ConnectionString!, tableName),
            SourceType.RestApi => await GetRestApiColumnsAsync(server, tableName),
            _ => new List<SchemaColumn>()
        };

    private async Task<List<SchemaColumn>> GetSqlServerColumnsAsync(string cs, string tableFullName)
    {
        var parts = tableFullName.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : tableFullName;

        var cols = new List<SchemaColumn>();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var sql = """
            SELECT COLUMN_NAME, DATA_TYPE,
                   ISNULL(CHARACTER_MAXIMUM_LENGTH, -1),
                   IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            cols.Add(new SchemaColumn
            {
                Name = rdr.GetString(0),
                DataType = rdr.GetString(1),
                MaxLength = rdr.GetInt32(2),
                IsNullable = rdr.GetString(3) == "YES"
            });
        }
        return cols;
    }

    private Task<List<SchemaColumn>> GetOdbcColumnsAsync(string cs, string tableName)
    {
        var cols = new List<SchemaColumn>();
        using var conn = new OdbcConnection(cs);
        conn.Open();
        var schema = conn.GetSchema("Columns", new[] { null, null, tableName, null });
        foreach (DataRow row in schema.Rows)
        {
            cols.Add(new SchemaColumn
            {
                Name = row["COLUMN_NAME"].ToString()!,
                DataType = row["TYPE_NAME"]?.ToString() ?? "varchar",
                MaxLength = row["COLUMN_SIZE"] is DBNull ? -1 : Convert.ToInt32(row["COLUMN_SIZE"]),
                IsNullable = row["IS_NULLABLE"]?.ToString() == "YES"
            });
        }
        return Task.FromResult(cols);
    }

    private async Task<List<SchemaColumn>> GetRestApiColumnsAsync(SourceServer server, string endpoint)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            if (!string.IsNullOrEmpty(server.AuthHeader))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", server.AuthHeader);

            var url = $"{server.BaseUrl!.TrimEnd('/')}/meta/tables/{Uri.EscapeDataString(endpoint)}/columns";
            var json = await client.GetStringAsync(url);
            return JsonConvert.DeserializeObject<List<SchemaColumn>>(json) ?? new List<SchemaColumn>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not discover REST API columns for {Endpoint}", endpoint);
            return new List<SchemaColumn>();
        }
    }

    // ────────────────────────────────────────────────────
    // Destination
    // ────────────────────────────────────────────────────

    public async Task<List<string>> GetDestinationTablesAsync(DestinationServer server, string database)
    {
        var tables = new List<string>();
        var builder = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = database
        };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        var sql = """
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
        return tables;
    }

    public async Task<List<SchemaColumn>> GetDestinationColumnsAsync(DestinationServer server, string database, string tableName)
        => await GetSqlServerColumnsAsync(
            new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = database }.ConnectionString,
            tableName);

    // ────────────────────────────────────────────────────
    // Connection Tests
    // ────────────────────────────────────────────────────

    public async Task<bool> TestSourceConnectionAsync(SourceServer server)
    {
        try
        {
            switch (server.SourceType)
            {
                case SourceType.SqlServer:
                    await using (var conn = new SqlConnection(server.ConnectionString))
                        await conn.OpenAsync();
                    return true;

                case SourceType.Odbc:
                    using (var conn = new OdbcConnection(server.ConnectionString))
                        conn.Open();
                    return true;

                case SourceType.RestApi:
                    var client = _httpFactory.CreateClient();
                    if (!string.IsNullOrEmpty(server.AuthHeader))
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", server.AuthHeader);
                    var resp = await client.GetAsync(server.BaseUrl!.TrimEnd('/') + "/meta/ping");
                    return resp.IsSuccessStatusCode;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Source connection test failed for {Name}", server.Name);
            return false;
        }
    }

    public async Task<bool> TestDestinationConnectionAsync(DestinationServer server)
    {
        try
        {
            await using var conn = new SqlConnection(server.ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Destination connection test failed for {Name}", server.Name);
            return false;
        }
    }

    public async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var databases = new List<string>();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) databases.Add(rdr.GetString(0));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetDatabasesAsync failed");
        }
        return databases;
    }

    public async Task<(bool ok, string message)> TestConnectionWithDatabaseAsync(string connectionString, string database)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
            await using var conn = new SqlConnection(b.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT TOP 1 1 FROM INFORMATION_SCHEMA.TABLES", conn);
            await cmd.ExecuteScalarAsync();
            return (true, "Connection successful — read access confirmed.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TestConnectionWithDatabaseAsync failed for database {Database}", database);
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task<List<SchemaColumn>> GetColumnsFromQueryAsync(SourceServer server, string query)
    {
        return server.SourceType switch
        {
            SourceType.SqlServer => await GetSqlServerColumnsFromQueryAsync(server.ConnectionString!, query),
            SourceType.Odbc => await GetOdbcColumnsFromQueryAsync(server.ConnectionString!, query),
            _ => new List<SchemaColumn>()
        };
    }

    private async Task<List<SchemaColumn>> GetSqlServerColumnsFromQueryAsync(string cs, string query)
    {
        var cols = new List<SchemaColumn>();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();

        // sp_describe_first_result_set is available on SQL Server 2012+ and is the
        // safest way to get result-set metadata without executing the query.
        var cmd = new SqlCommand("sys.sp_describe_first_result_set", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@tsql", query);

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            cols.Add(new SchemaColumn
            {
                Name = rdr["name"].ToString()!,
                DataType = rdr["system_type_name"].ToString()!
                                .Split('(')[0],   // strip e.g. "nvarchar(50)" → "nvarchar"
                MaxLength = rdr["max_length"] is DBNull ? -1
                                : Convert.ToInt32(rdr["max_length"]),
                IsNullable = rdr["is_nullable"] is not DBNull
                                && Convert.ToBoolean(rdr["is_nullable"])
            });
        }
        return cols;
    }

    private Task<List<SchemaColumn>> GetOdbcColumnsFromQueryAsync(string cs, string query)
    {
        var cols = new List<SchemaColumn>();

        using var conn = new OdbcConnection(cs);
        conn.Open();

        // Wrap in TOP 0 to get schema with zero rows — ServiceNow ODBC supports this
        // and doesn't support CommandBehavior.SchemaOnly or subquery aliasing
        var schemaQuery = $"SELECT TOP 0 * FROM ({query}) _dsm_schema";

        using var cmd = new OdbcCommand(schemaQuery, conn);
        using var rdr = cmd.ExecuteReader();
        var schemaTable = rdr.GetSchemaTable();

        if (schemaTable is null) return Task.FromResult(cols);

        foreach (DataRow row in schemaTable.Rows)
        {
            cols.Add(new SchemaColumn
            {
                Name = row["ColumnName"].ToString()!,
                DataType = ((Type)row["DataType"]).Name.ToLower(),
                MaxLength = row["ColumnSize"] is DBNull ? -1 : Convert.ToInt32(row["ColumnSize"]),
                IsNullable = row["AllowDBNull"] is not DBNull && Convert.ToBoolean(row["AllowDBNull"])
            });
        }
        return Task.FromResult(cols);
    }
}
