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
        /// <summary>จำนวนแถวต่อหน้า (เลือกได้ที่ท้ายหน้าข้อมูล จำไว้)</summary>
        static readonly int[] PageSizes = { 50, 100, 200, 500, 1000, 5000 };
        int _pageSize = int.TryParse(Ui.GetSetting("page_size"), out int ps) && Array.IndexOf(PageSizes, ps) >= 0 ? ps : 200;
        ComboBox _cmbPage = null!;
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
        /// <summary>ค้นหาในรายการ (ชื่อตาราง / ฐานข้อมูล) ของหน้านี้</summary>
        readonly TextBox _lvFind;

        // ปุ่มไอคอน (วาดไอคอนใหม่เมื่อเปลี่ยนธีม)
        readonly List<(Button Btn, string Glyph)> _iconBtns = new List<(Button, string)>();
        readonly ToolTip _btnTip = new ToolTip();
        /// <summary>โหมดดูข้อมูลแบบข้อความ (Text) แทนตาราง (Grid) — ใช้ทั้งหน้าตารางและผลลัพธ์ SQL</summary>
        bool _textMode = Ui.GetSetting("text_mode") == "1";
        readonly List<(Button Btn, bool Text)> _modeBtns = new List<(Button, bool)>();

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
        /// <summary>ข้อมูลหน้านี้แบบข้อความ (โหมด Text)</summary>
        readonly TextBox _txtData;
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
            _lvFind = new TextBox { Width = Ui.Px(this, 260), Margin = new Padding(0, 2, 10, 2) };
            _lvFind.TextChanged += (s, e) => FilterList();
            _lvFind.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { _lvFind.Clear(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down && _lv.Items.Count > 0) { _lv.Focus(); _lv.Items[0].Selected = _lv.Items[0].Focused = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter && _lv.Items.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    _lv.SelectedItems.Clear();
                    _lv.Items[0].Selected = true;
                    OpenListItem();
                }
            };
            _btnTip.SetToolTip(_lvFind, Tr.T("พิมพ์บางส่วนของชื่อ (หรือคำอธิบาย)\nEnter = เปิดรายการแรก   ↓ = ไปที่รายการ   Esc = ล้าง", "Type part of a name (or comment)\nEnter = open the first item   ↓ = go to the list   Esc = clear"));
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

            // ---- หน้าข้อมูล (แบบ DBeaver):  หัว = ช่องกรองยาวเต็มแถว · ตัว = Grid / Text (+ แถบโหมดด้านซ้าย) · ท้าย = ปุ่ม + สถานะ
            var dl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            dl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // หัว: ช่องกรอง WHERE
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 6) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _txtWhere = new TextBox
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 3, 4, 3),
                PlaceholderText = Tr.T("พิมพ์เงื่อนไขแบบ SQL เพื่อกรองข้อมูล เช่น  name LIKE '%สม%' AND id > 10   แล้วกด Enter", "Enter a SQL expression to filter results, e.g.  name LIKE '%abc%' AND id > 10   then press Enter"),
            };
            _txtWhere.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ApplyFilter(); }
                else if (e.KeyCode == Keys.Escape && _txtWhere.Text != "") { e.SuppressKeyPress = true; ClearFilter(); }
            };
            head.Controls.Add(new Label { Text = "WHERE", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "muted", Margin = new Padding(2, 0, 6, 0) }, 0, 0);
            head.Controls.Add(_txtWhere, 1, 0);
            head.Controls.Add(IconBtn("", Tr.T("กรองข้อมูล (Enter)", "Apply the filter (Enter)"), () => _ = ApplyFilter(), "", Tr.T("กรอง", "Filter")), 2, 0);
            head.Controls.Add(IconBtn("", Tr.T("ล้างตัวกรอง (Esc)", "Clear the filter (Esc)"), ClearFilter), 3, 0);
            dl.Controls.Add(head, 0, 0);

            // ท้าย: ปุ่ม (ซ้าย) + สถานะ (ขวา)
            var foot = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // แถวเดียว (ไม่ขึ้นบรรทัดใหม่ — FlowLayoutPanel ที่ wrap ในแถว AutoSize จะจองความสูงเกิน)
            var fb = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            fb.Controls.Add(IconBtn("", Tr.T("รีเฟรช — โหลดข้อมูลใหม่ (F5)", "Refresh — reload the data (F5)"), () => _ = ReloadPage(true)));
            var btnFirst = IconBtn("", Tr.T("หน้าแรก", "First page"), () => _ = Page(-1_000_000));
            _btnPrev = IconBtn("", Tr.T("หน้าก่อน", "Previous page"), () => _ = Page(-1));
            _lblPage = new Label { AutoSize = true, Margin = new Padding(2, 7, 6, 0) };
            _btnNext = IconBtn("", Tr.T("หน้าถัดไป", "Next page"), () => _ = Page(1));
            fb.Controls.AddRange(new Control[] { btnFirst, _btnPrev, _lblPage, _btnNext, Sep() });
            fb.Controls.Add(new Label { Text = Tr.T("แถว/หน้า", "Rows"), AutoSize = true, Margin = new Padding(0, 7, 4, 0), Tag = "muted" });
            _cmbPage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(this, 72), Margin = new Padding(0, 3, 0, 3) };
            foreach (int n in PageSizes) _cmbPage.Items.Add(n.ToString("N0"));
            _cmbPage.SelectedIndex = Math.Max(0, Array.IndexOf(PageSizes, _pageSize));
            _cmbPage.SelectedIndexChanged += async (s, e) =>
            {
                int n = PageSizes[Math.Max(0, _cmbPage.SelectedIndex)];
                if (n == _pageSize) return;
                if (_dt != null && !await OkToLeave()) { _cmbPage.SelectedIndex = Array.IndexOf(PageSizes, _pageSize); return; }
                _pageSize = n;
                Ui.SetSetting("page_size", n.ToString());
                _offset = 0;
                if (_dt != null) await ReloadPage(false);
            };
            fb.Controls.Add(_cmbPage);
            var gear = IconBtn("", Tr.T("ตั้งค่าตาราง (ดับเบิลคลิกที่ช่อง ฯลฯ)", "Grid settings (double-click behaviour …)"), () => { });
            gear.Margin = new Padding(8, 1, 0, 1);
            gear.Click += (s, e) => ShowGridSettings(gear);
            fb.Controls.Add(gear);
            _lblData = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = new Padding(8, 0, 0, 0), Tag = "keep" };
            foot.Controls.Add(fb, 0, 0);
            foot.Controls.Add(_lblData, 1, 0);
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
            // หัวคอลัมน์: คลิกซ้าย = เรียง, ลากไปวาง = สลับลำดับ (เฉพาะการแสดง), คลิกขวา = เมนูคอลัมน์
            _grid.AllowUserToOrderColumns = true;
            _grid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) _ = SortBy(e.ColumnIndex);
                else if (e.Button == MouseButtons.Right) ShowHeaderMenu(e.ColumnIndex);
            };
            _grid.ColumnDisplayIndexChanged += (s, e) => { if (_textMode && !_binding) RefreshTextView(); };
            _grid.DataError += (s, e) => { e.ThrowException = false; SetStatus("✖ " + e.Exception?.Message); };
            _grid.CellValueChanged += (s, e) => UpdateDirty();
            _grid.UserDeletedRow += (s, e) => UpdateDirty();
            // ค่าที่แก้ในช่องจะค้างอยู่ใน "โหมดแก้ไขแถว" จนกว่าจะเลื่อนไปแถวอื่น → จบการแก้ไขทันทีที่ออกจากช่อง (ปุ่มบันทึกขึ้นเลย)
            _grid.CellEndEdit += (s, e) => BeginInvoke((Action)(() => { FlushEdits(); UpdateDirty(); }));
            _grid.CellPainting += (s, e) =>
            {
                if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.ColumnIndex >= _cols.Count) return;
                var cm = _cols[e.ColumnIndex];
                PaintTypedHeader(_grid, e, cm.Name, cm.ColumnType, cm.Key == "PRI");
            };
            _grid.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0 && !_grid[e.ColumnIndex, e.RowIndex].Selected)
                {
                    _grid.ClearSelection();
                    _grid.CurrentCell = _grid[e.ColumnIndex, e.RowIndex];
                }
            };
            // ดับเบิลคลิก: ช่อง BLOB = เปิดดูไฟล์ / รูป,  ช่องอื่น / หัวแถว = หน้าต่างแก้ไข (ดู) ทีละแถว
            // ดับเบิลคลิก:  หัวแถว (ช่องซ้ายสุด) = หน้าต่างแก้ทั้งแถวเสมอ (ชี้ช่องที่เลือกอยู่ในแถวนั้น)
            //               ช่อง BLOB = เปิดดูไฟล์ / รูป
            //               ช่องอื่น = แก้ในช่อง (ค่าเริ่มต้น) หรือเปิดหน้าต่าง ตามที่ตั้งไว้ (⚙ ท้ายตาราง)
            _grid.CellDoubleClick += (s, e) => GridDoubleClick(e.RowIndex, e.ColumnIndex, false);
            // จำช่องที่ผู้ใช้เลือก (คลิก / ลูกศร) — คลิกหัวแถวไม่นับ (ไม่ให้ช่องที่ชี้ย้ายไปคอลัมน์แรก)
            _grid.CellMouseDown += (s, e) =>
            {
                _hdrClick = e.ColumnIndex < 0;
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0) { _pointRow = e.RowIndex; _pointCol = e.ColumnIndex; }
            };
            _grid.CellMouseUp += (s, e) => _hdrClick = false;
            _grid.CurrentCellChanged += (s, e) =>
            {
                if (!_hdrClick && _grid.CurrentCell is DataGridViewCell c && c.ColumnIndex >= 0) { _pointRow = c.RowIndex; _pointCol = c.ColumnIndex; }
            };
            var cm = new ContextMenuStrip();
            cm.Items.Add(Tr.T("ดูแถวนี้… (หน้าต่าง)", "View this row… (window)"), null, (s, e) => EditCurrentRow());
            var dbl = new ToolStripMenuItem(Tr.T("ดับเบิลคลิกที่ช่อง = เปิดหน้าต่าง", "Double-click a cell = open the window")) { CheckOnClick = true };
            dbl.Click += (s, e) => SetDblPopup(dbl.Checked);
            cm.Opening += (s, e) => dbl.Checked = _dblPopup;
            cm.Items.Add(dbl);
            cm.Items.Add(new ToolStripSeparator());
            // คัดลอก / วาง ทั้งกลุ่ม (เลือกด้วยการลาก หรือ Shift / Ctrl + คลิก)
            cm.Items.Add(new ToolStripMenuItem(Tr.T("คัดลอก", "Copy"), null, (s, e) => CopySelection(false)) { ShortcutKeyDisplayString = "Ctrl+C" });
            cm.Items.Add(new ToolStripMenuItem(Tr.T("คัดลอกพร้อมหัวคอลัมน์", "Copy with headers"), null, (s, e) => CopySelection(true)) { ShortcutKeyDisplayString = "Ctrl+Shift+C" });
            cm.Items.Add(Tr.T("คัดลอกเป็นคำสั่ง SQL INSERT", "Copy as SQL INSERT"), null, (s, e) => CopyAsInsert());
            _grid.SelectionChanged += (s, e) => ShowSelectionInfo();
            _grid.KeyDown += (s, e) =>
            {
                if (_grid.IsCurrentCellInEditMode) return;
                if (e.Control && e.Shift && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; CopySelection(true); }
            };
            // กรองตามค่าในช่อง (แบบ DBeaver)
            var fv = new ToolStripMenuItem(Tr.T("กรองตามค่านี้", "Filter by this value"));
            fv.DropDownItems.Add("=", null, (s, e) => FilterByCell("="));
            fv.DropDownItems.Add("<>", null, (s, e) => FilterByCell("<>"));
            fv.DropDownItems.Add("LIKE '%…%'", null, (s, e) => FilterByCell("LIKE"));
            fv.DropDownItems.Add("IS NULL", null, (s, e) => FilterByCell("IS NULL"));
            fv.DropDownItems.Add("IS NOT NULL", null, (s, e) => FilterByCell("IS NOT NULL"));
            cm.Items.Add(fv);
            cm.Opening += (s, e) => fv.DropDown.Renderer = new Theme.Renderer();
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tr.T("เปิดดูไฟล์ / รูป (BLOB)", "Open file / image (BLOB)"), null, (s, e) => { var c = _grid.CurrentCell; if (c != null && IsBlobCol(c.ColumnIndex)) _ = OpenBlob(c.RowIndex, c.ColumnIndex); });
            cm.Items.Add(Tr.T("บันทึก BLOB เป็นไฟล์…", "Save BLOB to file…"), null, (s, e) => _ = SaveBlob());
            _grid.ContextMenuStrip = cm;
            // ฟอนต์ที่มีทั้งตัวไทยและอังกฤษ (ฟอนต์ความกว้างคงที่ไม่มีตัวไทย Windows จะสลับฟอนต์ทั้งบรรทัด ทำให้คอลัมน์เบี้ยว)
            _txtData = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false, MaxLength = 0, Font = Ui.Font };
            var dataHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            dataHost.Controls.Add(_grid);
            dataHost.Controls.Add(_txtData);
            _txtData.Visible = _textMode;
            _grid.Visible = !_textMode;
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 4, 0) };
            side.Controls.AddRange(ModeButtons());
            side.Controls.Add(IconBtn("", Tr.T("Record — ดูข้อมูลทีละแถว (ดับเบิลคลิกที่แถวก็ได้)", "Record — one row at a time (or double-click a row)"), EditCurrentRow));
            foreach (Control c in side.Controls) c.Margin = new Padding(0, 0, 0, 4);
            body.Controls.Add(side, 0, 0);
            body.Controls.Add(dataHost, 1, 0);
            dl.Controls.Add(body, 0, 1);
            dl.Controls.Add(foot, 0, 2);
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
            _txtData.Font = Ui.Font;
            StyleModeButtons();
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

        static Label Sep() => new Label { Text = "|", AutoSize = true, Margin = new Padding(2, 7, 4, 0), Tag = "muted" };

        /// <summary>ปุ่มไอคอนขนาดเล็ก (มีคำอธิบายเมื่อชี้เมาส์)  text = มีข้อความต่อท้ายไอคอนด้วย</summary>
        Button IconBtn(string glyph, string tip, Action a, string tag = "", string? text = null)
        {
            var b = new Button { Tag = tag, Margin = new Padding(0, 1, 4, 1) };
            if (text == null)
            {
                b.AutoSize = false;
                b.Size = new Size(Ui.Px(this, 32), Ui.Px(this, 28));
                b.ImageAlign = ContentAlignment.MiddleCenter;
            }
            else
            {
                b.Text = text;
                b.AutoSize = true;
                b.MinimumSize = new Size(0, Ui.Px(this, 28));
                b.Padding = new Padding(6, 0, 8, 0);
                b.TextImageRelation = TextImageRelation.ImageBeforeText;
            }
            b.Click += (s, e) => a();
            _btnTip.SetToolTip(b, tip);
            _iconBtns.Add((b, glyph));
            return b;
        }

        /// <summary>วาดไอคอนของปุ่มตามสีธีม (ปุ่มหลัก = ไอคอนสีขาว, ปุ่มอันตราย = สีแดง)</summary>
        void PaintIcons()
        {
            int sz = Ui.Px(this, 15);
            foreach (var (b, g) in _iconBtns)
            {
                string tag = b.Tag as string ?? "";
                b.Image = Ui.Glyph(g, sz, tag == "primary" ? Theme.AccentText : tag == "danger" ? Theme.Err : Theme.Text);
            }
        }

        // ------------------------------------------------------------------ ตัวกรอง / เมนูหัวคอลัมน์ (แบบ DBeaver)

        /// <summary>กำลังผูกข้อมูลใหม่ (ไม่ต้องอัปเดตโหมด Text ทุกครั้งที่ลำดับคอลัมน์เปลี่ยน)</summary>
        bool _binding;

        /// <summary>ดับเบิลคลิกที่ช่อง = เปิดหน้าต่างแก้ทั้งแถว (false = แก้ในช่อง — ค่าเริ่มต้น)</summary>
        bool _dblPopup = Ui.GetSetting("dblclick_popup") == "1";
        /// <summary>ช่องที่ผู้ใช้เลือกล่าสุด (ใช้ชี้ช่องตอนดับเบิลคลิกหัวแถว)</summary>
        int _pointRow = -1, _pointCol = -1;
        bool _hdrClick;

        /// <summary>
        /// ดับเบิลคลิกในตาราง:  col &lt; 0 (หัวแถว) = หน้าต่างแก้ทั้งแถวเสมอ ชี้ช่องที่เลือกอยู่ในแถวนั้น · ช่อง BLOB = เปิดดูไฟล์
        /// ช่องอื่น = แก้ในช่อง (ค่าเริ่มต้น) หรือเปิดหน้าต่างชี้ช่องที่คลิก   show = false: เปิดแบบ modal (ปกติ)
        /// คืนหน้าต่างที่สร้าง เมื่อ show = true (ใช้ตอนทดสอบ)
        /// </summary>
        RowEditForm? GridDoubleClick(int row, int col, bool modeless)
        {
            if (row < 0) return null;
            int focus = col < 0 ? (_pointRow == row ? _pointCol : -1) : col;
            if (col >= 0 && IsBlobCol(col)) { _ = OpenBlob(row, col); return null; }
            if (col >= 0 && !_dblPopup)
            {
                if (!_grid.ReadOnly && !_grid.Columns[col].ReadOnly)
                {
                    _grid.CurrentCell = _grid[col, row];
                    _grid.BeginEdit(true);
                }
                return null;
            }
            if (modeless) return CreateRowEditor(row, focus);
            OpenRowEditor(row, focus);
            return null;
        }

        void SetDblPopup(bool on)
        {
            _dblPopup = on;
            Ui.SetSetting("dblclick_popup", on ? "1" : "0");
            SetStatus(on ? Tr.T("ดับเบิลคลิกที่ช่อง = เปิดหน้าต่างแก้ทั้งแถว", "Double-click a cell = open the row window")
                         : Tr.T("ดับเบิลคลิกที่ช่อง = แก้ในช่อง  (ดับเบิลคลิกหัวแถวซ้ายสุด = เปิดหน้าต่าง)", "Double-click a cell = edit in place  (double-click the row header = open the row window)"));
        }

        /// <summary>เมนูตั้งค่าตาราง (ปุ่ม ⚙ ท้ายตาราง)</summary>
        void ShowGridSettings(Control at)
        {
            var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            m.Items.Add(new ToolStripMenuItem(Tr.T("ดับเบิลคลิกที่ช่อง:", "Double-click a cell:")) { Enabled = false });
            string inPlace = Tr.T("ไม่เปิดหน้าต่าง (ค่าเริ่มต้น)", "Do not open a window (default)");
            m.Items.Add(new ToolStripMenuItem("    " + inPlace, null, (s, e) => SetDblPopup(false)) { Checked = !_dblPopup });
            m.Items.Add(new ToolStripMenuItem("    " + Tr.T("เปิดหน้าต่างแก้ทั้งแถว (ชี้ช่องที่คลิก)", "Open the row window (pointing at the clicked cell)"), null, (s, e) => SetDblPopup(true)) { Checked = _dblPopup });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(Tr.T("ดับเบิลคลิกหัวแถว (ช่องซ้ายสุด) = เปิดหน้าต่างเสมอ", "Double-click the row header (leftmost) = always opens the window")) { Enabled = false });
            m.Items.Add(new ToolStripMenuItem(Tr.T("ดับเบิลคลิกช่อง BLOB = เปิดดูไฟล์ / รูป", "Double-click a BLOB cell = open the file / image")) { Enabled = false });
            m.Show(at, new Point(0, -m.PreferredSize.Height));
        }

        void ClearFilter()
        {
            _txtWhere.Text = "";
            if (_where != "") _ = ApplyFilter();
        }

        /// <summary>กรองตามค่าในช่องที่เลือก: = / &lt;&gt; / LIKE / IS NULL / IS NOT NULL (ต่อท้ายตัวกรองเดิมด้วย AND)</summary>
        void FilterByCell(string op)
        {
            var c = _grid.CurrentCell;
            if (_dt == null || c == null || c.ColumnIndex >= _cols.Count) return;
            var cm = _cols[c.ColumnIndex];
            object? v = RowAt(c.RowIndex)?[c.ColumnIndex];
            if (v is BlobRef || v is byte[]) { Ui.Info(this, Tr.T("กรองด้วยค่า BLOB ไม่ได้", "Cannot filter on a BLOB value")); return; }
            string lit = v is null or DBNull ? "NULL" : IsNumType(cm.DataType) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : LiveValue.Quote(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            string cond = op switch
            {
                "IS NULL" or "IS NOT NULL" => Q(cm.Name) + " " + op,
                "LIKE" => Q(cm.Name) + " LIKE " + LiveValue.Quote("%" + (v is null or DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)) + "%"),
                _ when lit == "NULL" => Q(cm.Name) + (op == "=" ? " IS NULL" : " IS NOT NULL"),
                _ => Q(cm.Name) + " " + op + " " + lit,
            };
            string cur = _txtWhere.Text.Trim();
            _txtWhere.Text = cur == "" ? cond : "(" + cur + ") AND " + cond;
            _ = ApplyFilter();
        }

        /// <summary>คลิกขวาที่หัวคอลัมน์: เรียง · ซ่อน / แสดงคอลัมน์ · ปรับความกว้าง · คืนลำดับเดิม · คัดลอกชื่อ</summary>
        void ShowHeaderMenu(int col)
        {
            if (col < 0 || col >= _grid.Columns.Count || col >= _cols.Count) return;
            var gc = _grid.Columns[col];
            string name = _cols[col].Name;
            var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            m.Items.Add(Tr.T("เรียงน้อย → มาก", "Sort ascending"), null, (s, e) => _ = SortExplicit(name, false));
            m.Items.Add(Tr.T("เรียงมาก → น้อย", "Sort descending"), null, (s, e) => _ = SortExplicit(name, true));
            if (_orderCol != null) m.Items.Add(Tr.T("ไม่เรียง", "Remove sorting"), null, (s, e) => _ = SortExplicit(null, false));
            m.Items.Add(new ToolStripSeparator());
            var hide = m.Items.Add(Tr.T($"ซ่อนคอลัมน์ \"{name}\"", $"Hide column \"{name}\""), null, (s, e) => { gc.Visible = false; ColumnsChanged(); });
            hide.Enabled = _grid.Columns.Cast<DataGridViewColumn>().Count(x => x.Visible) > 1;
            var pick = new ToolStripMenuItem(Tr.T("เลือกคอลัมน์ที่แสดง", "Choose visible columns"));
            pick.DropDown.Renderer = new Theme.Renderer();
            foreach (var x in _grid.Columns.Cast<DataGridViewColumn>().OrderBy(x => x.DisplayIndex))
            {
                var it = new ToolStripMenuItem(x.Index < _cols.Count ? _cols[x.Index].Name : x.HeaderText) { Checked = x.Visible, CheckOnClick = true };
                var cx = x;
                it.CheckedChanged += (s, e) =>
                {
                    if (!it.Checked && _grid.Columns.Cast<DataGridViewColumn>().Count(y => y.Visible) <= 1) { it.Checked = true; return; }
                    cx.Visible = it.Checked;
                    ColumnsChanged();
                };
                pick.DropDownItems.Add(it);
            }
            pick.DropDown.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
            m.Items.Add(pick);
            m.Items.Add(Tr.T("แสดงทุกคอลัมน์", "Show all columns"), null, (s, e) => { foreach (DataGridViewColumn x in _grid.Columns) x.Visible = true; ColumnsChanged(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Tr.T("ปรับความกว้างพอดี", "Fit width"), null, (s, e) => _grid.AutoResizeColumn(col, DataGridViewAutoSizeColumnMode.AllCells));
            m.Items.Add(Tr.T("ปรับทุกคอลัมน์พอดี", "Fit all columns"), null, (s, e) =>
            {
                _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
                foreach (DataGridViewColumn x in _grid.Columns) if (x.Width > Ui.Px(this, 420)) x.Width = Ui.Px(this, 420);
            });
            m.Items.Add(Tr.T("คืนลำดับ / ความกว้างเดิม", "Reset order / widths"), null, (s, e) =>
            {
                _layoutKey = "";
                for (int i = 0; i < _grid.Columns.Count; i++) { _grid.Columns[i].DisplayIndex = i; _grid.Columns[i].Visible = true; }
                _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
                TypedHeaders(_grid, i => i < _cols.Count ? (_cols[i].Name, _cols[i].ColumnType, _cols[i].Key == "PRI") : ("", "", false));
                ColumnsChanged();
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Tr.T("คัดลอกชื่อคอลัมน์", "Copy column name"), null, (s, e) => Clipboard.SetText(name));
            var at = _grid.PointToClient(Cursor.Position);
            m.Show(_grid, at);
        }

        void ColumnsChanged()
        {
            int hidden = _grid.Columns.Cast<DataGridViewColumn>().Count(x => !x.Visible);
            if (hidden > 0) SetStatus(Tr.T($"ซ่อน {hidden:N0} คอลัมน์ (คลิกขวาที่หัวคอลัมน์ → แสดงทุกคอลัมน์)", $"{hidden:N0} column(s) hidden (right-click a header → Show all columns)"));
            if (_textMode) RefreshTextView();
        }

        /// <summary>ปุ่มสลับมุมมอง Grid / Text (มีทั้งหน้าตารางและหน้าผลลัพธ์ SQL ใช้ค่าเดียวกัน)</summary>
        Control[] ModeButtons()
        {
            var g = IconBtn("", Tr.T("ดูแบบตาราง (Grid)", "Grid view"), () => SetTextMode(false));
            var t = IconBtn("", Tr.T("ดูแบบข้อความ (Text) — คัดลอกไปวางได้ทันที", "Text view — easy to copy & paste"), () => SetTextMode(true));
            g.Margin = new Padding(0, 1, 0, 1);
            _modeBtns.Add((g, false));
            _modeBtns.Add((t, true));
            return new Control[] { g, t };
        }

        // ------------------------------------------------------------------ หัวคอลัมน์ 2 บรรทัด (ชื่อ + ชนิด แบบ pgAdmin)

        Font? _hdrBold, _hdrSmall;

        /// <summary>ตั้งความสูงหัวตาราง และขยายคอลัมน์ให้พอกับชื่อ / ชนิด</summary>
        void TypedHeaders(DataGridView g, Func<int, (string Name, string Type, bool Key)> info)
        {
            _hdrBold ??= new Font(Ui.Font, FontStyle.Bold);
            _hdrSmall ??= new Font(Ui.Font.FontFamily, Ui.Font.Size - 1.25f);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = Ui.Px(this, 44);
            for (int i = 0; i < g.Columns.Count; i++)
            {
                var (name, type, key) = info(i);
                int w = Math.Max(TextRenderer.MeasureText(name, _hdrBold).Width, TextRenderer.MeasureText((key ? "[PK] " : "") + type, _hdrSmall).Width) + Ui.Px(this, 26);
                if (g.Columns[i].Width < w) g.Columns[i].Width = Math.Min(w, Ui.Px(this, 320));
            }
        }

        /// <summary>วาดหัวคอลัมน์: ชื่อ (ตัวหนา) บรรทัดบน, ชนิด (สีจาง) บรรทัดล่าง, ลูกศรเรียงทางขวา</summary>
        void PaintTypedHeader(DataGridView g, DataGridViewCellPaintingEventArgs e, string name, string type, bool key)
        {
            _hdrBold ??= new Font(Ui.Font, FontStyle.Bold);
            _hdrSmall ??= new Font(Ui.Font.FontFamily, Ui.Font.Size - 1.25f);
            var gr = e.Graphics;
            if (gr == null) return;
            e.PaintBackground(e.ClipBounds, false);
            var b = e.CellBounds;
            int pad = Ui.Px(this, 6), mid = b.Top + b.Height / 2;
            var sort = g.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection;
            int right = b.Right - pad - (sort != SortOrder.None ? Ui.Px(this, 12) : 0);
            const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(gr, name, _hdrBold, new Rectangle(b.Left + pad, b.Top + Ui.Px(this, 4), right - b.Left - pad, mid - b.Top), Theme.Text, f | TextFormatFlags.Bottom);
            if (key)
            {
                const TextFormatFlags fk = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.Top;
                int kw = TextRenderer.MeasureText(gr, "[PK]", _hdrSmall, Size.Empty, fk).Width + Ui.Px(this, 4);
                TextRenderer.DrawText(gr, "[PK]", _hdrSmall, new Rectangle(b.Left + pad, mid + Ui.Px(this, 1), kw + Ui.Px(this, 2), b.Bottom - mid), Theme.IconDb, fk);
                TextRenderer.DrawText(gr, type, _hdrSmall, new Rectangle(b.Left + pad + kw, mid + Ui.Px(this, 1), right - b.Left - pad - kw, b.Bottom - mid), Theme.Muted, f | TextFormatFlags.Top);
            }
            else TextRenderer.DrawText(gr, type, _hdrSmall, new Rectangle(b.Left + pad, mid + Ui.Px(this, 1), right - b.Left - pad, b.Bottom - mid), Theme.Muted, f | TextFormatFlags.Top);
            if (sort != SortOrder.None)
                TextRenderer.DrawText(gr, sort == SortOrder.Ascending ? "▲" : "▼", _hdrSmall, new Rectangle(right, b.Top, b.Right - right - Ui.Px(this, 2), b.Height), Theme.Link, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            e.Handled = true;
        }


        void StyleModeButtons()
        {
            foreach (var (b, text) in _modeBtns)
            {
                b.Tag = text == _textMode ? "primary" : "";
                Theme.Apply(b);
            }
            PaintIcons();
        }

        void SetTextMode(bool on)
        {
            _textMode = on;
            Ui.SetSetting("text_mode", on ? "1" : "0");
            StyleModeButtons();
            if (on) { _grid.EndEdit(); RefreshTextView(); }
            _txtData.Visible = on;
            _grid.Visible = !on;
            if (_sqlTab >= 0) ShowSqlTab(_sqlTab);
        }

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
            // ช่องค้นหาในหน้า (ชื่อ หรือคอลัมน์อื่น เช่น คำอธิบาย) และช่องค้นหาทางซ้าย (ชื่อ)
            string q = _lvFind.Text.Trim(), q2 = _txtFind.Text.Trim();
            bool Hit(ListViewItem i) =>
                (q == "" || i.SubItems.Cast<ListViewItem.ListViewSubItem>().Any(s => s.Text.Contains(q, StringComparison.OrdinalIgnoreCase))) &&
                (q2 == "" || i.Text.Contains(q2, StringComparison.OrdinalIgnoreCase));
            var show = q == "" && q2 == "" ? _lvAll : _lvAll.Where(Hit).ToList();
            _lv.BeginUpdate();
            _lv.Items.Clear();
            _lv.Items.AddRange(show.ToArray());
            ApplyListSort();
            _lv.EndUpdate();
            if (q != "") SetStatus(Tr.T($"ค้นหา \"{q}\": พบ {show.Count:N0} จาก {_lvAll.Count:N0} รายการ   (Enter = เปิดรายการแรก)", $"Search \"{q}\": {show.Count:N0} of {_lvAll.Count:N0}   (Enter = open the first one)"));
        }

        /// <summary>เก็บรายการเต็มของ ListView ไว้กรอง / เรียง แล้วกรองตามคำค้นปัจจุบัน</summary>
        void ListLoaded()
        {
            _lvAll = _lv.Items.Cast<ListViewItem>().ToList();
            if (_txtFind.Text.Trim() != "" || _lvFind.Text.Trim() != "") FilterList();
        }

        /// <summary>ใส่ช่องค้นหาไว้หน้าสุดของแถบปุ่ม (เริ่มว่างทุกครั้งที่เปลี่ยนหน้า)</summary>
        void AddSearch(string placeholder)
        {
            _lvFind.Text = "";
            _lvFind.PlaceholderText = "🔍  " + placeholder;
            _actions.Controls.Add(_lvFind);
            Theme.Apply(_lvFind);
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
            // สร้าง / ลบฐานข้อมูล · Backup · Restore · SQL Editor อยู่บนแถบเครื่องมือด้านบนแล้ว (ใช้กับรายการที่เลือกในหน้านี้)
            AddSearch(Tr.T("ค้นหาฐานข้อมูล…", "Search databases…"));
            AddAction(Btn(Tr.T("เปิด", "Open"), OpenListItem, "primary"));
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
            // SQL Editor · Restore · ลบฐานข้อมูล อยู่บนแถบเครื่องมือด้านบนแล้ว
            AddSearch(Tr.T("ค้นหาตาราง…", "Search tables…"));
            AddAction(Btn(Tr.T("เปิดตาราง", "Open table"), OpenListItem, "primary"));
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
