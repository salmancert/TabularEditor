using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.UI;
using TabularEditor.UIServices;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Scans the tables of the connected model and reports, per column, how many rows are blank.
    /// Columns that are blank on every row are flagged. The result can be sorted by any header,
    /// filtered to blank columns only, and its columns can be dragged into a different order.
    /// </summary>
    internal class TableAnalyzerForm : Form
    {
        private class ColumnInfo { public string Name, DaxRef, DataType; }
        private class TableInfo { public string Name, DaxRef; public List<ColumnInfo> Columns; }

        private readonly DataGridView grid = new DataGridView();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripButton btnRun = new ToolStripButton("Analyze");
        private readonly ToolStripButton btnBlankOnly = new ToolStripButton("Blank columns only") { CheckOnClick = true };
        private readonly ToolStripComboBox cmbTable = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly ToolStripComboBox cmbSort = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private DataTable results;
        private bool running, cancel;

        private const string AllTables = "(All tables)";
        private const string StatusBlank = "Entirely blank";

        public TableAnalyzerForm(Table preselect = null)
        {
            Text = "Table Analyzer";
            Icon = FormMain.Singleton?.Icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1050, 700);

            var model = UIController.Current?.Handler?.Model;
            cmbTable.Items.Add(AllTables);
            if (model != null) foreach (var t in model.Tables.OrderBy(t => t.Name)) cmbTable.Items.Add(t.Name);
            cmbTable.SelectedItem = preselect != null ? (object)preselect.Name : AllTables;
            if (cmbTable.SelectedIndex < 0) cmbTable.SelectedIndex = 0;

            cmbSort.Items.AddRange(new object[] {
                "Sort: table, then column name A-Z",
                "Sort: column name A-Z",
                "Sort: column name Z-A",
                "Sort: most blank first",
                "Sort: model order" });
            cmbSort.SelectedIndex = 0;
            cmbSort.SelectedIndexChanged += (s, e) => ApplyView();
            btnBlankOnly.Click += (s, e) => ApplyView();
            btnRun.Click += (s, e) => Analyze();

            var btnCopy = new ToolStripButton("Copy");
            btnCopy.Click += (s, e) =>
            {
                if (grid.Rows.Count == 0) return;
                grid.SelectAll();
                var data = grid.GetClipboardContent();
                if (data != null) Clipboard.SetDataObject(data);
                grid.ClearSelection();
                status.Text = "Copied to the clipboard (paste into Excel)";
            };
            var btnHide = new ToolStripButton("Hide selected columns in model");
            btnHide.Click += (s, e) => HideSelected();

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            toolbar.Items.AddRange(new ToolStripItem[] {
                new ToolStripLabel("Table:"), cmbTable, btnRun, new ToolStripSeparator(),
                cmbSort, btnBlankOnly, new ToolStripSeparator(), btnHide, btnCopy });

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;   // drag headers to rearrange
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            grid.DataError += (s, e) => e.ThrowException = false;
            grid.CellFormatting += Grid_CellFormatting;
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) GotoColumn(grid.Rows[e.RowIndex]); };

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(status);

            Controls.Add(grid);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            FormClosing += (s, e) => cancel = true;
            Shown += (s, e) => { if (AdomdHelper.IsConnected) Analyze(); else status.Text = "Connect to a model instance to analyze its data."; };
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || !(grid.Rows[e.RowIndex].DataBoundItem is DataRowView row)) return;
            if (StatusBlank.Equals(row["Status"] as string))
                e.CellStyle.ForeColor = ThemeManager.IsDark ? Color.FromArgb(255, 120, 120) : Color.Firebrick;
        }

        private static Column FindColumn(DataGridViewRow gridRow)
        {
            var row = gridRow.DataBoundItem as DataRowView;
            var model = UIController.Current?.Handler?.Model;
            if (row == null || model == null) return null;
            var table = model.Tables.FirstOrDefault(t => t.Name == (string)row["Table"]);
            return table?.Columns.FirstOrDefault(c => c.Name == (string)row["Column"]);
        }

        private void GotoColumn(DataGridViewRow gridRow)
        {
            var column = FindColumn(gridRow);
            if (column != null) UIController.Current.Goto(column);
        }

        private void HideSelected()
        {
            var columns = grid.SelectedRows.Cast<DataGridViewRow>().Select(FindColumn).Where(c => c != null && !c.IsHidden).ToList();
            if (columns.Count == 0) { status.Text = "Select one or more rows first (columns already hidden are skipped)."; return; }
            if (MessageBox.Show(this, $"Hide {columns.Count} column(s) in the model? You can undo this with Ctrl+Z in the main window.",
                "Hide columns", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var handler = UIController.Current.Handler;
            handler.BeginUpdate("hide blank columns");
            try { foreach (var c in columns) c.IsHidden = true; }
            finally { handler.EndUpdate(); }
            status.Text = $"{columns.Count} column(s) hidden.";
        }

        private void ApplyView()
        {
            if (results == null) return;
            var view = results.DefaultView;
            view.RowFilter = btnBlankOnly.Checked ? "Status = '" + StatusBlank + "'" : "";
            switch (cmbSort.SelectedIndex)
            {
                case 0: view.Sort = "[Table] ASC, [Column] ASC"; break;
                case 1: view.Sort = "[Column] ASC, [Table] ASC"; break;
                case 2: view.Sort = "[Column] DESC, [Table] ASC"; break;
                case 3: view.Sort = "[% blank] DESC, [Table] ASC, [Column] ASC"; break;
                default: view.Sort = "[#] ASC"; break;
            }
        }

        private async void Analyze()
        {
            if (running || !AdomdHelper.EnsureConnected(this)) return;
            var model = UIController.Current.Handler.Model;
            var selected = cmbTable.SelectedItem as string;

            // Snapshot what we need from the model on the UI thread.
            var tables = model.Tables
                .Where(t => selected == null || selected == AllTables || t.Name == selected)
                .Select(t => new TableInfo
                {
                    Name = t.Name,
                    DaxRef = t.DaxObjectFullName,
                    Columns = t.Columns.Where(c => c.Type != ColumnType.RowNumber)
                        .Select(c => new ColumnInfo { Name = c.Name, DaxRef = c.DaxObjectFullName, DataType = c.DataType.ToString() }).ToList()
                }).Where(t => t.Columns.Count > 0).ToList();

            results = new DataTable();
            results.Columns.Add("#", typeof(int));
            results.Columns.Add("Table", typeof(string));
            results.Columns.Add("Column", typeof(string));
            results.Columns.Add("Data type", typeof(string));
            results.Columns.Add("Rows", typeof(long));
            results.Columns.Add("Blank rows", typeof(long));
            results.Columns.Add("% blank", typeof(double));
            results.Columns.Add("Status", typeof(string));

            running = true; cancel = false;
            btnRun.Enabled = false;
            int done = 0, order = 0, failed = 0;
            try
            {
                foreach (var table in tables)
                {
                    if (cancel) return;
                    status.Text = $"Analyzing {table.Name} ({++done} of {tables.Count})...";
                    var rows = await Task.Run(() => AnalyzeTable(table));
                    if (cancel || IsDisposed) return;
                    foreach (var r in rows)
                    {
                        if (r.Item3 < 0) failed++;
                        results.Rows.Add(++order, table.Name, r.Item1.Name, r.Item1.DataType,
                            r.Item2 < 0 ? (object)DBNull.Value : r.Item2,
                            r.Item3 < 0 ? (object)DBNull.Value : r.Item3,
                            r.Item2 > 0 && r.Item3 >= 0 ? (object)(100d * r.Item3 / r.Item2) : DBNull.Value,
                            r.Item3 < 0 ? "Could not be checked" : r.Item2 == 0 ? "Table is empty" : r.Item3 >= r.Item2 ? StatusBlank : r.Item3 > 0 ? "Partly blank" : "No blanks");
                    }
                }

                grid.DataSource = results.DefaultView;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    if (column.ValueType == typeof(long) || column.ValueType == typeof(int)) column.DefaultCellStyle.Format = "N0";
                    if (column.ValueType == typeof(double)) column.DefaultCellStyle.Format = "N1";
                    if (column.ValueType != typeof(string)) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                }
                grid.Columns["#"].Visible = false;
                ThemeManager.ApplyGrid(grid);
                ApplyView();
                int blank = results.Rows.Cast<DataRow>().Count(r => StatusBlank.Equals(r["Status"] as string));
                status.Text = $"{results.Rows.Count:N0} columns in {tables.Count:N0} table(s) checked: {blank:N0} entirely blank"
                    + (failed > 0 ? $", {failed:N0} could not be checked" : "") + ". Double-click a row to locate the column in the model.";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                status.Text = "Analysis failed";
                MessageBox.Show(this, ex.Message, "Table Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                running = false;
                if (!IsDisposed) btnRun.Enabled = true;
            }
        }

        /// <summary>Returns (column, row count, blank row count) per column. -1 means the value could not be determined.</summary>
        private static List<Tuple<ColumnInfo, long, long>> AnalyzeTable(TableInfo table)
        {
            var result = new List<Tuple<ColumnInfo, long, long>>();
            try
            {
                // One query for the whole table. COUNTBLANK counts blanks and empty strings.
                var values = QueryCounts(table, table.Columns);
                for (int i = 0; i < table.Columns.Count; i++) result.Add(Tuple.Create(table.Columns[i], values[0], values[i + 1]));
                return result;
            }
            catch
            {
                // Some column could not be evaluated: fall back to one query per column.
                result.Clear();
                long rowCount = -1;
                foreach (var column in table.Columns)
                {
                    try
                    {
                        var values = QueryCounts(table, new List<ColumnInfo> { column });
                        rowCount = values[0];
                        result.Add(Tuple.Create(column, values[0], values[1]));
                    }
                    catch
                    {
                        result.Add(Tuple.Create(column, rowCount, -1L));
                    }
                }
                return result;
            }
        }

        private static long[] QueryCounts(TableInfo table, List<ColumnInfo> columns)
        {
            var dax = new StringBuilder("EVALUATE ROW(\"r\", COUNTROWS(" + table.DaxRef + ")");
            for (int i = 0; i < columns.Count; i++) dax.Append(", \"c" + i + "\", COUNTBLANK(" + columns[i].DaxRef + ")");
            dax.Append(")");
            var data = AdomdHelper.Query(dax.ToString());
            var values = new long[columns.Count + 1];
            if (data.Rows.Count > 0)
                for (int i = 0; i < values.Length && i < data.Columns.Count; i++) values[i] = AdomdHelper.ToLong(data.Rows[0][i]);
            return values;
        }
    }
}
