using System;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using FastColoredTextBoxNS;
using TabularEditor.TextServices;
using TabularEditor.UIServices;

namespace TabularEditor.Modern
{
    /// <summary>
    /// A DAX query window: write an EVALUATE statement, press F5 and see the result in a grid.
    /// </summary>
    internal class DaxQueryForm : Form
    {
        private readonly FastColoredTextBox editor = new FastColoredTextBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly ToolStripButton btnRun = new ToolStripButton("Run (F5)");
        private readonly ToolStripButton btnCopy = new ToolStripButton("Copy results");
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Timer highlightTimer = new Timer { Interval = 400 };
        private bool running;

        public DaxQueryForm(string query = null, bool runImmediately = false)
        {
            Text = "DAX Query";
            Icon = FormMain.Singleton?.Icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 700);
            KeyPreview = true;

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            toolbar.Items.Add(btnRun);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(btnCopy);
            var btnSortColumns = new ToolStripButton("Sort columns A-Z");
            btnSortColumns.ToolTipText = "Rearrange the result columns alphabetically by name. You can also drag column headers.";
            btnSortColumns.Click += (s, e) => SortColumnsByName();
            toolbar.Items.Add(btnSortColumns);
            btnRun.Click += (s, e) => Run();
            btnCopy.Click += (s, e) => CopyResults();

            editor.Dock = DockStyle.Fill;
            editor.Font = new Font("Consolas", 10.5f);
            editor.BorderStyle = BorderStyle.None;
            editor.Text = query ?? "EVALUATE\n    ";
            editor.TextChanged += (s, e) => { highlightTimer.Stop(); highlightTimer.Start(); };
            highlightTimer.Tick += (s, e) => { highlightTimer.Stop(); Highlight(); };
            DaxAutocomplete.Attach(editor);

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            grid.DataError += (s, e) => e.ThrowException = false;

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(editor);
            split.Panel2.Controls.Add(grid);

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(status);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            Shown += (s, e) =>
            {
                split.SplitterDistance = Math.Max(100, split.Height / 3);
                Highlight();
                editor.Focus();
                if (runImmediately) Run();
            };
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { e.Handled = true; Run(); }
            };
        }

        private void Highlight()
        {
            try { ExpressionParser.SyntaxHighlight(editor); } catch { }
        }

        private async void Run()
        {
            if (running || !AdomdHelper.EnsureConnected(this)) return;
            var query = editor.SelectionLength > 0 ? editor.SelectedText : editor.Text;
            if (string.IsNullOrWhiteSpace(query)) return;

            running = true;
            btnRun.Enabled = false;
            status.Text = "Running...";
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await Task.Run(() => AdomdHelper.Query(query));
                if (IsDisposed) return;
                grid.DataSource = null;
                grid.DataSource = result;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    column.Width = Math.Min(300, Math.Max(80, column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true) + 20));
                    if (IsNumeric(column.ValueType)) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                }
                ThemeManager.ApplyGrid(grid);
                status.Text = $"{result.Rows.Count:N0} rows, {result.Columns.Count:N0} columns in {timer.Elapsed.TotalSeconds:N2} s";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                grid.DataSource = null;
                status.Text = "Query failed";
                MessageBox.Show(this, ex.Message, "Query failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                running = false;
                if (!IsDisposed) btnRun.Enabled = true;
            }
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal)
                || type == typeof(float) || type == typeof(short) || type == typeof(ulong) || type == typeof(uint);
        }

        private void SortColumnsByName()
        {
            var ordered = new System.Collections.Generic.List<DataGridViewColumn>();
            foreach (DataGridViewColumn column in grid.Columns) ordered.Add(column);
            ordered.Sort((a, b) => string.Compare(a.HeaderText, b.HeaderText, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        }

        private void CopyResults()
        {
            if (grid.Rows.Count == 0) return;
            grid.SelectAll();
            var data = grid.GetClipboardContent();
            if (data != null) Clipboard.SetDataObject(data);
            grid.ClearSelection();
            status.Text = "Results copied to the clipboard (paste into Excel)";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) highlightTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
