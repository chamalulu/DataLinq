using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using DataLinq.Extensions.Helpers;
using DataLinq.Interfaces;
using DataLinq.Logging;
using DataLinq.Metadata;
using DataLinq.Mutation;
using DataLinq.Query;
using Microsoft.Data.Sqlite;

namespace DataLinq.SQLite;

public enum SQLiteJournalMode
{
    OFF = 0,
    DELETE = 1,
    TRUNCATE = 2,
    PERSIST = 3,
    MEMORY = 4,
    WAL = 5
}

public class SQLiteProvider : IDatabaseProviderRegister
{
    public static bool HasBeenRegistered { get; private set; }

    //[ModuleInitializer]
    public static void RegisterProvider()
    {
        if (HasBeenRegistered)
            return;

        PluginHook.DatabaseProviders[DatabaseType.SQLite] = new SQLiteDatabaseCreator();
        PluginHook.SqlFromMetadataFactories[DatabaseType.SQLite] = new SqlFromSQLiteFactory();
        PluginHook.MetadataFromSqlFactories[DatabaseType.SQLite] = new MetadataFromSQLiteFactoryCreator();

        HasBeenRegistered = true;
    }
}

public class SQLiteProviderConstants : IDatabaseProviderConstants
{
    public string ParameterSign { get; } = "@";
    public string LastInsertCommand { get; } = "last_insert_rowid()";
    public string EscapeCharacter { get; } = "\"";
    public bool SupportsMultipleDatabases { get; } = false;
}

public class SQLiteProvider<T> : DatabaseProvider<T>, IDisposable
    where T : class, IDatabaseModel
{
    private SqliteConnectionStringBuilder connectionStringBuilder;
    private SQLiteDataLinqDataWriter dataWriter = new(new SqlFromSQLiteFactory());
    private SQLiteDbAccess dbAccess;
    public override IDatabaseProviderConstants Constants { get; } = new SQLiteProviderConstants();
    public override DatabaseAccess DatabaseAccess => dbAccess;

    static SQLiteProvider()
    {
        SQLiteProvider.RegisterProvider();
    }

    public SQLiteProvider(string connectionString) : base(connectionString, DatabaseType.SQLite, DataLinqLoggingConfiguration.NullConfiguration)
    {
        connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);
        DatabaseName = Path.GetFileNameWithoutExtension(connectionStringBuilder.DataSource);
        dbAccess = new SQLiteDbAccess(connectionString);
        SetJournalMode(SQLiteJournalMode.WAL);

    }

    //public SQLiteProvider(string connectionString, string databaseName) : base(connectionString, DatabaseType.SQLite, DataLinqLoggingConfiguration.NullConfiguration, databaseName)
    //{
    //    connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);
    //    dbAccess = new SQLiteDbAccess(connectionString);
    //    SetJournalMode(SQLiteJournalMode.WAL);
    //}

    //public override void CreateDatabase(string databaseName = null)
    //{
    //    if (databaseName == null && DatabaseName == null)
    //        throw new ArgumentNullException("DatabaseName not defined");

    //    using var transaction = GetNewDatabaseTransaction(TransactionType.ReadAndWrite);

    //    var query = $"CREATE DATABASE IF NOT EXISTS `{databaseName ?? DatabaseName}`;\n" +
    //        $"USE `{databaseName ?? DatabaseName}`;\n" +
    //        GetCreateSql();

    //    transaction.ExecuteNonQuery(query);
    //}

    public void SetJournalMode(SQLiteJournalMode journalMode)
    {
        switch (journalMode)
        {
            case SQLiteJournalMode.OFF:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = OFF");
                break;
            case SQLiteJournalMode.DELETE:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = DELETE");
                break;
            case SQLiteJournalMode.TRUNCATE:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = TRUNCATE");
                break;
            case SQLiteJournalMode.PERSIST:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = PERSIST");
                break;
            case SQLiteJournalMode.MEMORY:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = MEMORY");
                break;
            case SQLiteJournalMode.WAL:
                dbAccess.ExecuteNonQuery("PRAGMA journal_mode = WAL");
                break;
        }
    }

    public override DatabaseTransaction GetNewDatabaseTransaction(TransactionType type)
    {
        //if (type == TransactionType.ReadOnly)
        //    return new SQLiteDbAccess(ConnectionString, type);
        //else
        return new SQLiteDatabaseTransaction(ConnectionString, type);
    }

    public override DatabaseTransaction AttachDatabaseTransaction(IDbTransaction dbTransaction, TransactionType type)
    {
        return new SQLiteDatabaseTransaction(dbTransaction, type);
    }

    public override string GetLastIdQuery() => "SELECT last_insert_rowid()";

    public override string GetSqlForFunction(SqlFunctionType functionType, string quotedColumnName)
    {
        return functionType switch
        {
            // Date Parts
            SqlFunctionType.DatePartYear => $"CAST(strftime('%Y', {quotedColumnName}) AS INTEGER)",
            SqlFunctionType.DatePartMonth => $"CAST(strftime('%m', {quotedColumnName}) AS INTEGER)",
            SqlFunctionType.DatePartDay => $"CAST(strftime('%d', {quotedColumnName}) AS INTEGER)",
            SqlFunctionType.DatePartDayOfYear => $"CAST(strftime('%j', {quotedColumnName}) AS INTEGER)",
            // strftime('%w') in SQLite returns 0 for Sunday, which directly aligns with C#'s DayOfWeek enum.
            SqlFunctionType.DatePartDayOfWeek => $"CAST(strftime('%w', {quotedColumnName}) AS INTEGER)",

            // Time Parts
            SqlFunctionType.TimePartHour => $"CAST(strftime('%H', {quotedColumnName}) AS INTEGER)",
            SqlFunctionType.TimePartMinute => $"CAST(strftime('%M', {quotedColumnName}) AS INTEGER)",
            SqlFunctionType.TimePartSecond => $"CAST(strftime('%S', {quotedColumnName}) AS INTEGER)",
            // strftime('%f') returns seconds with fractional part. Multiply by 1000 and take integer part.
            SqlFunctionType.TimePartMillisecond => $"CAST((strftime('%f', {quotedColumnName}) * 1000) % 1000 AS INTEGER)",

            // String Parts
            SqlFunctionType.StringLength => $"LENGTH({quotedColumnName})",

            _ => throw new NotImplementedException($"SQL function '{functionType}' not implemented for SQLite."),
        };
    }

    public override Sql GetParameterValue(Sql sql, string key)
    {
        return sql.AddFormat("@{0}", key);
    }

    public override Sql GetParameterComparison(Sql sql, string field, Query.Relation relation, string[] key)
    {
        return sql.AddFormat("{0} {1} {2}", field, relation.ToSql(), GetParameterName(relation, key));
    }

    private string GetParameterName(Query.Relation relation, string[] key)
    {
        var builder = new StringBuilder();
        if (key.Length > 1 || relation == Query.Relation.In || relation == Query.Relation.NotIn)
        {
            builder.Append('(');
        }

        for (int i = 0; i < key.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append('@');
            builder.Append(key[i]);
        }

        if (key.Length > 1 || relation == Query.Relation.In || relation == Query.Relation.NotIn)
        {
            builder.Append(')');
        }

        return builder.ToString();
    }

    public override Sql GetParameter(Sql sql, string key, object? value)
    {
        return sql.AddParameters(new SqliteParameter("@" + key, value ?? DBNull.Value));
    }

    public override Sql GetLimitOffset(Sql sql, int? limit, int? offset)
    {
        if (!limit.HasValue && !offset.HasValue)
            return sql;

        if (limit.HasValue && !offset.HasValue)
            sql.AddText($"\nLIMIT {limit}");
        else if (!limit.HasValue && offset.HasValue)
            sql.AddText($"\nLIMIT -1 OFFSET {offset}");
        else
            sql.AddText($"\nLIMIT {limit} OFFSET {offset}");

        return sql;
    }

    public override Sql GetTableName(Sql sql, string tableName, string? alias = null)
    {
        sql.AddText(string.IsNullOrEmpty(alias)
        ? $"{Constants.EscapeCharacter}{tableName}{Constants.EscapeCharacter}"
        : $"{Constants.EscapeCharacter}{tableName}{Constants.EscapeCharacter} {alias}");

        return sql;
    }

    public override Sql GetCreateSql() => new SqlFromSQLiteFactory().GetCreateTables(Metadata, true);

    public override IDbCommand ToDbCommand(IQuery query)
    {
        var sql = query.ToSql();
        var command = new SqliteCommand(sql.Text);
        command.Parameters.AddRange(sql.Parameters.ToArray());

        return command;
    }

    public override bool DatabaseExists(string? databaseName = null)
    {
        return FileOrServerExists();
    }

    public override bool TableExists(string tableName, string? databaseName = null)
    {
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentNullException(nameof(tableName));

        if (!FileOrServerExists())
            throw new InvalidOperationException("Database file or server does not exist.");

        var literal = new Literal(ReadOnlyAccess, "SELECT name FROM sqlite_master WHERE type='table' AND name = @tableName", new SqliteParameter("@tableName", tableName));

        return DatabaseAccess
            .ReadReader(literal.ToDbCommand())
            .Any();
    }

    public override bool FileOrServerExists()
    {
        var source = connectionStringBuilder.DataSource;

        if (source == "memory")
            return true;

        return File.Exists(source);
    }

    public override IDataLinqDataWriter GetWriter()
    {
        return dataWriter;
    }

    public override IDbConnection GetDbConnection()
    {
        return new SqliteConnection(connectionStringBuilder.ConnectionString);
    }
}