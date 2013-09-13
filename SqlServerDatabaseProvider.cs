using System;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using Inedo.BuildMaster;
using Inedo.BuildMaster.Extensibility.Actions;
using Inedo.BuildMaster.Extensibility.Providers;
using Inedo.BuildMaster.Extensibility.Providers.Database;
using Inedo.BuildMaster.Web;
using Inedo.Diagnostics;

namespace Inedo.BuildMasterExtensions.SqlServer
{
    [ProviderProperties(
        "SQL Server",
        "Provides functionality for managing change scripts in Microsoft SQL Server databases.",
        RequiresTransparentProxy = true)]
    [CustomEditor(typeof(SqlServerDatabaseProviderEditor))]
    public sealed class SqlServerDatabaseProvider : DatabaseProviderBase, IRestoreProvider, IChangeScriptProvider
    {
        private SqlConnection sharedConnection;
        private SqlCommand sharedCommand;

        public SqlServerDatabaseProvider()
        {
        }

        public void InitializeDatabase()
        {
            if (this.IsDatabaseInitialized())
                throw new InvalidOperationException("The database has already been initialized.");

            this.ExecuteNonQuery(Properties.Resources.Initialize);
        }
        public bool IsDatabaseInitialized()
        {
            this.ValidateConnection();
            return (bool)this.ExecuteDataTable("SELECT CAST(CASE WHEN EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__BuildMaster_DbSchemaChanges') THEN 1 ELSE 0 END AS BIT)").Rows[0][0];
        }
        public long GetSchemaVersion()
        {
            this.ValidateInitialization();

            return (long)this.ExecuteDataTable("SELECT COALESCE(MAX(Numeric_Release_Number),0) FROM __BuildMaster_DbSchemaChanges").Rows[0][0];
        }
        public ChangeScript[] GetChangeHistory()
        {
            this.ValidateInitialization();

            var dt = ExecuteDataTable(
@"  SELECT [Numeric_Release_Number]
        ,[Script_Id]
        ,[Batch_Name]
        ,MIN([Executed_Date]) [Executed_Date]
        ,MIN([Success_Indicator]) [Success_Indicator]
    FROM [__BuildMaster_DbSchemaChanges]
GROUP BY [Script_Id], [Numeric_Release_Number], [Batch_Name]
ORDER BY [Numeric_Release_Number], MIN([Executed_Date]), [Batch_Name]");

            return dt.Rows
                .Cast<DataRow>()
                .Select(r => new SqlServerChangeScript(r))
                .ToArray();
        }
        public ExecutionResult ExecuteChangeScript(long numericReleaseNumber, int scriptId, string scriptName, string scriptText)
        {
            this.ValidateInitialization();

            var tables = this.ExecuteDataTable("SELECT * FROM __BuildMaster_DbSchemaChanges");
            var rows = tables.Rows.Cast<DataRow>();

            if (rows.Any(r => (int)r["Script_Id"] == scriptId))
                return new ExecutionResult(ExecutionResult.Results.Skipped, string.Format("The script \"{0}\" was already executed.", scriptName));

            var sqlMessageBuffer = new StringBuilder();
            bool errorOccured = false;
            EventHandler<LogReceivedEventArgs> logMessage = (s, e) =>
            {
                if (e.LogLevel == MessageLevel.Error)
                    errorOccured = true;

                sqlMessageBuffer.AppendLine(e.Message);
            };

            this.LogReceived += logMessage;

            try
            {
                var cmd = this.CreateCommand();
                try
                {
                    int scriptSequence = 0;
                    foreach (var sqlCommand in SqlSplitter.SplitSqlScript(scriptText))
                    {
                        if (string.IsNullOrWhiteSpace(sqlCommand))
                            continue;

                        scriptSequence++;
                        try
                        {
                            cmd.CommandText = sqlCommand;
                            cmd.ExecuteNonQuery();
                        }
                        catch (Exception ex)
                        {
                            this.InsertSchemaChange(numericReleaseNumber, scriptId, scriptName, scriptSequence, false);
                            return new ExecutionResult(ExecutionResult.Results.Failed, string.Format("The script \"{0}\" execution encountered a fatal error. Error details: {1}", scriptName, ex.Message) + Util.ConcatNE(" Additional SQL Output: ", sqlMessageBuffer.ToString()));
                        }

                        this.InsertSchemaChange(numericReleaseNumber, scriptId, scriptName, scriptSequence, true);

                        if (errorOccured)
                            return new ExecutionResult(ExecutionResult.Results.Failed, string.Format("The script \"{0}\" execution failed.", scriptName) + Util.ConcatNE(" SQL Error: ", sqlMessageBuffer.ToString()));
                    }
                }
                finally
                {
                    if (this.sharedCommand == null)
                        cmd.Dispose();

                    if (this.sharedConnection == null)
                        cmd.Connection.Dispose();
                }

                return new ExecutionResult(ExecutionResult.Results.Success, string.Format("The script \"{0}\" executed successfully.", scriptName) + Util.ConcatNE(" SQL Output: ", sqlMessageBuffer.ToString()));
            }
            finally 
            {
                this.LogReceived -= logMessage;
            }
        }

        public void BackupDatabase(string databaseName, string destinationPath)
        {
            if (!Directory.Exists(Path.GetDirectoryName(destinationPath)))
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));

            this.ExecuteNonQuery(string.Format(
                "BACKUP DATABASE [{0}] TO DISK = N'{1}' WITH FORMAT",
                databaseName.Replace("]", "]]"),
                destinationPath.Replace("'", "''")));
        }
        public void RestoreDatabase(string databaseName, string sourcePath)
        {
            if (string.IsNullOrEmpty(databaseName))
                throw new ArgumentNullException("databaseName");
            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentNullException("sourcePath");

            var quotedDatabaseName = databaseName.Replace("'", "''");
            var bracketedDatabaseName = databaseName.Replace("]", "]]");
            var quotedSourcePath = sourcePath.Replace("'", "''");

            this.ExecuteNonQuery(string.Format("USE master IF DB_ID('{0}') IS NULL CREATE DATABASE [{1}]", quotedDatabaseName, bracketedDatabaseName));
            this.ExecuteNonQuery(string.Format("ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", bracketedDatabaseName));
            this.ExecuteNonQuery(string.Format("USE master RESTORE DATABASE [{0}] FROM DISK = N'{1}' WITH REPLACE", bracketedDatabaseName, quotedSourcePath));
            this.ExecuteNonQuery(string.Format("ALTER DATABASE [{0}] SET MULTI_USER", bracketedDatabaseName));
        }

        public override bool IsAvailable()
        {
            return true;
        }
        public override string ToString()
        {
            try
            {
                var csb = new SqlConnectionStringBuilder(this.ConnectionString);
                var buffer = new StringBuilder("SQL Server database");
                if (!string.IsNullOrEmpty(csb.InitialCatalog))
                {
                    buffer.Append(" \"");
                    buffer.Append(csb.InitialCatalog);
                    buffer.Append('\"');
                }

                if (!string.IsNullOrEmpty(csb.DataSource))
                {
                    buffer.Append(" on server \"");
                    buffer.Append(csb.DataSource);
                    buffer.Append('\"');
                }

                return buffer.ToString();
            }
            catch
            {
                return "SQL Server database";
            }
        }

        public override void ExecuteQuery(string query)
        {
            this.ExecuteNonQuery(query);
        }
        public override void ExecuteQueries(string[] queries)
        {
            if (queries == null)
                throw new ArgumentNullException("queries");
            if (queries.Length == 0)
                return;

            var cmd = this.CreateCommand();
            try
            {
                foreach (var query in queries)
                {
                    foreach (string splitQuery in SqlSplitter.SplitSqlScript(query))
                    {
                        try
                        {
                            cmd.CommandText = splitQuery;
                            cmd.ExecuteNonQuery();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                if (this.sharedCommand == null)
                    cmd.Dispose();

                if (this.sharedConnection == null)
                    cmd.Connection.Close();
            }
        }
        public override void OpenConnection()
        {
            if (this.sharedConnection != null)
            {
                this.sharedConnection = this.CreateConnection();
                this.sharedCommand = this.CreateCommand();
            }
        }
        public override void CloseConnection()
        {
            if (this.sharedCommand != null)
            {
                this.sharedCommand.Dispose();
                this.sharedCommand = null;
            }

            if (this.sharedConnection != null)
            {
                this.sharedConnection.Dispose();
                this.sharedConnection = null;
            }
        }
        public override void ValidateConnection()
        {
            var dr = this.ExecuteDataTable("SELECT CAST(IS_MEMBER('db_owner') AS BIT) isDbOwner").Rows[0];
            bool db_owner = !Convert.IsDBNull(dr[0]) && (bool)dr[0];
            if (!db_owner)
                throw new NotAvailableException("The ConnectionString credentials must have 'db_owner' privileges.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.CloseConnection();

            base.Dispose(disposing);
        }

        private void ValidateInitialization()
        {
            if (!this.IsDatabaseInitialized())
                throw new InvalidOperationException("The database has not been initialized.");
        }

        private SqlConnection CreateConnection()
        {
            var conStr = new SqlConnectionStringBuilder(ConnectionString) 
            {
                Pooling = false
            };

            var con = new SqlConnection(conStr.ToString())
            {
                FireInfoMessageEventOnUserErrors = true
            };

            con.InfoMessage += (s, e) =>
            {
                foreach (SqlError errorMessage in e.Errors)
                {
                    if (errorMessage.Class > 10)
                        this.LogError(errorMessage.Message);
                    else
                        this.LogInformation(errorMessage.Message);
                }
            };

            return con;
        }
        private SqlCommand CreateCommand()
        {
            if (this.sharedCommand != null)
            {
                this.sharedCommand.Parameters.Clear();
                return this.sharedCommand;
            }

            var cmd = new SqlCommand
            {
                CommandTimeout = 0,
                CommandType = CommandType.Text,
                CommandText = string.Empty
            };

            if (this.sharedConnection != null)
            {
                cmd.Connection = this.sharedConnection;
            }
            else
            {
                var con = this.CreateConnection();
                con.Open();
                cmd.Connection = con;
            }

            return cmd;
        }

        private void ExecuteNonQuery(string cmdText)
        {
            if (string.IsNullOrEmpty(cmdText))
                return;

            var cmd = this.CreateCommand();
            try
            {
                foreach (var commandText in SqlSplitter.SplitSqlScript(cmdText))
                {
                    try
                    {
                        cmd.CommandText = commandText;
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqlException)
                    {
                        // TODO: no action context; not sure whether to continue on next
                        throw;
                    }
                }
            }
            finally
            {
                if (this.sharedCommand == null)
                    cmd.Dispose();

                if (this.sharedConnection == null)
                    cmd.Connection.Close();
            }
        }
        private DataTable ExecuteDataTable(string cmdText)
        {
            var dt = new DataTable();
            var cmd = this.CreateCommand();
            cmd.CommandText = cmdText;
            try
            {
                dt.Load(cmd.ExecuteReader());
            }
            finally
            {
                if (this.sharedCommand == null)
                    cmd.Dispose();

                if (this.sharedConnection == null)
                    cmd.Connection.Close();
            }

            return dt;
        }

        private void InsertSchemaChange(long numericReleaseNumber, int scriptId, string scriptName, int scriptSequence, bool success)
        {
            this.ExecuteQuery(string.Format(
                "INSERT INTO __BuildMaster_DbSchemaChanges "
                + " (Numeric_Release_Number, Script_Id, Script_Sequence, Batch_Name, Executed_Date, Success_Indicator) "
                + "VALUES "
                + "({0}, {1}, {2}, '{3}', GETDATE(), '{4}')",
                numericReleaseNumber,
                scriptId,
                scriptSequence,
                scriptName.Replace("'", "''"),
                success ? "Y" : "N")
            );
        }
    }
}
