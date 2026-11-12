using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FastColoredTextBoxNS;

namespace TabularEditor.UIServices
{
    /// <summary>
    /// Applies a dark colour scheme to the WinForms UI. The light theme is simply the
    /// stock WinForms look, so nothing is touched unless the dark theme is enabled.
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; }

        public static readonly Color Back = Color.FromArgb(30, 30, 30);
        public static readonly Color BackAlt = Color.FromArgb(37, 37, 38);
        public static readonly Color Surface = Color.FromArgb(45, 45, 48);
        public static readonly Color Border = Color.FromArgb(63, 63, 70);
        public static readonly Color Text = Color.FromArgb(220, 220, 220);
        public static readonly Color TextDim = Color.FromArgb(140, 140, 140);
        public static readonly Color Accent = Color.FromArgb(0, 122, 204);
        public static readonly Color Selection = Color.FromArgb(9, 71, 113);

        public static readonly Brush InactiveSelectionBrush = new SolidBrush(Color.FromArgb(63, 63, 70));

        /// <summary>Default text colour for tree nodes and other custom-drawn text.</summary>
        public static Color TextColor => IsDark ? Text : SystemColors.ControlText;
        public static Color DimTextColor => IsDark ? TextDim : Color.Gray;
        public static Color TranslatedTextColor => IsDark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(0, 0, 161);
        public static Color TranslatedDimTextColor => IsDark ? Color.FromArgb(90, 120, 190) : Color.FromArgb(111, 111, 191);

        private static readonly HashSet<IntPtr> themedForms = new HashSet<IntPtr>();
        private static bool hooked;

        /// <summary>
        /// Call once from the main form. Adds the "Dark theme" toggle to the given menu and
        /// applies the theme when the saved preference says so.
        /// </summary>
        public static void Initialize(ToolStripMenuItem viewMenu)
        {
            if (viewMenu != null)
            {
                var item = new ToolStripMenuItem("Dark theme") { CheckOnClick = true, Checked = Preferences.Current.View_DarkTheme };
                item.Click += (s, e) =>
                {
                    Preferences.Current.View_DarkTheme = item.Checked;
                    Preferences.Current.Save();
                    if (item.Checked) Enable();
                    else MessageBox.Show("The light theme will be restored the next time the application starts.",
                        "Theme", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                viewMenu.DropDownItems.Add(item);
            }
            if (Preferences.Current.View_DarkTheme) Enable();
        }

        private static void Enable()
        {
            IsDark = true;
            // Syntax colours that stay readable on a dark background.
            TabularEditor.TextServices.ExpressionParser.PlainStyle = new TextStyle(new SolidBrush(Text), null, FontStyle.Regular);
            TabularEditor.TextServices.ExpressionParser.KeywordStyle = new TextStyle(new SolidBrush(Color.FromArgb(86, 156, 214)), null, FontStyle.Regular);
            TabularEditor.TextServices.ExpressionParser.CommentStyle = new TextStyle(new SolidBrush(Color.FromArgb(106, 153, 85)), null, FontStyle.Regular);
            TabularEditor.TextServices.ExpressionParser.LiteralStyle = new TextStyle(new SolidBrush(Color.FromArgb(206, 145, 120)), null, FontStyle.Regular);
            TabularEditor.TextServices.ExpressionParser.ParensStyle = new TextStyle(new SolidBrush(Color.FromArgb(180, 180, 180)), null, FontStyle.Regular);
            themedForms.Clear();
            ThemeOpenForms();
            if (!hooked)
            {
                // Dialogs are created all over the code base; theme each one the first time
                // the message loop goes idle after it has been shown.
                Application.Idle += (s, e) => ThemeOpenForms();
                hooked = true;
            }
        }

        private static void ThemeOpenForms()
        {
            if (!IsDark) return;
            try
            {
                for (int i = 0; i < Application.OpenForms.Count; i++)
                {
                    var form = Application.OpenForms[i];
                    if (form == null || form.IsDisposed || !form.IsHandleCreated) continue;
                    if (themedForms.Add(form.Handle)) Apply(form);
                }
            }
            catch (InvalidOperationException) { /* form collection changed while iterating */ }
        }

        /// <summary>Applies the dark theme to a control and everything inside it.</summary>
        public static void Apply(Control root)
        {
            if (!IsDark || root == null) return;
            root.SuspendLayout();
            try { ApplyRecursive(root); }
            finally { root.ResumeLayout(); }
            if (root is Form f) SetDarkTitleBar(f);
            root.Invalidate(true);
        }

        private static void ApplyRecursive(Control c)
        {
            switch (c)
            {
                case FastColoredTextBox fctb:
                    fctb.BackColor = Back;
                    fctb.ForeColor = Text;
                    fctb.IndentBackColor = Back;
                    fctb.LineNumberColor = TextDim;
                    fctb.ServiceLinesColor = Border;
                    fctb.SelectionColor = Color.FromArgb(90, 51, 153, 255);
                    fctb.CaretColor = Text;
                    fctb.FoldingIndicatorColor = TextDim;
                    fctb.DisabledColor = Color.FromArgb(90, 60, 60, 60);
                    break;
                case PropertyGrid pg:
                    pg.BackColor = Surface;
                    pg.ViewBackColor = Back;
                    pg.ViewForeColor = Text;
                    pg.ViewBorderColor = Border;
                    pg.LineColor = Surface;
                    pg.CategoryForeColor = Text;
                    pg.CategorySplitterColor = Border;
                    pg.HelpBackColor = BackAlt;
                    pg.HelpForeColor = Text;
                    pg.HelpBorderColor = Border;
                    pg.CommandsBackColor = BackAlt;
                    pg.CommandsForeColor = Text;
                    pg.DisabledItemForeColor = TextDim;
                    pg.SelectedItemWithFocusBackColor = Selection;
                    pg.SelectedItemWithFocusForeColor = Color.White;
                    foreach (Control inner in pg.Controls)
                    {
                        if (inner is ToolStrip strip)
                        {
                            strip.Renderer = DarkRenderer.Instance;
                            strip.BackColor = Surface;
                            ThemeItems(strip.Items);
                        }
                    }
                    break;
                case ToolStrip ts:
                    ts.Renderer = DarkRenderer.Instance;
                    ts.BackColor = Surface;
                    ts.ForeColor = Text;
                    ThemeItems(ts.Items);
                    break;
                case TextBoxBase tb:
                    tb.BackColor = Back;
                    tb.ForeColor = Text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Back;
                    cb.ForeColor = Text;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = Surface;
                    b.ForeColor = Text;
                    break;
                case CheckBox _:
                case RadioButton _:
                case Label _:
                case GroupBox _:
                    c.ForeColor = Text;
                    if (c.BackColor != Color.Transparent) c.BackColor = Surface;
                    break;
                case ListView lv:
                    lv.BackColor = Back;
                    lv.ForeColor = Text;
                    break;
                case TreeView tv:
                    tv.BackColor = Back;
                    tv.ForeColor = Text;
                    tv.LineColor = Border;
                    break;
                case ListBox lb:
                    lb.BackColor = Back;
                    lb.ForeColor = Text;
                    break;
                case DataGridView grid:
                    ApplyGrid(grid);
                    break;
                case TabControl tabs:
                    // Flat, owner-drawn tabs: the native tab frame cannot be recoloured.
                    tabs.Appearance = TabAppearance.FlatButtons;
                    tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                    tabs.BackColor = Surface;
                    tabs.DrawItem -= DrawTab;
                    tabs.DrawItem += DrawTab;
                    break;
                case Aga.Controls.Tree.TreeViewAdv tva:
                    tva.BackColor = Back;
                    tva.ForeColor = Text;
                    tva.LineColor = Border;
                    tva.BorderStyle = BorderStyle.None;
                    break;
                default:
                    c.BackColor = Surface;
                    c.ForeColor = Text;
                    break;
            }

            if (c.ContextMenuStrip != null) ApplyRecursive(c.ContextMenuStrip);

            // Composite controls manage their own children.
            if (c is PropertyGrid || c is FastColoredTextBox || c is ToolStrip || c is DataGridView) return;
            foreach (Control child in c.Controls) ApplyRecursive(child);
        }

        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            var tabs = (TabControl)sender;
            if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;
            bool selected = tabs.SelectedIndex == e.Index;
            var bounds = tabs.GetTabRect(e.Index);
            using (var brush = new SolidBrush(selected ? Accent : Surface)) e.Graphics.FillRectangle(brush, bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, bounds, selected ? Color.White : Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public static void ApplyGrid(DataGridView grid)
        {
            if (!IsDark) return;
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Back;
            grid.GridColor = Border;
            grid.BorderStyle = BorderStyle.None;
            grid.DefaultCellStyle.BackColor = Back;
            grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Selection;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = BackAlt;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
            grid.RowHeadersDefaultCellStyle.BackColor = Surface;
            grid.RowHeadersDefaultCellStyle.ForeColor = Text;
        }

        private static void ThemeItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = Text;
                if (item is ToolStripTextBox t) { t.BackColor = Back; t.BorderStyle = BorderStyle.FixedSingle; }
                else if (item is ToolStripComboBox cb) { cb.BackColor = Back; cb.FlatStyle = FlatStyle.Flat; }
                if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
                {
                    dd.DropDown.Renderer = DarkRenderer.Instance;
                    dd.DropDown.BackColor = BackAlt;
                    dd.DropDown.ForeColor = Text;
                    ThemeItems(dd.DropDownItems);
                    // Menus that are filled on demand (recent files, macros, context menus).
                    dd.DropDownOpening -= DropDownOpening;
                    dd.DropDownOpening += DropDownOpening;
                }
            }
        }

        private static void DropDownOpening(object sender, EventArgs e)
        {
            if (IsDark && sender is ToolStripDropDownItem dd) ThemeItems(dd.DropDownItems);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void SetDarkTitleBar(Form form)
        {
            try
            {
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004+), 19 on older Windows 10 builds.
                if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
            }
            catch { /* not supported on this Windows version */ }
        }

        private class DarkRenderer : ToolStripProfessionalRenderer
        {
            public static readonly DarkRenderer Instance = new DarkRenderer();
            private DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : TextDim;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item != null && !e.Item.Enabled ? TextDim : Text;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
            }
        }

        private class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Surface;
            public override Color ToolStripGradientMiddle => Surface;
            public override Color ToolStripGradientEnd => Surface;
            public override Color ToolStripBorder => Surface;
            public override Color ToolStripDropDownBackground => BackAlt;
            public override Color ToolStripContentPanelGradientBegin => Surface;
            public override Color ToolStripContentPanelGradientEnd => Surface;
            public override Color ToolStripPanelGradientBegin => Surface;
            public override Color ToolStripPanelGradientEnd => Surface;
            public override Color MenuStripGradientBegin => Surface;
            public override Color MenuStripGradientEnd => Surface;
            public override Color StatusStripGradientBegin => Accent;
            public override Color StatusStripGradientEnd => Accent;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Border;
            public override Color MenuItemSelected => Border;
            public override Color MenuItemSelectedGradientBegin => Border;
            public override Color MenuItemSelectedGradientEnd => Border;
            public override Color MenuItemPressedGradientBegin => BackAlt;
            public override Color MenuItemPressedGradientMiddle => BackAlt;
            public override Color MenuItemPressedGradientEnd => BackAlt;
            public override Color ImageMarginGradientBegin => BackAlt;
            public override Color ImageMarginGradientMiddle => BackAlt;
            public override Color ImageMarginGradientEnd => BackAlt;
            public override Color ButtonSelectedHighlight => Border;
            public override Color ButtonSelectedBorder => Accent;
            public override Color ButtonSelectedGradientBegin => Border;
            public override Color ButtonSelectedGradientMiddle => Border;
            public override Color ButtonSelectedGradientEnd => Border;
            public override Color ButtonPressedBorder => Accent;
            public override Color ButtonPressedGradientBegin => Selection;
            public override Color ButtonPressedGradientMiddle => Selection;
            public override Color ButtonPressedGradientEnd => Selection;
            public override Color ButtonCheckedHighlight => Selection;
            public override Color ButtonCheckedGradientBegin => Selection;
            public override Color ButtonCheckedGradientMiddle => Selection;
            public override Color ButtonCheckedGradientEnd => Selection;
            public override Color CheckBackground => Selection;
            public override Color CheckSelectedBackground => Selection;
            public override Color CheckPressedBackground => Selection;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color GripDark => Border;
            public override Color GripLight => Surface;
            public override Color OverflowButtonGradientBegin => Surface;
            public override Color OverflowButtonGradientMiddle => Surface;
            public override Color OverflowButtonGradientEnd => Surface;
        }
    }
}
