using Inedo.BuildMaster.Extensibility.Providers;
using Inedo.BuildMaster.Web.Controls;
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
            this.txtConnectionString = new ValidatingTextBox
            {
                Required = true,
                Width = 300
            };

            this.Controls.Add(
                new FormFieldGroup(
                    "Connection String",
                    "Enter the connection string used to connect to the database.",
                    false,
                    new StandardFormField("Connection String:", this.txtConnectionString)
                )
            );
        }
    }
}
