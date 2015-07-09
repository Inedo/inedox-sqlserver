using Inedo.BuildMaster.Extensibility.Providers;
using Inedo.BuildMaster.Web.Controls.Extensions;
using Inedo.Web.Controls;

namespace Inedo.BuildMasterExtensions.SqlServer
{
    internal sealed class SqlServerDatabaseProviderEditor : ProviderEditorBase
    {
        private ValidatingTextBox txtConnectionString;

        public override void BindToForm(ProviderBase extension)
        {
            var sqlProv = (SqlServerDatabaseProvider)extension;
            txtConnectionString.Text = sqlProv.ConnectionString;
        }

        public override ProviderBase CreateFromForm()
        {
            return new SqlServerDatabaseProvider
            { 
                ConnectionString = txtConnectionString.Text
            };
        }

        protected override void CreateChildControls()
        {
            this.txtConnectionString = new ValidatingTextBox { Required = true };

            this.Controls.Add(
                new SlimFormField("Connection string:", this.txtConnectionString)
                {
                    HelpText = HelpText.FromHtml("<p>The connection string typically follows the format:</p>"
                    + "<code>Server=myServerAddress;Database=myDataBase;Integrated Security=true;</code>"
                    + "<p>For more examples, visit: <a target=\"_blank\" href=\"https://www.connectionstrings.com/sql-server/\">ConnectionStrings.com</a></p>")
                }
            );
        }
    }
}
