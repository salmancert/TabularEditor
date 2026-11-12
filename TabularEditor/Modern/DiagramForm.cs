using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.UI;
using TabularEditor.UIServices;

namespace TabularEditor.Modern
{
    /// <summary>
    /// Read-only relationship diagram. Drag tables to arrange them, drag the background to pan,
    /// Ctrl+wheel to zoom, double-click a table to locate it in the model tree.
    /// </summary>
    internal class DiagramForm : Form
    {
        private readonly DiagramCanvas canvas = new DiagramCanvas { Dock = DockStyle.Fill };

        public DiagramForm()
        {
            Text = "Model Diagram";
            Icon = FormMain.Singleton?.Icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1200, 800);

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var relatedOnly = new ToolStripButton("Only tables with relationships") { CheckOnClick = true };
            var reload = new ToolStripButton("Reload / auto-arrange");
            var reset = new ToolStripButton("Reset zoom");
            relatedOnly.Click += (s, e) => { canvas.RelatedOnly = relatedOnly.Checked; canvas.LoadModel(); };
            reload.Click += (s, e) => canvas.LoadModel();
            reset.Click += (s, e) => canvas.ResetView();
            toolbar.Items.AddRange(new ToolStripItem[] { reload, relatedOnly, reset,
                new ToolStripSeparator(), new ToolStripLabel("Drag tables to arrange. Drag background to pan. Ctrl+wheel to zoom.") });

            Controls.Add(canvas);
            Controls.Add(toolbar);
            Shown += (s, e) => canvas.LoadModel();
        }

        private class Node
        {
            public Table Table;
            public string Name;
            public List<string> Lines = new List<string>();
            public HashSet<string> KeyColumns = new HashSet<string>();
            public RectangleF Bounds;
        }

        private class Edge
        {
            public Node From, To;
            public string FromLabel, ToLabel;
            public bool Active;
        }

        private class DiagramCanvas : Control
        {
            private const float NodeWidth = 210, HeaderHeight = 26, LineHeight = 17, MaxLines = 14, Gap = 50;
            private readonly List<Node> nodes = new List<Node>();
            private readonly List<Edge> edges = new List<Edge>();
            private PointF offset = new PointF(20, 20);
            private float zoom = 1f;
            private Node dragNode;
            private bool panning;
            private Point lastMouse;

            public bool RelatedOnly;

            public DiagramCanvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public void ResetView()
            {
                zoom = 1f;
                offset = new PointF(20, 20);
                Invalidate();
            }

            public void LoadModel()
            {
                nodes.Clear();
                edges.Clear();
                var model = UIController.Current?.Handler?.Model;
                if (model == null) { Invalidate(); return; }

                var relationships = model.Relationships.OfType<SingleColumnRelationship>()
                    .Where(r => r.FromColumn != null && r.ToColumn != null).ToList();
                var byTable = new Dictionary<Table, Node>();
                foreach (var table in model.Tables)
                {
                    var keys = new HashSet<string>(relationships.Where(r => r.FromTable == table).Select(r => r.FromColumn.Name)
                        .Concat(relationships.Where(r => r.ToTable == table).Select(r => r.ToColumn.Name)));
                    if (RelatedOnly && keys.Count == 0) continue;

                    var node = new Node { Table = table, Name = table.Name, KeyColumns = keys };
                    // Relationship columns first, then the rest alphabetically.
                    var names = table.Columns.Where(c => c.Type != ColumnType.RowNumber).Select(c => c.Name)
                        .OrderBy(n => keys.Contains(n) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    if (names.Count > MaxLines)
                    {
                        int hidden = names.Count - ((int)MaxLines - 1);
                        names = names.Take((int)MaxLines - 1).ToList();
                        names.Add("... " + hidden + " more columns");
                    }
                    node.Lines = names;
                    int measures = table.Measures.Count;
                    if (measures > 0) node.Lines.Add(measures + (measures == 1 ? " measure" : " measures"));
                    nodes.Add(node);
                    byTable[table] = node;
                }

                foreach (var r in relationships)
                {
                    Node from, to;
                    if (!byTable.TryGetValue(r.FromTable, out from) || !byTable.TryGetValue(r.ToTable, out to)) continue;
                    edges.Add(new Edge
                    {
                        From = from,
                        To = to,
                        Active = r.IsActive,
                        FromLabel = r.FromCardinality == RelationshipEndCardinality.One ? "1" : "*",
                        ToLabel = r.ToCardinality == RelationshipEndCardinality.One ? "1" : "*"
                    });
                }

                Arrange();
                Invalidate();
            }

            /// <summary>
            /// Star-schema layout: fact tables (many-side only) in the middle band, the tables they
            /// look up split above and below, and unrelated tables at the bottom.
            /// </summary>
            private void Arrange()
            {
                var facts = nodes.Where(n => edges.Any(e => e.From == n) && !edges.Any(e => e.To == n)).OrderBy(n => n.Name).ToList();
                var unrelated = nodes.Where(n => !edges.Any(e => e.From == n || e.To == n)).OrderBy(n => n.Name).ToList();
                var lookups = nodes.Except(facts).Except(unrelated)
                    .OrderByDescending(n => edges.Count(e => e.From == n || e.To == n)).ThenBy(n => n.Name).ToList();
                var top = lookups.Where((n, i) => i % 2 == 0).ToList();
                var bottom = lookups.Where((n, i) => i % 2 == 1).ToList();

                int perRow = Math.Max(3, new[] { top.Count, bottom.Count, facts.Count }.Max());
                perRow = Math.Min(perRow, Math.Max(3, (int)Math.Ceiling(Math.Sqrt(nodes.Count) * 1.6)));
                float width = perRow * (NodeWidth + Gap) - Gap;
                float y = 0;
                foreach (var band in new[] { top, facts, bottom, unrelated })
                {
                    for (int start = 0; start < band.Count; start += perRow)
                    {
                        var row = band.Skip(start).Take(perRow).ToList();
                        float rowWidth = row.Count * (NodeWidth + Gap) - Gap;
                        float x = (width - rowWidth) / 2;   // centre each row
                        float rowHeight = 0;
                        foreach (var node in row)
                        {
                            float height = HeaderHeight + node.Lines.Count * LineHeight + 8;
                            node.Bounds = new RectangleF(x, y, NodeWidth, height);
                            rowHeight = Math.Max(rowHeight, height);
                            x += NodeWidth + Gap;
                        }
                        y += rowHeight + Gap * 1.6f;
                    }
                }
            }

            private PointF ToModel(Point p) => new PointF((p.X - offset.X) / zoom, (p.Y - offset.Y) / zoom);

            private Node HitTest(Point p)
            {
                var m = ToModel(p);
                for (int i = nodes.Count - 1; i >= 0; i--) if (nodes[i].Bounds.Contains(m)) return nodes[i];
                return null;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                lastMouse = e.Location;
                dragNode = e.Button == MouseButtons.Left ? HitTest(e.Location) : null;
                if (dragNode != null)
                {
                    nodes.Remove(dragNode);   // bring to front
                    nodes.Add(dragNode);
                }
                else panning = e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                float dx = e.X - lastMouse.X, dy = e.Y - lastMouse.Y;
                if (dragNode != null)
                {
                    dragNode.Bounds = new RectangleF(dragNode.Bounds.X + dx / zoom, dragNode.Bounds.Y + dy / zoom, dragNode.Bounds.Width, dragNode.Bounds.Height);
                    Invalidate();
                }
                else if (panning)
                {
                    offset = new PointF(offset.X + dx, offset.Y + dy);
                    Invalidate();
                }
                lastMouse = e.Location;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                dragNode = null;
                panning = false;
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                var node = HitTest(e.Location);
                if (node != null)
                {
                    try { UIController.Current.Goto(node.Table); } catch { }
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if ((ModifierKeys & Keys.Control) == Keys.Control)
                {
                    var before = ToModel(e.Location);
                    zoom = Math.Max(0.25f, Math.Min(2.5f, zoom * (e.Delta > 0 ? 1.1f : 1 / 1.1f)));
                    offset = new PointF(e.X - before.X * zoom, e.Y - before.Y * zoom);   // keep the point under the cursor fixed
                }
                else if ((ModifierKeys & Keys.Shift) == Keys.Shift) offset = new PointF(offset.X + e.Delta / 2f, offset.Y);
                else offset = new PointF(offset.X, offset.Y + e.Delta / 2f);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                bool dark = ThemeManager.IsDark;
                var back = dark ? ThemeManager.Back : Color.FromArgb(245, 246, 248);
                var box = dark ? ThemeManager.Surface : Color.White;
                var header = dark ? Color.FromArgb(0, 96, 160) : Color.FromArgb(0, 120, 212);
                var border = dark ? ThemeManager.Border : Color.FromArgb(190, 195, 202);
                var text = dark ? ThemeManager.Text : Color.FromArgb(30, 30, 30);
                var dim = dark ? ThemeManager.TextDim : Color.FromArgb(120, 120, 120);
                var line = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(100, 100, 100);

                var g = e.Graphics;
                g.Clear(back);
                if (nodes.Count == 0)
                {
                    TextRenderer.DrawText(g, "No tables to show. Open a model first.", Font, ClientRectangle, dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.TranslateTransform(offset.X, offset.Y);
                g.ScaleTransform(zoom, zoom);

                using (var regular = new Font("Segoe UI", 9f))
                using (var bold = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var textBrush = new SolidBrush(text))
                using (var dimBrush = new SolidBrush(dim))
                using (var lineBrush = new SolidBrush(line))
                using (var boxBrush = new SolidBrush(box))
                using (var headerBrush = new SolidBrush(header))
                using (var borderPen = new Pen(border))
                using (var linePen = new Pen(line, 1.5f))
                using (var inactivePen = new Pen(line, 1.5f) { DashStyle = DashStyle.Dash })
                using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
                {
                    foreach (var edge in edges)
                    {
                        PointF a, b, c1, c2;
                        Route(edge.From.Bounds, edge.To.Bounds, out a, out c1, out c2, out b);
                        g.DrawBezier(edge.Active ? linePen : inactivePen, a, c1, c2, b);
                        g.DrawString(edge.FromLabel, bold, lineBrush, a.X + Math.Sign(c1.X - a.X) * 10 - 5 + (c1.X == a.X ? 8 : 0), a.Y + Math.Sign(c1.Y - a.Y) * 10 - 8);
                        g.DrawString(edge.ToLabel, bold, lineBrush, b.X + Math.Sign(c2.X - b.X) * 10 - 5 + (c2.X == b.X ? 8 : 0), b.Y + Math.Sign(c2.Y - b.Y) * 10 - 8);
                    }

                    foreach (var node in nodes)
                    {
                        var r = node.Bounds;
                        g.FillRectangle(boxBrush, r);
                        g.FillRectangle(headerBrush, r.X, r.Y, r.Width, HeaderHeight);
                        g.DrawString(node.Name, bold, Brushes.White, new RectangleF(r.X + 8, r.Y, r.Width - 12, HeaderHeight), format);
                        float y = r.Y + HeaderHeight + 4;
                        foreach (var name in node.Lines)
                        {
                            bool key = node.KeyColumns.Contains(name);
                            bool note = !key && (name.StartsWith("... ") || name.EndsWith(" measure") || name.EndsWith(" measures"));
                            g.DrawString(name, key ? bold : regular, note ? dimBrush : textBrush, new RectangleF(r.X + 8, y, r.Width - 12, LineHeight), format);
                            y += LineHeight;
                        }
                        g.DrawRectangle(borderPen, r.X, r.Y, r.Width, r.Height);
                    }
                }
            }

            private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

            /// <summary>
            /// Connects two boxes through the gap between them: bottom-to-top when one is above the
            /// other, otherwise side to side. Returns the end points and the two bezier control points.
            /// </summary>
            private static void Route(RectangleF from, RectangleF to, out PointF a, out PointF c1, out PointF c2, out PointF b)
            {
                float fromX = from.X + from.Width / 2, toX = to.X + to.Width / 2;
                if (from.Bottom < to.Top || to.Bottom < from.Top)
                {
                    bool down = from.Bottom < to.Top;
                    a = new PointF(Clamp(toX, from.Left + 20, from.Right - 20), down ? from.Bottom : from.Top);
                    b = new PointF(Clamp(fromX, to.Left + 20, to.Right - 20), down ? to.Top : to.Bottom);
                    float bend = Math.Max(25, Math.Abs(b.Y - a.Y) / 2);
                    c1 = new PointF(a.X, a.Y + (down ? bend : -bend));
                    c2 = new PointF(b.X, b.Y + (down ? -bend : bend));
                }
                else
                {
                    bool right = from.Right <= to.Left;
                    bool left = to.Right <= from.Left;
                    float fy = from.Y + HeaderHeight / 2, ty = to.Y + HeaderHeight / 2;
                    a = new PointF(left ? from.Left : from.Right, fy);
                    b = new PointF(right ? to.Left : to.Right, ty);
                    float bend = Math.Max(25, Math.Abs(b.X - a.X) / 2);
                    c1 = new PointF(a.X + (left ? -bend : bend), a.Y);
                    c2 = new PointF(b.X + (right ? -bend : bend), b.Y);
                }
            }
        }
    }
}
