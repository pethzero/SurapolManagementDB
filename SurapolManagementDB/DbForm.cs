using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySqlConnector;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>
    /// หน้าต่างจัดการฐานข้อมูล MySQL / MariaDB (control panel แบบ DBeaver) ในหน้าเดียว:
    /// การเชื่อมต่อ → Databases (ตาราง / view) · Users · Administer (Session Manager) · System Info
    /// พร้อม CRUD ข้อมูล, สร้าง / แก้ / ลบฐานข้อมูล, จัดการผู้ใช้ และ Backup เป็น .sql
    /// </summary>
    public sealed partial class DbForm : Form
    {
        const int PageSize = 200;
        bool _inited, _embedded;
        readonly SplitContainer _split;

        /// <summary>ฝังเป็นส่วนหนึ่งของหน้าต่างหลัก (ไม่เปิดเป็นหน้าต่างแยก)</summary>
        public void Embed()
        {
            _embedded = true;
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Dock = DockStyle.Fill;
            _tbTheme.Visible = false;
        }

        /// <summary>เริ่มทำงานครั้งแรกตอนแสดง (โหลดรายการการเชื่อมต่อ)</summary>
        public void InitOnce()
        {
            if (_inited) return;
            _inited = true;
            // ความกว้างแผงซ้ายที่ลากไว้ครั้งล่าสุด (เก็บเป็นหน่วย 96 dpi)
            try { _split.Panel1MinSize = Ui.Px(this, 180); } catch { }
            int w = int.TryParse(Ui.GetSetting("db_tree_width"), out int v) ? v : 300;
            int px = Math.Max(_split.Panel1MinSize, Math.Min(Ui.Px(this, w), Math.Max(_split.Panel1MinSize, _split.Width - Ui.Px(this, 420))));
            _split.SplitterDistance = px;
            _split.SplitterMoved += (s, e) => Ui.SetSetting("db_tree_width", ((int)Math.Round(_split.SplitterDistance * 96.0 / DeviceDpi)).ToString());
            if (!_embedded) Theme.TitleBar(this);
            LoadTree();
            if (_conns.Count == 0 && _persist) BeginInvoke((Action)NewConn);
        }

        /// <summary>สร้างการเชื่อมต่อใหม่ (จากเมนูของหน้าต่างหลัก)</summary>
        public void NewConnection() => NewConn();

        /// <summary>ก่อนปิดโปรแกรม: ถามเรื่องข้อมูลที่ยังไม่บันทึก  false = อย่าเพิ่งปิด (retry จะถูกเรียกหลังบันทึกเสร็จ)</summary>
        public bool CanClose(Action retry)
        {
            if (!HasPending) return true;
            var r = AskPending();
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) { _ = SaveThen(retry); return false; }
            _dt!.RejectChanges();
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S) && _tablePage.Visible) { _ = Save(); return true; }
            if (_curPage == _sqlPage && keyData is (Keys.Control | Keys.Enter) or Keys.F9) { _ = RunSql(false); return true; }
            if (_curPage == _sqlPage && keyData == Keys.F5) { _ = RunSql(true); return true; }
            if (keyData == Keys.F5) { _ = RefreshSelected(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        enum NodeKind { Conn, DbFolder, Db, Table, View, UsersFolder, User, AdminFolder, Sessions, InfoFolder, Info, Dummy }

        sealed class NodeInfo
        {
            public NodeKind Kind;
            public SavedConn Conn = null!;
            public string Db = "", Obj = "", Host = "", Sql = "";
            /// <summary>ขนาดข้อมูล + index (bytes) ของฐานข้อมูล / ตาราง  -1 = ไม่แสดง</summary>
            public long Size = -1;
        }

        /// <summary>ข้อมูลคอลัมน์ของตารางที่เปิดอยู่</summary>
        sealed class ColMeta
        {
            public string Name = "", DataType = "", ColumnType = "", Key = "", Extra = "";
            public bool Nullable;
            /// <summary>BLOB / geometry: ไม่โหลดค่าจริงมาแสดง (แสดงแค่ขนาด)</summary>
            public bool Lazy;
            public bool Binary, Generated, Float;
        }

        /// <summary>ค่า BLOB ที่ยังไม่ได้โหลด (รู้แค่ขนาด)</summary>
        sealed class BlobRef
        {
            public long Size;
            public override string ToString() => "<BLOB " + Ui.HumanSize(Size) + ">";
        }

        static readonly string[] SystemDbs = { "information_schema", "mysql", "performance_schema", "sys" };

        static readonly (string Th, string En, string Sql, string Icon)[] InfoItems =
        {
            ("Session Status", "Session Status", "SHOW SESSION STATUS", "status"),
            ("Global Status", "Global Status", "SHOW GLOBAL STATUS", "status"),
            ("Session Variables", "Session Variables", "SHOW SESSION VARIABLES", "var"),
            ("Global Variables", "Global Variables", "SHOW GLOBAL VARIABLES", "var"),
            ("Engines", "Engines", "SHOW ENGINES", "engine"),
            ("Charsets", "Charsets", "SHOW CHARACTER SET", "list"),
            ("Collations", "Collations", "SHOW COLLATION", "list"),
            ("User Privileges", "User Privileges", "SELECT * FROM information_schema.USER_PRIVILEGES", "key"),
            ("Plugin", "Plugins", "SHOW PLUGINS", "plugin"),
        };

        readonly bool _persist;
        readonly List<SavedConn> _conns;
        readonly Dictionary<SavedConn, string> _pw = new Dictionary<SavedConn, string>();

        readonly ToolStrip _tool;
        readonly List<(ToolStripButton Btn, string Glyph)> _toolIcons = new List<(ToolStripButton, string)>();
        readonly ToolStripButton _tbEdit, _tbRemove, _tbTheme;
        readonly ImageList _icons;
        readonly TreeView _tree;
        readonly TextBox _txtFind;
        readonly Panel _hdr;
        readonly PictureBox _hdrIcon;
        string _hdrIconKey = "";
        readonly Label _hdrTitle, _hdrSub;
        readonly Panel _pages;

        // หน้า: รายการ (การเชื่อมต่อ / ฐานข้อมูล / ผู้ใช้)
        readonly TableLayoutPanel _listPage, _card;
        readonly FlowLayoutPanel _actions;
        readonly ListView _lv;

        // หน้า: ตารางผลลัพธ์ (System Info / Session / สิทธิ์ผู้ใช้)
        readonly TableLayoutPanel _gridPage;
        readonly FlowLayoutPanel _gActions;
        readonly TextBox _gFilter;
        readonly DataGridView _gGrid;
        readonly Label _gInfo;
        readonly Timer _sessionTimer = new Timer { Interval = 3000 };

        // หน้า: ตาราง (ข้อมูล / โครงสร้าง / DDL)
        readonly TableLayoutPanel _tablePage;
        readonly Button[] _tabBtns = new Button[3];
        readonly Control[] _tabPanels = new Control[3];
        readonly DataGridView _grid, _gridCols;
        readonly TextBox _txtDdl, _txtWhere;
        readonly Label _lblPage, _lblData;
        readonly Button _btnPrev, _btnNext;
        readonly ToolStripStatusLabel _status;

        // ตารางที่เปิดอยู่
        SavedConn? _sc;
        string _db = "", _tbl = "";
        bool _isView, _realKey;
        List<ColMeta> _cols = new List<ColMeta>();
        List<int> _keyIdx = new List<int>();
        DataTable? _dt;
        int _offset;
        bool _hasMore;
        string _where = "";
        string? _orderCol;
        bool _orderDesc;
        bool _busy;

        public DbForm(bool persist)
        {
            _persist = persist;
            _conns = persist ? ConnStore.Load() : new List<SavedConn>();
            Text = Tr.T("ฐานข้อมูล - ", "Databases - ") + Ui.AppName;
            Font = Ui.Font;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1320, 820);
            MinimumSize = new Size(960, 600);
            if (Ui.AppIcon != null) Icon = Ui.AppIcon;
            int isz = Ui.Px(this, 18);

            // ---- ไอคอน
            _icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(isz, isz) };
            BuildIcons();

            // ---- แถบเครื่องมือ
            _tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 4, 8, 4), ImageScalingSize = new Size(isz, isz) };
            ToolStripButton TB(string text, string glyph, Action a, string tip)
            {
                var b = new ToolStripButton(text) { ToolTipText = tip, Padding = new Padding(4, 3, 6, 3), Margin = new Padding(0, 0, 2, 0), DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
                b.Click += (s, e) => a();
                _toolIcons.Add((b, glyph));
                return b;
            }
            var tbNew = TB(Tr.T("เชื่อมต่อใหม่", "New connection"), "", NewConn, Tr.T("สร้างการเชื่อมต่อ MySQL / MariaDB", "Create a MySQL / MariaDB connection"));
            _tbEdit = TB(Tr.T("แก้ไข", "Edit"), "", EditConn, Tr.T("แก้ไขการเชื่อมต่อที่เลือก", "Edit the selected connection"));
            _tbRemove = TB(Tr.T("ลบการเชื่อมต่อ", "Remove"), "", RemoveConn, Tr.T("ลบการเชื่อมต่อออกจากรายการ (ไม่ลบอะไรบน server)", "Remove the connection from the list (nothing on the server is deleted)"));
            var tbRefresh = TB(Tr.T("รีเฟรช", "Refresh"), "", () => _ = RefreshSelected(), "F5");
            _tbTheme = TB("", Theme.Dark ? "" : "", ToggleTheme, Tr.T("สลับธีม มืด / สว่าง", "Switch dark / light theme"));
            _tbTheme.Alignment = ToolStripItemAlignment.Right;
            _tbTheme.DisplayStyle = ToolStripItemDisplayStyle.Image;
            var tbSql = TB("SQL Editor", "", () => OpenSqlEditor(null), Tr.T("หน้าพิมพ์คำสั่ง SQL (SELECT / INSERT / UPDATE / DELETE …)  Ctrl+Enter = รัน", "Type and run SQL (SELECT / INSERT / UPDATE / DELETE …)  Ctrl+Enter = run"));
            // ตัว demo ไม่มีหน้าต่างหลัก (เมนูภาษา) → ปุ่มสลับภาษาบน toolbar (จำไว้ แล้วเปิดโปรแกรมใหม่)
            var tbLang = new ToolStripButton(Tr.En ? "ไทย" : "EN") { Alignment = ToolStripItemAlignment.Right, ToolTipText = Tr.T("Switch to English", "เปลี่ยนเป็นภาษาไทย"), Padding = new Padding(6, 3, 6, 3) };
            tbLang.Click += (s, e) =>
            {
                if (HasPending && AskPending() != DialogResult.No) return;
                Ui.SetSetting("lang", Tr.En ? "th" : "en");
                Application.Restart();
            };
            _tool.Items.AddRange(new ToolStripItem[] { tbNew, _tbEdit, _tbRemove, new ToolStripSeparator(), tbRefresh, new ToolStripSeparator(), tbSql, _tbTheme, tbLang });

            var ss = new StatusStrip { SizingGrip = false };
            _status = new ToolStripStatusLabel(Tr.T("พร้อม", "Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            ss.Items.Add(_status);

            // ---- ซ้าย: ค้นหา + tree
            // แผงซ้ายลากปรับความกว้างได้ (เส้นแบ่งกว้างพอให้จับด้วยเมาส์ได้ง่าย, จำความกว้างไว้)
            var split = _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = Ui.Px(this, 6) };
            split.Paint += (s, e) =>
            {
                var r = split.SplitterRectangle;
                using var p = new Pen(Theme.Border);
                e.Graphics.DrawLine(p, r.Left + r.Width / 2, r.Top, r.Left + r.Width / 2, r.Bottom);
            };
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 8, 0, 8) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _txtFind = new TextBox { Dock = DockStyle.Fill, PlaceholderText = Tr.T("ค้นหาฐานข้อมูล / ตาราง…", "Filter databases / tables…"), Margin = new Padding(0, 0, 0, 6) };
            _txtFind.TextChanged += (s, e) => { _findTimer.Stop(); _findTimer.Start(); };
            _txtFind.KeyDown += OnFindKey;
            _findTimer.Tick += (s, e) => { _findTimer.Stop(); FilterTree(); };
            new ToolTip().SetToolTip(_txtFind, Tr.T("พิมพ์บางส่วนของชื่อตาราง / ฐานข้อมูล (ตารางของฐานที่เปิดไว้แล้ว)\nEnter = เปิดตารางแรกที่เจอ   ↓ = ไปที่รายการ   Esc = ล้าง",
                                                    "Type part of a table / database name (tables of opened databases)\nEnter = open the first match   ↓ = go to the list   Esc = clear"));
            _tree = new TreeView
            {
                Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, HotTracking = true,
                ImageList = _icons, ItemHeight = Ui.Px(this, 26), BorderStyle = BorderStyle.None, Indent = Ui.Px(this, 18),
                DrawMode = TreeViewDrawMode.OwnerDrawAll,
            };
            _tree.DrawNode += OnDrawNode;
            _tree.Resize += (s, e) => _tree.Invalidate();
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.BeforeSelect += OnBeforeSelect;
            _tree.AfterSelect += (s, e) => { if (!_filtering) _ = OnSelect(e.Node); };
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) { _tree.SelectedNode = e.Node; ShowNodeMenu(e.Node, e.Location); }
                // คลิก node เดิมซ้ำตอนอยู่หน้า SQL Editor → กลับไปหน้าของ node นั้น
                else if (e.Button == MouseButtons.Left && e.Node == _tree.SelectedNode && _curPage == _sqlPage &&
                         _tree.HitTest(e.Location).Location != TreeViewHitTestLocations.PlusMinus) _ = OnSelect(e.Node);
            };
            left.Controls.Add(_txtFind, 0, 0);
            left.Controls.Add(_tree, 0, 1);
            split.Panel1.Controls.Add(left);

            // ---- ขวา: หัวเรื่อง + หน้าเนื้อหา
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _hdr = new Panel { Dock = DockStyle.Fill, Height = Ui.Px(this, 64), Padding = new Padding(16, 10, 16, 8), Margin = new Padding(0), Tag = "header" };
            _hdrIcon = new PictureBox { Size = new Size(Ui.Px(this, 32), Ui.Px(this, 32)), Location = new Point(Ui.Px(this, 16), Ui.Px(this, 16)), SizeMode = PictureBoxSizeMode.CenterImage, Tag = "header" };
            _hdrTitle = new Label { AutoSize = true, Font = new Font(Ui.Font.FontFamily, 14f, FontStyle.Bold), Location = new Point(Ui.Px(this, 58), Ui.Px(this, 8)), BackColor = Color.Transparent };
            _hdrSub = new Label { AutoSize = true, Location = new Point(Ui.Px(this, 60), Ui.Px(this, 38)), Tag = "muted", BackColor = Color.Transparent };
            _hdr.Controls.AddRange(new Control[] { _hdrIcon, _hdrTitle, _hdrSub });
            _hdr.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, _hdr.Height - 1, _hdr.Width, _hdr.Height - 1); };
            _pages = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            right.Controls.Add(_hdr, 0, 0);
            right.Controls.Add(_pages, 0, 1);
            split.Panel2.Controls.Add(right);

            // ---- หน้า: รายการ
            _listPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            _listPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _listPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _listPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _listPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 12), Tag = "card", Visible = false };
            _actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            _lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Px(this, 28)) } };
            Theme.OwnerDrawList(_lv);
            _lv.DoubleClick += (s, e) => OpenListItem();
            _lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OpenListItem(); };
            _lv.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowListMenu(e.Location); };
            _lv.ColumnClick += (s, e) => SortList(e.Column);
            _listPage.Controls.Add(_card, 0, 0);
            _listPage.Controls.Add(_actions, 0, 1);
            _listPage.Controls.Add(_lv, 0, 2);

            // ---- หน้า: ตารางผลลัพธ์
            _gridPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Visible = false };
            _gridPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _gridPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _gridPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _gridPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _gActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            _gFilter = new TextBox { Width = Ui.Px(this, 260), PlaceholderText = Tr.T("กรอง…", "Filter…"), Margin = new Padding(0, 3, 10, 3) };
            _gFilter.TextChanged += (s, e) => ApplyGridFilter();
            _gGrid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AllowUserToResizeRows = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            };
            _gGrid.RowTemplate.Height = Ui.Px(this, 26);
            _gInfo = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0) };
            _gridPage.Controls.Add(_gActions, 0, 0);
            _gridPage.Controls.Add(_gGrid, 0, 1);
            _gridPage.Controls.Add(_gInfo, 0, 2);

            // ---- หน้า: ตาราง
            _tablePage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Visible = false };
            _tablePage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _tablePage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _tablePage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            string[] tabNames = { Tr.T("ข้อมูล", "Data"), Tr.T("โครงสร้าง", "Structure"), "DDL" };
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                _tabBtns[i] = new Button { Text = tabNames[i], AutoSize = true, Padding = new Padding(14, 3, 14, 3), Margin = new Padding(0, 0, 4, 0) };
                _tabBtns[i].Click += (s, e) => SelectTab(k);
                tabBar.Controls.Add(_tabBtns[i]);
            }
            var tabHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };

            var dl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            dl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            bar.Controls.Add(new Label { Text = "WHERE", AutoSize = true, Margin = new Padding(0, 7, 4, 0), Tag = "muted" });
            _txtWhere = new TextBox { Width = Ui.Px(this, 260), Margin = new Padding(0, 3, 4, 3), PlaceholderText = "name LIKE '%abc%'" };
            _txtWhere.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ApplyFilter(); } };
            new ToolTip().SetToolTip(_txtWhere, Tr.T("เงื่อนไขแบบ SQL เช่น  name LIKE '%สม%' AND id > 10   แล้วกด Enter", "SQL condition, e.g.  name LIKE '%abc%' AND id > 10   then press Enter"));
            bar.Controls.Add(_txtWhere);
            bar.Controls.Add(Btn(Tr.T("กรอง", "Filter"), () => _ = ApplyFilter()));
            _btnPrev = Btn("◀", () => _ = Page(-1));
            _lblPage = new Label { AutoSize = true, Margin = new Padding(2, 7, 2, 0) };
            _btnNext = Btn("▶", () => _ = Page(1));
            bar.Controls.AddRange(new Control[] { Sep(), _btnPrev, _lblPage, _btnNext, Sep() });
            bar.Controls.Add(Btn(Tr.T("รีเฟรช", "Refresh"), () => _ = ReloadPage(true)));
            dl.Controls.Add(bar, 0, 0);
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToResizeRows = false, RowHeadersWidth = Ui.Px(this, 28),
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            };
            _grid.RowTemplate.Height = Ui.Px(this, 26);
            _grid.DefaultCellStyle.NullValue = "NULL";
            _grid.DefaultCellStyle.DataSourceNullValue = DBNull.Value;
            _grid.CellFormatting += OnCellFormatting;
            _grid.ColumnHeaderMouseClick += (s, e) => _ = SortBy(e.ColumnIndex);
            _grid.DataError += (s, e) => { e.ThrowException = false; SetStatus("✖ " + e.Exception?.Message); };
            _grid.CellValueChanged += (s, e) => UpdateDirty();
            _grid.UserDeletedRow += (s, e) => UpdateDirty();
            _grid.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0 && !_grid[e.ColumnIndex, e.RowIndex].Selected)
                {
                    _grid.ClearSelection();
                    _grid.CurrentCell = _grid[e.ColumnIndex, e.RowIndex];
                }
            };
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && IsBlobCol(e.ColumnIndex)) _ = OpenBlob(e.RowIndex, e.ColumnIndex); };
            var cm = new ContextMenuStrip();
            cm.Items.Add(Tr.T("คัดลอก", "Copy"), null, (s, e) => { var d = _grid.GetClipboardContent(); if (d != null) Clipboard.SetDataObject(d); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tr.T("เปิดดูไฟล์ / รูป (BLOB)", "Open file / image (BLOB)"), null, (s, e) => { var c = _grid.CurrentCell; if (c != null && IsBlobCol(c.ColumnIndex)) _ = OpenBlob(c.RowIndex, c.ColumnIndex); });
            cm.Items.Add(Tr.T("บันทึก BLOB เป็นไฟล์…", "Save BLOB to file…"), null, (s, e) => _ = SaveBlob());
            _grid.ContextMenuStrip = cm;
            dl.Controls.Add(_grid, 0, 1);
            _lblData = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0), Tag = "keep" };
            dl.Controls.Add(_lblData, 0, 2);
            _tabPanels[0] = dl;
            _gridCols = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, Visible = false,
            };
            _gridCols.RowTemplate.Height = Ui.Px(this, 26);
            _tabPanels[1] = _gridCols;
            _txtDdl = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false };
            _tabPanels[2] = _txtDdl;
            tabHost.Controls.AddRange(_tabPanels);
            _tablePage.Controls.Add(tabBar, 0, 0);
            _tablePage.Controls.Add(tabHost, 0, 1);

            _sqlPage = BuildSqlPage();
            _pages.Controls.AddRange(new Control[] { _listPage, _gridPage, _tablePage, _sqlPage });
            Controls.Add(split);
            Controls.Add(_tool);
            Controls.Add(ss);

            ApplyTheme();
            Shown += (s, e) => InitOnce();
            Theme.Changed += OnThemeChanged;
            Disposed += (s, e) => { Theme.Changed -= OnThemeChanged; _sessionTimer.Dispose(); };
            ShowList(Tr.T("ยินดีต้อนรับ", "Welcome"), Tr.T("เลือกการเชื่อมต่อทางซ้าย หรือกด เชื่อมต่อใหม่", "Select a connection on the left or click New connection"), "conn");
        }

        // ------------------------------------------------------------------ ธีม / ไอคอน

        void BuildIcons()
        {
            int sz = _icons.ImageSize.Width;
            _icons.Images.Clear();
            void Add(string key, string glyph, Color c) { var b = Ui.Glyph(glyph, sz, c); if (b != null) _icons.Images.Add(key, b); }
            Add("conn", "", Theme.IconConn);
            Add("folder", "", Theme.IconFolder);
            Add("db", "", Theme.IconDb);
            Add("table", "", Theme.IconTable);
            Add("view", "", Theme.IconView);
            Add("users", "", Theme.IconTable);
            Add("user", "", Theme.IconTable);
            Add("admin", "", Theme.IconFolder);
            Add("session", "", Theme.IconConn);
            Add("info", "", Theme.IconInfo);
            Add("status", "", Theme.IconInfo);
            Add("var", "", Theme.IconInfo);
            Add("engine", "", Theme.IconInfo);
            Add("list", "", Theme.IconInfo);
            Add("key", "", Theme.IconDb);
            Add("plugin", "", Theme.IconInfo);
            Add("dummy", "", Theme.Muted);
            BuildExtraIcons();
        }

        Bitmap? _chevRight, _chevDown;

        /// <summary>ไอคอนเพิ่มเติม: SQL Editor และลูกศรขยาย / ยุบ ของต้นไม้ (วาดเอง)</summary>
        void BuildExtraIcons()
        {
            var b = Ui.Glyph("", _icons.ImageSize.Width, Theme.IconTable);
            if (b != null) _icons.Images.Add("sql", b);
            int cz = Ui.Px(this, 12);
            _chevRight?.Dispose(); _chevDown?.Dispose();
            _chevRight = Ui.Glyph("", cz, Theme.Muted);
            _chevDown = Ui.Glyph("", cz, Theme.Muted);
        }

        void ApplyTheme()
        {
            Theme.Apply(this);
            foreach (var (b, g) in _toolIcons) b.Image = Ui.Glyph(g, _tool.ImageScalingSize.Width, Theme.Text);
            foreach (Control c in new Control[] { _grid, _gridCols, _gGrid, _sqlGrid }) Theme.StyleGrid((DataGridView)c);
            _txtDdl.Font = Ui.Font;
            ThemeSqlPage();
            _grid.ContextMenuStrip!.Renderer = new Theme.Renderer();
            BuildIcons();
            _tree.ImageList = null;
            _tree.ImageList = _icons;
            SelectTab(Array.FindIndex(_tabPanels, p => p.Visible) is int i && i >= 0 ? i : 0);
            _hdr.Invalidate();
            _lv.Invalidate();
            _grid.Invalidate();
        }

        void ToggleTheme() => Theme.Set(!Theme.Dark);

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            _toolIcons.RemoveAll(x => x.Btn == _tbTheme);
            _toolIcons.Add((_tbTheme, Theme.Dark ? "" : ""));
            ApplyTheme();
            if (_icons.Images.ContainsKey(_hdrIconKey)) _hdrIcon.Image = _icons.Images[_hdrIconKey];
            UpdateDirty();
        }

        Button Btn(string text, Action a, string tag = "")
        {
            var b = Ui.MakeButton(text, a);
            b.Margin = new Padding(0, 1, 6, 1);
            b.Padding = new Padding(10, 3, 10, 3);
            b.Tag = tag;
            return b;
        }

        static Label Sep() => new Label { Text = "|", AutoSize = true, Margin = new Padding(4, 7, 8, 0), Tag = "muted" };

        void SetStatus(string? s) => _status.Text = s ?? "";

        static string Q(string n) => LiveValue.Q(n);

        static string Err(Exception ex) => (ex is AggregateException a && a.InnerException != null ? a.InnerException : ex).Message;

        void SetHeader(string title, string sub, string icon)
        {
            _hdrTitle.Text = title;
            _hdrSub.Text = sub;
            _hdrIcon.Image = _icons.Images.ContainsKey(icon) ? _icons.Images[icon] : null;
            _hdrIconKey = icon;
        }

        /// <summary>หน้าที่แสดงอยู่ (ไม่ใช้ .Visible เพราะเป็น false ได้ถ้าหน้าต่างหลักซ่อน / กำลังสลับหน้า)</summary>
        Control? _curPage;

        void ShowPage(Control page)
        {
            _curPage = page;
            foreach (Control c in _pages.Controls) c.Visible = c == page;
            if (page != _gridPage) _sessionTimer.Stop();
        }

        void SelectTab(int k)
        {
            for (int i = 0; i < 3; i++)
            {
                _tabPanels[i].Visible = i == k;
                var b = _tabBtns[i];
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = i == k ? Theme.Accent : Theme.Surface;
                b.ForeColor = i == k ? Theme.AccentText : Theme.Text;
                b.FlatAppearance.BorderColor = i == k ? Theme.Accent : Theme.Border;
                b.FlatAppearance.MouseOverBackColor = i == k ? Theme.Accent : Theme.Hover;
            }
        }

        // ------------------------------------------------------------------ การเชื่อมต่อ

        void SaveConns()
        {
            if (!_persist) return;
            try { ConnStore.Save(_conns); }
            catch (Exception ex) { Ui.Error(this, Tr.T("บันทึกรายการการเชื่อมต่อไม่ได้\n\n", "Cannot save the connection list\n\n") + ex.Message); }
        }

        /// <summary>หน้าต่างเล็กตามธีม (ใช้กับกล่องถามรหัสผ่าน / สร้างฐานข้อมูล / ยืนยันการลบ)</summary>
        Form SmallDialog(string title)
        {
            var f = new Form
            {
                Text = title, Font = Ui.Font, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false,
            };
            if (Ui.AppIcon != null) f.Icon = Ui.AppIcon;
            f.Shown += (s, e) => Theme.TitleBar(f);
            return f;
        }

        /// <summary>รหัสผ่านของการเชื่อมต่อ (ที่จำไว้ / ที่กรอกไว้ในรอบนี้ / ถามใหม่)  null = ยกเลิก</summary>
        string? Password(SavedConn sc)
        {
            if (_pw.TryGetValue(sc, out var p)) return p;
            if (sc.SavePassword && sc.PasswordEnc != "") return _pw[sc] = ConnStore.Unprotect(sc.PasswordEnc);
            using var f = SmallDialog(sc.Display);
            var fl = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16) };
            var txt = new TextBox { Width = Ui.Px(this, 280), UseSystemPasswordChar = true, Margin = new Padding(0, 6, 0, 10) };
            var ok = Btn("OK", () => { f.DialogResult = DialogResult.OK; f.Close(); }, "primary");
            fl.Controls.AddRange(new Control[] { new Label { Text = Tr.T("รหัสผ่านของ ", "Password for ") + sc.User + "@" + sc.Host, AutoSize = true }, txt, ok });
            f.Controls.Add(fl);
            f.AcceptButton = ok;
            Theme.Apply(f);
            if (f.ShowDialog(this) != DialogResult.OK) return null;
            return _pw[sc] = txt.Text;
        }

        DbConnInfo? Conn(SavedConn sc, string? db = null)
        {
            var p = Password(sc);
            return p == null ? null : sc.ToInfo(p, db ?? "");
        }

        void NewConn()
        {
            using var d = new ConnDialog(null, "");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _conns.Add(d.Result);
            _pw[d.Result] = d.Password;
            SaveConns();
            var n = AddConnNode(d.Result);
            _tree.SelectedNode = n;
            n.Expand();
        }

        void EditConn()
        {
            var sc = Info(_tree.SelectedNode)?.Conn;
            if (sc == null) return;
            using var d = new ConnDialog(sc, Password(sc) ?? "");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            int i = _conns.IndexOf(sc);
            _conns[i] = d.Result;
            _pw.Remove(sc);
            _pw[d.Result] = d.Password;
            SaveConns();
            LoadTree();
        }

        void RemoveConn()
        {
            var sc = Info(_tree.SelectedNode)?.Conn;
            if (sc == null) return;
            if (MessageBox.Show(this, Tr.T($"ลบการเชื่อมต่อ \"{sc.Display}\" ออกจากรายการ?\n(ฐานข้อมูลบน server ไม่ถูกลบ)", $"Remove connection \"{sc.Display}\" from the list?\n(nothing on the server is deleted)"),
                Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _conns.Remove(sc);
            _pw.Remove(sc);
            SaveConns();
            LoadTree();
        }

        // ------------------------------------------------------------------ tree

        static NodeInfo? Info(TreeNode? n) => n?.Tag as NodeInfo;

        static TreeNode Node(string text, string icon, NodeInfo ni, bool lazy)
        {
            var n = new TreeNode(text) { Tag = ni, ImageKey = icon, SelectedImageKey = icon };
            if (lazy) n.Nodes.Add(new TreeNode("…") { Tag = new NodeInfo { Kind = NodeKind.Dummy }, ImageKey = "dummy", SelectedImageKey = "dummy" });
            return n;
        }

        void LoadTree()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (var sc in _conns) AddConnNode(sc);
            _tree.EndUpdate();
            UpdateToolButtons(null);
        }

        TreeNode AddConnNode(SavedConn sc)
        {
            var n = Node(sc.Display, "conn", new NodeInfo { Kind = NodeKind.Conn, Conn = sc }, true);
            _tree.Nodes.Add(n);
            return n;
        }

        /// <summary>โครงของการเชื่อมต่อ (แบบ DBeaver): Databases · Users · Administer · System Info</summary>
        void FillConnChildren(TreeNode n, SavedConn sc)
        {
            n.Nodes.Clear();
            n.Nodes.Add(Node(Tr.T("ฐานข้อมูล", "Databases"), "folder", new NodeInfo { Kind = NodeKind.DbFolder, Conn = sc }, true));
        }

        void OnBeforeExpand(object? s, TreeViewCancelEventArgs e)
        {
            var n = e.Node;
            if (n == null || n.Nodes.Count != 1 || Info(n.Nodes[0])?.Kind != NodeKind.Dummy) return;
            if (Info(n)?.Kind == NodeKind.Conn)
            {
                FillConnChildren(n, Info(n)!.Conn);
                n.Nodes[0].Expand();
                return;
            }
            e.Cancel = true;
            _ = LoadChildren(n);
        }

        async Task LoadChildren(TreeNode n)
        {
            var ni = Info(n)!;
            if (ni.Kind == NodeKind.Conn)
            {
                if (n.Nodes.Count == 1 && Info(n.Nodes[0])?.Kind == NodeKind.Dummy) FillConnChildren(n, ni.Conn);
                n.Expand();
                return;
            }
            var c = Conn(ni.Conn);
            if (c == null) return;
            try
            {
                SetStatus(Tr.T("กำลังโหลด…", "Loading…"));
                if (ni.Kind == NodeKind.DbFolder)
                {
                    var dbs = await Task.Run(() => Query(c, null,
                        "SELECT s.SCHEMA_NAME, IFNULL(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0) FROM information_schema.SCHEMATA s " +
                        "LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME GROUP BY s.SCHEMA_NAME ORDER BY s.SCHEMA_NAME"));
                    _fullKids.Clear();
                    n.Nodes.Clear();
                    foreach (var r in dbs)
                        n.Nodes.Add(Node(r[0]!, "db", new NodeInfo { Kind = NodeKind.Db, Conn = ni.Conn, Db = r[0]!, Size = long.TryParse(r[1], out long sz) ? sz : -1 }, true));
                    n.Text = Tr.T("ฐานข้อมูล", "Databases") + $"  ({dbs.Count})";
                }
                else if (ni.Kind == NodeKind.Db)
                {
                    var ts = await Task.Run(() => Query(c, null, "SELECT TABLE_NAME, TABLE_TYPE, DATA_LENGTH + INDEX_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_TYPE = 'VIEW', TABLE_NAME", ("@db", ni.Db)));
                    _fullKids.Remove(n);
                    n.Nodes.Clear();
                    long total = 0;
                    foreach (var r in ts)
                    {
                        bool view = r[1] == "VIEW";
                        long sz = !view && long.TryParse(r[2], out long v) ? v : -1;
                        if (sz > 0) total += sz;
                        n.Nodes.Add(Node(r[0]!, view ? "view" : "table", new NodeInfo { Kind = view ? NodeKind.View : NodeKind.Table, Conn = ni.Conn, Db = ni.Db, Obj = r[0]!, Size = sz }, false));
                    }
                    ni.Size = total;
                }
                n.Expand();
                // กำลังค้นหาอยู่ → กรองรายการที่เพิ่งโหลดด้วย
                if (_txtFind.Text.Trim() != "" && ni.Kind is NodeKind.DbFolder or NodeKind.Db) FilterTree();
                SetStatus(Tr.T("พร้อม", "Ready"));
            }
            catch (Exception ex)
            {
                SetStatus("✖ " + Err(ex));
                Ui.Error(this, Tr.T("เชื่อมต่อ / อ่านข้อมูลไม่ได้\n\n", "Cannot connect / read\n\n") + Err(ex));
            }
        }

        // ------------------------------------------------------------------ ค้นหา

        /// <summary>รายการลูกทั้งหมด (ก่อนกรอง) ของ node: กลุ่มฐานข้อมูล → ฐานข้อมูล, ฐานข้อมูล → ตาราง / view</summary>
        readonly Dictionary<TreeNode, List<TreeNode>> _fullKids = new Dictionary<TreeNode, List<TreeNode>>();
        readonly Timer _findTimer = new Timer { Interval = 180 };
        /// <summary>กำลังจัดรายการในต้นไม้ (ไม่ให้การเลือก node ที่เปลี่ยนเองระหว่างนี้ไปเปิดหน้าใหม่)</summary>
        bool _filtering;

        /// <summary>ลูกทั้งหมดของ node (จำไว้ตั้งแต่ครั้งแรกที่กรอง)  null = ยังไม่ได้โหลด</summary>
        List<TreeNode>? FullKids(TreeNode parent, bool dbs)
        {
            if (_fullKids.TryGetValue(parent, out var l)) return l;
            var kids = parent.Nodes.Cast<TreeNode>().Where(x => Info(x)?.Kind is NodeKind k && (dbs ? k == NodeKind.Db : k is NodeKind.Table or NodeKind.View)).ToList();
            if (kids.Count == 0 && parent.Nodes.Count > 0) return null;   // ยังเป็น "…" (ยังไม่โหลด)
            return _fullKids[parent] = kids;
        }

        static bool SameNodes(TreeNodeCollection have, List<TreeNode> want)
        {
            if (have.Count != want.Count) return false;
            for (int i = 0; i < want.Count; i++) if (have[i] != want[i]) return false;
            return true;
        }

        /// <summary>
        /// ค้นหาในต้นไม้: แสดงเฉพาะตาราง / view ที่ชื่อตรง (ในฐานข้อมูลที่โหลดรายการตารางมาแล้ว) และฐานข้อมูลที่ชื่อตรง
        /// ฐานข้อมูลที่ชื่อตรงแต่ไม่มีตารางตรง แสดงตารางทั้งหมดของฐานนั้น   ลบคำค้นแล้วกลับเป็นรายการเต็ม
        /// </summary>
        void FilterTree()
        {
            _findTimer.Stop();
            string q = _txtFind.Text.Trim();
            var keep = _tree.SelectedNode;
            _filtering = true;
            _tree.BeginUpdate();
            try
            {
                foreach (TreeNode cn in _tree.Nodes)
                    foreach (TreeNode folder in cn.Nodes)
                    {
                        if (Info(folder)?.Kind != NodeKind.DbFolder) continue;
                        var dbs = FullKids(folder, true);
                        if (dbs == null) continue;
                        var showDbs = new List<TreeNode>();
                        foreach (var dn in dbs)
                        {
                            bool dbHit = q == "" || dn.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
                            var tbls = FullKids(dn, false);
                            bool childHit = false;
                            if (tbls != null)
                            {
                                var hits = q == "" ? tbls : tbls.Where(t => t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                                childHit = q != "" && hits.Count > 0;
                                var show = hits.Count == 0 && dbHit ? tbls : hits;
                                if (!SameNodes(dn.Nodes, show)) { dn.Nodes.Clear(); dn.Nodes.AddRange(show.ToArray()); }
                            }
                            if (dbHit || childHit) showDbs.Add(dn);
                        }
                        if (!SameNodes(folder.Nodes, showDbs)) { folder.Nodes.Clear(); folder.Nodes.AddRange(showDbs.ToArray()); }
                        if (q != "") foreach (var dn in showDbs) if (dn.Nodes.Count > 0 && Info(dn.Nodes[0])?.Kind != NodeKind.Dummy && !dn.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) dn.Expand();
                    }
                if (q == "") _fullKids.Clear();
            }
            finally
            {
                _tree.EndUpdate();
                // node ที่เลือกไว้ยังอยู่ → เลือกไว้เหมือนเดิม (ไม่เปิดหน้าใหม่)
                if (keep != null && keep.TreeView == _tree) { if (_tree.SelectedNode != keep) _tree.SelectedNode = keep; }
                else if (_tree.SelectedNode != null) _tree.SelectedNode = null;
                _filtering = false;
            }
            FilterList();
            if (q != "")
            {
                int n = 0;
                foreach (var l in _fullKids.Where(k => Info(k.Key)?.Kind == NodeKind.Db).Select(k => k.Key))
                    n += l.Nodes.Cast<TreeNode>().Count(t => t.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
                SetStatus(Tr.T($"ค้นหา \"{q}\": พบ {n:N0} ตาราง/view", $"Filter \"{q}\": {n:N0} tables/views") +
                          (n == 0 ? Tr.T("  (ค้นได้เฉพาะฐานข้อมูลที่เปิดรายการตารางแล้ว — คลิกขยายฐานข้อมูลก่อน)", "  (only databases whose tables are loaded — expand the database first)") : ""));
            }
        }

        void OnFindKey(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _txtFind.Clear(); FilterTree(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { _tree.Focus(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                FilterTree();
                string q = _txtFind.Text.Trim();
                TreeNode? First(TreeNodeCollection ns)
                {
                    foreach (TreeNode n in ns)
                    {
                        if (Info(n)?.Kind is NodeKind.Table or NodeKind.View && (q == "" || n.Text.Contains(q, StringComparison.OrdinalIgnoreCase))) return n;
                        var f = First(n.Nodes);
                        if (f != null) return f;
                    }
                    return null;
                }
                var hit = First(_tree.Nodes);
                if (hit != null) { _tree.SelectedNode = hit; hit.EnsureVisible(); }
            }
        }

        /// <summary>คำค้นจากช่องค้นหาทางซ้าย กรองรายการทางขวาด้วย (รายชื่อตาราง / ฐานข้อมูล)</summary>
        List<ListViewItem> _lvAll = new List<ListViewItem>();

        void FilterList()
        {
            if (_lvAll.Count == 0 || _curPage != _listPage) return;
            string q = _txtFind.Text.Trim();
            var show = q == "" ? _lvAll : _lvAll.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            _lv.BeginUpdate();
            _lv.Items.Clear();
            _lv.Items.AddRange(show.ToArray());
            ApplyListSort();
            _lv.EndUpdate();
        }

        /// <summary>เก็บรายการเต็มของ ListView ไว้กรอง / เรียง แล้วกรองตามคำค้นปัจจุบัน</summary>
        void ListLoaded()
        {
            _lvAll = _lv.Items.Cast<ListViewItem>().ToList();
            if (_txtFind.Text.Trim() != "") FilterList();
        }

        // ------------------------------------------------------------------ เรียงรายการ (คลิกหัวคอลัมน์)

        int _lvSortCol = -1;
        bool _lvSortDesc;

        void SortList(int col)
        {
            if (col == _lvSortCol) _lvSortDesc = !_lvSortDesc;
            else
            {
                _lvSortCol = col;
                // ตัวเลข (แถว / ขนาด) เริ่มจากมากไปน้อย, ข้อความเริ่มจาก ก-ฮ / A-Z
                _lvSortDesc = _lv.Items.Count > 0 && _lv.Items[0].SubItems.Count > col && _lv.Items[0].SubItems[col].Tag is long;
            }
            _lv.BeginUpdate();
            ApplyListSort();
            _lv.EndUpdate();
            for (int i = 0; i < _lv.Columns.Count; i++)
            {
                // ลูกศรไว้หน้าชื่อคอลัมน์ (คอลัมน์แคบ ท้ายชื่อจะถูกตัดเป็น …)
                string t = _lv.Columns[i].Text;
                if (t.StartsWith("▲ ") || t.StartsWith("▼ ")) t = t.Substring(2);
                _lv.Columns[i].Text = i == _lvSortCol ? (_lvSortDesc ? "▼ " : "▲ ") + t : t;
            }
        }

        void ApplyListSort()
        {
            if (_lvSortCol < 0 || _lvSortCol >= _lv.Columns.Count) return;
            _lv.ListViewItemSorter = new ListSorter(_lvSortCol, _lvSortDesc);
            // ไม่ให้เรียงซ้ำทุกครั้งที่เพิ่มรายการ (ช้า) — เรียงเองเมื่อจำเป็น
            _lv.ListViewItemSorter = null;
        }

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        /// <summary>เรียงตามคอลัมน์: ตัวเลขดิบใน SubItem.Tag (long) ถ้ามี ไม่อย่างนั้นเรียงข้อความแบบธรรมชาติ (table2 ก่อน table10)</summary>
        sealed class ListSorter : System.Collections.IComparer
        {
            readonly int _col;
            readonly bool _desc;
            public ListSorter(int col, bool desc) { _col = col; _desc = desc; }

            public int Compare(object? x, object? y)
            {
                var a = (ListViewItem)x!;
                var b = (ListViewItem)y!;
                var sa = _col < a.SubItems.Count ? a.SubItems[_col] : null;
                var sb = _col < b.SubItems.Count ? b.SubItems[_col] : null;
                int r;
                if (sa?.Tag is long la && sb?.Tag is long lb) r = la.CompareTo(lb);
                else if (sa?.Tag is long) r = 1;
                else if (sb?.Tag is long) r = -1;
                else r = StrCmpLogicalW(sa?.Text ?? "", sb?.Text ?? "");
                if (_desc) r = -r;
                // ค่าเท่ากัน → เรียงตามชื่อ (คอลัมน์แรก) จากน้อยไปมากเสมอ
                return r != 0 || _col == 0 ? r : StrCmpLogicalW(a.Text, b.Text);
            }
        }

        // ------------------------------------------------------------------ วาดต้นไม้ (ขนาดชิดขวาแบบ DBeaver)

        /// <summary>ขนาดแบบสั้น: 512B · 16K · 10.5M · 1.2G</summary>
        static string ShortSize(long n)
        {
            if (n < 1024) return n + "B";
            double v = n / 1024.0;
            foreach (var u in new[] { "K", "M", "G", "T" })
            {
                if (v < 1024 || u == "T") return (v < 100 && u != "K" ? v.ToString("0.#") : Math.Round(v).ToString("0")) + u;
                v /= 1024;
            }
            return n + "B";
        }

        void OnDrawNode(object? s, DrawTreeNodeEventArgs e)
        {
            var n = e.Node;
            if (n == null || e.Bounds.Height <= 0 || e.Bounds.Width <= 0) return;
            var g = e.Graphics;
            var row = new Rectangle(0, e.Bounds.Top, _tree.ClientSize.Width, e.Bounds.Height);
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            bool hot = (e.State & TreeNodeStates.Hot) != 0;
            using (var b = new SolidBrush(sel ? Theme.Selection : hot ? Theme.Hover : Theme.Panel)) g.FillRectangle(b, row);

            // ตำแหน่งอ้างอิงจาก layout ของ native tree (n.Bounds = ช่องข้อความ) ให้คลิกลูกศร / ไอคอน ตรงกับที่ระบบตรวจ
            int img = _icons.ImageSize.Width;
            int textX = n.Bounds.X;
            int imgX = textX - img - Ui.Px(_tree, 3);
            int cy = row.Top + row.Height / 2;
            if (n.Nodes.Count > 0)
            {
                var chev = n.IsExpanded ? _chevDown : _chevRight;
                if (chev != null) g.DrawImage(chev, imgX - _tree.Indent / 2 - chev.Width / 2, cy - chev.Height / 2);
            }
            int ii = _icons.Images.IndexOfKey(n.ImageKey);
            if (ii >= 0) _icons.Draw(g, imgX, cy - img / 2, ii);

            var ni = Info(n);
            int right = row.Right - Ui.Px(_tree, 8);
            if (ni != null && ni.Size >= 0 && ni.Kind is NodeKind.Db or NodeKind.Table)
            {
                string st = ShortSize(ni.Size);
                var f = TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                int w = TextRenderer.MeasureText(g, st, _tree.Font, Size.Empty, f).Width;
                TextRenderer.DrawText(g, st, _tree.Font, new Rectangle(right - w, row.Top, w, row.Height), Theme.Muted, f);
                right -= w + Ui.Px(_tree, 8);
            }
            Color fg = ni?.Kind == NodeKind.Dummy ? Theme.Muted : n.ForeColor != Color.Empty ? n.ForeColor : Theme.Text;
            var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, n.Text, n.NodeFont ?? _tree.Font, new Rectangle(textX, row.Top, Math.Max(0, right - textX), row.Height), fg, tf);
        }

        void OnBeforeSelect(object? s, TreeViewCancelEventArgs e)
        {
            if (_filtering) return;
            if (_busy) { e.Cancel = true; return; }
            if (!HasPending) return;
            var r = AskPending();
            if (r == DialogResult.Cancel) e.Cancel = true;
            else if (r == DialogResult.Yes)
            {
                e.Cancel = true;
                var target = e.Node;
                _ = SaveThen(() => _tree.SelectedNode = target);
            }
            else _dt!.RejectChanges();
        }

        void UpdateToolButtons(NodeInfo? ni)
        {
            bool any = ni != null && ni.Kind != NodeKind.Dummy;
            _tbEdit.Enabled = _tbRemove.Enabled = any;
        }

        async Task OnSelect(TreeNode? n)
        {
            var ni = Info(n);
            UpdateToolButtons(ni);
            if (ni == null || ni.Kind == NodeKind.Dummy) return;
            switch (ni.Kind)
            {
                case NodeKind.Conn: await ShowConnOverview(ni.Conn); break;
                case NodeKind.DbFolder: await ShowConnOverview(ni.Conn); break;
                case NodeKind.Db: await ShowDbOverview(ni.Conn, ni.Db); break;
                case NodeKind.Table: case NodeKind.View: await OpenTable(ni.Conn, ni.Db, ni.Obj, ni.Kind == NodeKind.View); break;
            }
        }

        async Task RefreshSelected()
        {
            var n = _tree.SelectedNode;
            var ni = Info(n);
            if (ni == null) { LoadTree(); return; }
            if (ni.Kind is NodeKind.DbFolder or NodeKind.Db or NodeKind.UsersFolder)
            {
                _fullKids.Remove(n!);
                n!.Nodes.Clear();
                n.Nodes.Add(new TreeNode("…") { Tag = new NodeInfo { Kind = NodeKind.Dummy } });
                await LoadChildren(n);
            }
            await OnSelect(n);
        }

        static List<string?[]> Query(DbConnInfo c, string? db, string sql, params (string, object)[] ps)
        {
            using var conn = LiveValue.Open(c, db);
            using var cmd = new MySqlCommand(sql, conn);
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            using var r = cmd.ExecuteReader();
            var l = new List<string?[]>();
            while (r.Read())
            {
                var a = new string?[r.FieldCount];
                for (int i = 0; i < a.Length; i++) a[i] = r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                l.Add(a);
            }
            return l;
        }


        // ------------------------------------------------------------------ เมนูคลิกขวา

        void ShowNodeMenu(TreeNode? n, Point at)
        {
            var ni = Info(n);
            if (ni == null || ni.Kind == NodeKind.Dummy) return;
            var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            void Item(string text, Action a, bool danger = false)
            {
                var it = new ToolStripMenuItem(text);
                it.Click += (s, e) => a();
                if (danger) it.ForeColor = Theme.Err;
                m.Items.Add(it);
            }
            switch (ni.Kind)
            {
                case NodeKind.Conn:
                case NodeKind.DbFolder:
                    Item("SQL Editor", () => OpenSqlEditor(ni));
                    Item(Tr.T("รีเฟรช", "Refresh"), () => _ = RefreshSelected());
                    if (ni.Kind == NodeKind.Conn)
                    {
                        m.Items.Add(new ToolStripSeparator());
                        Item(Tr.T("แก้ไขการเชื่อมต่อ…", "Edit connection…"), EditConn);
                        Item(Tr.T("ลบการเชื่อมต่อ", "Remove connection"), RemoveConn);
                    }
                    break;
                case NodeKind.Db:
                    Item(Tr.T("SQL Editor (ฐานข้อมูลนี้)", "SQL Editor (this database)"), () => OpenSqlEditor(ni));
                    Item(Tr.T("รีเฟรช", "Refresh"), () => _ = RefreshSelected());
                    break;
                case NodeKind.Table:
                case NodeKind.View:
                    Item(Tr.T("SQL Editor (ตารางนี้)", "SQL Editor (this table)"), () => OpenSqlEditor(ni));
                    var tpl = new ToolStripMenuItem(Tr.T("สร้างคำสั่ง SQL", "Generate SQL"));
                    tpl.DropDown.Renderer = new Theme.Renderer();
                    foreach (var k in ni.Kind == NodeKind.View ? new[] { "SELECT" } : SqlTemplates)
                    {
                        string kk = k;
                        tpl.DropDownItems.Add(kk, null, (s2, e2) => { OpenSqlEditor(ni, true); _ = InsertTemplate(kk, ni.Db, ni.Obj); });
                    }
                    m.Items.Add(tpl);
                    m.Items.Add(new ToolStripSeparator());
                    Item(Tr.T("รีเฟรช", "Refresh"), () => _ = RefreshSelected());
                    break;
                default: return;
            }
            m.Show(_tree, at);
        }

        void ShowListMenu(Point at)
        {
            var ni = Info(_tree.SelectedNode);
            if (ni == null || _lv.SelectedItems.Count == 0) return;
            string name = (string)_lv.SelectedItems[0].Tag!;
            var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            void Item(string text, Action a, bool danger = false)
            {
                var it = new ToolStripMenuItem(text);
                it.Click += (s, e) => a();
                if (danger) it.ForeColor = Theme.Err;
                m.Items.Add(it);
            }
            Item(Tr.T("เปิด", "Open"), OpenListItem);
            m.Show(_lv, at);
        }

        // ------------------------------------------------------------------ หน้า: รายการ

        void ShowList(string title, string sub, string icon)
        {
            ShowPage(_listPage);
            SetHeader(title, sub, icon);
            _card.Visible = false;
            _card.Controls.Clear();
            _actions.Controls.Clear();
            _lv.BeginUpdate();
            _lv.Clear();
            _lv.EndUpdate();
            _lvAll = new List<ListViewItem>();
            _lvSortCol = -1;
            _lvSortDesc = false;
        }

        void AddAction(Button b)
        {
            _actions.Controls.Add(b);
            Theme.Apply(b);
        }

        /// <summary>การ์ดข้อมูลแบบ ชื่อ: ค่า (2 คู่ต่อแถว)</summary>
        void FillCard(List<(string K, string V)> items)
        {
            _card.Controls.Clear();
            _card.ColumnStyles.Clear();
            for (int i = 0; i < 4; i++) _card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < items.Count; i++)
            {
                var k = new Label { Text = items[i].K, AutoSize = true, Tag = "muted", Margin = new Padding(0, 3, 10, 3) };
                var v = new Label { Text = items[i].V, AutoSize = true, Margin = new Padding(0, 3, 24, 3), MaximumSize = new Size(Ui.Px(this, 420), 0) };
                _card.Controls.Add(k, (i % 2) * 2, i / 2);
                _card.Controls.Add(v, (i % 2) * 2 + 1, i / 2);
            }
            _card.Visible = items.Count > 0;
            Theme.Apply(_card);
            foreach (Control c in _card.Controls) c.BackColor = Theme.Panel;
        }

        static string SizeText(string? s) => long.TryParse(s, out long v) ? Ui.HumanSize(v) : "";

        static string Uptime(string? s)
        {
            if (!long.TryParse(s, out long sec)) return "";
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalDays >= 1 ? Tr.T($"{(int)t.TotalDays} วัน {t.Hours} ชม.", $"{(int)t.TotalDays} d {t.Hours} h") : Tr.T($"{t.Hours} ชม. {t.Minutes} นาที", $"{t.Hours} h {t.Minutes} min");
        }

        async Task ShowConnOverview(SavedConn sc)
        {
            ShowList(sc.Display, $"{sc.User}@{sc.Host}:{sc.Port}", "conn");
            var c = Conn(sc);
            if (c == null) return;
            AddAction(Btn(Tr.T("เปิดฐานข้อมูล", "Open database"), OpenListItem, "primary"));
            AddAction(Btn("SQL Editor", () => OpenSqlEditor(new NodeInfo { Kind = NodeKind.Conn, Conn = sc })));
            _lv.Columns.Add(Tr.T("ฐานข้อมูล", "Database Name"), Ui.Px(this, 240));
            _lv.Columns.Add("Default Charset", Ui.Px(this, 130));
            _lv.Columns.Add("Default Collation", Ui.Px(this, 200));
            _lv.Columns.Add(Tr.T("ตาราง", "Tables"), Ui.Px(this, 80), HorizontalAlignment.Right);
            _lv.Columns.Add(Tr.T("ขนาด", "Database size"), Ui.Px(this, 120), HorizontalAlignment.Right);
            try
            {
                var rows = await Task.Run(() => Query(c, null,
                    "SELECT s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME, COUNT(t.TABLE_NAME), IFNULL(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0) " +
                    "FROM information_schema.SCHEMATA s LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME " +
                    "GROUP BY s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME ORDER BY s.SCHEMA_NAME"));
                var srv = await Task.Run(() => Query(c, null, "SELECT VERSION(), @@character_set_server, @@collation_server, @@max_allowed_packet, @@datadir, @@version_compile_os"));
                var st = (await Task.Run(() => Query(c, null, "SHOW GLOBAL STATUS WHERE Variable_name IN ('Uptime','Threads_connected','Questions')")))
                    .ToDictionary(r => r[0]!, r => r[1]);
                var s0 = srv[0];
                FillCard(new List<(string, string)>
                {
                    ("Server", s0[0] ?? ""), (Tr.T("เปิดมาแล้ว", "Uptime"), Uptime(st.GetValueOrDefault("Uptime"))),
                    ("Host", $"{sc.Host}:{sc.Port}"), (Tr.T("กำลังเชื่อมต่อ", "Connections"), st.GetValueOrDefault("Threads_connected") ?? ""),
                    (Tr.T("ผู้ใช้", "User"), sc.User), ("Charset / Collation", (s0[1] ?? "") + " / " + (s0[2] ?? "")),
                    ("max_allowed_packet", long.TryParse(s0[3], out long mp) ? Db.Mb(mp) : ""), ("Data dir", s0[4] ?? ""),
                });
                _lv.BeginUpdate();
                foreach (var r in rows)
                {
                    bool sys = SystemDbs.Contains(r[0]);
                    var it = new ListViewItem(new[] { r[0] ?? "", r[1] ?? "", r[2] ?? "", r[3] ?? "", SizeText(r[4]) }) { Tag = r[0] };
                    if (sys) it.ForeColor = Theme.Muted;
                    if (long.TryParse(r[3], out long tc)) it.SubItems[3].Tag = tc;
                    if (long.TryParse(r[4], out long sz)) it.SubItems[4].Tag = sz;
                    _lv.Items.Add(it);
                }
                _lv.EndUpdate();
                ListLoaded();
                Theme.FitLastColumn(_lv);
                SetStatus($"{sc.Display}  —  {s0[0]}  —  " + Tr.T($"{rows.Count:N0} ฐานข้อมูล", $"{rows.Count:N0} databases"));
                UpdateToolButtons(Info(_tree.SelectedNode));
            }
            catch (Exception ex) { SetStatus("✖ " + Err(ex)); FillCard(new List<(string, string)> { ("✖", Err(ex)) }); }
        }

        async Task ShowDbOverview(SavedConn sc, string db)
        {
            ShowList(db, Tr.T("ฐานข้อมูล", "Database") + "  ·  " + sc.Display, "db");
            var c = Conn(sc);
            if (c == null) return;
            AddAction(Btn(Tr.T("เปิดตาราง", "Open table"), OpenListItem));
            AddAction(Btn("SQL Editor", () => OpenSqlEditor(new NodeInfo { Kind = NodeKind.Db, Conn = sc, Db = db })));
            _lv.Columns.Add(Tr.T("ตาราง", "Table"), Ui.Px(this, 240));
            _lv.Columns.Add(Tr.T("ชนิด", "Type"), Ui.Px(this, 70));
            _lv.Columns.Add("Engine", Ui.Px(this, 80));
            _lv.Columns.Add(Tr.T("แถว (ประมาณ)", "Rows (approx.)"), Ui.Px(this, 110), HorizontalAlignment.Right);
            _lv.Columns.Add(Tr.T("ขนาด", "Size"), Ui.Px(this, 90), HorizontalAlignment.Right);
            _lv.Columns.Add("Collation", Ui.Px(this, 170));
            _lv.Columns.Add(Tr.T("คำอธิบาย", "Comment"), Ui.Px(this, 200));
            try
            {
                var rows = await Task.Run(() => Query(c, null,
                    "SELECT TABLE_NAME, TABLE_TYPE, ENGINE, TABLE_ROWS, DATA_LENGTH + INDEX_LENGTH, TABLE_COLLATION, TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME", ("@db", db)));
                var info = await Task.Run(() => Query(c, null, "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @db", ("@db", db)));
                long total = rows.Sum(r => long.TryParse(r[4], out long v) ? v : 0);
                FillCard(new List<(string, string)>
                {
                    ("Default Charset", info.Count > 0 ? info[0][0] ?? "" : ""), (Tr.T("ตาราง / view", "Tables / views"), $"{rows.Count(r => r[1] != "VIEW"):N0} / {rows.Count(r => r[1] == "VIEW"):N0}"),
                    ("Default Collation", info.Count > 0 ? info[0][1] ?? "" : ""), (Tr.T("ขนาดรวม", "Total size"), Ui.HumanSize(total)),
                });
                _lv.BeginUpdate();
                foreach (var r in rows)
                {
                    string type = r[1] == "VIEW" ? "View" : "Table";
                    var it = new ListViewItem(new[] { r[0] ?? "", type, r[2] ?? "", long.TryParse(r[3], out long n) ? n.ToString("N0") : "", SizeText(r[4]), r[5] ?? "", r[6] ?? "" }) { Tag = r[0] };
                    if (long.TryParse(r[3], out long rc)) it.SubItems[3].Tag = rc;
                    if (long.TryParse(r[4], out long sz)) it.SubItems[4].Tag = sz;
                    _lv.Items.Add(it);
                }
                _lv.EndUpdate();
                ListLoaded();
                Theme.FitLastColumn(_lv);
                SetStatus(Tr.T($"{db}: {rows.Count:N0} ตาราง/view — ดับเบิลคลิกเพื่อเปิด · คลิกหัวคอลัมน์เพื่อเรียง", $"{db}: {rows.Count:N0} tables/views — double-click to open · click a column header to sort"));
            }
            catch (Exception ex) { SetStatus("✖ " + Err(ex)); }
        }


        /// <summary>ดับเบิลคลิกในรายการ → เลือก node ที่ตรงกันในต้นไม้</summary>
        void OpenListItem()
        {
            if (_lv.SelectedItems.Count == 0) return;
            string name = (string)_lv.SelectedItems[0].Tag!;
            var n = _tree.SelectedNode;
            if (n == null) return;
            if (Info(n)?.Kind == NodeKind.Conn)
            {
                if (n.Nodes.Count == 1 && Info(n.Nodes[0])?.Kind == NodeKind.Dummy) FillConnChildren(n, Info(n)!.Conn);
                n = n.Nodes[0];
            }
            _ = ExpandThenSelect(n, name);
        }

        async Task ExpandThenSelect(TreeNode n, string name)
        {
            if (n.Nodes.Count == 1 && Info(n.Nodes[0])?.Kind == NodeKind.Dummy) await LoadChildren(n);
            else n.Expand();
            var child = n.Nodes.Cast<TreeNode>().FirstOrDefault(x => x.Text == name || Info(x)?.Obj == name || Info(x)?.Db == name);
            if (child != null) _tree.SelectedNode = child;
        }

        // ------------------------------------------------------------------ หน้า: ตารางผลลัพธ์


        void ApplyGridFilter()
        {
            if (_gGrid.DataSource is not DataTable t) return;
            string q = _gFilter.Text.Trim().Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
            t.DefaultView.RowFilter = q == "" ? "" : string.Join(" OR ", t.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName.Replace("]", "\\]")}] LIKE '%{q}%'"));
        }

    }
}
