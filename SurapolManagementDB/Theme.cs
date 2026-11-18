using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>
    /// ธีมแบบทันสมัย (มืด / สว่าง) สำหรับหน้าต่างฐานข้อมูล
    /// สีอ่านจากที่นี่ทุกครั้ง เปลี่ยนธีมแล้วเรียก Apply ใหม่ได้ทันที
    /// </summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; } = Ui.GetSetting("theme", "dark") != "light";

        /// <summary>เปลี่ยนธีมแล้ว (ทุกหน้าที่เปิดอยู่ใส่สีใหม่)</summary>
        public static event Action? Changed;

        public static void Set(bool dark)
        {
            Dark = dark;
            Ui.SetSetting("theme", dark ? "dark" : "light");
            Changed?.Invoke();
        }

        /// <summary>ใส่ธีมให้หน้าต่าง dialog (เรียกใน Load) และเปลี่ยนตามเมื่อสลับธีม</summary>
        public static void Dialog(Form f)
        {
            Apply(f);
            f.Shown += (s, e) => TitleBar(f);
            Action h = () => { if (!f.IsDisposed) Apply(f); };
            Changed += h;
            f.FormClosed += (s, e) => Changed -= h;
        }

        static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

        /// <summary>แปลงสีความหมายเดิม (เทา / เขียว / แดง / ส้ม / ฟ้า) ของทั้ง 2 ธีมและสีระบบ ให้เป็นสีของธีมปัจจุบัน</summary>
        static Color Map(Color c)
        {
            int v = c.ToArgb() & 0xFFFFFF;
            if (c == Color.DimGray || c == Color.Gray || c == Color.Silver || v == 0x8C8F94 || v == 0x6B7280 || c == SystemColors.GrayText) return Muted;
            if (c == Color.ForestGreen || v == 0x5FB865 || v == 0x1A7F37) return Ok;
            if (c == Color.Firebrick || v == 0xF26D6D || v == 0xC62828) return Err;
            if (c == Color.DarkOrange || v == 0xE0A33B || v == 0xB35900) return Warn;
            if (c == Color.SteelBlue || v == 0x6EA8FE || v == 0x1F5FD1) return Link;
            return Text;
        }

        public static Color Back => Dark ? C(0x1E1F22) : C(0xF3F4F6);
        public static Color Panel => Dark ? C(0x2B2D30) : C(0xFFFFFF);
        public static Color Surface => Dark ? C(0x393B40) : C(0xFFFFFF);
        public static Color Header => Dark ? C(0x2B2D30) : C(0xF8F9FB);
        public static Color Border => Dark ? C(0x43454A) : C(0xD8DCE2);
        public static Color Grid => Dark ? C(0x393B40) : C(0xEBEDF0);
        public static Color Text => Dark ? C(0xDFE1E5) : C(0x1F2328);
        public static Color Muted => Dark ? C(0x8C8F94) : C(0x6B7280);
        public static Color Accent => C(0x3574F0);
        public static Color AccentText => Color.White;
        public static Color Selection => Dark ? C(0x2E436E) : C(0xD5E4FF);
        public static Color Hover => Dark ? C(0x35383D) : C(0xEEF2F8);
        public static Color Alt => Dark ? C(0x2F3134) : C(0xFAFBFC);
        public static Color Ok => Dark ? C(0x5FB865) : C(0x1A7F37);
        public static Color Warn => Dark ? C(0xE0A33B) : C(0xB35900);
        public static Color Err => Dark ? C(0xF26D6D) : C(0xC62828);
        public static Color Link => Dark ? C(0x6EA8FE) : C(0x1F5FD1);
        public static Color Added => Dark ? C(0x1E3A28) : C(0xE6F6EA);
        public static Color Modified => Dark ? C(0x46411F) : C(0xFFF6CC);

        // สีของไอคอน
        public static Color IconConn => C(0x5FB865);
        public static Color IconDb => C(0xE0A33B);
        public static Color IconTable => Dark ? C(0x6EA8FE) : C(0x2F6FD6);
        public static Color IconView => Dark ? C(0xB392F0) : C(0x7C4DDB);
        public static Color IconFolder => Dark ? C(0xC9A45C) : C(0xB8862F);
        public static Color IconInfo => Dark ? C(0x9AA0A6) : C(0x5F6670);

        // ---- Win32: แถบชื่อหน้าต่าง / scrollbar แบบมืด
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

        public static void TitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int v = Dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
            }
            catch { }
        }

        static void NativeTheme(Control c)
        {
            void Set() { try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }
            if (c.IsHandleCreated) Set();
            else c.HandleCreated += (s, e) => Set();
        }

        /// <summary>ใส่สีให้ control และลูกทั้งหมด  (Tag = "muted" / "title" / "primary" / "keep" ใช้กำหนดแบบพิเศษ)</summary>
        public static void Apply(Control root)
        {
            Style(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        static void Style(Control c)
        {
            string tag = c.Tag as string ?? "";
            if (tag == "keep") return;
            switch (c)
            {
                case Form f:
                    f.BackColor = Back;
                    f.ForeColor = Text;
                    TitleBar(f);
                    break;
                case ToolStrip ts:
                    ts.Renderer = new Renderer();
                    ts.BackColor = Header;
                    ts.ForeColor = Text;
                    break;
                case TreeView tv:
                    tv.BackColor = Panel;
                    tv.ForeColor = Text;
                    tv.LineColor = Border;
                    NativeTheme(tv);
                    break;
                case ListView lv:
                    lv.BackColor = Panel;
                    lv.ForeColor = Text;
                    NativeTheme(lv);
                    lv.Invalidate();
                    break;
                case DataGridView g:
                    StyleGrid(g);
                    break;
                case TextBox tb:
                    tb.BackColor = tb.ReadOnly && tb.Multiline ? Panel : Surface;
                    tb.ForeColor = Text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = Surface;
                    cb.ForeColor = Text;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case NumericUpDown nu:
                    nu.BackColor = Surface;
                    nu.ForeColor = Text;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    bool primary = tag == "primary";
                    bool danger = tag == "danger";
                    b.BackColor = primary ? Accent : Surface;
                    b.ForeColor = primary ? AccentText : danger ? Err : Text;
                    b.FlatAppearance.BorderColor = primary ? Accent : danger ? Err : Border;
                    b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.15f) : Hover;
                    b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(Accent, 0.05f) : Selection;
                    break;
                case CheckBox cb2:
                    cb2.ForeColor = Map(cb2.ForeColor);
                    break;
                case RadioButton rb:
                    rb.ForeColor = Map(rb.ForeColor);
                    break;
                case GroupBox gb:
                    gb.BackColor = Back;
                    gb.ForeColor = Map(gb.ForeColor);
                    break;
                case Label l:
                    l.ForeColor = tag == "muted" ? Muted : Map(l.ForeColor);
                    if (tag == "card") l.BackColor = Panel;
                    break;
                case FlatTabs ft:
                    ft.BackColor = Back;
                    ft.Restyle();
                    break;
                default:
                    c.BackColor = tag == "card" ? Panel : tag == "header" ? Header : Back;
                    c.ForeColor = Text;
                    break;
            }
        }

        public static void StyleGrid(DataGridView g)
        {
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = Panel;
            g.GridColor = Grid;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.DefaultCellStyle.BackColor = Panel;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Selection;
            g.DefaultCellStyle.SelectionForeColor = Text;
            g.AlternatingRowsDefaultCellStyle.BackColor = Alt;
            g.ColumnHeadersDefaultCellStyle.BackColor = Header;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Header;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.RowHeadersDefaultCellStyle.BackColor = Header;
            g.RowHeadersDefaultCellStyle.ForeColor = Muted;
            g.RowHeadersDefaultCellStyle.SelectionBackColor = Selection;
            g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            foreach (Control sb in g.Controls) if (sb is ScrollBar) NativeTheme(sb);
        }

        /// <summary>ListView แบบ owner-draw (หัวตารางและแถวเป็นสีของธีม)</summary>
        public static void OwnerDrawList(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Header)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font, Rectangle.Inflate(e.Bounds, -8, 0), Muted, flags);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                if (e.Item == null || e.SubItem == null) return;
                bool sel = e.Item.Selected;
                Color bg = sel ? Selection : (e.ItemIndex % 2 == 1 ? Alt : Panel);
                using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
                Color fg = e.ColumnIndex == 0 && e.Item.ForeColor != lv.ForeColor && e.Item.ForeColor != Color.Empty ? e.Item.ForeColor : Text;
                if (e.ColumnIndex > 0 && e.SubItem.ForeColor != lv.ForeColor && e.SubItem.ForeColor != Color.Empty && e.SubItem.ForeColor != SystemColors.WindowText) fg = e.SubItem.ForeColor;
                var r = Rectangle.Inflate(e.Bounds, -8, 0);
                if (e.ColumnIndex == 0 && lv.CheckBoxes)
                {
                    // ช่องติ๊กแบบแบนตามธีม (ตำแหน่งเดียวกับช่องติ๊กเดิม คลิกได้ตามปกติ)
                    int sz = Ui.Px(lv, 14);
                    var cb = new Rectangle(e.Bounds.Left + Ui.Px(lv, 4), e.Bounds.Top + (e.Bounds.Height - sz) / 2, sz, sz);
                    var mode = e.Graphics.SmoothingMode;
                    using (var bb = new SolidBrush(e.Item.Checked ? Accent : Surface)) e.Graphics.FillRectangle(bb, cb);
                    using (var pp = new Pen(e.Item.Checked ? Accent : Muted)) e.Graphics.DrawRectangle(pp, cb);
                    if (e.Item.Checked)
                    {
                        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var pc = new Pen(Color.White, Math.Max(2f, sz / 7f));
                        e.Graphics.DrawLines(pc, new[] { new Point(cb.Left + sz / 5, cb.Top + sz / 2), new Point(cb.Left + sz * 2 / 5, cb.Bottom - sz / 4), new Point(cb.Right - sz / 5, cb.Top + sz / 4) });
                        e.Graphics.SmoothingMode = mode;
                    }
                    r = new Rectangle(cb.Right + Ui.Px(lv, 6), r.Top, Math.Max(0, r.Right - cb.Right - Ui.Px(lv, 6)), r.Height);
                }
                if (e.ColumnIndex == 0 && e.Item.ImageList != null && e.Item.ImageKey != "" && e.Item.ImageList.Images.ContainsKey(e.Item.ImageKey))
                {
                    var img = e.Item.ImageList.Images[e.Item.ImageKey]!;
                    e.Graphics.DrawImage(img, r.Left, e.Bounds.Top + (e.Bounds.Height - img.Height) / 2);
                    r = new Rectangle(r.Left + img.Width + 6, r.Top, r.Width - img.Width - 6, r.Height);
                }
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            (lv.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, r, fg, flags);
            };
            // คอลัมน์สุดท้ายยืดเต็มความกว้าง (ไม่มีช่องว่างสีขาวท้ายหัวตาราง)
            lv.Resize += (s, e) => FitLastColumn(lv);
        }

        /// <summary>วาดเฉพาะหัวตารางตามธีม (แถววาดตามปกติ ใช้กับ ListView ที่มีช่องติ๊ก)</summary>
        public static void OwnerDrawHeader(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Header)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font, Rectangle.Inflate(e.Bounds, -6, 0), Muted, flags);
            };
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
        }

        public static void FitLastColumn(ListView lv)
        {
            if (lv.Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
            int w = lv.ClientSize.Width - used - 1;
            var last = lv.Columns[lv.Columns.Count - 1];
            // ยืดให้เต็มเท่านั้น ไม่ให้แคบกว่าความกว้างเดิมของคอลัมน์
            if (last.Tag is not int orig) last.Tag = orig = last.Width;
            last.Width = Math.Max(orig, w);
        }

        /// <summary>
        /// แท็บแบบแบน (แทน TabControl ที่ใส่สีธีมไม่ได้): แถวปุ่มด้านบน + หน้าเนื้อหา
        /// ใช้ AddPage(ชื่อ) แล้วใส่ control ลงใน panel ที่ได้, เลือกหน้าด้วย SelectedIndex
        /// </summary>
        public sealed class FlatTabs : Panel
        {
            readonly FlowLayoutPanel _bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
            readonly Panel _body = new Panel { Dock = DockStyle.Fill };
            readonly System.Collections.Generic.List<(Button Btn, Panel Page)> _pages = new System.Collections.Generic.List<(Button, Panel)>();
            int _sel = -1;
            public event EventHandler? SelectedIndexChanged;

            public FlatTabs()
            {
                Controls.Add(_body);
                Controls.Add(_bar);
            }

            public Panel AddPage(string title)
            {
                var page = new Panel { Dock = DockStyle.Fill, Visible = false };
                var b = new Button { Text = title, AutoSize = true, Padding = new Padding(14, 3, 14, 3), Margin = new Padding(0, 0, 4, 0), Tag = "keep" };
                int idx = _pages.Count;
                b.Click += (s, e) => SelectedIndex = idx;
                _pages.Add((b, page));
                _bar.Controls.Add(b);
                _body.Controls.Add(page);
                if (_sel < 0) SelectedIndex = 0;
                else Restyle();
                return page;
            }

            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public int SelectedIndex
            {
                get => _sel;
                set
                {
                    if (value < 0 || value >= _pages.Count) return;
                    bool changed = value != _sel;
                    _sel = value;
                    for (int i = 0; i < _pages.Count; i++) _pages[i].Page.Visible = i == value;
                    Restyle();
                    if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public void Restyle()
            {
                _bar.BackColor = _body.BackColor = Back;
                for (int i = 0; i < _pages.Count; i++)
                {
                    var b = _pages[i].Btn;
                    bool on = i == _sel;
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = on ? Accent : Surface;
                    b.ForeColor = on ? AccentText : Theme.Text;
                    b.FlatAppearance.BorderColor = on ? Accent : Border;
                    b.FlatAppearance.MouseOverBackColor = on ? Accent : Hover;
                    _pages[i].Page.BackColor = Back;
                }
            }
        }

        /// <summary>ToolStrip / เมนูคลิกขวา ตามธีม</summary>
        public sealed class Renderer : ToolStripProfessionalRenderer
        {
            public Renderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
                else using (var p = new Pen(Border)) e.Graphics.DrawLine(p, 0, e.AffectedBounds.Bottom - 1, e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Header;
            public override Color ToolStripGradientMiddle => Header;
            public override Color ToolStripGradientEnd => Header;
            public override Color ToolStripBorder => Border;
            public override Color ToolStripDropDownBackground => Panel;
            public override Color ImageMarginGradientBegin => Panel;
            public override Color ImageMarginGradientMiddle => Panel;
            public override Color ImageMarginGradientEnd => Panel;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Selection;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Selection;
            public override Color MenuItemPressedGradientEnd => Selection;
            public override Color ButtonSelectedHighlight => Hover;
            public override Color ButtonSelectedGradientBegin => Hover;
            public override Color ButtonSelectedGradientMiddle => Hover;
            public override Color ButtonSelectedGradientEnd => Hover;
            public override Color ButtonSelectedBorder => Border;
            public override Color ButtonPressedGradientBegin => Selection;
            public override Color ButtonPressedGradientMiddle => Selection;
            public override Color ButtonPressedGradientEnd => Selection;
            public override Color ButtonCheckedGradientBegin => Selection;
            public override Color ButtonCheckedGradientMiddle => Selection;
            public override Color ButtonCheckedGradientEnd => Selection;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Header;
            public override Color StatusStripGradientBegin => Header;
            public override Color StatusStripGradientEnd => Header;
        }
    }
}
