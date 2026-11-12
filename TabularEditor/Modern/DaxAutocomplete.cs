using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FastColoredTextBoxNS;
using TabularEditor.TOMWrapper;
using TabularEditor.UI;
using TabularEditor.UIServices;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Autocomplete for DAX editors: functions, tables, Table[Column] references and [Measure] references.
    /// </summary>
    internal static class DaxAutocomplete
    {
        public static AutocompleteMenu Attach(FastColoredTextBox editor)
        {
            var menu = new AutocompleteMenu(editor);
            menu.AppearInterval = 150;
            menu.MinFragmentLength = 1;
            menu.SearchPattern = @"[\w\['\.]";
            menu.AllowTabKey = true;
            menu.Items.MaximumSize = new Size(380, 300);
            menu.Items.Width = 380;
            menu.Items.SetAutocompleteItems(new DaxItems());
            if (ThemeManager.IsDark)
            {
                menu.BackColor = ThemeManager.BackAlt;
                menu.ForeColor = ThemeManager.Text;
                menu.SelectedColor = ThemeManager.Selection;
            }

            editor.LeftBracket = '(';
            editor.RightBracket = ')';
            editor.AutoCompleteBrackets = true;
            return menu;
        }

        private class DaxItems : IEnumerable<AutocompleteItem>
        {
            public IEnumerator<AutocompleteItem> GetEnumerator()
            {
                var model = UIController.Current?.Handler?.Model;
                if (model != null)
                {
                    foreach (var table in model.Tables.ToList())
                    {
                        yield return new TableItem(table);
                        foreach (var column in table.Columns.ToList()) yield return new ColumnItem(table, column);
                    }
                    foreach (var measure in model.AllMeasures.ToList()) yield return new MeasureItem(measure);
                }
                foreach (var name in DaxFunctionNames.All) yield return new FunctionItem(name);
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static bool StartsWith(string text, string prefix) =>
            text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private class FunctionItem : AutocompleteItem
        {
            public FunctionItem(string name) : base(name) { }

            public override CompareResult Compare(string fragmentText)
            {
                if (fragmentText.IndexOf('[') >= 0 || fragmentText.IndexOf('\'') >= 0) return CompareResult.Hidden;
                if (StartsWith(Text, fragmentText) && !Text.Equals(fragmentText, StringComparison.OrdinalIgnoreCase))
                    return CompareResult.Visible;
                return CompareResult.Hidden;
            }
        }

        private class TableItem : AutocompleteItem
        {
            private readonly string name;
            public TableItem(Table table) : base(table.DaxTableName)
            {
                name = table.Name;
                MenuText = table.Name + "  (table)";
            }

            public override CompareResult Compare(string fragmentText)
            {
                if (fragmentText.IndexOf('[') >= 0) return CompareResult.Hidden;
                var fragment = fragmentText.TrimStart('\'');
                if (fragment.Length == 0) return CompareResult.Visible;
                return StartsWith(name, fragment) ? CompareResult.Visible : CompareResult.Hidden;
            }
        }

        private class ColumnItem : AutocompleteItem
        {
            private readonly string tableRef;
            private readonly string tableName;
            private readonly string columnName;
            private string replacement;

            public ColumnItem(Table table, Column column) : base(column.DaxObjectFullName)
            {
                tableRef = table.DaxTableName;
                tableName = table.Name;
                columnName = column.Name;
                MenuText = table.Name + "[" + column.Name + "]";
                replacement = Text;
            }

            public override CompareResult Compare(string fragmentText)
            {
                var idx = fragmentText.LastIndexOf('[');
                if (idx <= 0) return CompareResult.Hidden;
                var prefix = fragmentText.Substring(0, idx);
                var columnPart = fragmentText.Substring(idx + 1);

                // For quoted table names containing spaces, only the tail of the name is part of the fragment.
                var tableMatches = tableRef.EndsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || tableName.Equals(prefix.Trim('\''), StringComparison.OrdinalIgnoreCase);
                if (!tableMatches || !StartsWith(columnName, columnPart)) return CompareResult.Hidden;

                replacement = prefix + "[" + columnName.Replace("]", "]]") + "]";
                return CompareResult.Visible;
            }

            public override string GetTextForReplace() => replacement;
        }

        private class MeasureItem : AutocompleteItem
        {
            private readonly string name;
            public MeasureItem(Measure measure) : base("[" + measure.Name.Replace("]", "]]") + "]")
            {
                name = measure.Name;
                MenuText = "[" + measure.Name + "]  (measure)";
            }

            public override CompareResult Compare(string fragmentText)
            {
                if (fragmentText.StartsWith("["))
                    return StartsWith(name, fragmentText.Substring(1)) ? CompareResult.Visible : CompareResult.Hidden;
                if (fragmentText.IndexOf('[') >= 0 || fragmentText.IndexOf('\'') >= 0) return CompareResult.Hidden;
                return StartsWith(name, fragmentText) ? CompareResult.Visible : CompareResult.Hidden;
            }
        }
    }
}
