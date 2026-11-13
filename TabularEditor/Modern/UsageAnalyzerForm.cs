using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using TabularEditor.TOMWrapper;
using TabularEditor.UI;
using TabularEditor.UIServices;
using DataColumn = TabularEditor.TOMWrapper.DataColumn;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Finds measures and columns that nothing uses, in the style of dedicated model clean-up tools:
    /// every measure and column gets a status (Used / Used by unused / Unused) with a where-used
    /// summary, unused DAX objects can be deleted, and unused data columns can be removed by adding
    /// a step to the table's Power Query (M) expression.
    /// </summary>
    internal class UsageAnalyzerForm : Form
    {
        private const string Used = "Used", UsedByUnused = "Used by unused", Unused = "Unused", UnusedManual = "Unused (remove manually)";

        private class Item
        {
            public object Object;
            public string Table, Name, Type;
            public readonly List<string> Roots = new List<string>();
            public readonly HashSet<Item> ReferencedBy = new HashSet<Item>();
            public bool IsUsed;
            public string Status;
            public long Size = -1;
        }

        private readonly List<Item> items = new List<Item>();
        private readonly List<string> reportPaths = new List<string>();
        private ReportScanner scanner = new ReportScanner();

        private readonly DataGridView grid = NewGrid(true);
        private readonly DataGridView tableGrid = NewGrid(false);
        private readonly ToolStripComboBox cmbStatus = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 190 };
        private readonly ToolStripComboBox cmbType = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 150 };
        private readonly ToolStripTextBox txtSearch = new ToolStripTextBox { AutoSize = false, Width = 160 };
        private readonly ToolStripLabel lblReports = new ToolStripLabel();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ComboBox cmbKillTable = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly RadioButton rbRemove = new RadioButton { Text = "Remove the unused columns (Table.RemoveColumns)", AutoSize = true, Checked = true };
        private readonly RadioButton rbKeep = new RadioButton { Text = "Keep only the used columns (Table.SelectColumns)", AutoSize = true };
        private readonly TextBox txtM = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
        private readonly Label lblKill = new Label { AutoSize = true };
        private DataTable results;

        public UsageAnalyzerForm()
        {
            Text = "Usage Analyzer - unused measures and columns";
            Icon = FormMain.Singleton?.Icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 640);

            // ---- toolbar ----
            var btnAnalyze = new ToolStripButton("Analyze");
            var btnAddReport = new ToolStripButton("Add reports...") { ToolTipText = "Scan .pbix / .pbit files or a .pbip project so that fields used in visuals and filters count as used." };
            var btnAddFolder = new ToolStripButton("Add report folder...");
            var btnClear = new ToolStripButton("Clear reports");
            var btnCopy = new ToolStripButton("Copy") { ToolTipText = "Copy the list to the clipboard (paste into Excel)." };
            var btnDelete = new ToolStripButton("Delete selected measures / calculated columns");
            btnAnalyze.Click += (s, e) => Analyze();
            btnAddReport.Click += (s, e) => AddReports();
            btnAddFolder.Click += (s, e) => AddReportFolder();
            btnClear.Click += (s, e) => { reportPaths.Clear(); scanner = new ReportScanner(); Analyze(); };
            btnCopy.Click += (s, e) => CopyGrid();
            btnDelete.Click += (s, e) => DeleteSelected();

            cmbStatus.Items.AddRange(new object[] { "Status: all", "Status: unused", "Status: used by unused", "Status: unused + used by unused", "Status: used" });
            cmbStatus.SelectedIndex = 0;
            cmbType.Items.AddRange(new object[] { "Type: all", "Type: measures", "Type: columns", "Type: calculated columns" });
            cmbType.SelectedIndex = 0;
            cmbStatus.SelectedIndexChanged += (s, e) => ApplyFilter();
            cmbType.SelectedIndexChanged += (s, e) => ApplyFilter();
            txtSearch.TextChanged += (s, e) => ApplyFilter();

            var toolbar1 = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            toolbar1.Items.AddRange(new ToolStripItem[] { btnAnalyze, new ToolStripSeparator(), btnAddReport, btnAddFolder, btnClear, lblReports });
            var toolbar2 = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            toolbar2.Items.AddRange(new ToolStripItem[] { cmbStatus, cmbType, new ToolStripLabel("Search:"), txtSearch, new ToolStripSeparator(), btnDelete, btnCopy });

            // ---- tabs ----
            var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
            var pageResults = new TabPage("Measures and columns");
            var pageTables = new TabPage("Tables");
            var pageKill = new TabPage("Remove unused columns (M code)");
            pageResults.Controls.Add(grid);
            pageTables.Controls.Add(tableGrid);

            var btnCopyM = new Button { Text = "Copy M code", AutoSize = true };
            var btnApplyM = new Button { Text = "Apply to model", AutoSize = true };
            btnCopyM.Click += (s, e) => { if (txtM.TextLength > 0) { Clipboard.SetText(txtM.Text); status.Text = "M code copied. Paste it into the table's Advanced Editor in Power Query."; } };
            btnApplyM.Click += (s, e) => ApplyKillColumns();
            cmbKillTable.SelectedIndexChanged += (s, e) => ShowKillCode();
            rbRemove.CheckedChanged += (s, e) => ShowKillCode();
            var killTop = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4) };
            killTop.Controls.AddRange(new Control[] { new Label { Text = "Table:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, cmbKillTable, rbRemove, rbKeep, btnCopyM, btnApplyM });
            killTop.SetFlowBreak(btnApplyM, true);
            killTop.Controls.Add(lblKill);
            pageKill.Controls.Add(txtM);
            pageKill.Controls.Add(killTop);
            tabs.TabPages.AddRange(new[] { pageResults, pageTables, pageKill });

            grid.CellFormatting += Grid_CellFormatting;
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Goto(grid.Rows[e.RowIndex]); };

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(status);

            Controls.Add(tabs);
            Controls.Add(toolbar2);
            Controls.Add(toolbar1);
            Controls.Add(statusStrip);
            Shown += (s, e) => Analyze();
        }

        private static DataGridView NewGrid(bool multiSelect)
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = true,
                RowHeadersVisible = false,
                MultiSelect = multiSelect,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
            };
            g.DataError += (s, e) => e.ThrowException = false;
            return g;
        }

        // ------------------------------------------------------------------ reports

        private void AddReports()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Add Power BI reports that use this model",
                Filter = "Power BI reports (*.pbix;*.pbit;*.pbip;*.pbir)|*.pbix;*.pbit;*.pbip;*.pbir|All files (*.*)|*.*",
                Multiselect = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
            }
        }

        private void AddReportFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Select a .pbip project folder or a .Report folder" })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dialog.SelectedPath });
            }
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            var errors = new StringBuilder();
            foreach (var path in paths)
            {
                if (reportPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    scanner.Scan(path);
                    reportPaths.Add(path);
                }
                catch (Exception ex) { errors.AppendLine(Path.GetFileName(path) + ": " + ex.Message); }
                finally { Cursor = Cursors.Default; }
            }
            if (errors.Length > 0) MessageBox.Show(this, errors.ToString(), "Some reports could not be read", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Analyze();
        }

        // ------------------------------------------------------------------ analysis

        private void Analyze()
        {
            var model = UIController.Current?.Handler?.Model;
            items.Clear();
            if (model == null) { status.Text = "Open a model first."; return; }

            var lookup = new Dictionary<object, Item>();
            foreach (var m in model.AllMeasures)
                lookup[m] = new Item { Object = m, Table = m.Table.Name, Name = m.Name, Type = "Measure" };
            foreach (var c in model.AllColumns.Where(c => c.Type != ColumnType.RowNumber))
                lookup[c] = new Item
                {
                    Object = c, Table = c.Table.Name, Name = c.Name,
                    Type = c is CalculatedColumn ? "Calculated column" : c is CalculatedTableColumn ? "Calculated table column" : "Column"
                };
            items.AddRange(lookup.Values);

            bool anyReport = reportPaths.Count > 0;
            foreach (var item in items)
            {
                var column = item.Object as Column;
                IEnumerable<IDaxDependantObject> referencedBy = column != null ? column.ReferencedBy : ((Measure)item.Object).ReferencedBy;
                foreach (var reference in referencedBy)
                {
                    Item other;
                    if (ReferenceEquals(reference, item.Object)) continue;
                    if (lookup.TryGetValue(reference, out other)) item.ReferencedBy.Add(other);
                    else if (reference is Table) AddRoot(item, "Calculated table " + ((Table)reference).Name);
                    else if (reference is CalculationItem) AddRoot(item, "Calculation item");
                    else if (reference is TablePermission) AddRoot(item, "Row-level security");
                    else AddRoot(item, "Other DAX (" + reference.GetType().Name + ")");
                }

                if (anyReport && scanner.Fields.Contains(ReportScanner.Key(item.Table, item.Name))) AddRoot(item, "Report visuals / filters");

                if (column == null) continue;
                if (column.UsedInRelationships.Any()) AddRoot(item, "Relationship");
                if (column.UsedInVariations.Any()) AddRoot(item, "Date hierarchy (variation)");
                if (column.UsedInAlternateOfs.Any()) AddRoot(item, "Aggregation table mapping");
                foreach (var sorted in column.UsedInSortBy)
                {
                    Item other;
                    if (lookup.TryGetValue(sorted, out other) && other != item) item.ReferencedBy.Add(other);   // used only if the sorted column is used
                }
                foreach (var hierarchy in column.Table.Hierarchies)
                    foreach (var level in hierarchy.Levels.Where(l => l.Column == column))
                        if (anyReport && scanner.Levels.Contains(ReportScanner.Key(item.Table, hierarchy.Name, level.Name)))
                            AddRoot(item, "Report (hierarchy " + hierarchy.Name + ")");
            }

            // Anything referenced, directly or indirectly, by a used object is used as well.
            foreach (var item in items) item.IsUsed = item.Roots.Count > 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var item in items)
                    if (!item.IsUsed && item.ReferencedBy.Any(r => r.IsUsed)) { item.IsUsed = true; changed = true; }
            }
            foreach (var item in items)
                item.Status = item.IsUsed ? Used : item.ReferencedBy.Count > 0 ? UsedByUnused : item.Type == "Calculated table column" ? UnusedManual : Unused;

            BuildResults();
            BuildTables();
            BuildKillTables();
            UpdateSummary(null);
            lblReports.Text = anyReport
                ? "  " + reportPaths.Count + " report source(s) scanned"
                : "  No report scanned: fields used only in visuals will show as unused";
            lblReports.ForeColor = anyReport ? (ThemeManager.IsDark ? ThemeManager.Text : SystemColors.ControlText) : Color.OrangeRed;
            LoadSizes();
        }

        private static void AddRoot(Item item, string root)
        {
            if (!item.Roots.Contains(root)) item.Roots.Add(root);
        }

        private static string WhereUsed(Item item)
        {
            var parts = new List<string>(item.Roots);
            foreach (var group in item.ReferencedBy.GroupBy(r => r.Type == "Measure" ? "Measures" : "Columns"))
            {
                var names = group.Select(r => r.Name).OrderBy(n => n).ToList();
                parts.Add(group.Key + ": " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? " (+" + (names.Count - 3) + ")" : ""));
            }
            return string.Join("; ", parts);
        }

        private void BuildResults()
        {
            results = new DataTable();
            results.Columns.Add("Id", typeof(int));
            results.Columns.Add("Table", typeof(string));
            results.Columns.Add("Name", typeof(string));
            results.Columns.Add("Type", typeof(string));
            results.Columns.Add("Status", typeof(string));
            results.Columns.Add("Where used", typeof(string));
            results.Columns.Add("Size (bytes)", typeof(long));
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                results.Rows.Add(i, item.Table, item.Name, item.Type, item.Status, WhereUsed(item), DBNull.Value);
            }
            results.DefaultView.Sort = "[Table] ASC, [Name] ASC";
            grid.DataSource = results.DefaultView;
            grid.Columns["Id"].Visible = false;
            grid.Columns["Where used"].FillWeight = 250;
            grid.Columns["Size (bytes)"].DefaultCellStyle.Format = "N0";
            grid.Columns["Size (bytes)"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            ThemeManager.ApplyGrid(grid);
            ApplyFilter();
        }

        private void BuildTables()
        {
            var tables = new DataTable();
            tables.Columns.Add("Table", typeof(string));
            tables.Columns.Add("Columns", typeof(int));
            tables.Columns.Add("Unused columns", typeof(int));
            tables.Columns.Add("Measures", typeof(int));
            tables.Columns.Add("Unused measures", typeof(int));
            tables.Columns.Add("% used", typeof(double));
            tables.Columns.Add("Status", typeof(string));
            foreach (var group in items.GroupBy(i => i.Table).OrderBy(g => g.Key))
            {
                int columns = group.Count(i => i.Type != "Measure"), measures = group.Count(i => i.Type == "Measure");
                int unusedColumns = group.Count(i => i.Type != "Measure" && !i.IsUsed), unusedMeasures = group.Count(i => i.Type == "Measure" && !i.IsUsed);
                int total = columns + measures, unused = unusedColumns + unusedMeasures;
                tables.Rows.Add(group.Key, columns, unusedColumns, measures, unusedMeasures, total == 0 ? 0d : 100d * (total - unused) / total,
                    unused == total ? "Unused" : unused == 0 ? "Fully used" : "Partly used");
            }
            tableGrid.DataSource = tables;
            tableGrid.Columns["% used"].DefaultCellStyle.Format = "N0";
            ThemeManager.ApplyGrid(tableGrid);
        }

        private void UpdateSummary(string extra)
        {
            int unused = items.Count(i => i.Status == Unused || i.Status == UnusedManual), byUnused = items.Count(i => i.Status == UsedByUnused);
            status.Text = $"{items.Count:N0} measures and columns: {items.Count - unused - byUnused:N0} used, {unused:N0} unused, {byUnused:N0} used by unused."
                + (extra ?? "") + " Double-click a row to locate it in the model.";
        }

        /// <summary>When connected, adds the in-memory size of each column so the saving is visible.</summary>
        private async void LoadSizes()
        {
            if (!AdomdHelper.IsConnected) return;
            try
            {
                var stats = await Task.Run(() => VertiPaqForm.Analyze());
                if (IsDisposed || results == null) return;
                var sizes = new Dictionary<string, long>();
                foreach (DataRow row in stats.Item2.Rows) sizes[ReportScanner.Key((string)row["Table"], (string)row["Column"])] = (long)row["Total size"];
                long total = 0, unusedSize = 0;
                foreach (DataRow row in results.Rows)
                {
                    var item = items[(int)row["Id"]];
                    long size;
                    if (item.Type == "Measure" || !sizes.TryGetValue(ReportScanner.Key(item.Table, item.Name), out size)) continue;
                    item.Size = size;
                    row["Size (bytes)"] = size;
                    total += size;
                    if (!item.IsUsed) unusedSize += size;
                }
                if (total > 0) UpdateSummary($" Unused columns hold {unusedSize / 1048576d:N1} MB of {total / 1048576d:N1} MB ({100d * unusedSize / total:N0}%).");
            }
            catch { /* sizes are optional */ }
        }

        // ------------------------------------------------------------------ list view

        private void ApplyFilter()
        {
            if (results == null) return;
            var filters = new List<string>();
            switch (cmbStatus.SelectedIndex)
            {
                case 1: filters.Add("Status LIKE 'Unused*'"); break;
                case 2: filters.Add("Status = '" + UsedByUnused + "'"); break;
                case 3: filters.Add("Status <> '" + Used + "'"); break;
                case 4: filters.Add("Status = '" + Used + "'"); break;
            }
            switch (cmbType.SelectedIndex)
            {
                case 1: filters.Add("Type = 'Measure'"); break;
                case 2: filters.Add("Type <> 'Measure'"); break;
                case 3: filters.Add("Type = 'Calculated column'"); break;
            }
            var search = txtSearch.Text.Trim();
            if (search.Length > 0)
            {
                var escaped = new StringBuilder();
                foreach (var ch in search)
                {
                    if (ch == '\'') escaped.Append("''");
                    else if (ch == '[' || ch == ']' || ch == '*' || ch == '%') escaped.Append('[').Append(ch).Append(']');
                    else escaped.Append(ch);
                }
                filters.Add($"(Name LIKE '*{escaped}*' OR Table LIKE '*{escaped}*')");
            }
            try { results.DefaultView.RowFilter = string.Join(" AND ", filters); } catch (Exception) { }
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Status") return;
            var value = e.Value as string;
            bool dark = ThemeManager.IsDark;
            if (value == Used) e.CellStyle.ForeColor = dark ? Color.FromArgb(110, 200, 120) : Color.ForestGreen;
            else if (value == UsedByUnused) e.CellStyle.ForeColor = dark ? Color.FromArgb(230, 190, 80) : Color.DarkGoldenrod;
            else e.CellStyle.ForeColor = dark ? Color.FromArgb(255, 120, 120) : Color.Firebrick;
        }

        private Item ItemOf(DataGridViewRow row)
        {
            var view = row.DataBoundItem as DataRowView;
            return view == null ? null : items[(int)view["Id"]];
        }

        private void Goto(DataGridViewRow row)
        {
            var item = ItemOf(row);
            if (item == null) return;
            try { UIController.Current.Goto((ITabularNamedObject)item.Object); } catch { }
        }

        private void CopyGrid()
        {
            if (grid.Rows.Count == 0) return;
            grid.SelectAll();
            var data = grid.GetClipboardContent();
            if (data != null) Clipboard.SetDataObject(data);
            grid.ClearSelection();
            status.Text = "List copied to the clipboard (paste into Excel).";
        }

        // ------------------------------------------------------------------ delete DAX objects

        private void DeleteSelected()
        {
            var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(ItemOf).Where(i => i != null).ToList();
            var targets = selected.Where(i => !i.IsUsed && (i.Type == "Measure" || i.Type == "Calculated column")).ToList();
            int skipped = selected.Count - targets.Count;
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Select unused measures or calculated columns first.\n\nUsed objects are never deleted, and data columns are removed on the "
                    + "\"Remove unused columns (M code)\" tab.", "Nothing to delete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var warning = reportPaths.Count == 0 ? "\n\nNo report has been scanned, so anything used only in report visuals looks unused. Add your reports first unless you are sure." : "";
            if (MessageBox.Show(this, $"Delete {targets.Count} unused measure(s) / calculated column(s)?" + (skipped > 0 ? $" ({skipped} selected row(s) are skipped.)" : "")
                + "\n\nTheir expressions are backed up first, and Ctrl+Z in the main window undoes the deletion." + warning,
                "Delete unused objects", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            string backup;
            try { backup = Backup(targets); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The backup could not be written, so nothing was deleted.\n\n" + ex.Message, "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var handler = UIController.Current.Handler;
            handler.BeginUpdate("delete unused objects");
            try
            {
                foreach (var item in targets) ((TabularNamedObject)item.Object).Delete();
            }
            finally { handler.EndUpdate(); }
            Analyze();
            status.Text = $"{targets.Count} object(s) deleted. Backup: {backup}";
        }

        private static string Backup(List<Item> targets)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabularEditorFork", "backups");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "deleted_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            var entries = targets.Select(i => new
            {
                i.Table,
                i.Name,
                i.Type,
                Expression = (i.Object as Measure)?.Expression ?? (i.Object as CalculatedColumn)?.Expression,
                FormatString = (i.Object as Measure)?.FormatString ?? (i.Object as Column)?.FormatString,
                DisplayFolder = (i.Object as Measure)?.DisplayFolder ?? (i.Object as Column)?.DisplayFolder,
                Description = (i.Object as Measure)?.Description ?? (i.Object as Column)?.Description
            });
            File.WriteAllText(file, JsonConvert.SerializeObject(entries, Formatting.Indented));
            return file;
        }

        // ------------------------------------------------------------------ remove unused data columns

        private List<DataColumn> KillColumns(Table table) =>
            items.Where(i => i.Status == Unused && i.Type == "Column" && i.Object is DataColumn && ((Column)i.Object).Table == table)
                 .Select(i => (DataColumn)i.Object).ToList();

        private static bool IsPowerQueryTable(Table table) =>
            table.Partitions.Count > 0 && table.Partitions.All(p => p is MPartition);

        private void BuildKillTables()
        {
            var model = UIController.Current.Handler.Model;
            var previous = cmbKillTable.SelectedItem as string;
            cmbKillTable.Items.Clear();
            foreach (var table in model.Tables.Where(t => IsPowerQueryTable(t) && KillColumns(t).Count > 0).OrderBy(t => t.Name))
                cmbKillTable.Items.Add(table.Name);
            if (previous != null && cmbKillTable.Items.Contains(previous)) cmbKillTable.SelectedItem = previous;
            else if (cmbKillTable.Items.Count > 0) cmbKillTable.SelectedIndex = 0;
            ShowKillCode();
        }

        private Table KillTable()
        {
            var name = cmbKillTable.SelectedItem as string;
            return name == null ? null : UIController.Current.Handler.Model.Tables.FirstOrDefault(t => t.Name == name);
        }

        private void ShowKillCode()
        {
            var table = KillTable();
            if (table == null)
            {
                txtM.Text = "";
                lblKill.Text = "No Power Query table has unused data columns. (Columns marked \"used by unused\" become unused once the objects that reference them are deleted.)";
                return;
            }
            var columns = KillColumns(table);
            lblKill.Text = $"{columns.Count} unused column(s): " + string.Join(", ", columns.Select(c => c.Name).Take(12)) + (columns.Count > 12 ? ", ..." : "")
                + (table.Partitions.Count > 1 ? $"   ({table.Partitions.Count} partitions: the code shown is for the first, Apply updates all.)" : "");
            txtM.Text = BuildM(table.Partitions.First().Expression, table, columns).Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
        private static string Source(DataColumn column) => string.IsNullOrEmpty(column.SourceColumn) ? column.Name : column.SourceColumn;

        private string BuildM(string expression, Table table, List<DataColumn> remove)
        {
            string call;
            if (rbRemove.Checked)
                call = "Table.RemoveColumns(@PREV, {" + string.Join(", ", remove.Select(c => Quote(Source(c)))) + "}, MissingField.Ignore)";
            else
            {
                var keep = table.Columns.OfType<DataColumn>().Where(c => !remove.Contains(c)).Select(c => Quote(Source(c)));
                call = "Table.SelectColumns(@PREV, {" + string.Join(", ", keep) + "}, MissingField.Ignore)";
            }
            return AppendStep(expression ?? "", "Removed unused columns", call);
        }

        /// <summary>
        /// Adds a final step to an M query. A plain "let ... in LastStep" query gets the step appended;
        /// anything else is wrapped in an outer let so the original expression is left untouched.
        /// </summary>
        internal static string AppendStep(string expression, string stepName, string call)
        {
            var name = stepName;
            for (int n = 2; expression.IndexOf("#\"" + name + "\"", StringComparison.OrdinalIgnoreCase) >= 0; n++) name = stepName + " " + n;
            var step = "#\"" + name + "\"";
            var text = expression.Replace("\r\n", "\n").TrimEnd();

            var match = Regex.Match(text, @"\bin\s+(#""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_.]*)$");
            if (match.Success && Regex.IsMatch(text, @"^\s*let\b"))
            {
                var head = text.Substring(0, match.Index).TrimEnd();
                var lastLine = head.Substring(head.LastIndexOf('\n') + 1);
                if (!lastLine.Contains("//") && !head.EndsWith(",") && !head.EndsWith("*/"))
                    return head + ",\n    " + step + " = " + call.Replace("@PREV", match.Groups[1].Value) + "\nin\n    " + step;
            }

            var indented = string.Join("\n", text.Split('\n').Select(line => "        " + line));
            return "let\n    Source = (\n" + indented + "\n    ),\n    " + step + " = " + call.Replace("@PREV", "Source") + "\nin\n    " + step;
        }

        private void ApplyKillColumns()
        {
            var table = KillTable();
            if (table == null) return;
            var columns = KillColumns(table);
            if (columns.Count == 0) return;
            var warning = reportPaths.Count == 0 ? "\n\nNo report has been scanned, so columns used only in report visuals look unused." : "";
            if (MessageBox.Show(this, $"Remove {columns.Count} unused column(s) from '{table.Name}'?\n\nThis adds a step to the table's Power Query expression and deletes the "
                + "columns from the model. Ctrl+Z in the main window undoes it. Keep a backup of the file, and refresh the table after saving." + warning,
                "Remove unused columns", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            var handler = UIController.Current.Handler;
            handler.BeginUpdate("remove unused columns");
            try
            {
                foreach (var partition in table.Partitions.ToList()) partition.Expression = BuildM(partition.Expression, table, columns);
                foreach (var column in columns) column.Delete();
            }
            finally { handler.EndUpdate(); }
            Analyze();
            status.Text = $"{columns.Count} column(s) removed from '{table.Name}'.";
        }
    }
}
