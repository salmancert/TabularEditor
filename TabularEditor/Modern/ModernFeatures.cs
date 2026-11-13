using System;
using System.Linq;
using System.Windows.Forms;
using FastColoredTextBoxNS;
using TabularEditor.TOMWrapper;
using TabularEditor.UI;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Wires the added features (DAX autocomplete, DAX query window, data preview, table analyzer,
    /// diagram and VertiPaq analyzer) into the main window.
    /// </summary>
    internal static class ModernFeatures
    {
        public static void Initialize(Form owner, ToolStripMenuItem toolsMenu, FastColoredTextBox expressionEditor)
        {
            try { DaxAutocomplete.Attach(expressionEditor); } catch { }

            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            Add(toolsMenu, "New DAX query", Keys.Control | Keys.Shift | Keys.Q, () =>
            {
                new DaxQueryForm().Show(owner);
            });
            Add(toolsMenu, "Preview data of selected table", Keys.Control | Keys.Shift | Keys.P, () =>
            {
                var table = SelectedTable();
                if (table == null) { Info(owner, "Select a table (or one of its columns or measures) in the model tree first."); return; }
                if (AdomdHelper.EnsureConnected(owner))
                    new DaxQueryForm("EVALUATE\n    TOPN(1000, " + table.DaxObjectFullName + ")", true).Show(owner);
            });
            Add(toolsMenu, "Usage analyzer (unused measures and columns)", Keys.Control | Keys.Shift | Keys.K, () =>
            {
                if (HasModel(owner)) new UsageAnalyzerForm().Show(owner);
            });
            Add(toolsMenu, "Table analyzer (blank columns)", Keys.Control | Keys.Shift | Keys.B, () =>
            {
                if (!HasModel(owner)) return;
                new TableAnalyzerForm(SelectedTable()).Show(owner);
            });
            Add(toolsMenu, "VertiPaq analyzer", Keys.None, () =>
            {
                if (AdomdHelper.EnsureConnected(owner)) new VertiPaqForm().Show(owner);
            });
            Add(toolsMenu, "Model diagram", Keys.Control | Keys.Shift | Keys.D, () =>
            {
                if (HasModel(owner)) new DiagramForm().Show(owner);
            });
        }

        private static void Add(ToolStripMenuItem menu, string text, Keys shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text);
            if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
            item.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            menu.DropDownItems.Add(item);
        }

        private static void Info(IWin32Window owner, string message) =>
            MessageBox.Show(owner, message, "Tabular Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private static bool HasModel(IWin32Window owner)
        {
            if (UIController.Current?.Handler?.Model != null) return true;
            Info(owner, "Open a model first.");
            return false;
        }

        private static Table SelectedTable()
        {
            var selection = UIController.Current?.Selection;
            if (selection == null) return null;
            try
            {
                return selection.Tables.FirstOrDefault()
                    ?? selection.Columns.FirstOrDefault()?.Table
                    ?? selection.Measures.FirstOrDefault()?.Table;
            }
            catch { return null; }
        }
    }
}
