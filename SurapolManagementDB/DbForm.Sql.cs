using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySqlConnector;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>
    /// หน้าต่างฐานข้อมูล: SQL Editor — พิมพ์คำสั่ง SQL (SELECT / INSERT / UPDATE / DELETE / CREATE …) แล้วรัน ดูผลเป็นตาราง
    /// Ctrl+Enter = รันคำสั่งที่เคอร์เซอร์อยู่ (หรือส่วนที่เลือก)   F5 = รันทั้งหมด   ปุ่ม SELECT / INSERT / UPDATE / DELETE สร้างคำสั่งจากคอลัมน์ของตาราง
    /// </summary>
    public sealed partial class DbForm
    {
        static readonly string[] SqlTemplates = { "SELECT", "INSERT", "UPDATE", "DELETE" };
        /// <summary>จำนวนแถวสูงสุดที่ดึงมาแสดงต่อ 1 ผลลัพธ์ (0 = ทั้งหมด)</summary>
        static readonly int[] SqlMaxRows = { 200, 1000, 5000, 50000, 0 };

        TableLayoutPanel _sqlPage = null!;
        ComboBox _sqlDb = null!, _sqlTable = null!, _sqlMax = null!;
        TextBox _sqlEdit = null!, _sqlMsg = null!;
        SplitContainer _sqlSplit = null!;
        DataGridView _sqlGrid = null!;
        /// <summary>ผลลัพธ์แบบข้อความ (โหมด Text)</summary>
        TextBox _sqlText = null!;
        FlowLayoutPanel _sqlTabs = null!;
        Label _sqlInfo = null!;
        Button _sqlRun = null!, _sqlRunAll = null!, _sqlStop = null!;
        readonly ToolTip _sqlTip = new ToolTip();
        SavedConn? _sqlConn;
        bool _sqlRunning, _sqlDbLoading, _sqlSplitSet;
        volatile bool _sqlStopReq;
        MySqlCommand? _sqlCmd;
        readonly object _sqlLock = new object();
        List<SqlResult> _sqlResults = new List<SqlResult>();
        /// <summary>แท็บผลลัพธ์ที่แสดงอยู่  -1 = ข้อความ</summary>
        int _sqlTab = -1;

        sealed class SqlResult
        {
            public string Sql = "";
            public DataTable Table = null!;
            public bool Truncated;
            public int Max;
        }

        readonly struct SqlStmt
        {
            public readonly string Sql;
            public readonly int Start, End;
            public SqlStmt(string sql, int start, int end) { Sql = sql; Start = start; End = end; }
        }

        static string SqlFile => Path.Combine(Ui.DataDir, "sql_editor.sql");

        string SqlDbName => _sqlDb.SelectedIndex > 0 ? (string)_sqlDb.SelectedItem! : "";

        // ------------------------------------------------------------------ สร้างหน้า

        TableLayoutPanel BuildSqlPage()
        {
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Visible = false };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ---- แถบเครื่องมือ
            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            Label L(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(0, 7, 4, 0), Tag = "muted" };
            _sqlDb = new ComboBox { Width = Ui.Px(this, 170), DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 10, 3) };
            _sqlDb.SelectedIndexChanged += (s, e) => { if (_sqlDbLoading) return; UpdateSqlHeader(); _ = LoadSqlTables(null); };
            _sqlTip.SetToolTip(_sqlDb, Tr.T("ฐานข้อมูลที่ใช้รันคำสั่ง (เหมือน USE ชื่อฐาน)", "Database the statements run in (like USE name)"));
            _sqlRun = Btn(Tr.T("▶ รันคำสั่งนี้", "▶ Run statement"), () => _ = RunSql(false), "primary");
            _sqlTip.SetToolTip(_sqlRun, Tr.T("Ctrl+Enter — รันคำสั่งที่เคอร์เซอร์อยู่ (ถ้าเลือกข้อความไว้ รันเฉพาะส่วนที่เลือก)", "Ctrl+Enter — run the statement at the cursor (or the selected text)"));
            _sqlRunAll = Btn(Tr.T("▶▶ รันทั้งหมด", "▶▶ Run all"), () => _ = RunSql(true));
            _sqlTip.SetToolTip(_sqlRunAll, Tr.T("F5 — รันทุกคำสั่งในหน้านี้ตามลำดับ (หยุดเมื่อเจอ error)", "F5 — run every statement in order (stops at the first error)"));
            _sqlStop = Btn(Tr.T("■ หยุด", "■ Stop"), StopSql, "danger");
            _sqlStop.Enabled = false;
            _sqlStop.EnabledChanged += (s, e) => StyleStopButton();
            _sqlMax = new ComboBox { Width = Ui.Px(this, 88), DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 10, 3) };
            foreach (int m in SqlMaxRows) _sqlMax.Items.Add(m == 0 ? Tr.T("ทั้งหมด", "All") : m.ToString("N0"));
            int mi = Array.IndexOf(SqlMaxRows, int.TryParse(Ui.GetSetting("sql_max_rows", "1000"), out int mr) ? mr : 1000);
            _sqlMax.SelectedIndex = mi >= 0 ? mi : 1;
            _sqlMax.SelectedIndexChanged += (s, e) => Ui.SetSetting("sql_max_rows", SqlMaxRows[_sqlMax.SelectedIndex].ToString());
            _sqlTip.SetToolTip(_sqlMax, Tr.T("จำนวนแถวสูงสุดที่ดึงมาแสดงต่อ 1 ผลลัพธ์ (กันตารางใหญ่ค้าง)", "Maximum rows fetched per result (keeps huge tables responsive)"));
            _sqlTable = new ComboBox
            {
                Width = Ui.Px(this, 200), DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(0, 3, 4, 3),
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            _sqlTip.SetToolTip(_sqlTable, Tr.T("ตารางที่ใช้สร้างคำสั่งจากปุ่ม SELECT / INSERT / UPDATE / DELETE", "Table used by the SELECT / INSERT / UPDATE / DELETE buttons"));
            bar.Controls.AddRange(new Control[] { L(Tr.T("ฐานข้อมูล", "Database")), _sqlDb, _sqlRun, _sqlRunAll, _sqlStop, L(Tr.T("แถวสูงสุด", "Max rows")), _sqlMax, Sep(), L(Tr.T("ตาราง", "Table")), _sqlTable });
            foreach (var k in SqlTemplates)
            {
                string kk = k;
                var b = Btn(kk, () => _ = InsertTemplate(kk));
                b.Padding = new Padding(6, 3, 6, 3);
                b.Margin = new Padding(0, 1, 4, 1);
                _sqlTip.SetToolTip(b, Tr.T($"สร้างคำสั่ง {kk} จากคอลัมน์ของตารางที่เลือก (ต่อท้ายในหน้าพิมพ์)", $"Generate a {kk} statement from the selected table's columns (appended to the editor)"));
                bar.Controls.Add(b);
            }
            bar.Controls.Add(Sep());
            bar.Controls.Add(Btn(Tr.T("ล้าง", "Clear"), () => { _sqlEdit.SelectAll(); _sqlEdit.SelectedText = ""; _sqlEdit.Focus(); }));

            // ---- ช่องพิมพ์ SQL
            _sqlEdit = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = Ui.Mono, MaxLength = 0, HideSelection = false,
                PlaceholderText = Tr.T("พิมพ์คำสั่ง SQL …   Ctrl+Enter = รันคำสั่งที่เคอร์เซอร์อยู่   F5 = รันทั้งหมด", "Type SQL …   Ctrl+Enter = run statement at cursor   F5 = run all"),
            };
            _sqlEdit.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.A) { _sqlEdit.SelectAll(); e.SuppressKeyPress = true; } };
            _sqlEdit.Leave += (s, e) => SaveSqlText();
            try { if (File.Exists(SqlFile)) _sqlEdit.Text = File.ReadAllText(SqlFile); } catch { }

            // ---- ผลลัพธ์
            var res = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            res.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            res.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            res.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            res.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _sqlTabs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 6, 0, 6) };
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _sqlGrid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, Visible = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            };
            _sqlGrid.RowTemplate.Height = Ui.Px(this, 24);
            _sqlGrid.CellFormatting += (s, e) =>
            {
                if (e.Value is DBNull || e.Value == null) { e.Value = "NULL"; e.CellStyle!.ForeColor = Theme.Muted; e.FormattingApplied = true; }
                else if (e.Value is string str && str.Length > 500) { e.Value = Ui.DisplayValue(str, 500); e.FormattingApplied = true; }
            };
            _sqlGrid.DataError += (s, e) => e.ThrowException = false;
            _sqlGrid.CellPainting += (s, e) =>
            {
                if (e.RowIndex != -1 || e.ColumnIndex < 0 || _sqlGrid.DataSource is not DataTable st || e.ColumnIndex >= _sqlGrid.Columns.Count) return;
                var dc = st.Columns[_sqlGrid.Columns[e.ColumnIndex].DataPropertyName];
                if (dc != null) PaintTypedHeader(_sqlGrid, e, dc.Caption, dc.ExtendedProperties["type"] as string ?? "", false);
            };
            var cm = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            cm.Items.Add(Tr.T("คัดลอก", "Copy"), null, (s, e) => CopyGrid(false));
            cm.Items.Add(Tr.T("คัดลอกพร้อมหัวคอลัมน์", "Copy with headers"), null, (s, e) => CopyGrid(true));
            _sqlGrid.ContextMenuStrip = cm;
            _sqlMsg = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.Mono };
            _sqlText = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false, MaxLength = 0, Font = Ui.Font };
            host.Controls.Add(_sqlGrid);
            host.Controls.Add(_sqlMsg);
            host.Controls.Add(_sqlText);
            _sqlInfo = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0) };
            // แท็บผลลัพธ์ทางซ้าย, ปุ่มสลับ Grid / Text ทางขวา
            var resTop = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            resTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            resTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(8, 5, 0, 5) };
            modes.Controls.AddRange(ModeButtons());
            resTop.Controls.Add(_sqlTabs, 0, 0);
            resTop.Controls.Add(modes, 1, 0);
            res.Controls.Add(resTop, 0, 0);
            res.Controls.Add(host, 0, 1);
            res.Controls.Add(_sqlInfo, 0, 2);

            _sqlSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = Ui.Px(this, 6) };
            _sqlSplit.Paint += (s, e) =>
            {
                var r = _sqlSplit.SplitterRectangle;
                using var p = new Pen(Theme.Border);
                e.Graphics.DrawLine(p, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
            };
            _sqlSplit.Panel1.Controls.Add(_sqlEdit);
            _sqlSplit.Panel2.Controls.Add(res);
            page.Controls.Add(bar, 0, 0);
            page.Controls.Add(_sqlSplit, 0, 1);
            Disposed += (s, e) => { SaveSqlText(); _sqlTip.Dispose(); };
            RebuildSqlTabs();
            return page;
        }

        /// <summary>สีของหน้า SQL ที่ Theme.Apply ไม่ได้จัดให้ (ฟอนต์ช่องพิมพ์ / แท็บผลลัพธ์)</summary>
        void ThemeSqlPage()
        {
            _sqlEdit.Font = Ui.Mono;
            _sqlMsg.Font = Ui.Mono;
            _sqlText.Font = Ui.Font;
            _sqlGrid.ContextMenuStrip!.Renderer = new Theme.Renderer();
            StyleSqlTabs();
            StyleStopButton();
        }

        /// <summary>ปุ่มหยุด: สีแดงเฉพาะตอนกำลังรัน ไม่อย่างนั้นเป็นสีจาง (ดูออกว่ากดไม่ได้)</summary>
        void StyleStopButton()
        {
            bool on = _sqlStop.Enabled;
            _sqlStop.ForeColor = on ? Theme.Err : Theme.Muted;
            _sqlStop.FlatAppearance.BorderColor = on ? Theme.Err : Theme.Border;
        }

        void InitSqlSplit()
        {
            if (_sqlSplitSet || _sqlSplit.Height < Ui.Px(this, 200)) return;
            _sqlSplitSet = true;
            try
            {
                _sqlSplit.Panel1MinSize = Ui.Px(this, 60);
                _sqlSplit.Panel2MinSize = Ui.Px(this, 90);
                int want = int.TryParse(Ui.GetSetting("sql_split"), out int v) ? Ui.Px(this, v) : _sqlSplit.Height * 40 / 100;
                _sqlSplit.SplitterDistance = Math.Max(_sqlSplit.Panel1MinSize, Math.Min(want, _sqlSplit.Height - _sqlSplit.Panel2MinSize - _sqlSplit.SplitterWidth));
            }
            catch { }
            _sqlSplit.SplitterMoved += (s, e) => Ui.SetSetting("sql_split", ((int)Math.Round(_sqlSplit.SplitterDistance * 96.0 / DeviceDpi)).ToString());
        }

        void SaveSqlText()
        {
            try
            {
                Directory.CreateDirectory(Ui.DataDir);
                File.WriteAllText(SqlFile, _sqlEdit.Text);
            }
            catch { }
        }

        // ------------------------------------------------------------------ เปิดหน้า

        /// <summary>เปิด SQL Editor ของการเชื่อมต่อ / ฐานข้อมูล / ตาราง ที่ระบุ (null = node ที่เลือกในต้นไม้)  keepText = ไม่เติม SELECT ให้</summary>
        void OpenSqlEditor(NodeInfo? ni, bool keepText = false)
        {
            ni ??= Info(_tree.SelectedNode);
            if (ni == null || ni.Kind == NodeKind.Dummy) { Ui.Info(this, Tr.T("เลือกการเชื่อมต่อ หรือฐานข้อมูล ทางซ้ายก่อน", "Select a connection or database on the left first")); return; }
            if (HasPending)
            {
                var r = AskPending();
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) { _ = SaveThen(() => OpenSqlEditor(ni, keepText)); return; }
                _dt!.RejectChanges();
            }
            bool sameConn = _sqlConn == ni.Conn;
            string db = ni.Kind is NodeKind.Db or NodeKind.Table or NodeKind.View ? ni.Db : sameConn ? SqlDbName : "";
            string? table = ni.Kind is NodeKind.Table or NodeKind.View ? ni.Obj : null;
            _sqlConn = ni.Conn;
            ShowPage(_sqlPage);
            BeginInvoke((Action)InitSqlSplit);
            UpdateSqlHeader(db);
            _ = LoadSqlDbs(db, table);
            if (table != null && !keepText)
                AppendSql("SELECT * FROM " + Q(table) + "\r\nLIMIT 100;");
            _sqlEdit.Focus();
            SetStatus(Tr.T("SQL Editor — Ctrl+Enter = รันคำสั่งที่เคอร์เซอร์อยู่ · F5 = รันทั้งหมด", "SQL Editor — Ctrl+Enter = run statement at cursor · F5 = run all"));
        }

        void UpdateSqlHeader(string? db = null)
        {
            if (_curPage != _sqlPage || _sqlConn == null) return;
            db ??= SqlDbName;
            SetHeader("SQL Editor", _sqlConn.Display + (db != "" ? "  ·  " + db : ""), "sql");
        }

        async Task LoadSqlDbs(string db, string? table)
        {
            var sc = _sqlConn;
            var c = sc == null ? null : Conn(sc);
            if (c == null) return;
            try
            {
                var dbs = await Task.Run(() => Query(c, null, "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME"));
                if (sc != _sqlConn) return;
                _sqlDbLoading = true;
                _sqlDb.BeginUpdate();
                _sqlDb.Items.Clear();
                _sqlDb.Items.Add(Tr.T("(ไม่ระบุ)", "(none)"));
                foreach (var r in dbs) _sqlDb.Items.Add(r[0]!);
                _sqlDb.EndUpdate();
                int i = db == "" ? 0 : _sqlDb.Items.IndexOf(db);
                _sqlDb.SelectedIndex = i >= 0 ? i : 0;
                _sqlDbLoading = false;
                UpdateSqlHeader();
                await LoadSqlTables(table);
            }
            catch (Exception ex) { SetStatus("✖ " + Err(ex)); }
            finally { _sqlDbLoading = false; }
        }

        async Task LoadSqlTables(string? select)
        {
            var sc = _sqlConn;
            string db = SqlDbName;
            string keep = select ?? _sqlTable.Text;
            _sqlTable.Items.Clear();
            if (sc == null || db == "") { _sqlTable.Text = ""; return; }
            var c = Conn(sc);
            if (c == null) return;
            try
            {
                var ts = await Task.Run(() => Query(c, null, "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME", ("@db", db)));
                if (sc != _sqlConn || db != SqlDbName) return;
                _sqlTable.BeginUpdate();
                _sqlTable.Items.Clear();
                foreach (var r in ts) _sqlTable.Items.Add(r[0]!);
                _sqlTable.EndUpdate();
                _sqlTable.Text = _sqlTable.Items.Contains(keep) ? keep : "";
            }
            catch (Exception ex) { SetStatus("✖ " + Err(ex)); }
        }

        /// <summary>ต่อท้ายข้อความในช่องพิมพ์ (เว้นบรรทัดจากของเดิม) แล้ววางเคอร์เซอร์ไว้ที่คำสั่งใหม่</summary>
        void AppendSql(string sql)
        {
            var t = _sqlEdit;
            string cur = t.Text;
            int trim = cur.Length;
            while (trim > 0 && char.IsWhiteSpace(cur[trim - 1])) trim--;
            // ลบช่องว่าง / บรรทัดว่างท้ายเดิมก่อน (ผ่าน SelectedText เพื่อให้ Ctrl+Z ยังใช้ได้)
            t.Select(trim, cur.Length - trim);
            string sep = trim == 0 ? "" : "\r\n\r\n";
            t.SelectedText = sep + sql + "\r\n";
            int start = trim + sep.Length;
            t.Select(start, 0);
            t.ScrollToCaret();
        }

        // ------------------------------------------------------------------ คำสั่งสำเร็จรูป (SELECT / INSERT / UPDATE / DELETE)

        async Task InsertTemplate(string kind, string? db = null, string? table = null)
        {
            var sc = _sqlConn;
            if (sc == null) return;
            db ??= SqlDbName;
            table ??= _sqlTable.Text.Trim();
            if (db == "" || table == "")
            {
                Ui.Info(this, Tr.T("เลือกฐานข้อมูล และพิมพ์ / เลือกชื่อตาราง ในช่อง \"ตาราง\" ก่อน", "Choose a database and type / pick a table in the \"Table\" box first"));
                _sqlTable.Focus();
                return;
            }
            var c = Conn(sc);
            if (c == null) return;
            List<string?[]> cols;
            try
            {
                cols = await Task.Run(() => Query(c, null,
                    "SELECT COLUMN_NAME, COLUMN_TYPE, COLUMN_KEY, EXTRA, IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION",
                    ("@db", db), ("@t", table)));
            }
            catch (Exception ex) { Ui.Error(this, Err(ex)); return; }
            if (cols.Count == 0) { Ui.Info(this, Tr.T($"ไม่พบตาราง {db}.{table}", $"Table {db}.{table} not found")); return; }
            string name = db == SqlDbName ? Q(table) : Q(db) + "." + Q(table);
            AppendSql(BuildTemplate(kind, name, cols));
            _sqlEdit.Focus();
            SetStatus(kind is "UPDATE" or "DELETE"
                ? Tr.T("ใส่ค่าใน WHERE ให้ครบก่อนรัน (ถ้าไม่ใส่ คำสั่งจะ error ไม่แก้ข้อมูล)", "Fill in the WHERE values before running (left empty it errors instead of changing data)")
                : Tr.T("แก้ค่าแล้วกด Ctrl+Enter เพื่อรันคำสั่งนี้", "Edit the values, then press Ctrl+Enter to run this statement"));
        }

        static string SqlPlaceholder(string type)
        {
            string t = type.ToLowerInvariant();
            if (Regex.IsMatch(t, @"^(tinyint|smallint|mediumint|int|integer|bigint|decimal|numeric|float|double|real|bit|year)")) return "0";
            if (t.StartsWith("datetime") || t.StartsWith("timestamp")) return "NOW()";
            if (t == "date") return "CURDATE()";
            if (t.StartsWith("time")) return "CURTIME()";
            if (t.Contains("blob") || t.StartsWith("binary") || t.StartsWith("varbinary") || t.StartsWith("geometry") || t.StartsWith("point")) return "NULL";
            return "''";
        }

        /// <summary>คำสั่งสำเร็จรูปจากคอลัมน์ (UPDATE / DELETE เว้นค่าใน WHERE ไว้ — รันทั้งที่ยังไม่ใส่ค่าจะ error ไม่แก้ข้อมูลทั้งตาราง)</summary>
        static string BuildTemplate(string kind, string tbl, List<string?[]> cols)
        {
            const string NL = "\r\n";
            bool Gen(string?[] c) => (c[3] ?? "").Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || (c[3] ?? "").Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase) || (c[3] ?? "").Contains("STORED", StringComparison.OrdinalIgnoreCase);
            bool Auto(string?[] c) => (c[3] ?? "").Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            var keys = cols.Where(c => c[2] == "PRI").ToList();
            if (keys.Count == 0) keys = cols.Where(c => c[2] == "UNI").Take(1).ToList();
            if (keys.Count == 0) keys = cols.Take(1).ToList();
            string where = "WHERE " + string.Join(NL + "  AND ", keys.Select(k => Q(k[0]!) + " = "));
            string keyHint = "   -- ← " + Tr.T("ใส่ค่า ", "value of ") + string.Join(", ", keys.Select(k => k[0] + " " + k[1]));

            // รายชื่อคอลัมน์ขึ้นบรรทัดใหม่ทุก ๆ ~90 ตัวอักษร
            string Wrap(IEnumerable<string> items, string indent)
            {
                var sb = new StringBuilder();
                int line = 0;
                foreach (var it in items)
                {
                    if (sb.Length > 0) { sb.Append(','); line++; if (line + it.Length > 90) { sb.Append(NL).Append(indent); line = 0; } else { sb.Append(' '); line++; } }
                    sb.Append(it);
                    line += it.Length;
                }
                return sb.ToString();
            }

            // ค่า + คอมเมนต์ชนิดคอลัมน์ ชิดกัน 1 คอลัมน์ต่อบรรทัด
            string Lines(List<string?[]> cs, Func<string?[], string> left)
            {
                var parts = cs.Select(c => left(c)).ToList();
                int w = parts.Count == 0 ? 0 : parts.Max(p => p.Length) + 1;
                var sb = new StringBuilder();
                for (int i = 0; i < cs.Count; i++)
                {
                    string p = parts[i] + (i < cs.Count - 1 ? "," : "");
                    sb.Append("  ").Append(p.PadRight(w + 1)).Append("-- ").Append(cs[i][0]).Append(' ').Append(cs[i][1]).Append(cs[i][4] == "NO" ? " NOT NULL" : "");
                    if (i < cs.Count - 1) sb.Append(NL);
                }
                return sb.ToString();
            }

            switch (kind)
            {
                case "SELECT":
                    return "SELECT " + Wrap(cols.Select(c => Q(c[0]!)), "       ") + NL + "FROM " + tbl + NL + "-- " + where.Replace(NL, " ") + NL + "LIMIT 100;";
                case "INSERT":
                {
                    var ins = cols.Where(c => !Auto(c) && !Gen(c)).ToList();
                    if (ins.Count == 0) ins = cols;
                    return "INSERT INTO " + tbl + " (" + NL + "  " + Wrap(ins.Select(c => Q(c[0]!)), "  ") + NL + ") VALUES (" + NL +
                           Lines(ins, c => SqlPlaceholder(c[1] ?? "")) + NL + ");";
                }
                case "UPDATE":
                {
                    var set = cols.Where(c => !keys.Contains(c) && !Gen(c)).ToList();
                    if (set.Count == 0) set = cols.Where(c => !Gen(c)).ToList();
                    return "UPDATE " + tbl + NL + "SET" + NL + Lines(set, c => Q(c[0]!) + " = " + SqlPlaceholder(c[1] ?? "")) + NL + where + ";" + keyHint;
                }
                default:
                    return "DELETE FROM " + tbl + NL + where + ";" + keyHint;
            }
        }

        // ------------------------------------------------------------------ แยกคำสั่ง

        /// <summary>
        /// แยกข้อความเป็นทีละคำสั่ง (ตัดที่ ; หรือ DELIMITER ที่ตั้งไว้) ไม่ตัดใน '...' "..." `...` และ comment (-- # /* */)
        /// คำสั่งที่มีแต่ comment ข้ามไป   Start/End = ตำแหน่งในข้อความ
        /// </summary>
        static List<SqlStmt> SplitSql(string s)
        {
            var list = new List<SqlStmt>();
            int i = 0, n = s.Length, codeStart = -1;
            string delim = ";";
            while (i < n)
            {
                char c = s[i];
                if (codeStart < 0 && (i == 0 || s[i - 1] == '\n') && i + 10 <= n && string.Compare(s, i, "DELIMITER", 0, 9, StringComparison.OrdinalIgnoreCase) == 0 && char.IsWhiteSpace(s[i + 9]))
                {
                    int eol = s.IndexOf('\n', i);
                    if (eol < 0) eol = n;
                    string d = s.Substring(i + 9, eol - i - 9).Trim();
                    if (d != "") delim = d;
                    i = eol;
                    continue;
                }
                if (c == '\'' || c == '"' || c == '`')
                {
                    if (codeStart < 0) codeStart = i;
                    i = SkipQuoted(s, i, c);
                    continue;
                }
                if ((c == '-' && i + 1 < n && s[i + 1] == '-' && (i + 2 >= n || char.IsWhiteSpace(s[i + 2]))) || c == '#')
                {
                    int eol = s.IndexOf('\n', i);
                    i = eol < 0 ? n : eol;
                    continue;
                }
                if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    bool exec = i + 2 < n && s[i + 2] == '!';   // /*! ... */ เป็นคำสั่งจริงของ MySQL
                    if (exec && codeStart < 0) codeStart = i;
                    int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    continue;
                }
                if (string.CompareOrdinal(s, i, delim, 0, delim.Length) == 0)
                {
                    if (codeStart >= 0) list.Add(new SqlStmt(s.Substring(codeStart, i - codeStart).TrimEnd(), codeStart, i + delim.Length));
                    codeStart = -1;
                    i += delim.Length;
                    continue;
                }
                if (codeStart < 0 && !char.IsWhiteSpace(c)) codeStart = i;
                i++;
            }
            if (codeStart >= 0)
            {
                string tail = s.Substring(codeStart).TrimEnd();
                if (tail != "") list.Add(new SqlStmt(tail, codeStart, n));
            }
            return list;
        }

        static int SkipQuoted(string s, int i, char q)
        {
            i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && q != '`') { i += 2; continue; }
                if (c == q)
                {
                    if (i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                    return i + 1;
                }
                i++;
            }
            return s.Length;
        }

        /// <summary>ข้อความคำสั่งที่ตัด comment และค่าในเครื่องหมายคำพูดออก (ใช้ตรวจคำสั่งอันตราย)</summary>
        static string CodeOnly(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '\'' || c == '"') { i = SkipQuoted(s, i, c); sb.Append("''"); continue; }
                if ((c == '-' && i + 1 < n && s[i + 1] == '-' && (i + 2 >= n || char.IsWhiteSpace(s[i + 2]))) || c == '#')
                {
                    int eol = s.IndexOf('\n', i);
                    i = eol < 0 ? n : eol;
                    continue;
                }
                if (c == '/' && i + 1 < n && s[i + 1] == '*' && !(i + 2 < n && s[i + 2] == '!'))
                {
                    int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>คำสั่งที่ควรถามก่อนรัน: UPDATE / DELETE ไม่มี WHERE, TRUNCATE, DROP</summary>
        static string? RiskOf(string sql)
        {
            string code = CodeOnly(sql).Trim();
            var m = Regex.Match(code, @"^\s*(\w+)");
            if (!m.Success) return null;
            string kw = m.Groups[1].Value.ToUpperInvariant();
            if (kw is "UPDATE" or "DELETE" && !Regex.IsMatch(code, @"\bWHERE\b", RegexOptions.IgnoreCase))
                return Tr.T($"• {kw} ไม่มี WHERE (มีผลกับทุกแถวในตาราง):  ", $"• {kw} without WHERE (affects every row):  ") + Preview(sql);
            if (kw == "TRUNCATE") return Tr.T("• TRUNCATE (ลบข้อมูลทั้งตาราง):  ", "• TRUNCATE (deletes all rows):  ") + Preview(sql);
            if (kw == "DROP") return Tr.T("• DROP (ลบถาวร):  ", "• DROP (permanent):  ") + Preview(sql);
            return null;
        }

        static string Preview(string sql)
        {
            string s = Regex.Replace(sql, @"\s+", " ").Trim();
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        static int LineOf(string s, int off)
        {
            int line = 1;
            for (int i = 0; i < off && i < s.Length; i++) if (s[i] == '\n') line++;
            return line;
        }

        // ------------------------------------------------------------------ รัน

        void StopSql()
        {
            _sqlStopReq = true;
            lock (_sqlLock)
            {
                try { _sqlCmd?.Cancel(); } catch { }
            }
            SetStatus(Tr.T("กำลังหยุด…", "Stopping…"));
        }

        /// <summary>รันคำสั่ง: all = ทุกคำสั่ง, ไม่อย่างนั้น = ส่วนที่เลือก หรือคำสั่งที่เคอร์เซอร์อยู่</summary>
        async Task RunSql(bool all)
        {
            if (_sqlRunning) return;
            var sc = _sqlConn;
            if (sc == null) return;
            string full = _sqlEdit.Text;
            int baseOff = 0;
            List<SqlStmt> stmts;
            if (_sqlEdit.SelectionLength > 0)
            {
                baseOff = _sqlEdit.SelectionStart;
                stmts = SplitSql(_sqlEdit.SelectedText);
            }
            else
            {
                stmts = SplitSql(full);
                if (!all && stmts.Count > 0)
                {
                    // คำสั่งที่เคอร์เซอร์อยู่ (อยู่ระหว่างคำสั่ง = คำสั่งก่อนหน้า)
                    int caret = _sqlEdit.SelectionStart, k = 0;
                    for (int i = 0; i < stmts.Count; i++) if (stmts[i].Start <= caret) k = i;
                    stmts = new List<SqlStmt> { stmts[k] };
                    _sqlEdit.Select(stmts[0].Start, 0);
                }
            }
            if (stmts.Count == 0) { SetStatus(Tr.T("ไม่มีคำสั่งให้รัน", "Nothing to run")); return; }

            var risky = stmts.Select(st => RiskOf(st.Sql)).Where(r => r != null).ToList();
            if (risky.Count > 0 && MessageBox.Show(this,
                    Tr.T("คำสั่งต่อไปนี้อาจแก้ / ลบข้อมูลจำนวนมาก หรือลบถาวร:\n\n", "These statements may change / delete many rows or drop objects permanently:\n\n") +
                    string.Join("\n", risky.Take(8)) + (risky.Count > 8 ? "\n…" : "") + Tr.T("\n\nรันต่อหรือไม่?", "\n\nRun anyway?"),
                    Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            var c = Conn(sc);
            if (c == null) return;
            string db = SqlDbName;
            int max = SqlMaxRows[Math.Max(0, _sqlMax.SelectedIndex)];
            SaveSqlText();
            _sqlRunning = true;
            _sqlStopReq = false;
            _sqlRun.Enabled = _sqlRunAll.Enabled = false;
            _sqlStop.Enabled = true;
            SetStatus(Tr.T($"กำลังรัน {stmts.Count:N0} คำสั่ง…", $"Running {stmts.Count:N0} statement(s)…"));

            var results = new List<SqlResult>();
            var log = new StringBuilder();
            bool failed = false, stopped = false, ddl = false;
            int done = 0;
            long affSum = 0;
            string errMsg = "";
            var total = Stopwatch.StartNew();
            string Sec(Stopwatch sw) => sw.Elapsed.TotalSeconds.ToString("0.000") + Tr.T(" วินาที", " s");
            await Task.Run(() =>
            {
                MySqlConnection conn;
                try { conn = LiveValue.Open(c, db == "" ? null : db); }
                catch (Exception ex) { log.AppendLine("✖ " + Err(ex)); errMsg = Err(ex); failed = true; return; }
                using (conn)
                {
                    for (int k = 0; k < stmts.Count; k++)
                    {
                        if (_sqlStopReq) { stopped = true; break; }
                        var st = stmts[k];
                        string head = $"#{k + 1}  " + Tr.T("บรรทัด ", "line ") + LineOf(full, baseOff + st.Start) + "  " + Preview(st.Sql);
                        var sw = Stopwatch.StartNew();
                        bool trunc = false;
                        try
                        {
                            using var cmd = new MySqlCommand(st.Sql, conn) { CommandTimeout = 0 };
                            lock (_sqlLock) _sqlCmd = cmd;
                            int sets = 0, affected = -1;
                            var reader = cmd.ExecuteReader();
                            try
                            {
                                do
                                {
                                    if (reader.FieldCount == 0) continue;
                                    var (t, more) = ReadSqlResult(reader, max);
                                    sets++;
                                    results.Add(new SqlResult { Sql = st.Sql, Table = t, Truncated = more, Max = max });
                                    log.AppendLine(head).Append("    → ").Append(Tr.T($"{t.Rows.Count:N0} แถว", $"{t.Rows.Count:N0} rows"))
                                       .Append(more ? Tr.T($" (แสดงแค่ {max:N0} แถวแรก — ใส่ LIMIT หรือเพิ่ม \"แถวสูงสุด\")", $" (first {max:N0} rows only — add LIMIT or raise \"Max rows\")") : "")
                                       .Append("  ·  ").AppendLine(Sec(sw));
                                    if (more) { trunc = true; try { cmd.Cancel(); } catch { } break; }
                                } while (reader.NextResult());
                                if (!trunc) affected = reader.RecordsAffected;
                                if (affected > 0) affSum += affected;
                            }
                            finally
                            {
                                // ตัดผลลัพธ์ที่เกินจำนวนแถวสูงสุด: server หยุดส่ง (KILL QUERY) แล้วทิ้ง error "interrupted"
                                try { reader.Dispose(); } catch (Exception) when (trunc) { }
                            }
                            if (sets == 0)
                            {
                                log.AppendLine(head).Append("    → ✔ ").Append(affected >= 0 ? Tr.T($"มีผล {affected:N0} แถว", $"{affected:N0} row(s) affected") : Tr.T("สำเร็จ", "OK"))
                                   .Append("  ·  ").AppendLine(Sec(sw));
                                if (Regex.IsMatch(CodeOnly(st.Sql), @"^\s*(CREATE|DROP|ALTER|RENAME)\b", RegexOptions.IgnoreCase)) ddl = true;
                            }
                            done++;
                        }
                        catch (Exception) when (_sqlStopReq)
                        {
                            stopped = true;
                            log.AppendLine(head).AppendLine(Tr.T("    ■ หยุดแล้ว", "    ■ Stopped"));
                            break;
                        }
                        catch (Exception ex)
                        {
                            failed = true;
                            errMsg = Tr.T($"คำสั่งที่ {k + 1}: ", $"Statement {k + 1}: ") + Err(ex);
                            log.AppendLine(head).Append("    ✖ ").AppendLine(Err(ex));
                            break;
                        }
                        finally { lock (_sqlLock) _sqlCmd = null; }
                    }
                }
            });
            total.Stop();
            if (IsDisposed) return;

            _sqlRunning = false;
            _sqlRun.Enabled = _sqlRunAll.Enabled = true;
            _sqlStop.Enabled = false;
            if (ddl) log.AppendLine(Tr.T("ℹ มีการเปลี่ยนโครงสร้าง — คลิกขวาที่ฐานข้อมูลทางซ้าย → รีเฟรช เพื่ออัปเดตรายการตาราง", "ℹ Structure changed — right-click the database on the left → Refresh to update the table list"));

            // ข้อความ: ต่อท้ายของเดิม (เก็บประวัติไว้ ไม่เกิน ~200,000 ตัวอักษร)
            string stamp = $"──── {DateTime.Now:HH:mm:ss}  {sc.Display}{(db != "" ? " / " + db : "")}  ────";
            string add = stamp + "\r\n" + log.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            string msg = _sqlMsg.Text + add;
            if (msg.Length > 200_000) msg = msg.Substring(msg.Length - 150_000);
            _sqlMsg.Text = msg;
            _sqlMsg.Select(_sqlMsg.TextLength, 0);
            _sqlMsg.ScrollToCaret();

            _sqlResults = results;
            RebuildSqlTabs();
            ShowSqlTab(failed || stopped || results.Count == 0 ? -1 : results.Count - 1);
            string time = total.Elapsed.TotalSeconds.ToString("0.000") + Tr.T(" วินาที", " s");
            if (failed) SetStatus(Tr.T($"✖ error ที่คำสั่งที่ {done + 1:N0} (สำเร็จ {done:N0} คำสั่ง) — ดูแท็บ ข้อความ", $"✖ Error in statement {done + 1:N0} ({done:N0} succeeded) — see the Messages tab"));
            else if (stopped) SetStatus(Tr.T($"■ หยุดแล้ว (สำเร็จ {done:N0} คำสั่ง)", $"■ Stopped ({done:N0} succeeded)"));
            else SetStatus(Tr.T($"✔ รัน {done:N0} คำสั่ง  ·  {time}", $"✔ Ran {done:N0} statement(s)  ·  {time}"));
            _sqlEdit.Focus();
        }

        static bool IsNumericType(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(sbyte) || t == typeof(byte) || t == typeof(uint) ||
            t == typeof(ulong) || t == typeof(ushort) || t == typeof(decimal) || t == typeof(double) || t == typeof(float);

        /// <summary>อ่านผลลัพธ์ 1 ชุด (ไม่เกิน max แถว; 0 = ทั้งหมด)  more = ยังมีแถวเหลือ</summary>
        static (DataTable Table, bool More) ReadSqlResult(MySqlDataReader r, int max)
        {
            var t = new DataTable();
            int fc = r.FieldCount;
            var num = new bool[fc];
            for (int i = 0; i < fc; i++)
            {
                string name = r.GetName(i);
                if (name == "") name = "?";
                string nm = name;
                for (int k = 2; t.Columns.Contains(nm); k++) nm = name + "_" + k;
                var col = t.Columns.Add(nm, typeof(object));
                col.Caption = name;
                try { num[i] = IsNumericType(r.GetFieldType(i)); } catch { }
                col.ExtendedProperties["num"] = num[i];
                // ชนิดของคอลัมน์ (แสดงใต้ชื่อในหัวตาราง แบบ pgAdmin)
                try { col.ExtendedProperties["type"] = r.GetDataTypeName(i).ToLowerInvariant(); } catch { }
            }
            int rows = 0;
            t.BeginLoadData();
            while (r.Read())
            {
                if (max > 0 && rows >= max) { t.EndLoadData(); return (t, true); }
                var vals = new object[fc];
                for (int i = 0; i < fc; i++) vals[i] = SqlCell(r, i, num[i]);
                t.Rows.Add(vals);
                rows++;
            }
            t.EndLoadData();
            return (t, false);
        }

        static object SqlCell(MySqlDataReader r, int i, bool num)
        {
            if (r.IsDBNull(i)) return DBNull.Value;
            if (num)
            {
                // เก็บเป็นตัวเลขจริง → คลิกหัวคอลัมน์แล้วเรียงตามค่า (ไม่ใช่ตามตัวอักษร)
                try
                {
                    object v = r.GetValue(i);
                    if (v is int or long or short or sbyte or byte or uint or ulong or ushort or decimal or double or float) return v;
                }
                catch { }
            }
            object? x = LiveValue.Read(r, i);
            return x is byte[] b ? Ui.DisplayValue(b) : x ?? DBNull.Value;
        }

        // ------------------------------------------------------------------ แท็บผลลัพธ์

        readonly List<Button> _sqlTabBtns = new List<Button>();

        void RebuildSqlTabs()
        {
            _sqlTabs.SuspendLayout();
            _sqlTabs.Controls.Clear();
            _sqlTabBtns.Clear();
            for (int i = 0; i < _sqlResults.Count; i++)
            {
                int k = i;
                var r = _sqlResults[i];
                var b = new Button { Text = Tr.T($"ผลลัพธ์ {i + 1}", $"Result {i + 1}") + $"  ({r.Table.Rows.Count:N0}{(r.Truncated ? "+" : "")})", AutoSize = true, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 4, 0), Tag = k };
                b.Click += (s, e) => ShowSqlTab(k);
                _sqlTip.SetToolTip(b, Preview(r.Sql));
                _sqlTabBtns.Add(b);
            }
            var msg = new Button { Text = Tr.T("ข้อความ", "Messages"), AutoSize = true, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 4, 0), Tag = -1 };
            msg.Click += (s, e) => ShowSqlTab(-1);
            _sqlTabBtns.Add(msg);
            _sqlTabs.Controls.AddRange(_sqlTabBtns.ToArray());
            _sqlTabs.ResumeLayout();
            StyleSqlTabs();
        }

        void StyleSqlTabs()
        {
            foreach (var b in _sqlTabBtns)
            {
                bool on = b.Tag is int k && k == _sqlTab;
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = on ? Theme.Accent : Theme.Surface;
                b.ForeColor = on ? Theme.AccentText : Theme.Text;
                b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
                b.FlatAppearance.MouseOverBackColor = on ? Theme.Accent : Theme.Hover;
            }
        }

        void ShowSqlTab(int k)
        {
            if (k >= _sqlResults.Count) k = -1;
            _sqlTab = k;
            StyleSqlTabs();
            if (k < 0)
            {
                _sqlGrid.Visible = false;
                _sqlText.Visible = false;
                _sqlMsg.Visible = true;
                _sqlInfo.Text = _sqlResults.Count == 0 ? Tr.T("ยังไม่มีผลลัพธ์แบบตาราง", "No result sets") : Tr.T($"{_sqlResults.Count:N0} ผลลัพธ์", $"{_sqlResults.Count:N0} result set(s)");
                return;
            }
            var r = _sqlResults[k];
            _sqlMsg.Visible = false;
            string info = Tr.T($"{r.Table.Rows.Count:N0} แถว  ·  {r.Table.Columns.Count:N0} คอลัมน์", $"{r.Table.Rows.Count:N0} rows  ·  {r.Table.Columns.Count:N0} columns") +
                          (r.Truncated ? Tr.T($"  ·  ⚠ แสดงแค่ {r.Max:N0} แถวแรก", $"  ·  ⚠ first {r.Max:N0} rows only") : "");
            if (_textMode)
            {
                _sqlGrid.Visible = false;
                _sqlText.Visible = true;
                _sqlText.Text = TextGrid.Format(r.Table, c => r.Table.Columns[c].ExtendedProperties["num"] is true,
                    v => v is null or DBNull ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "", _sqlText.Font);
                _sqlInfo.Text = info + Tr.T("  ·  โหมดข้อความ (Ctrl+A แล้ว Ctrl+C เพื่อคัดลอก)", "  ·  text view (Ctrl+A, Ctrl+C to copy)");
                return;
            }
            _sqlText.Visible = false;
            _sqlGrid.Visible = true;
            _sqlGrid.DataSource = null;
            _sqlGrid.Columns.Clear();
            _sqlGrid.DataSource = r.Table;
            foreach (DataGridViewColumn gc in _sqlGrid.Columns)
            {
                var dc = r.Table.Columns[gc.DataPropertyName];
                if (dc == null) continue;
                gc.HeaderText = dc.Caption;
                gc.SortMode = DataGridViewColumnSortMode.Automatic;
                if (dc.ExtendedProperties["num"] is true) gc.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                gc.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            }
            _sqlGrid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            TypedHeaders(_sqlGrid, i => { var dc = r.Table.Columns[_sqlGrid.Columns[i].DataPropertyName]; return (dc?.Caption ?? "", dc?.ExtendedProperties["type"] as string ?? "", false); });
            foreach (DataGridViewColumn gc in _sqlGrid.Columns)
            {
                if (gc.Width > Ui.Px(this, 420)) gc.Width = Ui.Px(this, 420);
                if (gc.Width < Ui.Px(this, 60)) gc.Width = Ui.Px(this, 60);
            }
            _sqlInfo.Text = info + Tr.T("  ·  คลิกหัวคอลัมน์เพื่อเรียง", "  ·  click a header to sort");
        }

        void CopyGrid(bool headers)
        {
            _sqlGrid.ClipboardCopyMode = headers ? DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText : DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            var d = _sqlGrid.GetClipboardContent();
            if (d != null) Clipboard.SetDataObject(d);
            _sqlGrid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        }
    }
}
