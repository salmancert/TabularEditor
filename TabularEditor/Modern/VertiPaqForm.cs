using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.UIServices;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Shows how much memory each table and column of the connected model uses, based on the
    /// storage engine DMVs (DISCOVER_STORAGE_TABLES / _TABLE_COLUMNS / _TABLE_COLUMN_SEGMENTS).
    /// </summary>
    internal class VertiPaqForm : Form
    {
        private readonly DataGridView tableGrid = NewGrid();
        private readonly DataGridView columnGrid = NewGrid();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel("Loading...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private DataTable columns;

        public VertiPaqForm()
        {
            Text = "VertiPaq Analyzer";
            Icon = FormMain.Singleton?.Icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, 750);

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var refresh = new ToolStripButton("Refresh");
            var showAll = new ToolStripButton("Show all columns");
            refresh.Click += (s, e) => LoadData();
            showAll.Click += (s, e) => { tableGrid.ClearSelection(); FilterColumns(null); };
            toolbar.Items.Add(refresh);
            toolbar.Items.Add(showAll);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(tableGrid);
            split.Panel2.Controls.Add(columnGrid);

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(status);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            tableGrid.SelectionChanged += (s, e) =>
            {
                if (tableGrid.Focused && tableGrid.CurrentRow?.DataBoundItem is DataRowView row) FilterColumns((string)row["Table"]);
            };
            Shown += (s, e) => { split.SplitterDistance = split.Height * 2 / 5; LoadData(); };
        }

        private static DataGridView NewGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
            };
            grid.DataError += (s, e) => e.ThrowException = false;
            return grid;
        }

        private void FilterColumns(string table)
        {
            if (columns == null) return;
            columns.DefaultView.RowFilter = table == null ? "" : "Table = '" + table.Replace("'", "''") + "'";
        }

        private async void LoadData()
        {
            status.Text = "Reading storage statistics...";
            try
            {
                var result = await Task.Run(() => Analyze());
                if (IsDisposed) return;
                columns = result.Item2;
                tableGrid.DataSource = result.Item1;
                columnGrid.DataSource = columns.DefaultView;
                Format(tableGrid);
                Format(columnGrid);
                ThemeManager.ApplyGrid(tableGrid);
                ThemeManager.ApplyGrid(columnGrid);
                long total = result.Item1.Rows.Cast<DataRow>().Sum(r => (long)r["Total size"]);
                status.Text = $"Model size in memory: {total / 1024d / 1024d:N1} MB across {result.Item1.Rows.Count:N0} tables. Select a table to see its columns.";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                status.Text = "Could not read storage statistics";
                MessageBox.Show(this, ex.Message + "\n\nReading storage statistics requires administrator permission on the model.",
                    "VertiPaq Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void Format(DataGridView grid)
        {
            foreach (DataGridViewColumn column in grid.Columns)
            {
                if (column.ValueType == typeof(long))
                {
                    column.DefaultCellStyle.Format = "N0";
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                }
                else if (column.ValueType == typeof(double))
                {
                    column.DefaultCellStyle.Format = "N1";
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                }
            }
        }

        private static bool IsInternal(string tableId) =>
            tableId.StartsWith("H$") || tableId.StartsWith("R$") || tableId.StartsWith("U$");

        private class ColumnStats
        {
            public string Table, Column;
            public long Cardinality = -1, Data, Dictionary, Hierarchy;
            public long Total => Data + Dictionary + Hierarchy;
        }

        private static Tuple<DataTable, DataTable> Analyze()
        {
            var storageTables = AdomdHelper.Query("SELECT DIMENSION_NAME, TABLE_ID, ROWS_COUNT FROM $SYSTEM.DISCOVER_STORAGE_TABLES");
            var storageColumns = AdomdHelper.Query("SELECT DIMENSION_NAME, COLUMN_ID, ATTRIBUTE_NAME, DICTIONARY_SIZE, COLUMN_TYPE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS");
            var segments = AdomdHelper.Query("SELECT DIMENSION_NAME, TABLE_ID, COLUMN_ID, USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS");

            // Columns keyed by table name + column id.
            var stats = new Dictionary<string, ColumnStats>();
            foreach (DataRow row in storageColumns.Rows)
            {
                if (!"BASIC_DATA".Equals(Convert.ToString(row["COLUMN_TYPE"]), StringComparison.OrdinalIgnoreCase)) continue;
                var name = Convert.ToString(row["ATTRIBUTE_NAME"]);
                if (name.StartsWith("RowNumber-", StringComparison.OrdinalIgnoreCase)) continue;
                var table = Convert.ToString(row["DIMENSION_NAME"]);
                var key = table + "\n" + Convert.ToString(row["COLUMN_ID"]);
                stats[key] = new ColumnStats { Table = table, Column = name, Dictionary = AdomdHelper.ToLong(row["DICTIONARY_SIZE"]) };
            }

            var otherSize = new Dictionary<string, long>();   // relationships and user hierarchies per table
            foreach (DataRow row in segments.Rows)
            {
                var table = Convert.ToString(row["DIMENSION_NAME"]);
                var tableId = Convert.ToString(row["TABLE_ID"]);
                var columnId = Convert.ToString(row["COLUMN_ID"]);
                var size = AdomdHelper.ToLong(row["USED_SIZE"]);
                ColumnStats column;
                if (!IsInternal(tableId))
                {
                    if (stats.TryGetValue(table + "\n" + columnId, out column)) column.Data += size;
                }
                else if (tableId.StartsWith("H$") && stats.TryGetValue(table + "\n" + tableId.Substring(tableId.LastIndexOf('$') + 1), out column))
                {
                    column.Hierarchy += size;
                }
                else
                {
                    long current;
                    otherSize.TryGetValue(table, out current);
                    otherSize[table] = current + size;
                }
            }

            var rowCounts = new Dictionary<string, long>();
            foreach (DataRow row in storageTables.Rows)
            {
                var table = Convert.ToString(row["DIMENSION_NAME"]);
                var tableId = Convert.ToString(row["TABLE_ID"]);
                var rows = AdomdHelper.ToLong(row["ROWS_COUNT"]);
                ColumnStats column;
                if (!IsInternal(tableId)) rowCounts[table] = rows;
                else if (tableId.StartsWith("H$") && stats.TryGetValue(table + "\n" + tableId.Substring(tableId.LastIndexOf('$') + 1), out column))
                    column.Cardinality = Math.Max(0, rows - 3);   // the attribute hierarchy holds three system rows
            }

            long modelTotal = stats.Values.Sum(c => c.Total) + otherSize.Values.Sum();
            if (modelTotal == 0) modelTotal = 1;

            var tables = new DataTable();
            tables.Columns.Add("Table", typeof(string));
            tables.Columns.Add("Rows", typeof(long));
            tables.Columns.Add("Columns", typeof(long));
            tables.Columns.Add("Data size", typeof(long));
            tables.Columns.Add("Dictionary size", typeof(long));
            tables.Columns.Add("Hierarchies size", typeof(long));
            tables.Columns.Add("Relationships size", typeof(long));
            tables.Columns.Add("Total size", typeof(long));
            tables.Columns.Add("% of model", typeof(double));

            var tableTotals = new Dictionary<string, long>();
            foreach (var group in stats.Values.GroupBy(c => c.Table))
            {
                long other, rows;
                otherSize.TryGetValue(group.Key, out other);
                rowCounts.TryGetValue(group.Key, out rows);
                long total = group.Sum(c => c.Total) + other;
                tableTotals[group.Key] = total;
                tables.Rows.Add(group.Key, rows, (long)group.Count(), group.Sum(c => c.Data), group.Sum(c => c.Dictionary),
                    group.Sum(c => c.Hierarchy), other, total, 100d * total / modelTotal);
            }
            tables.DefaultView.Sort = "[Total size] DESC";

            var columnTable = new DataTable();
            columnTable.Columns.Add("Table", typeof(string));
            columnTable.Columns.Add("Column", typeof(string));
            columnTable.Columns.Add("Cardinality", typeof(long));
            columnTable.Columns.Add("Data size", typeof(long));
            columnTable.Columns.Add("Dictionary size", typeof(long));
            columnTable.Columns.Add("Hierarchy size", typeof(long));
            columnTable.Columns.Add("Total size", typeof(long));
            columnTable.Columns.Add("% of table", typeof(double));
            columnTable.Columns.Add("% of model", typeof(double));
            foreach (var c in stats.Values)
            {
                long tableTotal = Math.Max(1, tableTotals[c.Table]);
                columnTable.Rows.Add(c.Table, c.Column, c.Cardinality < 0 ? (object)DBNull.Value : c.Cardinality, c.Data, c.Dictionary,
                    c.Hierarchy, c.Total, 100d * c.Total / tableTotal, 100d * c.Total / modelTotal);
            }
            columnTable.DefaultView.Sort = "[Total size] DESC";

            return Tuple.Create(tables.DefaultView.ToTable(), columnTable);
        }
    }
}
