﻿using System;
using DataLinq.Metadata;

namespace DataLinq.SQLite;

/// <summary>
/// Represents a data writer for SQLite database.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SQLiteDataLinqDataWriter"/> class.
/// </remarks>
public class SQLiteDataLinqDataWriter(SqlFromSQLiteFactory sqlFromSQLiteFactory) : IDataLinqDataWriter
{
    protected SqlFromSQLiteFactory sqlFromSQLiteFactory { get; } = sqlFromSQLiteFactory;


    /// <summary>
    /// Converts the specified value to the appropriate type for the specified column.
    /// </summary>
    /// <param name="column">The column metadata.</param>
    /// <param name="value">The value to convert.</param>
    /// <returns>The converted value.</returns>
    public object? ConvertValue(ColumnDefinition column, object? value)
    {
        if (value == null)
            return null;

        if (value is Guid guid)
        {
            var dbType = sqlFromSQLiteFactory.GetDbType(column);

            if (dbType.Name == "binary" && dbType.Length == 16)
                return guid.ToByteArray();
        }

        return value;
    }
}
