using System;
using System.Data;
using System.Data.Common;
using System.Windows.Forms;
using Microsoft.AnalysisServices.AdomdClient;
using TabularEditor.UI;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Runs DAX and DMV queries against the Analysis Services / Power BI instance the model was loaded from.
    /// </summary>
    internal static class AdomdHelper
    {
        public static bool IsConnected
        {
            get
            {
                var handler = UIController.Current?.Handler;
                return handler != null && handler.IsConnected && handler.Database?.Server != null;
            }
        }

        /// <summary>Shows a message and returns false when the current model is not loaded from a server.</summary>
        public static bool EnsureConnected(IWin32Window owner)
        {
            if (IsConnected) return true;
            MessageBox.Show(owner,
                "This feature needs a live connection.\n\nOpen the model with File > Open > From DB (Analysis Services, a Power BI XMLA endpoint, " +
                "or Power BI Desktop via External Tools) and try again.",
                "No connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private static string GetConnectionString()
        {
            var database = UIController.Current.Handler.Database;
            var builder = new DbConnectionStringBuilder { ConnectionString = database.Server.ConnectionString };
            builder.Remove("Initial Catalog");
            builder.Remove("Catalog");
            builder.Remove("Database");
            builder["Initial Catalog"] = database.Name;
            return builder.ConnectionString;
        }

        public static DataTable Query(string query)
        {
            using (var connection = new AdomdConnection(GetConnectionString()))
            {
                connection.Open();
                using (var command = new AdomdCommand(query, connection))
                {
                    command.CommandTimeout = 0;
                    var table = new DataTable();
                    using (var adapter = new AdomdDataAdapter(command)) adapter.Fill(table);
                    return table;
                }
            }
        }

        public static long ToLong(object value)
        {
            if (value == null || value is DBNull) return 0;
            try { return Convert.ToInt64(value); } catch { return 0; }
        }
    }
}
