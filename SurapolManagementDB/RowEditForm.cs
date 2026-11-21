using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>คอลัมน์ 1 ช่องในหน้าต่างแก้ไขแถว</summary>
    internal sealed class RowField
    {
        public string Name = "", Type = "";
        /// <summary>เป็นส่วนของ key ที่ใช้ระบุแถว (primary key / unique)</summary>
        public bool Key;
        public bool Nullable, ReadOnly;
        /// <summary>"num" / "text" / "date" / "blob" / "json"</summary>
        public string Kind = "text";
        /// <summary>ข้อความยาว (TEXT / JSON / VARCHAR ยาว) → ช่องหลายบรรทัด</summary>
        public bool Long;
    }

    /// <summary>
    /// ดู / แก้ไขข้อมูลทีละแถว (ดับเบิลคลิกแถวในตาราง)  แถวถูกระบุด้วย key (เช่น ROWID / primary key)
    /// แก้แล้วกด บันทึก (Ctrl+S) จะบันทึกลงฐานข้อมูลทันที   ◀ ▶ = แถวก่อน / ถัดไปในหน้านี้
    /// </summary>
    internal sealed class RowEditForm : Form
    {
        readonly List<RowField> _fields;
        readonly List<DataRow> _rows;
        readonly bool _readOnly;
        readonly Func<DataRow, int, Task> _openBlob, _saveBlob;
        int _idx;

        readonly Label _keyLabel, _pos;
        readonly TableLayoutPanel _body;
        readonly Button _prev, _next;
        readonly Control?[] _editors;
        readonly CheckBox?[] _nulls;
        readonly Label?[] _names;
        readonly Panel _scroll;
        readonly ToolTip _tip = new ToolTip();
        /// <summary>ช่องที่ชี้ไว้ (ช่องที่เลือกอยู่ในตารางตอนเปิด)  -1 = ช่องแรกที่แก้ได้</summary>
        int _focus;

        /// <param name="focusField">ช่อง (คอลัมน์) ที่ให้ชี้ / วางเคอร์เซอร์ไว้ตอนเปิด  -1 = ช่องแรกที่แก้ได้</param>
        public RowEditForm(string table, List<RowField> fields, List<DataRow> rows, int index, bool readOnly,
                           Func<DataRow, int, Task> openBlob, Func<DataRow, int, Task> saveBlob, Func<Task<bool>>? save, int focusField = -1)
        {
            _focus = focusField >= 0 && focusField < fields.Count ? focusField : -1;
            _names = new Label?[fields.Count];
            _fields = fields;
            _rows = rows;
            _idx = Math.Max(0, Math.Min(index, rows.Count - 1));
            _openBlob = openBlob;
            _saveBlob = saveBlob;
            _readOnly = true;
            _editors = new Control?[fields.Count];
            _nulls = new CheckBox?[fields.Count];

            Text = (_readOnly ? Tr.T("ดูแถว — ", "View row — ") : Tr.T("แก้ไขแถว — ", "Edit row — ")) + table;
            Font = Ui.Font;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            KeyPreview = true;
            if (Ui.AppIcon != null) Icon = Ui.AppIcon;
            ClientSize = new Size(660, Math.Min(640, 150 + fields.Sum(f => f.Long ? 74 : 32)));
            MinimumSize = new Size(480, 300);

            // ---- หัว: key ของแถว
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12, 10, 12, 6) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var keyIcon = new PictureBox { Size = new Size(Ui.Px(this, 18), Ui.Px(this, 18)), SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 2, 6, 0), Image = Ui.Glyph("", Ui.Px(this, 16), Theme.IconDb) };
            _keyLabel = new Label { AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 1, 0, 0) };
            head.Controls.Add(keyIcon, 0, 0);
            head.Controls.Add(_keyLabel, 1, 0);

            // ---- ช่องข้อมูล
            var scroll = _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 4, 12, 4) };
            _body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Margin = new Padding(0) };
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // ไอคอน
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // ชื่อ
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // ค่า
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // NULL
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // ชนิด
            for (int i = 0; i < fields.Count; i++) AddField(i);
            scroll.Controls.Add(_body);

            // ---- ปุ่มล่าง
            var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 5, Padding = new Padding(12, 8, 12, 10) };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _prev = IconButton("", Tr.T("แถวก่อนหน้า (Alt+←)", "Previous row (Alt+←)"), () => GoRow(-1));
            _next = IconButton("", Tr.T("แถวถัดไป (Alt+→)", "Next row (Alt+→)"), () => GoRow(1));
            _pos = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(8, 7, 0, 0) };
            var close = Ui.MakeButton(Tr.T("ปิด", "Close"), Close);
            close.Margin = new Padding(6, 0, 0, 0);
            foot.Controls.Add(_prev, 0, 0);
            foot.Controls.Add(_next, 1, 0);
            foot.Controls.Add(_pos, 2, 0);
            foot.Controls.Add(close, 4, 0);

            Controls.Add(scroll);
            Controls.Add(head);
            Controls.Add(foot);
            Load += (s, e) => { Theme.Apply(this); ShowRow(); };
            Shown += (s, e) => FocusField();
            KeyDown += OnKey;
            FormClosing += OnClosing;
        }

        static string Glyph(RowField f) => f.Key ? "" : f.Kind switch
        {
            "num" => "",
            "date" => "",
            "blob" => "",
            "json" => "",
            _ => "",
        };

        Button IconButton(string glyph, string tip, Action a)
        {
            var b = new Button
            {
                AutoSize = false, Size = new Size(Ui.Px(this, 32), Ui.Px(this, 28)), Margin = new Padding(0, 0, 4, 0),
                Image = Ui.Glyph(glyph, Ui.Px(this, 14), Theme.Text), ImageAlign = ContentAlignment.MiddleCenter,
            };
            b.Click += (s, e) => a();
            _tip.SetToolTip(b, tip);
            return b;
        }

        void AddField(int i)
        {
            var f = _fields[i];
            int row = _body.RowCount++;
            _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            string tip = f.Name + "  ·  " + f.Type + (f.Key ? "  ·  KEY" : "") + (f.Nullable ? "" : "  ·  NOT NULL") + (f.ReadOnly ? Tr.T("  ·  อ่านอย่างเดียว", "  ·  read-only") : "");
            var icon = new PictureBox
            {
                Size = new Size(Ui.Px(this, 18), Ui.Px(this, 18)), SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 6, 6, 0),
                Image = Ui.Glyph(Glyph(f), Ui.Px(this, 15), f.Key ? Theme.IconDb : f.Kind == "blob" ? Theme.IconView : Theme.IconTable),
            };
            var name = new Label
            {
                Text = f.Name, AutoSize = true, Margin = new Padding(0, 6, 10, 0), MaximumSize = new Size(Ui.Px(this, 190), 0),
                Font = f.Key ? new Font(Ui.Font, FontStyle.Bold) : Ui.Font,
            };
            _tip.SetToolTip(icon, tip);
            _tip.SetToolTip(name, tip);
            Control editor;
            if (f.Kind == "blob")
            {
                var fl = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2), Dock = DockStyle.Fill };
                var lbl = new Label { AutoSize = true, Margin = new Padding(0, 6, 8, 0), Tag = "muted" };
                var open = IconButton("", Tr.T("เปิดดูไฟล์ / รูป", "Open file / image"), () => _ = _openBlob(_rows[_idx], i));
                var save = IconButton("", Tr.T("บันทึกเป็นไฟล์…", "Save to file…"), () => _ = _saveBlob(_rows[_idx], i));
                fl.Controls.AddRange(new Control[] { lbl, open, save });
                editor = fl;
            }
            else
            {
                var tb = new TextBox
                {
                    Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2), ReadOnly = _readOnly || f.ReadOnly,
                    Multiline = f.Long, WordWrap = true, ScrollBars = f.Long ? ScrollBars.Vertical : ScrollBars.None,
                };
                if (f.Long) tb.Height = Ui.Px(this, 66);
                if (f.Kind == "num") tb.TextAlign = HorizontalAlignment.Left;
                editor = tb;
            }
            _editors[i] = editor;
            _names[i] = name;
            editor.Enter += (s, e) => _lastFocus = i;
            _body.Controls.Add(icon, 0, row);
            _body.Controls.Add(name, 1, row);
            _body.Controls.Add(editor, 2, row);
            if (f.Nullable && f.Kind != "blob" && !_readOnly && !f.ReadOnly)
            {
                var nc = new CheckBox { Text = "NULL", AutoSize = true, Margin = new Padding(0, 5, 8, 0) };
                _nulls[i] = nc;
                _body.Controls.Add(nc, 3, row);
            }
            var type = new Label { Text = f.Type, AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(Ui.Px(this, 150), 0) };
            _body.Controls.Add(type, 4, row);
        }

        static string Show(object? v) => v switch
        {
            null or DBNull => "",
            byte[] b => "0x" + Convert.ToHexString(b, 0, Math.Min(b.Length, 256)) + (b.Length > 256 ? "…" : ""),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };

        /// <summary>ชี้ช่องที่เลือกไว้: ไฮไลต์ชื่อ เลื่อนให้เห็น และวางเคอร์เซอร์ (เลือกข้อความไว้ พิมพ์ทับได้เลย)</summary>
        void FocusField()
        {
            int i = _focus >= 0 ? _focus : Array.FindIndex(_editors, c => c is TextBox t && !t.ReadOnly);
            if (i < 0) i = Array.FindIndex(_editors, c => c != null);
            if (i < 0) return;
            for (int k = 0; k < _names.Length; k++)
            {
                var n = _names[k];
                if (n == null) continue;
                bool on = k == i && _focus >= 0;
                n.BackColor = on ? Theme.Selection : Color.Transparent;
                n.ForeColor = on ? Theme.Link : Theme.Text;
                n.Text = (on ? "▸ " : "") + _fields[k].Name;
            }
            var ed = _editors[i];
            if (ed == null) return;
            _scroll.ScrollControlIntoView(ed);
            if (ed is TextBox tb) { tb.Focus(); tb.SelectAll(); }
            else if (ed is FlowLayoutPanel fl && fl.Controls.Count > 1) fl.Controls[1].Focus();
        }

        void ShowRow()
        {
            if (_rows.Count == 0) return;
            var r = _rows[_idx];
            for (int i = 0; i < _fields.Count; i++)
            {
                object v = r[i];
                if (_editors[i] is TextBox tb)
                {
                    tb.Text = Show(v).Replace("\r\n", "\n").Replace("\n", "\r\n");
                    tb.PlaceholderText = v is DBNull ? "NULL" : "";
                    if (_nulls[i] is CheckBox nc) nc.Checked = v is DBNull;
                }
                else if (_editors[i] is FlowLayoutPanel fl && fl.Controls[0] is Label lbl)
                {
                    lbl.Text = v is DBNull ? "NULL" : v is byte[] b ? Tr.T($"<ไฟล์ใหม่ {Ui.HumanSize(b.Length)}>", $"<new file {Ui.HumanSize(b.Length)}>") : v.ToString() ?? "";
                    // ปุ่ม เปิดดู / บันทึก ใช้ได้เมื่อมีค่า   (ปุ่มใส่ไฟล์ใช้ได้เสมอ)
                    if (fl.Controls.Count > 1 && fl.Controls[1] is Button ob) ob.Enabled = v is not DBNull;
                    if (fl.Controls.Count > 2 && fl.Controls[2] is Button sb) sb.Enabled = v is not DBNull;
                }
            }
            var keys = _fields.Select((f, i) => (f, i)).Where(x => x.f.Key).Select(x => x.f.Name + " = " + (r[x.i] is DBNull ? "NULL" : Show(r[x.i]))).ToList();
            _keyLabel.Text = keys.Count > 0 ? string.Join("   ·   ", keys) : Tr.T("ตารางนี้ไม่มี primary key (ระบุแถวจากทุกคอลัมน์)", "No primary key (row matched on all columns)");
            _pos.Text = Tr.T($"แถว {_idx + 1:N0} / {_rows.Count:N0} ในหน้านี้", $"Row {_idx + 1:N0} / {_rows.Count:N0} on this page") +
                        (_readOnly ? Tr.T("   ·   อ่านอย่างเดียว", "   ·   read-only") : "") + Pending();
            _prev.Enabled = _idx > 0;
            _next.Enabled = _idx < _rows.Count - 1;
        }

        string Pending()
        {
            return "";
        }

        void GoRow(int d)
        {
            int j = _idx + d;
            if (j < 0 || j >= _rows.Count) return;
            _idx = j;
            ShowRow();
            // แถวถัดไปยังชี้ช่องเดิม (ช่องที่ชี้ไว้ หรือช่องที่ผู้ใช้คลิก / พิมพ์อยู่ล่าสุด)
            if (_lastFocus >= 0) _focus = _lastFocus;
            FocusField();
        }

        /// <summary>ช่องที่ผู้ใช้คลิก / พิมพ์อยู่ล่าสุด (กด ◀ ▶ แล้วชี้ช่องนี้ต่อ)</summary>
        int _lastFocus = -1;

        void OnKey(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (e.Alt && e.KeyCode == Keys.Left) { e.Handled = true; GoRow(-1); }
            else if (e.Alt && e.KeyCode == Keys.Right) { e.Handled = true; GoRow(1); }
        }


        void OnClosing(object? s, FormClosingEventArgs e)
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
