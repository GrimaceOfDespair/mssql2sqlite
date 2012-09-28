using System;
using System.Collections.Generic;
using System.Text;
using System.Data;
using System.Data.SqlClient;
using System.Data.SQLite;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using log4net;

namespace DbAccess
{
    /// <summary>
    /// This class is resposible to take a single SQL Server database
    /// and convert it to an SQLite database file.
    /// </summary>
    /// <remarks>The class knows how to convert table and index structures only.</remarks>
    public class SqlServerToSQLite
    {
        #region Public Properties
        /// <summary>
        /// Gets a value indicating whether this instance is active.
        /// </summary>
        /// <value><c>true</c> if this instance is active; otherwise, <c>false</c>.</value>
        public static bool IsActive
        {
            get { return _isActive; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Cancels the conversion.
        /// </summary>
        public static void CancelConversion()
        {
            _cancelled = true;
        }

        /// <summary>
        /// This method takes as input the connection string to an SQL Server database
        /// and creates a corresponding SQLite database file with a schema derived from
        /// the SQL Server database.
        /// </summary>
        /// <param name="sqlServerConnString">The connection string to the SQL Server database.</param>
        /// <param name="sqlitePath">The path to the SQLite database file that needs to get created.</param>
        /// <param name="password">The password to use or NULL if no password should be used to encrypt the DB</param>
        /// <param name="handler">A handler delegate for progress notifications.</param>
        /// <param name="selectionHandler">The selection handler that allows the user to select which
        /// tables to convert</param>
        /// <remarks>The method continues asynchronously in the background and the caller returned
        /// immediatly.</remarks>
        public static void ConvertSqlServerToSQLiteDatabase(string sqlServerConnString,
            string sqlitePath, string password, SqlConversionHandler handler,
            SqlTableSelectionHandler selectionHandler,
            FailedViewDefinitionHandler viewFailureHandler,
            bool createTriggers, bool useThread = true)
        {
            // Clear cancelled flag
            _cancelled = false;

          WaitCallback convert = delegate(object state)
                                        {
                                          try
                                          {
                                            _isActive = true;
                                            ConvertSqlServerDatabaseToSQLiteFile(sqlServerConnString, sqlitePath, password, handler, selectionHandler, viewFailureHandler, createTriggers);
                                            _isActive = false;
                                            handler(true, true, 100, "Finished converting database");
                                          }
                                          catch (Exception ex)
                                          {
                                            _log.Error("Failed to convert SQL Server database to SQLite database", ex);
                                            _isActive = false;
                                            handler(true, false, 100, ex.Message);
                                          } // catch
                                        };

          if (useThread)
          {
            ThreadPool.QueueUserWorkItem(convert);
          }
          else
          {
            convert(new{});
          }
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Do the entire process of first reading the SQL Server schema, creating a corresponding
        /// SQLite schema, and copying all rows from the SQL Server database to the SQLite database.
        /// </summary>
        /// <param name="sqlConnString">The SQL Server connection string</param>
        /// <param name="sqlitePath">The path to the generated SQLite database file</param>
        /// <param name="password">The password to use or NULL if no password should be used to encrypt the DB</param>
        /// <param name="handler">A handler to handle progress notifications.</param>
        /// <param name="selectionHandler">The selection handler which allows the user to select which tables to 
        /// convert.</param>
        private static void ConvertSqlServerDatabaseToSQLiteFile(
            string sqlConnString, string sqlitePath, string password, SqlConversionHandler handler,
            SqlTableSelectionHandler selectionHandler,
            FailedViewDefinitionHandler viewFailureHandler,
            bool createTriggers)
        {
            // Delete the target file if it exists already.
            if (File.Exists(sqlitePath))
                File.Delete(sqlitePath);

            // Read the schema of the SQL Server database into a memory structure
            DatabaseSchema ds = ReadSqlServerSchema(sqlConnString, handler, selectionHandler);

            // Create the SQLite database and apply the schema
            CreateSQLiteDatabase(sqlitePath, ds, password, handler, viewFailureHandler);

            // Copy all rows from SQL Server tables to the newly created SQLite database
            CopySqlServerRowsToSQLiteDB(sqlConnString, sqlitePath, ds.Tables, password, handler);

            // Add triggers based on foreign key constraints
            if (createTriggers)
                AddTriggersForForeignKeys(sqlitePath, ds.Tables, password, handler);

        }

        /// <summary>
        /// Copies table rows from the SQL Server database to the SQLite database.
        /// </summary>
        /// <param name="sqlConnString">The SQL Server connection string</param>
        /// <param name="sqlitePath">The path to the SQLite database file.</param>
        /// <param name="schema">The schema of the SQL Server database.</param>
        /// <param name="password">The password to use for encrypting the file</param>
        /// <param name="handler">A handler to handle progress notifications.</param>
        private static void CopySqlServerRowsToSQLiteDB(
            string sqlConnString, string sqlitePath, List<TableSchema> schema,
            string password, SqlConversionHandler handler)
        {
            CheckCancelled();
            handler(false, true, 0, "Preparing to insert tables...");
            _log.Debug("preparing to insert tables ...");

            // Connect to the SQL Server database
            using (SqlConnection ssconn = new SqlConnection(sqlConnString))
            {
                ssconn.Open();

                // Connect to the SQLite database next
                string sqliteConnString = CreateSQLiteConnectionString(sqlitePath, password);
                using (SQLiteConnection sqconn = new SQLiteConnection(sqliteConnString))
                {
                    sqconn.Open();

                    // Go over all tables in the schema and copy their rows
                    for (int i = 0; i < schema.Count; i++)
                    {
                        SQLiteTransaction tx = sqconn.BeginTransaction();
                        try
                        {
                            string tableQuery = BuildSqlServerTableQuery(schema[i]);
                            SqlCommand query = new SqlCommand(tableQuery, ssconn);
                            using (SqlDataReader reader = query.ExecuteReader())
                            {
                                SQLiteCommand insert = BuildSQLiteInsert(schema[i]);
                                int counter = 0;
                                while (reader.Read())
                                {
                                    insert.Connection = sqconn;
                                    insert.Transaction = tx;
                                    List<string> pnames = new List<string>();
                                    for (int j = 0; j < schema[i].Columns.Count; j++)
                                    {
                                        string pname = "@" + GetNormalizedName(schema[i].Columns[j].ColumnName, pnames);
                                        insert.Parameters[pname].Value = CastValueForColumn(reader[j], schema[i].Columns[j]);
                                        pnames.Add(pname);
                                    }
                                    insert.ExecuteNonQuery();
                                    counter++;
                                    if (counter % 1000 == 0)
                                    {
                                        CheckCancelled();
                                        tx.Commit();
                                        handler(false, true, (int)(100.0 * i / schema.Count),
                                            "Added " + counter + " rows to table " + schema[i].TableName + " so far");
                                        tx = sqconn.BeginTransaction();
                                    }
                                } // while
                            } // using

                            CheckCancelled();
                            tx.Commit();

                            handler(false, true, (int)(100.0 * i / schema.Count), "Finished inserting rows for table " + schema[i].TableName);
                            _log.Debug("finished inserting all rows for table [" + schema[i].TableName + "]");
                        }
                        catch (Exception ex)
                        {
                            _log.Error("unexpected exception", ex);
                            tx.Rollback();
                            throw;
                        } // catch
                    }
                } // using
            } // using
        }

        /// <summary>
        /// Used in order to adjust the value received from SQL Servr for the SQLite database.
        /// </summary>
        /// <param name="val">The value object</param>
        /// <param name="columnSchema">The corresponding column schema</param>
        /// <returns>SQLite adjusted value.</returns>
        private static object CastValueForColumn(object val, ColumnSchema columnSchema)
        {
            if (val is DBNull)
                return null;

            DbType dt = GetDbTypeOfColumn(columnSchema);

            switch (dt)
            {
                case DbType.Int32:
                    if (val is short)
                        return (int)(short)val;
                    if (val is byte)
                        return (int)(byte)val;
                    if (val is long)
                        return (int)(long)val;
                    if (val is decimal)
                        return (int)(decimal)val;
                    break;

                case DbType.Int16:
                    if (val is int)
                        return (short)(int)val;
                    if (val is byte)
                        return (short)(byte)val;
                    if (val is long)
                        return (short)(long)val;
                    if (val is decimal)
                        return (short)(decimal)val;
                    break;

                case DbType.Int64:
                    if (val is int)
                        return (long)(int)val;
                    if (val is short)
                        return (long)(short)val;
                    if (val is byte)
                        return (long)(byte)val;
                    if (val is decimal)
                        return (long)(decimal)val;
                    break;

                case DbType.Single:
                    if (val is double)
                        return (float)(double)val;
                    if (val is decimal)
                        return (float)(decimal)val;
                    break;

                case DbType.Double:
                    if (val is float)
                        return (double)(float)val;
                    if (val is double)
                        return (double)val;
                    if (val is decimal)
                        return (double)(decimal)val;
                    break;

                case DbType.String:
                    if (val is Guid)
                        return ((Guid)val).ToString();
                    break;

                case DbType.Guid:
                    if (val is string)
                        return ParseStringAsGuid((string)val);
                    if (val is byte[])
                        return ParseBlobAsGuid((byte[])val);
                    break;

                case DbType.Binary:
                case DbType.Boolean:
                case DbType.DateTime:
                    break;

                default:
                    _log.Error("argument exception - illegal database type");
                    throw new ArgumentException("Illegal database type [" + Enum.GetName(typeof(DbType), dt) + "]");
            } // switch

            return val;
        }

        private static Guid ParseBlobAsGuid(byte[] blob)
        {
            byte[] data = blob;
            if (blob.Length > 16)
            {
                data = new byte[16];
                for (int i = 0; i < 16; i++)
                    data[i] = blob[i];
            }
            else if (blob.Length < 16)
            {
                data = new byte[16];
                for (int i = 0; i < blob.Length; i++)
                    data[i] = blob[i];
            }

            return new Guid(data);
        }

        private static Guid ParseStringAsGuid(string str)
        {
            try
            {
                return new Guid(str);
            }
            catch (Exception ex)
            {
                return Guid.Empty;
            } // catch
        }

        /// <summary>
        /// Creates a command object needed to insert values into a specific SQLite table.
        /// </summary>
        /// <param name="ts">The table schema object for the table.</param>
        /// <returns>A command object with the required functionality.</returns>
        private static SQLiteCommand BuildSQLiteInsert(TableSchema ts)
        {
            SQLiteCommand res = new SQLiteCommand();

            StringBuilder sb = new StringBuilder();
            sb.Append("INSERT INTO [" + ts.TableName + "] (");
            for (int i = 0; i < ts.Columns.Count; i++)
            {
                sb.Append("[" + ts.Columns[i].ColumnName + "]");
                if (i < ts.Columns.Count - 1)
                    sb.Append(", ");
            } // for
            sb.Append(") VALUES (");

            List<string> pnames = new List<string>();
            for (int i = 0; i < ts.Columns.Count; i++)
            {
                string pname = "@" + GetNormalizedName(ts.Columns[i].ColumnName, pnames);
                sb.Append(pname);
                if (i < ts.Columns.Count - 1)
                    sb.Append(", ");

                DbType dbType = GetDbTypeOfColumn(ts.Columns[i]);
                SQLiteParameter prm = new SQLiteParameter(pname, dbType, ts.Columns[i].ColumnName);
                res.Parameters.Add(prm);

                // Remember the parameter name in order to avoid duplicates
                pnames.Add(pname);
            } // for
            sb.Append(")");
            res.CommandText = sb.ToString();
            res.CommandType = CommandType.Text;
            return res;
        }

        /// <summary>
        /// Used in order to avoid breaking naming rules (e.g., when a table has
        /// a name in SQL Server that cannot be used as a basis for a matching index
        /// name in SQLite).
        /// </summary>
        /// <param name="str">The name to change if necessary</param>
        /// <param name="names">Used to avoid duplicate names</param>
        /// <returns>A normalized name</returns>
        private static string GetNormalizedName(string str, List<string> names)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < str.Length; i++)
            {
                if (Char.IsLetterOrDigit(str[i]) || str[i] == '_')
                    sb.Append(str[i]);
                else
                    sb.Append("_");
            } // for

            // Avoid returning duplicate name
            if (names.Contains(sb.ToString()))
                return GetNormalizedName(sb.ToString() + "_", names);
            else
                return sb.ToString();
        }

        /// <summary>
        /// Matches SQL Server types to general DB types
        /// </summary>
        /// <param name="cs">The column schema to use for the match</param>
        /// <returns>The matched DB type</returns>
        private static DbType GetDbTypeOfColumn(ColumnSchema cs)
        {
            if (cs.ColumnType == "tinyint")
                return DbType.Byte;
            if (cs.ColumnType == "int")
                return DbType.Int32;
            if (cs.ColumnType == "smallint")
                return DbType.Int16;
            if (cs.ColumnType == "bigint")
                return DbType.Int64;
            if (cs.ColumnType == "bit")
                return DbType.Boolean;
            if (cs.ColumnType == "nvarchar" || cs.ColumnType == "varchar" ||
                cs.ColumnType == "text" || cs.ColumnType == "ntext")
                return DbType.String;
            if (cs.ColumnType == "float")
                return DbType.Double;
            if (cs.ColumnType == "real")
                return DbType.Single;
            if (cs.ColumnType == "blob")
                return DbType.Binary;
            if (cs.ColumnType == "numeric")
                return DbType.Double;
            if (cs.ColumnType == "timestamp" || cs.ColumnType == "datetime")
                return DbType.DateTime;
            if (cs.ColumnType == "nchar" || cs.ColumnType == "char")
                return DbType.String;
            if (cs.ColumnType == "uniqueidentifier" || cs.ColumnType == "guid")
                return DbType.Guid;
            if (cs.ColumnType == "xml")
                return DbType.String;
            if (cs.ColumnType == "sql_variant")
                return DbType.Object;
            if (cs.ColumnType == "integer")
                return DbType.Int64;

            _log.Error("illegal db type found");
            throw new ApplicationException("Illegal DB type found (" + cs.ColumnType + ")");
        }

        /// <summary>
        /// Builds a SELECT query for a specific table. Needed in the process of copying rows
        /// from the SQL Server database to the SQLite database.
        /// </summary>
        /// <param name="ts">The table schema of the table for which we need the query.</param>
        /// <returns>The SELECT query for the table.</returns>
        private static string BuildSqlServerTableQuery(TableSchema ts)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SELECT ");
            for (int i = 0; i < ts.Columns.Count; i++)
            {
                sb.Append("[" + ts.Columns[i].ColumnName + "]");
                if (i < ts.Columns.Count - 1)
                    sb.Append(", ");
            } // for
            sb.Append(" FROM " + ts.TableSchemaName + "." + "[" + ts.TableName + "]");
            return sb.ToString();
        }

        /// <summary>
        /// Creates the SQLite database from the schema read from the SQL Server.
        /// </summary>
        /// <param name="sqlitePath">The path to the generated DB file.</param>
        /// <param name="schema">The schema of the SQL server database.</param>
        /// <param name="password">The password to use for encrypting the DB or null if non is needed.</param>
        /// <param name="handler">A handle for progress notifications.</param>
        private static void CreateSQLiteDatabase(string sqlitePath, DatabaseSchema schema, string password,
            SqlConversionHandler handler,
            FailedViewDefinitionHandler viewFailureHandler)
        {
            _log.Debug("Creating SQLite database...");

            // Create the SQLite database file
            SQLiteConnection.CreateFile(sqlitePath);

            _log.Debug("SQLite file was created successfully at [" + sqlitePath + "]");

            // Connect to the newly created database
            string sqliteConnString = CreateSQLiteConnectionString(sqlitePath, password);
            using (SQLiteConnection conn = new SQLiteConnection(sqliteConnString))
            {
                conn.Open();

                // Create all tables in the new database
                int count = 0;
                foreach (TableSchema dt in schema.Tables)
                {
                    try
                    {
                        AddSQLiteTable(conn, dt);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("AddSQLiteTable failed", ex);
                        throw;
                    }
                    count++;
                    CheckCancelled();
                    handler(false, true, (int)(count * 50.0 / schema.Tables.Count), "Added table " + dt.TableName + " to the SQLite database");

                    _log.Debug("added schema for SQLite table [" + dt.TableName + "]");
                } // foreach

                // Create all views in the new database
                count = 0;
                foreach (ViewSchema vs in schema.Views)
                {
                    try
                    {
                        AddSQLiteView(conn, vs, viewFailureHandler);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("AddSQLiteView failed", ex);
                        throw;
                    } // catch
                    count++;
                    CheckCancelled();
                    handler(false, true, 50 + (int)(count * 50.0 / schema.Views.Count), "Added view " + vs.ViewName + " to the SQLite database");

                    _log.Debug("added schema for SQLite view [" + vs.ViewName + "]");

                } // foreach
            } // using

            _log.Debug("finished adding all table/view schemas for SQLite database");
        }

        private static void AddSQLiteView(SQLiteConnection conn, ViewSchema vs, FailedViewDefinitionHandler handler)
        {
            // Prepare a CREATE VIEW DDL statement
            string stmt = vs.ViewSQL;
            _log.Info("\n\n" + stmt + "\n\n");

            // Execute the query in order to actually create the view.
            SQLiteTransaction tx = conn.BeginTransaction();
            try
            {
                SQLiteCommand cmd = new SQLiteCommand(stmt, conn, tx);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch (SQLiteException ex)
            {
                tx.Rollback();

                if (handler != null)
                {
                    ViewSchema updated = new ViewSchema();
                    updated.ViewName = vs.ViewName;
                    updated.ViewSQL = vs.ViewSQL;

                    // Ask the user to supply the new view definition SQL statement
                    string sql = handler(updated);

                    if (sql == null)
                        return; // Discard the view
                    else
                    {
                        // Try to re-create the view with the user-supplied view definition SQL
                        updated.ViewSQL = sql;
                        AddSQLiteView(conn, updated, handler);
                    }
                }
                else
                    throw;
            } // catch
        }

        /// <summary>
        /// Creates the CREATE TABLE DDL for SQLite and a specific table.
        /// </summary>
        /// <param name="conn">The SQLite connection</param>
        /// <param name="dt">The table schema object for the table to be generated.</param>
        private static void AddSQLiteTable(SQLiteConnection conn, TableSchema dt)
        {
            // Prepare a CREATE TABLE DDL statement
            string stmt = BuildCreateTableQuery(dt);

            _log.Info("\n\n" + stmt + "\n\n");

            // Execute the query in order to actually create the table.
            SQLiteCommand cmd = new SQLiteCommand(stmt, conn);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// returns the CREATE TABLE DDL for creating the SQLite table from the specified
        /// table schema object.
        /// </summary>
        /// <param name="ts">The table schema object from which to create the SQL statement.</param>
        /// <returns>CREATE TABLE DDL for the specified table.</returns>
        private static string BuildCreateTableQuery(TableSchema ts)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("CREATE TABLE [" + ts.TableName + "] (\n");

            bool pkey = false;
            for (int i = 0; i < ts.Columns.Count; i++)
            {
                ColumnSchema col = ts.Columns[i];
                string cline = BuildColumnStatement(col, ts, ref pkey);
                sb.Append(cline);
                if (i < ts.Columns.Count - 1)
                    sb.Append(",\n");
            } // foreach

            // add primary keys...
            if (ts.PrimaryKey != null && ts.PrimaryKey.Count > 0 & !pkey)
            {
                sb.Append(",\n");
                sb.Append("    PRIMARY KEY (");
                for (int i = 0; i < ts.PrimaryKey.Count; i++)
                {
                    sb.Append("[" + ts.PrimaryKey[i] + "]");
                    if (i < ts.PrimaryKey.Count - 1)
                        sb.Append(", ");
                } // for
                sb.Append(")\n");
            }
            else
                sb.Append("\n");

            // add foreign keys...
            if (ts.ForeignKeys.Count > 0)
            {
                sb.Append(",\n");
                for (int i = 0; i < ts.ForeignKeys.Count; i++)
                {
                    ForeignKeySchema foreignKey = ts.ForeignKeys[i];
                    string stmt = string.Format("    FOREIGN KEY ([{0}])\n        REFERENCES [{1}]([{2}])",
                                foreignKey.ColumnName, foreignKey.ForeignTableName, foreignKey.ForeignColumnName);

                    sb.Append(stmt);
                    if (i < ts.ForeignKeys.Count - 1)
                        sb.Append(",\n");
                } // for
            }

            sb.Append("\n");
            sb.Append(");\n");

            // Create any relevant indexes
            if (ts.Indexes != null)
            {
                for (int i = 0; i < ts.Indexes.Count; i++)
                {
                    string stmt = BuildCreateIndex(ts.TableName, ts.Indexes[i]);
                    sb.Append(stmt + ";\n");
                } // for
            } // if

            string query = sb.ToString();
            return query;
        }

        /// <summary>
        /// Creates a CREATE INDEX DDL for the specified table and index schema.
        /// </summary>
        /// <param name="tableName">The name of the indexed table.</param>
        /// <param name="indexSchema">The schema of the index object</param>
        /// <returns>A CREATE INDEX DDL (SQLite format).</returns>
        private static string BuildCreateIndex(string tableName, IndexSchema indexSchema)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("CREATE ");
            if (indexSchema.IsUnique)
                sb.Append("UNIQUE ");
            sb.Append("INDEX [" + tableName + "_" + indexSchema.IndexName + "]\n");
            sb.Append("ON [" + tableName + "]\n");
            sb.Append("(");
            for (int i = 0; i < indexSchema.Columns.Count; i++)
            {
                sb.Append("[" + indexSchema.Columns[i].ColumnName + "]");
                if (!indexSchema.Columns[i].IsAscending)
                    sb.Append(" DESC");
                if (i < indexSchema.Columns.Count - 1)
                    sb.Append(", ");
            } // for
            sb.Append(")");

            return sb.ToString();
        }

        /// <summary>
        /// Used when creating the CREATE TABLE DDL. Creates a single row
        /// for the specified column.
        /// </summary>
        /// <param name="col">The column schema</param>
        /// <returns>A single column line to be inserted into the general CREATE TABLE DDL statement</returns>
        private static string BuildColumnStatement(ColumnSchema col, TableSchema ts, ref bool pkey)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\t\"" + col.ColumnName + "\"\t\t");

            // Special treatment for IDENTITY columns
            if (col.IsIdentity)
            {
                if (ts.PrimaryKey.Count == 1 && (col.ColumnType == "tinyint" || col.ColumnType == "int" || col.ColumnType == "smallint" ||
                    col.ColumnType == "bigint" || col.ColumnType == "integer"))
                {
                    sb.Append("integer PRIMARY KEY AUTOINCREMENT");
                    pkey = true;
                }
                else
                    sb.Append("integer");
            }
            else
            {
                if (col.ColumnType == "int")
                    sb.Append("integer");
                else
                {
                    sb.Append(col.ColumnType);
                }
                if (col.Length > 0)
                    sb.Append("(" + col.Length + ")");
            }
            if (!col.IsNullable)
                sb.Append(" NOT NULL");

            if (col.IsCaseSensitivite.HasValue && !col.IsCaseSensitivite.Value)
                sb.Append(" COLLATE NOCASE");

            string defval = StripParens(col.DefaultValue);
            defval = DiscardNational(defval);
            _log.Debug("DEFAULT VALUE BEFORE [" + col.DefaultValue + "] AFTER [" + defval + "]");
            if (defval != string.Empty && defval.ToUpper().Contains("GETDATE"))
            {
                _log.Debug("converted SQL Server GETDATE() to CURRENT_TIMESTAMP for column [" + col.ColumnName + "]");
                sb.Append(" DEFAULT (CURRENT_TIMESTAMP)");
            }
            else if (defval != string.Empty && IsValidDefaultValue(defval))
                sb.Append(" DEFAULT " + defval);

            return sb.ToString();
        }

        /// <summary>
        /// Discards the national prefix if exists (e.g., N'sometext') which is not
        /// supported in SQLite.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns></returns>
        private static string DiscardNational(string value)
        {
            Regex rx = new Regex(@"N\'([^\']*)\'");
            Match m = rx.Match(value);
            if (m.Success)
                return m.Groups[1].Value;
            else
                return value;
        }

        /// <summary>
        /// Check if the DEFAULT clause is valid by SQLite standards
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static bool IsValidDefaultValue(string value)
        {
            if (IsSingleQuoted(value))
                return true;

            double testnum;
            if (!double.TryParse(value, out testnum))
                return false;
            return true;
        }

        private static bool IsSingleQuoted(string value)
        {
            value = value.Trim();
            if (value.StartsWith("'") && value.EndsWith("'"))
                return true;
            return false;
        }

        /// <summary>
        /// Strip any parentheses from the string.
        /// </summary>
        /// <param name="value">The string to strip</param>
        /// <returns>The stripped string</returns>
        private static string StripParens(string value)
        {
            Regex rx = new Regex(@"\(([^\)]*)\)");
            Match m = rx.Match(value);
            if (!m.Success)
                return value;
            else
                return StripParens(m.Groups[1].Value);
        }

        /// <summary>
        /// Reads the entire SQL Server DB schema using the specified connection string.
        /// </summary>
        /// <param name="connString">The connection string used for reading SQL Server schema.</param>
        /// <param name="handler">A handler for progress notifications.</param>
        /// <param name="selectionHandler">The selection handler which allows the user to select 
        /// which tables to convert.</param>
        /// <returns>database schema objects for every table/view in the SQL Server database.</returns>
        private static DatabaseSchema ReadSqlServerSchema(string connString, SqlConversionHandler handler,
            SqlTableSelectionHandler selectionHandler)
        {
            // First step is to read the names of all tables in the database
            List<TableSchema> tables = new List<TableSchema>();
            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();

                List<string> tableNames = new List<string>();
                List<string> tblschema = new List<string>();

                // This command will read the names of all tables in the database
                SqlCommand cmd = new SqlCommand(@"select * from INFORMATION_SCHEMA.TABLES  where TABLE_TYPE = 'BASE TABLE'", conn);
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader["TABLE_NAME"] == DBNull.Value)
                            continue;
                        if (reader["TABLE_SCHEMA"] == DBNull.Value)
                            continue;
                        tableNames.Add((string)reader["TABLE_NAME"]);
                        tblschema.Add((string)reader["TABLE_SCHEMA"]);
                    } // while
                } // using

                // Next step is to use ADO APIs to query the schema of each table.
                int count = 0;
                for (int i = 0; i < tableNames.Count; i++)
                {
                    string tname = tableNames[i];
                    string tschma = tblschema[i];
                    TableSchema ts = CreateTableSchema(conn, tname, tschma);
                    CreateForeignKeySchema(conn, ts);
                    tables.Add(ts);
                    count++;
                    CheckCancelled();
                    handler(false, true, (int)(count * 50.0 / tableNames.Count), "Parsed table " + tname);

                    _log.Debug("parsed table schema for [" + tname + "]");
                } // foreach
            } // using

            _log.Debug("finished parsing all tables in SQL Server schema");

            // Allow the user a chance to select which tables to convert
            if (selectionHandler != null)
            {
                List<TableSchema> updated = selectionHandler(tables);
                if (updated != null)
                    tables = updated;
            } // if

            Regex removedbo = new Regex(@"dbo\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // Continue and read all of the views in the database
            List<ViewSchema> views = new List<ViewSchema>();
            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();

                SqlCommand cmd = new SqlCommand(@"SELECT TABLE_NAME, VIEW_DEFINITION  from INFORMATION_SCHEMA.VIEWS", conn);
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    int count = 0;
                    while (reader.Read())
                    {
                        ViewSchema vs = new ViewSchema();

                        if (reader["TABLE_NAME"] == DBNull.Value)
                            continue;
                        if (reader["VIEW_DEFINITION"] == DBNull.Value)
                            continue;
                        vs.ViewName = (string)reader["TABLE_NAME"];
                        vs.ViewSQL = (string)reader["VIEW_DEFINITION"];

                        // Remove all ".dbo" strings from the view definition
                        vs.ViewSQL = removedbo.Replace(vs.ViewSQL, string.Empty);

                        views.Add(vs);

                        count++;
                        CheckCancelled();
                        handler(false, true, 50 + (int)(count * 50.0 / views.Count), "Parsed view " + vs.ViewName);

                        _log.Debug("parsed view schema for [" + vs.ViewName + "]");
                    } // while
                } // using

            } // using

            DatabaseSchema ds = new DatabaseSchema();
            ds.Tables = tables;
            ds.Views = views;
            return ds;
        }

        /// <summary>
        /// Convenience method for checking if the conversion progress needs to be cancelled.
        /// </summary>
        private static void CheckCancelled()
        {
            if (_cancelled)
                throw new ApplicationException("User cancelled the conversion");
        }

        /// <summary>
        /// Creates a TableSchema object using the specified SQL Server connection
        /// and the name of the table for which we need to create the schema.
        /// </summary>
        /// <param name="conn">The SQL Server connection to use</param>
        /// <param name="tableName">The name of the table for which we wants to create the table schema.</param>
        /// <returns>A table schema object that represents our knowledge of the table schema</returns>
        private static TableSchema CreateTableSchema(SqlConnection conn, string tableName, string tschma)
        {
            TableSchema res = new TableSchema();
            res.TableName = tableName;
            res.TableSchemaName = tschma;
            res.Columns = new List<ColumnSchema>();
            SqlCommand cmd = new SqlCommand(@"SELECT COLUMN_NAME,COLUMN_DEFAULT,IS_NULLABLE,DATA_TYPE, " +
                @" (columnproperty(object_id(TABLE_NAME), COLUMN_NAME, 'IsIdentity')) AS [IDENT], " +
                @"CHARACTER_MAXIMUM_LENGTH AS CSIZE " +
                "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '" + tableName + "' ORDER BY " +
                "ORDINAL_POSITION ASC", conn);
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    object tmp = reader["COLUMN_NAME"];
                    if (tmp is DBNull)
                        continue;
                    string colName = (string)reader["COLUMN_NAME"];

                    tmp = reader["COLUMN_DEFAULT"];
                    string colDefault;
                    if (tmp is DBNull)
                        colDefault = string.Empty;
                    else
                        colDefault = (string)tmp;

                    tmp = reader["IS_NULLABLE"];
                    bool isNullable = ((string)tmp == "YES");
                    string dataType = (string)reader["DATA_TYPE"];
                    bool isIdentity = false;
                    if (reader["IDENT"] != DBNull.Value)
                        isIdentity = ((int)reader["IDENT"]) == 1 ? true : false;
                    int length = reader["CSIZE"] != DBNull.Value ? Convert.ToInt32(reader["CSIZE"]) : 0;

                    ValidateDataType(dataType);

                    // Note that not all data type names need to be converted because
                    // SQLite establishes type affinity by searching certain strings
                    // in the type name. For example - everything containing the string
                    // 'int' in its type name will be assigned an INTEGER affinity
                    if (dataType == "timestamp")
                        dataType = "blob";
                    else if (dataType == "datetime" || dataType == "smalldatetime")
                        dataType = "datetime";
                    else if (dataType == "decimal")
                        dataType = "numeric";
                    else if (dataType == "money" || dataType == "smallmoney")
                        dataType = "numeric";
                    else if (dataType == "binary" || dataType == "varbinary" ||
                        dataType == "image")
                        dataType = "blob";
                    else if (dataType == "tinyint")
                        dataType = "smallint";
                    else if (dataType == "bigint")
                        dataType = "integer";
                    else if (dataType == "sql_variant")
                        dataType = "blob";
                    else if (dataType == "xml")
                        dataType = "varchar";
                    else if (dataType == "uniqueidentifier")
                        dataType = "guid";
                    else if (dataType == "ntext")
                        dataType = "text";
                    else if (dataType == "nchar")
                        dataType = "char";

                    if (dataType == "bit" || dataType == "int")
                    {
                        if (colDefault == "('False')")
                            colDefault = "(0)";
                        else if (colDefault == "('True')")
                            colDefault = "(1)";
                    }

                    colDefault = FixDefaultValueString(colDefault);

                    ColumnSchema col = new ColumnSchema();
                    col.ColumnName = colName;
                    col.ColumnType = dataType;
                    col.Length = length;
                    col.IsNullable = isNullable;
                    col.IsIdentity = isIdentity;
                    col.DefaultValue = AdjustDefaultValue(colDefault);
                    res.Columns.Add(col);
                } // while
            } // using

            // Find PRIMARY KEY information
            SqlCommand cmd2 = new SqlCommand(@"EXEC sp_pkeys '" + tableName + "'", conn);
            using (SqlDataReader reader = cmd2.ExecuteReader())
            {
                res.PrimaryKey = new List<string>();
                while (reader.Read())
                {
                    string colName = (string)reader["COLUMN_NAME"];
                    res.PrimaryKey.Add(colName);
                } // while
            } // using

            // Find COLLATE information for all columns in the table
            SqlCommand cmd4 = new SqlCommand(
                @"EXEC sp_tablecollations '" + tschma + "." + tableName + "'", conn);
            using (SqlDataReader reader = cmd4.ExecuteReader())
            {
                while (reader.Read())
                {
                    bool? isCaseSensitive = null;
                    string colName = (string)reader["name"];
                    if (reader["tds_collation"] != DBNull.Value)
                    {
                        byte[] mask = (byte[])reader["tds_collation"];
                        if ((mask[2] & 0x10) != 0)
                            isCaseSensitive = false;
                        else
                            isCaseSensitive = true;
                    } // if

                    if (isCaseSensitive.HasValue)
                    {
                        // Update the corresponding column schema.
                        foreach (ColumnSchema csc in res.Columns)
                        {
                            if (csc.ColumnName == colName)
                            {
                                csc.IsCaseSensitivite = isCaseSensitive;
                                break;
                            }
                        } // foreach
                    } // if
                } // while
            } // using

            try
            {
                // Find index information
                SqlCommand cmd3 = new SqlCommand(
                    @"exec sp_helpindex '" + tschma + "." + tableName + "'", conn);
                using (SqlDataReader reader = cmd3.ExecuteReader())
                {
                    res.Indexes = new List<IndexSchema>();
                    while (reader.Read())
                    {
                        string indexName = (string)reader["index_name"];
                        string desc = (string)reader["index_description"];
                        string keys = (string)reader["index_keys"];

                        // Don't add the index if it is actually a primary key index
                        if (desc.Contains("primary key"))
                            continue;

                        IndexSchema index = BuildIndexSchema(indexName, desc, keys);
                        res.Indexes.Add(index);
                    } // while
                } // using
            }
            catch (Exception ex)
            {
                _log.Warn("failed to read index information for table [" + tableName + "]");
            } // catch

            return res;
        }

        /// <summary>
        /// Small validation method to make sure we don't miss anything without getting
        /// an exception.
        /// </summary>
        /// <param name="dataType">The datatype to validate.</param>
        private static void ValidateDataType(string dataType)
        {
            if (dataType == "int" || dataType == "smallint" ||
                dataType == "bit" || dataType == "float" ||
                dataType == "real" || dataType == "nvarchar" ||
                dataType == "varchar" || dataType == "timestamp" ||
                dataType == "varbinary" || dataType == "image" ||
                dataType == "text" || dataType == "ntext" ||
                dataType == "bigint" ||
                dataType == "char" || dataType == "numeric" ||
                dataType == "binary" || dataType == "smalldatetime" ||
                dataType == "smallmoney" || dataType == "money" ||
                dataType == "tinyint" || dataType == "uniqueidentifier" ||
                dataType == "xml" || dataType == "sql_variant" ||
                dataType == "decimal" || dataType == "nchar" || dataType == "datetime")
                return;
            throw new ApplicationException("Validation failed for data type [" + dataType + "]");
        }

        /// <summary>
        /// Does some necessary adjustments to a value string that appears in a column DEFAULT
        /// clause.
        /// </summary>
        /// <param name="colDefault">The original default value string (as read from SQL Server).</param>
        /// <returns>Adjusted DEFAULT value string (for SQLite)</returns>
        private static string FixDefaultValueString(string colDefault)
        {
            bool replaced = false;
            string res = colDefault.Trim();

            // Find first/last indexes in which to search
            int first = -1;
            int last = -1;
            for (int i = 0; i < res.Length; i++)
            {
                if (res[i] == '\'' && first == -1)
                    first = i;
                if (res[i] == '\'' && first != -1 && i > last)
                    last = i;
            } // for

            if (first != -1 && last > first)
                return res.Substring(first, last - first + 1);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < res.Length; i++)
            {
                if (res[i] != '(' && res[i] != ')')
                {
                    sb.Append(res[i]);
                    replaced = true;
                }
            }
            if (replaced)
                return "(" + sb.ToString() + ")";
            else
                return sb.ToString();
        }



        /// <summary>
        /// Add foreign key schema object from the specified components (Read from SQL Server).
        /// </summary>
        /// <param name="conn">The SQL Server connection to use</param>
        /// <param name="ts">The table schema to whom foreign key schema should be added to</param>
        private static void CreateForeignKeySchema(SqlConnection conn, TableSchema ts)
        {
            ts.ForeignKeys = new List<ForeignKeySchema>();

            SqlCommand cmd = new SqlCommand(
                @"SELECT " +
                @"  ColumnName = CU.COLUMN_NAME, " +
                @"  ForeignTableName  = PK.TABLE_NAME, " +
                @"  ForeignColumnName = PT.COLUMN_NAME, " +
                @"  DeleteRule = C.DELETE_RULE, " +
                @"  IsNullable = COL.IS_NULLABLE " +
                @"FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS C " +
                @"INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS FK ON C.CONSTRAINT_NAME = FK.CONSTRAINT_NAME " +
                @"INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS PK ON C.UNIQUE_CONSTRAINT_NAME = PK.CONSTRAINT_NAME " +
                @"INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE CU ON C.CONSTRAINT_NAME = CU.CONSTRAINT_NAME " +
                @"INNER JOIN " +
                @"  ( " +
                @"    SELECT i1.TABLE_NAME, i2.COLUMN_NAME " +
                @"    FROM  INFORMATION_SCHEMA.TABLE_CONSTRAINTS i1 " +
                @"    INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE i2 ON i1.CONSTRAINT_NAME = i2.CONSTRAINT_NAME " +
                @"    WHERE i1.CONSTRAINT_TYPE = 'PRIMARY KEY' " +
                @"  ) " +
                @"PT ON PT.TABLE_NAME = PK.TABLE_NAME " +
                @"INNER JOIN INFORMATION_SCHEMA.COLUMNS AS COL ON CU.COLUMN_NAME = COL.COLUMN_NAME AND FK.TABLE_NAME = COL.TABLE_NAME " +
                @"WHERE FK.Table_NAME='" + ts.TableName + "'", conn);

            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    ForeignKeySchema fkc = new ForeignKeySchema();
                    fkc.ColumnName = (string)reader["ColumnName"];
                    fkc.ForeignTableName = (string)reader["ForeignTableName"];
                    fkc.ForeignColumnName = (string)reader["ForeignColumnName"];
                    fkc.CascadeOnDelete = (string)reader["DeleteRule"] == "CASCADE";
                    fkc.IsNullable = (string)reader["IsNullable"] == "YES";
                    fkc.TableName = ts.TableName;
                    ts.ForeignKeys.Add(fkc);
                }
            }
        }

        /// <summary>
        /// Builds an index schema object from the specified components (Read from SQL Server).
        /// </summary>
        /// <param name="indexName">The name of the index</param>
        /// <param name="desc">The description of the index</param>
        /// <param name="keys">Key columns that are part of the index.</param>
        /// <returns>An index schema object that represents our knowledge of the index</returns>
        private static IndexSchema BuildIndexSchema(string indexName, string desc, string keys)
        {
            IndexSchema res = new IndexSchema();
            res.IndexName = indexName;

            // Determine if this is a unique index or not.
            string[] descParts = desc.Split(',');
            foreach (string p in descParts)
            {
                if (p.Trim().Contains("unique"))
                {
                    res.IsUnique = true;
                    break;
                }
            } // foreach

            // Get all key names and check if they are ASCENDING or DESCENDING
            res.Columns = new List<IndexColumn>();
            string[] keysParts = keys.Split(',');
            foreach (string p in keysParts)
            {
                Match m = _keyRx.Match(p);
                if (!m.Success)
                {
                    throw new ApplicationException("Illegal key name [" + p + "] in index [" +
                        indexName + "]");
                }

                string key = m.Groups[1].Value;
                IndexColumn ic = new IndexColumn();
                ic.ColumnName = key;
                if (m.Groups[2].Success)
                    ic.IsAscending = false;
                else
                    ic.IsAscending = true;

                res.Columns.Add(ic);
            } // foreach

            return res;
        }

        /// <summary>
        /// More adjustments for the DEFAULT value clause.
        /// </summary>
        /// <param name="val">The value to adjust</param>
        /// <returns>Adjusted DEFAULT value string</returns>
        private static string AdjustDefaultValue(string val)
        {
            if (val == null || val == string.Empty)
                return val;

            Match m = _defaultValueRx.Match(val);
            if (m.Success)
                return m.Groups[1].Value;
            return val;
        }

        /// <summary>
        /// Creates SQLite connection string from the specified DB file path.
        /// </summary>
        /// <param name="sqlitePath">The path to the SQLite database file.</param>
        /// <returns>SQLite connection string</returns>
        private static string CreateSQLiteConnectionString(string sqlitePath, string password)
        {
            SQLiteConnectionStringBuilder builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = sqlitePath;
            if (string.IsNullOrEmpty(password) == false)
                builder.Password = password;
            builder.PageSize = 4096;
            builder.UseUTF16Encoding = true;
            string connstring = builder.ConnectionString;

            return connstring;
        }
        #endregion

        #region Trigger related
        private static void AddTriggersForForeignKeys(string sqlitePath, IEnumerable<TableSchema> schema,
            string password, SqlConversionHandler handler)
        {
            // Connect to the newly created database
            string sqliteConnString = CreateSQLiteConnectionString(sqlitePath, password);
            using (SQLiteConnection conn = new SQLiteConnection(sqliteConnString))
            {
                conn.Open();
                // foreach
                foreach (TableSchema dt in schema)
                {
                    try
                    {
                        AddTableTriggers(conn, dt);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("AddTableTriggers failed", ex);
                        throw;
                    }
                }

            } // using

            _log.Debug("finished adding triggers to schema");
        }

        private static void AddTableTriggers(SQLiteConnection conn, TableSchema dt)
        {
            IList<TriggerSchema> triggers = TriggerBuilder.GetForeignKeyTriggers(dt);
            foreach (TriggerSchema trigger in triggers)
            {
                SQLiteCommand cmd = new SQLiteCommand(WriteTriggerSchema(trigger), conn);
                cmd.ExecuteNonQuery();
            }
        }
        #endregion

        /// <summary>
        /// Gets a create script for the triggerSchema in sqlite syntax
        /// </summary>
        /// <param name="ts">Trigger to script</param>
        /// <returns>Executable script</returns>
        public static string WriteTriggerSchema(TriggerSchema ts)
        {
            return @"CREATE TRIGGER [" + ts.Name + "] " +
                   ts.Type + " " + ts.Event +
                   " ON [" + ts.Table + "] " +
                   "BEGIN " + ts.Body + " END;";
        }

        #region Private Variables
        private static bool _isActive = false;
        private static bool _cancelled = false;
        private static Regex _keyRx = new Regex(@"([a-zA-Z_0-9]+)(\(\-\))?");
        private static Regex _defaultValueRx = new Regex(@"\(N(\'.*\')\)");
        private static ILog _log = LogManager.GetLogger(typeof(SqlServerToSQLite));
        #endregion
    }

    /// <summary>
    /// This handler is called whenever a progress is made in the conversion process.
    /// </summary>
    /// <param name="done">TRUE indicates that the entire conversion process is finished.</param>
    /// <param name="success">TRUE indicates that the current step finished successfully.</param>
    /// <param name="percent">Progress percent (0-100)</param>
    /// <param name="msg">A message that accompanies the progress.</param>
    public delegate void SqlConversionHandler(bool done, bool success, int percent, string msg);

    /// <summary>
    /// This handler allows the user to change which tables get converted from SQL Server
    /// to SQLite.
    /// </summary>
    /// <param name="schema">The original SQL Server DB schema</param>
    /// <returns>The same schema minus any table we don't want to convert.</returns>
    public delegate List<TableSchema> SqlTableSelectionHandler(List<TableSchema> schema);

    /// <summary>
    /// This handler is called in order to handle the case when copying the SQL Server view SQL
    /// statement is not enough and the user needs to either update the view definition himself
    /// or discard the view definition from the generated SQLite database.
    /// </summary>
    /// <param name="vs">The problematic view definition</param>
    /// <returns>The updated view definition, or NULL in case the view should be discarded</returns>
    public delegate string FailedViewDefinitionHandler(ViewSchema vs);
}
