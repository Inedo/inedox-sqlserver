using System;
using System.Data;
using Inedo.BuildMaster.Extensibility.Providers.Database;

namespace Inedo.BuildMasterExtensions.SqlServer
{
    /// <summary>
    /// Represents a SQL Server change script.
    /// </summary>
    [Serializable]
    public sealed class SqlServerChangeScript : ChangeScript
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerChangeScript"/> class.
        /// </summary>
        /// <param name="dr">The change script data row.</param>
        internal SqlServerChangeScript(DataRow dr)
            : base((long)dr["Numeric_Release_Number"], (int)dr["Script_Id"], (string)dr["Batch_Name"], (DateTime)dr["Executed_Date"], (string)dr["Success_Indicator"] == "Y")
        {
        }
    }
}
