using System.ComponentModel;
using System.Reflection;
using Inedo.Data;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility.DatabaseConnections;
using Inedo.Extensions.SqlServer.Properties;
using Inedo.IO;
using Inedo.Serialization;
using Microsoft.Data.SqlClient;

namespace Inedo.Extensions.SqlServer
{
    [DisplayName("SQL Server")]
    [Description("Provides functionality for managing change scripts in Microsoft SQL Server databases.")]
    [Tag("sql-server"), Tag("databases")]
    [PersistFrom("Inedo.BuildMasterExtensions.SqlServer.SqlServerDatabaseProvider,SqlServer")]
    public sealed class SqlServerDatabaseProvider : DatabaseConnection, IBackupRestore, IChangeScriptExecuter
    {
        private SqlConnection connection;
        private readonly object connectionLock = new();
        private bool disposed;

        public int MaxChangeScriptVersion => 2;

        public static IEnumerable<Assembly> EnumerateChangeScripterAssemblies() => Enumerable.Empty<Assembly>();

        public override Task ExecuteQueryAsync(string query, CancellationToken cancellationToken) => this.ExecuteQueryAsync(query, null, cancellationToken);

        public Task BackupDatabaseAsync(string databaseName, string destinationPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentNullException(nameof(databaseName));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentNullException(nameof(destinationPath));

            var destinationDir = PathEx.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
                DirectoryEx.Create(destinationDir);

            return this.ExecuteQueryAsync($"BACKUP DATABASE [{EscapeName(databaseName)}] TO DISK = N'{EscapeString(destinationPath)}' WITH FORMAT", cancellationToken);
        }
        public async Task RestoreDatabaseAsync(string databaseName, string sourcePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentNullException(nameof(databaseName));
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentNullException(nameof(sourcePath));

            var connectionString = new SqlConnectionStringBuilder(this.ConnectionString) { InitialCatalog = "master" }.ToString();

            var quotedDatabaseName = EscapeString(databaseName);
            var bracketedDatabaseName = EscapeName(databaseName);
            var quotedSourcePath = EscapeString(sourcePath);

            using var conn = new SqlConnection(connectionString);
            conn.InfoMessage += this.Connection_InfoMessage;
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            using (var cmd = new SqlCommand($"IF DB_ID('{quotedDatabaseName}') IS NULL CREATE DATABASE [{bracketedDatabaseName}]", conn))
            {
                if (this.VerboseLogging)
                    this.LogDebug("Executing query: " + cmd.CommandText);

                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = new SqlCommand($"ALTER DATABASE [{bracketedDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", conn))
            {
                if (this.VerboseLogging)
                    this.LogDebug("Executing query: " + cmd.CommandText);

                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = new SqlCommand($"RESTORE DATABASE [{bracketedDatabaseName}] FROM DISK = N'{quotedSourcePath}' WITH REPLACE", conn))
            {
                if (this.VerboseLogging)
                    this.LogDebug("Executing query: " + cmd.CommandText);

                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = new SqlCommand($"ALTER DATABASE [{bracketedDatabaseName}] SET MULTI_USER", conn))
            {
                if (this.VerboseLogging)
                    this.LogDebug("Executing query: " + cmd.CommandText);

                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
        {
            using var transaction = (await this.GetConnectionAsync(cancellationToken).ConfigureAwait(false)).BeginTransaction();
            int version = await this.GetChangeScriptVersionAsync(cancellationToken, transaction).ConfigureAwait(false);
            if (version > 0)
                return;

            await this.ExecuteQueryAsync(Resources.Initialize, transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async Task UpgradeSchemaAsync(IReadOnlyDictionary<int, Guid> canoncialGuids, CancellationToken cancellationToken)
        {
            if (canoncialGuids == null)
                throw new ArgumentNullException(nameof(canoncialGuids));

            var state = await this.GetStateAsync(cancellationToken);
            if (state.ChangeScripterVersion == 0)
                throw new InvalidOperationException("The database has not been initialized.");
            if (state.ChangeScripterVersion != 1)
                throw new InvalidOperationException("The database has already been upgraded.");

            using var transaction = (await this.GetConnectionAsync(cancellationToken).ConfigureAwait(false)).BeginTransaction();
            await this.ExecuteQueryAsync(Resources.Initialize, transaction, cancellationToken).ConfigureAwait(false);

            foreach (var script in state.Scripts)
            {
                if (canoncialGuids.TryGetValue(script.Id.ScriptId, out var guid))
                {
                    await this.ExecuteQueryAsync(
                        $"INSERT INTO [__BuildMaster_DbSchemaChanges2] ([Script_Id], [Script_Guid], [Script_Name], [Executed_Date], [Success_Indicator]) VALUES ({script.Id.ScriptId}, '{guid:D}', N'{EscapeString(script.Name)}', '{script.ExecutionDate:yyyy'-'MM'-'dd' 'hh':'mm':'ss'.'fff}', '{(YNIndicator)script.SuccessfullyExecuted}')",
                        transaction
,
                        cancellationToken);
                }
                else
                {
                }
            }


            transaction.Commit();
        }
        public async Task<ChangeScriptState> GetStateAsync(CancellationToken cancellationToken)
        {
            int version = await this.GetChangeScriptVersionAsync(cancellationToken).ConfigureAwait(false);

            if (version == 2)
            {
                return new ChangeScriptState(
                    2,
                    await this.ExecuteTableAsync("SELECT * FROM [__BuildMaster_DbSchemaChanges2]", ReadExecutionRecord, cancellationToken).ConfigureAwait(false)
                );
            }

            if (version == 1)
            {
                return new ChangeScriptState(
                    1,
                    await this.ExecuteTableAsync(
                        "SELECT [Script_Id], [Numeric_Release_Number], [Batch_Name], [Executed_Date] = MIN([Executed_Date]), [Success_Indicator] = MIN([Success_Indicator]) FROM [__BuildMaster_DbSchemaChanges] WHERE [Script_Id] > 0 GROUP BY [Numeric_Release_Number], [Script_Id], [Batch_Name]",
                        ReadLegacyExecutionRecord,
                        cancellationToken
                    ).ConfigureAwait(false)
                );
            }

            return new ChangeScriptState(false);
        }
        public async Task ExecuteChangeScriptAsync(ChangeScriptId scriptId, string scriptName, string scriptText, CancellationToken cancellationToken)
        {
            int version = await this.GetChangeScriptVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version < 1 || version > 2)
                throw new InvalidOperationException("The database has not been initialized.");

            string getQuery;
            if (version == 2)
                getQuery = $"SELECT [Script_Name] FROM [__BuildMaster_DbSchemaChanges2] WHERE [Script_Guid] = '{scriptId.Guid:D}'";
            else
                getQuery = "SELECT [Batch_Name] FROM [__BuildMaster_DbSchemaChanges] WHERE [Script_Id] = " + scriptId.ScriptId;

            bool alreadyRan = (await this.ExecuteTableAsync(getQuery, r => r[0]?.ToString(), cancellationToken).ConfigureAwait(false)).Count > 0;
            if (alreadyRan)
            {
                this.LogInformation($"Script {scriptName} has already been executed; skipping...");
                return;
            }

            this.LogInformation($"Executing {scriptName}...");

            YNIndicator success;
            try
            {
                await this.ExecuteQueryAsync(scriptText, cancellationToken).ConfigureAwait(false);
                success = true;
                this.LogInformation(scriptName + " executed successfully.");
            }
            catch (SqlException ex)
            {
                foreach (SqlError error in ex.Errors)
                {
                    if (error.Class > 10)
                        this.LogError(error.Message);
                    else
                        this.LogInformation(error.Message);
                }

                success = false;
            }

            string updateQuery;

            if (version == 2)
                updateQuery = $"INSERT INTO [__BuildMaster_DbSchemaChanges2] ([Script_Id], [Script_Guid], [Script_Name], [Executed_Date], [Success_Indicator]) VALUES ({scriptId.ScriptId}, '{scriptId.Guid}', N'{EscapeString(scriptName)}', GETUTCDATE(), '{success}')";
            else
                updateQuery = $"INSERT INTO [__BuildMaster_DbSchemaChanges] ([Numeric_Release_Number], [Script_Id], [Script_Sequence], [Batch_Name], [Executed_Date], [Success_Indicator]) VALUES ({scriptId.LegacyReleaseSequence}, {scriptId.ScriptId}, 1, N'{EscapeString(scriptName)}', GETDATE(), '{success}')";

            this.LogDebug($"Recording execution result for {scriptName}...");
            await this.ExecuteQueryAsync(updateQuery, cancellationToken).ConfigureAwait(false);
            this.LogDebug($"Execution result for {scriptName} recorded.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this.disposed)
            {
                this.connection?.Dispose();
                this.connection = null;
                this.disposed = true;
            }

            base.Dispose(disposing);
        }

        private async Task ExecuteQueryAsync(string query, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            foreach (var sql in SqlSplitter.SplitSqlScript(query))
            {
                if (this.VerboseLogging)
                    this.LogDebug("Executing query: " + query);

                using var command = new SqlCommand(sql, await this.GetConnectionAsync(cancellationToken).ConfigureAwait(false), transaction);
                // using the cancellation token for timeouts, so disable it here
                command.CommandTimeout = 0;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        private async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken)
        {
            SqlConnection connection;
            lock (this.connectionLock)
            {
                connection = this.connection;
            }

            if (connection == null)
            {
                connection = new SqlConnection(DbUpdater.InedoSqlUtil.EnsureRequireEncryptionDefaultsToFalse(this.ConnectionString));
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                lock (this.connectionLock)
                {
                    if (this.connection == null)
                    {
                        connection.InfoMessage += this.Connection_InfoMessage;
                        this.connection = connection;
                    }
                    else
                    {
                        connection.Dispose();
                        connection = this.connection;
                    }
                }
            }

            return connection;
        }

        private void Connection_InfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                if (error.Class > 10)
                    this.LogError(error.Message);
                else
                    this.LogInformation(error.Message);
            }
        }
        private async Task<List<TResult>> ExecuteTableAsync<TResult>(string query, Func<SqlDataReader, TResult> adapter, CancellationToken cancellationToken, SqlTransaction transaction = null)
        {
            using var command = new SqlCommand(query, await this.GetConnectionAsync(cancellationToken).ConfigureAwait(false), transaction);
            // using the cancellation token for timeouts, so disable it here
            command.CommandTimeout = 0;

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var table = new List<TResult>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                table.Add(adapter(reader));
            }

            return table;
        }
        private async Task<int> GetChangeScriptVersionAsync(CancellationToken cancellationToken, SqlTransaction transaction = null)
        {
            var table = await this.ExecuteTableAsync(
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('__BuildMaster_DbSchemaChanges', '__BuildMaster_DbSchemaChanges2')",
                t => t["TABLE_NAME"]?.ToString(),
                cancellationToken,
                transaction
            ).ConfigureAwait(false);

            bool hasV1Table = table.Contains("__BuildMaster_DbSchemaChanges", StringComparer.OrdinalIgnoreCase);
            bool hasV2Table = table.Contains("__BuildMaster_DbSchemaChanges2", StringComparer.OrdinalIgnoreCase);
            return hasV2Table ? 2 : hasV1Table ? 1 : 0;
        }

        private static string EscapeName(string name) => name?.Replace("]", "]]");
        private static string EscapeString(string s) => s?.Replace("'", "''");
        private static ChangeScriptExecutionRecord ReadExecutionRecord(SqlDataReader r)
        {
            return new ChangeScriptExecutionRecord(
                new ChangeScriptId((int)r["Script_Id"], (Guid)r["Script_Guid"]),
                r["Script_Name"]?.ToString(),
                new DateTime(((DateTime)r["Executed_Date"]).Ticks, DateTimeKind.Utc),
                YNIndicator.Parse(r["Success_Indicator"]?.ToString())
            );
        }
        private static ChangeScriptExecutionRecord ReadLegacyExecutionRecord(SqlDataReader r)
        {
            return new ChangeScriptExecutionRecord(
                new ChangeScriptId((int)r["Script_Id"], (long)r["Numeric_Release_Number"]),
                r["Batch_Name"]?.ToString(),
                new DateTime(((DateTime)r["Executed_Date"]).Ticks, DateTimeKind.Local).ToUniversalTime(),
                YNIndicator.Parse(r["Success_Indicator"]?.ToString())
            );
        }
    }
}
