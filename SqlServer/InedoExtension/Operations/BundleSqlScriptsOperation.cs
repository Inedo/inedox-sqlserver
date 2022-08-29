using System.ComponentModel;
using System.IO.Compression;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.IO;
using Inedo.Web;

namespace Inedo.Extensions.SqlServer.Operations
{
    [ScriptAlias("Bundle-SqlScripts")]
    [DisplayName("Build Database Updater Executable")]
    [Description("Generates an executable file that can act as a self-installing package of database change scripts.")]
    [Tag("databases"), Tag("sql-server")]
    [ScriptNamespace("SqlServer")]
    public sealed class BundleSqlScriptsOperation : RemoteExecuteOperation
    {
        [ScriptAlias("Directory")]
        [DisplayName("From directory")]
        [PlaceholderText("$WorkingDirectory")]
        [FieldEditMode(FieldEditMode.ServerDirectoryPath)]
        public string SourceDirectory { get; set; }
        [Category("File Masks")]
        [ScriptAlias("Include")]
        [MaskingDescription]
        public IEnumerable<string> Includes { get; set; } = new[] { "**.sql" };
        [Category("File Masks")]
        [ScriptAlias("Exclude")]
        [MaskingDescription]
        public IEnumerable<string> Excludes { get; set; }
        [Required]
        [ScriptAlias("OutputFile")]
        [DisplayName("Output file")]
        public string OutputFile { get; set; } = "inedosql.exe";

        protected override Task<object> RemoteExecuteAsync(IRemoteOperationExecutionContext context)
        {
            var sourcePath = context.ResolvePath(this.SourceDirectory);
            this.LogInformation($"Finding matching files in {sourcePath}...");
            if (!DirectoryEx.Exists(sourcePath))
            {
                this.LogError($"Directory {sourcePath} does not exist.");
                return Complete;
            }

            var mask = new MaskingContext(this.Includes, this.Excludes);
            var matches = DirectoryEx.GetFileSystemInfos(sourcePath, mask)
                .OfType<SlimFileInfo>()
                .Where(f => f.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                this.LogError($"No matching .sql files were found in {sourcePath}.");
                return Complete;
            }

            var outputFileName = context.ResolvePath(this.OutputFile);
            DirectoryEx.Create(PathEx.GetDirectoryName(outputFileName));

            using (var buffer = new TemporaryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var f in matches)
                    {
                        var entryName = getEntryName(f.FullName);
                        this.LogDebug($"Adding {entryName}...");
                        zip.CreateEntryFromFile(f.FullName, entryName, CompressionLevel.Optimal);
                    }
                }

                buffer.Position = 0;

                using var outputStream = FileEx.Open(outputFileName, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.SequentialScan);
                using (var inedoSqlStream = typeof(BundleSqlScriptsOperation).Assembly.GetManifestResourceStream("Inedo.Extensions.SqlServer.Operations.inedosql.exe"))
                {
                    inedoSqlStream.CopyTo(outputStream);
                }

                buffer.CopyTo(outputStream);
            }

            this.LogInformation($"{outputFileName} created.");
            return Complete;

            string getEntryName(string fullName) => fullName[..sourcePath.Length].TrimStart('\\', '/').Replace('\\', '/');
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription(
                    "Bundle ",
                    new MaskHilite(config[nameof(Includes)], config[nameof(Excludes)]),
                    " in ",
                    new DirectoryHilite(config[nameof(SourceDirectory)])
                ),
                new RichDescription(
                    "into ",
                    new DirectoryHilite(config[nameof(OutputFile)])
                )
            );
        }
    }
}
