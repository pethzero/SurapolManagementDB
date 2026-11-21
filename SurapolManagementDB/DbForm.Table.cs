using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySqlConnector;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>หน้าต่างฐานข้อมูล: ข้อมูลในตาราง (ดู / เพิ่ม / แก้ / ลบ) และ BLOB</summary>
    public sealed partial class DbForm
    {
        // ------------------------------------------------------------------ ตาราง

        /// <summary>มีการแก้ไขที่ยังไม่บันทึก (จบการแก้ไขในช่อง / แถวที่ค้างอยู่ก่อนตรวจ)</summary>
        bool HasPending
        {
            get
            {
                FlushEdits();
                return _dt != null && _dt.GetChanges() != null;
            }
        }

        /// <summary>
        /// ค่าที่พิมพ์ในตารางจะค้างอยู่ใน "โหมดแก้ไขแถว" ของ DataRowView จนกว่าจะเลื่อนไปแถวอื่น
        /// (ระหว่างนั้น RowState ยังเป็น Unchanged → ปุ่มบันทึกไม่ขึ้น / Ctrl+S ไม่บันทึก) → สั่งจบการแก้ไขให้มีผลทันที
        /// </summary>
        void FlushEdits()
        {
            if (_dt == null) return;
            try
            {
                if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
                if (_grid.DataSource == _dt) _grid.BindingContext?[_dt].EndCurrentEdit();
                foreach (DataRowView rv in _dt.DefaultView) if (rv.IsEdit) rv.EndEdit();
            }
            catch (Exception ex) { SetStatus("✖ " + Err(ex)); }
        }

        /// <summary>จำนวนแถวที่แก้ / เพิ่ม / ลบ แล้วยังไม่บันทึก</summary>
        int PendingCount() => _dt == null ? 0 : _dt.Rows.Cast<DataRow>().Count(r => r.RowState is DataRowState.Added or DataRowState.Modified or DataRowState.Deleted);

        DialogResult AskPending() =>
            MessageBox.Show(this, Tr.T("มีข้อมูลที่แก้ไขแล้วยังไม่บันทึก\n\nYes = บันทึก     No = ทิ้งการแก้ไข     Cancel = กลับไปแก้ต่อ", "There are unsaved changes\n\nYes = save     No = discard     Cancel = keep editing"),
                Ui.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        /// <summary>ก่อนเปลี่ยนหน้า / กรอง / เรียง: ถามเรื่องข้อมูลที่ยังไม่บันทึก  false = ยกเลิก</summary>
        async Task<bool> OkToLeave()
        {
            if (!HasPending) return true;
            var r = AskPending();
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return await Save();
            _dt!.RejectChanges();
            return true;
        }

        async Task SaveThen(Action next)
        {
            if (await Save()) next();
        }

        async Task OpenTable(SavedConn sc, string db, string table, bool view)
        {
            var c = Conn(sc, db);
            if (c == null) return;
            _sc = sc; _db = db; _tbl = table; _isView = view;
            _offset = 0; _where = ""; _orderCol = null; _orderDesc = false;
            _txtWhere.Text = "";
            _layoutKey = "";
            ShowPage(_tablePage);
            SetHeader(table, (view ? "View" : Tr.T("ตาราง", "Table")) + "  ·  " + db + "  ·  " + sc.Display, view ? "view" : "table");
            SelectTab(0);
            _grid.DataSource = null;
            _dt = null;
            try
            {
                SetStatus(Tr.T("กำลังโหลด ", "Loading ") + table + " …");
                var meta = await Task.Run(() => Query(c, null,
                    "SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, IFNULL(COLUMN_DEFAULT, 'NULL'), EXTRA, IFNULL(COLLATION_NAME, ''), COLUMN_COMMENT " +
                    "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION", ("@db", db), ("@t", table)));
                var idx = await Task.Run(() => Query(c, null,
                    "SELECT INDEX_NAME, COLUMN_NAME, NON_UNIQUE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY INDEX_NAME <> 'PRIMARY', INDEX_NAME, SEQ_IN_INDEX", ("@db", db), ("@t", table)));
                var ddl = await Task.Run(() => Query(c, db, "SHOW CREATE " + (view ? "VIEW " : "TABLE ") + Q(table)));
                _cols = meta.Select(r =>
                {
                    string dt = (r[1] ?? "").ToLowerInvariant();
                    return new ColMeta
                    {
                        Name = r[0]!, DataType = dt, ColumnType = r[2] ?? "", Nullable = r[3] == "YES", Key = r[4] ?? "", Extra = r[6] ?? "",
                        Lazy = dt.EndsWith("blob") || dt is "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection",
                        Binary = dt is "binary" or "varbinary",
                        Generated = (r[6] ?? "").Contains("GENERATED", StringComparison.OrdinalIgnoreCase),
                        Float = dt is "float" or "double" or "real",
                    };
                }).ToList();
                // คีย์สำหรับระบุแถว: primary key → unique index ที่ไม่มีค่า NULL → ทุกคอลัมน์
                _keyIdx = new List<int>();
                _realKey = false;
                foreach (var g in idx.GroupBy(r => r[0]))
                {
                    if (g.First()[2] != "0") continue;
                    var ix = g.Select(r => _cols.FindIndex(cm => cm.Name == r[1])).ToList();
                    if (ix.Any(i => i < 0 || _cols[i].Lazy)) continue;
                    if (g.Key != "PRIMARY" && ix.Any(i => _cols[i].Nullable)) continue;
                    _keyIdx = ix;
                    _realKey = true;
                    break;
                }
                if (!_realKey)
                {
                    _keyIdx = Enumerable.Range(0, _cols.Count).Where(i => !_cols[i].Lazy && !_cols[i].Float).ToList();
                    if (_keyIdx.Count == 0) _keyIdx = Enumerable.Range(0, _cols.Count).Where(i => !_cols[i].Lazy).ToList();
                }
                var cdt = new DataTable();
                foreach (var h in new[] { Tr.T("คอลัมน์", "Column"), Tr.T("ชนิด", "Type"), "NULL", Tr.T("คีย์", "Key"), "Default", "Extra", "Collation", Tr.T("คำอธิบาย", "Comment") }) cdt.Columns.Add(h);
                foreach (var r in meta) cdt.Rows.Add(r[0], r[2], r[3], r[4], r[5], r[6], r[7], r[8]);
                _gridCols.DataSource = cdt;
                _txtDdl.Text = ddl.Count > 0 ? (ddl[0][1] ?? "").Replace("\n", "\r\n") : "";
                await ReloadPage(false);
            }
            catch (Exception ex)
            {
                SetStatus("✖ " + Err(ex));
                _lblData.Text = "✖ " + Err(ex);
            }
        }

        async Task ApplyFilter()
        {
            if (_dt == null || !await OkToLeave()) return;
            _where = _txtWhere.Text.Trim();
            _offset = 0;
            await ReloadPage(false);
        }

        async Task Page(int dir)
        {
            if (_dt == null || !await OkToLeave()) return;
            _offset = Math.Max(0, _offset + dir * _pageSize);
            await ReloadPage(false);
        }

        async Task SortBy(int col)
        {
            if (_dt == null || col < 0 || col >= _cols.Count || !await OkToLeave()) return;
            string name = _cols[col].Name;
            if (_orderCol == name) _orderDesc = !_orderDesc;
            else { _orderCol = name; _orderDesc = false; }
            _offset = 0;
            await ReloadPage(false);
        }

        /// <summary>เรียงตามคอลัมน์ที่ระบุ (null = ไม่เรียง)</summary>
        async Task SortExplicit(string? col, bool desc)
        {
            if (_dt == null || !await OkToLeave()) return;
            _orderCol = col;
            _orderDesc = desc;
            _offset = 0;
            await ReloadPage(false);
        }

        /// <summary>ตารางที่จำลำดับ / ความกว้าง / การซ่อนคอลัมน์ไว้ ("" = ใช้ค่าเริ่มต้น)</summary>
        string _layoutKey = "";

        async Task ReloadPage(bool ask)
        {
            if (_sc == null || _busy) return;
            if (ask && !await OkToLeave()) return;
            var c = Conn(_sc, _db);
            if (c == null) return;
            string sel = string.Join(", ", _cols.Select(cm => cm.Lazy ? "LENGTH(" + Q(cm.Name) + ") AS " + Q(cm.Name) : Q(cm.Name)));
            string sql = "SELECT " + sel + " FROM " + Q(_tbl) + (_where != "" ? " WHERE " + _where : "") +
                         (_orderCol != null ? " ORDER BY " + Q(_orderCol) + (_orderDesc ? " DESC" : " ASC") : "") +
                         $" LIMIT {_pageSize + 1} OFFSET {_offset}";
            var cols = _cols;
            _busy = true;
            UseWaitCursor = true;
            try
            {
                SetStatus(Tr.T("กำลังโหลด…", "Loading…"));
                var (dt, more) = await Task.Run(() =>
                {
                    var t = new DataTable();
                    foreach (var cm in cols) t.Columns.Add(cm.Name, typeof(object));
                    using var conn = LiveValue.Open(c, _db);
                    using var cmd = new MySqlCommand(sql, conn);
                    using var r = cmd.ExecuteReader();
                    int n = 0;
                    bool m = false;
                    while (r.Read())
                    {
                        if (++n > _pageSize) { m = true; break; }
                        var vals = new object[cols.Count];
                        for (int i = 0; i < cols.Count; i++)
                        {
                            if (r.IsDBNull(i)) vals[i] = DBNull.Value;
                            else if (cols[i].Lazy) vals[i] = new BlobRef { Size = Convert.ToInt64(r.GetValue(i)) };
                            else vals[i] = LiveValue.Read(r, i) ?? (object)DBNull.Value;
                        }
                        t.Rows.Add(vals);
                    }
                    t.AcceptChanges();
                    return (t, m);
                });
                BindPage(dt, more, cols);
            }
            catch (Exception ex)
            {
                SetStatus("✖ " + Err(ex));
                _lblData.Text = "✖ " + Err(ex);
            }
            finally { _busy = false; UseWaitCursor = false; }
        }

        /// <summary>แสดงข้อมูล 1 หน้าในตาราง (หัวคอลัมน์ ความกว้าง ปุ่ม สถานะ)</summary>
        void BindPage(DataTable dt, bool more, List<ColMeta> cols)
        {
            // จำลำดับ / ความกว้าง / การซ่อนคอลัมน์ของตารางเดิมไว้ (เลื่อนหน้า / กรอง / เรียง / รีเฟรช แล้วยังเหมือนเดิม)
            string key = _db + "." + _tbl;
            Dictionary<string, (int Pos, bool Vis, int W)>? keep = null;
            if (_layoutKey == key && _grid.Columns.Count > 0)
                keep = _grid.Columns.Cast<DataGridViewColumn>().GroupBy(c => c.DataPropertyName).ToDictionary(g => g.Key, g => (g.First().DisplayIndex, g.First().Visible, g.First().Width));
            _binding = true;
            _dt = dt;
            _hasMore = more;
            _grid.DataSource = dt;
            for (int i = 0; i < _grid.Columns.Count && i < cols.Count; i++)
            {
                var gc = _grid.Columns[i];
                var cm = cols[i];
                gc.SortMode = DataGridViewColumnSortMode.Programmatic;
                gc.HeaderCell.SortGlyphDirection = _orderCol == cm.Name ? (_orderDesc ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
                gc.ReadOnly = _isView || cm.Lazy || cm.Binary || cm.Generated;
                gc.ToolTipText = cm.ColumnType + (cm.Key == "PRI" ? "  PRIMARY KEY" : "") + (cm.Extra != "" ? "  " + cm.Extra : "") + (cm.Nullable ? "" : "  NOT NULL");
                if (IsNumType(cm.DataType)) gc.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            foreach (DataGridViewColumn gc in _grid.Columns) if (gc.Width > Ui.Px(this, 320)) gc.Width = Ui.Px(this, 320);
            TypedHeaders(_grid, i => i < cols.Count ? (cols[i].Name, cols[i].ColumnType, cols[i].Key == "PRI") : ("", "", false));
            if (keep != null)
            {
                foreach (DataGridViewColumn gc in _grid.Columns)
                    if (keep.TryGetValue(gc.DataPropertyName, out var k)) { gc.Visible = k.Vis; gc.Width = k.W; }
                foreach (var (name, k) in keep.OrderBy(x => x.Value.Pos))
                    if (_grid.Columns[name] is DataGridViewColumn gc && k.Pos < _grid.Columns.Count) gc.DisplayIndex = k.Pos;
            }
            _layoutKey = key;
            _binding = false;
            _grid.ReadOnly = true;
            _grid.AllowUserToDeleteRows = false;
            _btnPrev.Enabled = _offset > 0;
            _btnNext.Enabled = more;
            if (_cmbPage.SelectedIndex != Array.IndexOf(PageSizes, _pageSize)) _cmbPage.SelectedIndex = Math.Max(0, Array.IndexOf(PageSizes, _pageSize));
            _lblPage.Text = dt.Rows.Count == 0 ? "0" : $"{_offset + 1:N0}–{_offset + dt.Rows.Count:N0}";
            UpdateDirty();
            if (_textMode) RefreshTextView();
            SetStatus($"{_db}.{_tbl}  —  " + Tr.T("อ่านอย่างเดียว (Demo)", "Read-only (Demo)"));
        }

        void UpdateDirty()
        {
            if (_dt == null) return;
            int add = 0, mod = 0, del = 0;
            foreach (DataRow r in _dt.Rows)
                switch (r.RowState)
                {
                    case DataRowState.Added: add++; break;
                    case DataRowState.Modified: mod++; break;
                    case DataRowState.Deleted: del++; break;
                }
            bool dirty = add + mod + del > 0;
            // สถานะแบบสั้น (มุมขวาล่าง)  รายละเอียด / วิธีใช้อยู่ใน tooltip
            string info = Tr.T($"{_dt.Rows.Count - del:N0} แถว", $"{_dt.Rows.Count - del:N0} rows") + (_hasMore ? Tr.T(" · มีหน้าถัดไป", " · more pages") : "") +
                          (_where != "" ? Tr.T(" · กรองอยู่", " · filtered") : "");
            if (dirty) info += Tr.T($"  ·  ยังไม่บันทึก: แก้ {mod}, เพิ่ม {add}, ลบ {del}", $"  ·  unsaved: {mod} changed, {add} added, {del} deleted");
            _lblData.Text = info;
            _lblData.ForeColor = dirty ? Theme.Warn : Theme.Muted;
            _btnTip.SetToolTip(_lblData, Tr.T("ดับเบิลคลิกแถว = ดู / แก้ทีละแถว   ·   ดับเบิลคลิกช่อง BLOB = เปิดดูไฟล์ / รูป\nคลิกขวาที่ช่อง = คัดลอก / กรองตามค่า   ·   คลิกขวาที่หัวคอลัมน์ = เรียง / ซ่อน / แสดงคอลัมน์\nลากหัวคอลัมน์ = สลับลำดับ",
                                              "Double-click a row = view / edit it   ·   double-click a BLOB cell = open the file / image\nRight-click a cell = copy / filter by value   ·   right-click a header = sort / hide / show columns\nDrag a header = reorder columns"));
        }

        bool IsBlobCol(int col) => col >= 0 && col < _cols.Count && (_cols[col].Lazy || _cols[col].Binary);

        void OnCellFormatting(object? s, DataGridViewCellFormattingEventArgs e)
        {
            if (e.Value is DBNull || e.Value == null)
            {
                e.Value = "NULL";
                e.CellStyle!.ForeColor = Theme.Muted;
                e.FormattingApplied = true;
            }
            else if (e.Value is BlobRef br)
            {
                e.Value = br.ToString();
                e.CellStyle!.ForeColor = Theme.Link;
                e.FormattingApplied = true;
            }
            else if (e.Value is byte[] b)
            {
                bool isNew = e.ColumnIndex < _cols.Count && _cols[e.ColumnIndex].Lazy;
                e.Value = isNew ? Tr.T($"<ไฟล์ใหม่ {Ui.HumanSize(b.Length)}>", $"<new file {Ui.HumanSize(b.Length)}>") : "0x" + Convert.ToHexString(b, 0, Math.Min(b.Length, 64)) + (b.Length > 64 ? "…" : "");
                e.CellStyle!.ForeColor = isNew ? Theme.Warn : Theme.Link;
                e.FormattingApplied = true;
            }
            if (e.RowIndex >= 0 && e.RowIndex < _grid.Rows.Count && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView rv)
            {
                if (rv.Row.RowState == DataRowState.Added) e.CellStyle!.BackColor = Theme.Added;
                else if (rv.Row.RowState == DataRowState.Modified && e.ColumnIndex < _dt!.Columns.Count &&
                         !Same(rv.Row[e.ColumnIndex, DataRowVersion.Original], rv.Row[e.ColumnIndex, DataRowVersion.Current]))
                    e.CellStyle!.BackColor = Theme.Modified;
            }
        }

        static bool Same(object? a, object? b) => ReferenceEquals(a, b) || Equals(a, b) || (a is DBNull && b is DBNull);

        DataRow? RowAt(int gridRow) => gridRow >= 0 && gridRow < _grid.Rows.Count ? (_grid.Rows[gridRow].DataBoundItem as DataRowView)?.Row : null;


        /// <summary>แปลงค่าในตารางเป็นค่าของ parameter (BIT ส่งเป็นตัวเลข ไม่ใช่ข้อความ)</summary>
        static object ParamValue(ColMeta cm, object v)
        {
            if (v is DBNull || v == null) return DBNull.Value;
            if (v is byte[]) return v;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (cm.DataType == "bit" && ulong.TryParse(s, out ulong u)) return u;
            return s;
        }

        /// <summary>WHERE ที่ระบุแถวเดิม (ใช้ค่าก่อนแก้ไข)</summary>
        string Where(DataRow r, List<(string, object)> ps)
        {
            var parts = new List<string>();
            foreach (int i in _keyIdx)
            {
                object v = r[i, DataRowVersion.Original];
                if (v is DBNull) { parts.Add(Q(_cols[i].Name) + " IS NULL"); continue; }
                string p = "@k" + ps.Count;
                parts.Add(Q(_cols[i].Name) + (_realKey ? " = " : " <=> ") + p);
                ps.Add((p, ParamValue(_cols[i], v)));
            }
            return " WHERE " + string.Join(" AND ", parts) + (_realKey ? "" : " LIMIT 1");
        }

        /// <summary>ตัว demo อ่านอย่างเดียว: ไม่มีอะไรให้บันทึก</summary>
        public Task<bool> Save() => Task.FromResult(true);


        // ------------------------------------------------------------------ BLOB

        /// <summary>นามสกุลไฟล์ที่เดาจากเนื้อไฟล์ (.jpg / .png / .pdf …)</summary>
        static string BlobExt(byte[] b) => Ui.BlobKind(b).Ext;

        /// <summary>อ่านค่า BLOB จริงของช่อง (โหลดจากฐานข้อมูลถ้ายังไม่ได้โหลด)</summary>
        Task<byte[]?> BlobBytes(int gridRow, int col) => BlobBytesOf(RowAt(gridRow), col);

        async Task<byte[]?> BlobBytesOf(DataRow? r, int col)
        {
            if (r == null || _sc == null) return null;
            object v = r[col];
            if (v is byte[] b) return b;
            if (v is not BlobRef) return null;
            if (r.RowState == DataRowState.Added) return null;
            var c = Conn(_sc, _db);
            if (c == null) return null;
            var ps = new List<(string, object)>();
            string sql = "SELECT " + Q(_cols[col].Name) + " FROM " + Q(_tbl) + Where(r, ps).Replace(" LIMIT 1", "") + " LIMIT 1";
            return await Task.Run(() =>
            {
                using var conn = LiveValue.Open(c, _db);
                using var cmd = new MySqlCommand(sql, conn);
                foreach (var (k, pv) in ps) cmd.Parameters.AddWithValue(k, pv);
                return cmd.ExecuteScalar() as byte[];
            });
        }

        Task OpenBlob(int gridRow, int col) => OpenBlobOf(RowAt(gridRow), col);

        async Task OpenBlobOf(DataRow? r, int col)
        {
            try
            {
                var b = await BlobBytesOf(r, col);
                if (b == null) return;
                int n = r != null && _dt != null ? _dt.Rows.IndexOf(r) : 0;
                string path = Path.Combine(Path.GetTempPath(), Ui.AppName + "_" + _tbl + "_" + _cols[col].Name + "_" + n + BlobExt(b));
                File.WriteAllBytes(path, b);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Ui.Error(this, Err(ex)); }
        }

        async Task SaveBlobOf(DataRow r, int col)
        {
            try
            {
                var b = await BlobBytesOf(r, col);
                if (b == null) { Ui.Info(this, Tr.T("ช่องนี้เป็น NULL", "This cell is NULL")); return; }
                string ext = BlobExt(b);
                using var dlg = new SaveFileDialog { FileName = _tbl + "_" + _cols[col].Name + ext, Filter = Tr.T($"ไฟล์ {ext}|*{ext}|ทุกไฟล์|*.*", $"{ext} file|*{ext}|All files|*.*") };
                if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(dlg.FileName, b);
            }
            catch (Exception ex) { Ui.Error(this, Err(ex)); }
        }

        // ------------------------------------------------------------------ ดู / แก้ไขทีละแถว

        static bool IsNumType(string dt) => dt is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "decimal" or "numeric" or "float" or "double" or "real" or "bit" or "year";

        static RowField FieldOf(ColMeta c, bool key)
        {
            string dt = c.DataType;
            int len = 0;
            int p = c.ColumnType.IndexOf('(');
            if (p > 0) int.TryParse(new string(c.ColumnType.Substring(p + 1).TakeWhile(char.IsDigit).ToArray()), out len);
            return new RowField
            {
                Name = c.Name, Type = c.ColumnType + (c.Extra != "" ? "  " + c.Extra : ""), Key = key, Nullable = c.Nullable,
                ReadOnly = c.Generated || c.Binary,
                Kind = c.Lazy ? "blob" : dt == "json" ? "json" : IsNumType(dt) ? "num" : dt is "date" or "datetime" or "timestamp" or "time" ? "date" : "text",
                Long = dt is "text" or "mediumtext" or "longtext" or "json" || (dt is "varchar" or "char" && len > 150),
            };
        }

        /// <summary>เปิดหน้าต่างของแถวที่เลือก โดยชี้ช่องที่เลือกอยู่</summary>
        void EditCurrentRow() => OpenRowEditor(_grid.CurrentCell?.RowIndex ?? 0, _grid.CurrentCell?.ColumnIndex ?? -1);

        /// <summary>สร้างหน้าต่างของแถวในตาราง (แถวในหน้านี้ทั้งหมด เลื่อน ◀ ▶ ได้)  null = ไม่มีข้อมูล</summary>
        RowEditForm? CreateRowEditor(int gridRow, int focusCol = -1)
        {
            if (_dt == null || _busy || _cols.Count == 0) return null;
            _grid.EndEdit();
            var rows = _grid.Rows.Cast<DataGridViewRow>().Select(r => (r.DataBoundItem as DataRowView)?.Row)
                .Where(r => r != null && r.RowState != DataRowState.Deleted).Select(r => r!).ToList();
            if (rows.Count == 0) return null;
            var cur = RowAt(gridRow);
            int idx = cur == null ? 0 : Math.Max(0, rows.IndexOf(cur));
            var keys = new HashSet<int>(_realKey ? _keyIdx : new List<int>());
            var fields = _cols.Select((c, i) => FieldOf(c, keys.Contains(i))).ToList();
            Func<Task<bool>>? save = null;
            return new RowEditForm(_tbl, fields, rows, idx, save == null, OpenBlobOf, SaveBlobOf, save, focusCol < _cols.Count ? focusCol : -1);
        }

        /// <summary>หน้าต่างดู / แก้ไขทีละแถว (ดับเบิลคลิกที่แถว)  แถวถูกระบุด้วย key (ROWID / primary key)</summary>
        void OpenRowEditor(int gridRow, int focusCol = -1)
        {
            using (var f = CreateRowEditor(gridRow, focusCol))
            {
                if (f == null) return;
                f.ShowDialog(this);
            }
            if (_dt == null) return;
            _grid.Invalidate();
            UpdateDirty();
            if (_textMode) RefreshTextView();
        }

        // ------------------------------------------------------------------ คัดลอก / วาง ทั้งกลุ่ม (แบบ DBeaver / Excel)

        /// <summary>ช่องที่เลือกเป็นข้อความคั่นด้วย Tab (ตามลำดับคอลัมน์ที่แสดง)  headers = มีหัวคอลัมน์</summary>
        string SelectionText(bool headers)
        {
            var old = _grid.ClipboardCopyMode;
            _grid.ClipboardCopyMode = headers ? DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText : DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            string t;
            try { t = _grid.GetClipboardContent()?.GetText(TextDataFormat.UnicodeText) ?? ""; }
            finally { _grid.ClipboardCopyMode = old; }
            // พร้อมหัวคอลัมน์: ตาราง Windows ใส่ช่องหัวแถว (ว่าง) มาด้วย → ตัด Tab หน้าสุดออก
            var lines = t.Split("\r\n");
            if (headers && lines.Where(l => l != "").All(l => l.StartsWith("\t"))) t = string.Join("\r\n", lines.Select(l => l.Length > 0 ? l.Substring(1) : l));
            return t;
        }

        void CopySelection(bool headers)
        {
            string t = SelectionText(headers);
            if (t == "") return;
            try { Clipboard.SetText(t); } catch (Exception ex) { SetStatus("✖ " + ex.Message); return; }
            SetStatus(Tr.T($"คัดลอก {_grid.SelectedCells.Count:N0} ช่องแล้ว", $"Copied {_grid.SelectedCells.Count:N0} cell(s)") + (headers ? Tr.T(" (พร้อมหัวคอลัมน์)", " (with headers)") : ""));
        }

        /// <summary>แถวที่มีช่องถูกเลือก เป็นคำสั่ง INSERT (ทุกคอลัมน์ ยกเว้น BLOB ที่ยังไม่ได้โหลด / generated)</summary>
        string InsertText()
        {
            if (_dt == null) return "";
            var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().OrderBy(i => i).Select(RowAt).Where(r => r != null && r.RowState != DataRowState.Deleted).ToList();
            var idx = Enumerable.Range(0, _cols.Count).Where(i => !_cols[i].Lazy && !_cols[i].Generated).ToList();
            if (rows.Count == 0 || idx.Count == 0) return "";
            string Lit(object? v, ColMeta cm) => v switch
            {
                null or DBNull or BlobRef => "NULL",
                byte[] b => "0x" + Convert.ToHexString(b),
                string s when IsNumType(cm.DataType) && decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) => s,
                _ => LiveValue.Quote(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            };
            return "INSERT INTO " + Q(_tbl) + " (" + string.Join(", ", idx.Select(i => Q(_cols[i].Name))) + ") VALUES\n" +
                   string.Join(",\n", rows.Select(r => "(" + string.Join(", ", idx.Select(i => Lit(r![i], _cols[i]))) + ")")) + ";";
        }

        void CopyAsInsert()
        {
            string t = InsertText();
            if (t == "") return;
            try { Clipboard.SetText(t); } catch (Exception ex) { SetStatus("✖ " + ex.Message); return; }
            SetStatus(Tr.T("คัดลอกเป็นคำสั่ง INSERT แล้ว", "Copied as INSERT statements"));
        }

        /// <summary>สรุปช่องที่เลือก (แถว × คอลัมน์) ที่แถบสถานะ</summary>
        void ShowSelectionInfo()
        {
            int n = _grid.SelectedCells.Count;
            if (n <= 1) return;
            var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
            int rows = cells.Select(c => c.RowIndex).Distinct().Count(), cols = cells.Select(c => c.ColumnIndex).Distinct().Count();
            SetStatus(Tr.T($"เลือก {n:N0} ช่อง ({rows:N0} แถว × {cols:N0} คอลัมน์)  ·  Ctrl+C คัดลอก", $"{n:N0} cells selected ({rows:N0} rows × {cols:N0} columns)  ·  Ctrl+C copy") +
                      "");
        }


        // ------------------------------------------------------------------ โหมดดูแบบข้อความ

        static string CellText(object? v) => v switch
        {
            null or DBNull => "NULL",
            byte[] b => "<BLOB " + Ui.HumanSize(b.Length) + ">",
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };

        void RefreshTextView()
        {
            if (_dt == null) { _txtData.Text = ""; return; }
            var cols = _cols;
            // ตามลำดับและคอลัมน์ที่แสดงอยู่ในตาราง (ลาก / ซ่อนคอลัมน์แล้ว Text ก็เป็นแบบเดียวกัน)
            var order = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Select(c => c.Index).Where(i => i < _dt.Columns.Count).ToList();
            if (order.Count == 0) order = Enumerable.Range(0, _dt.Columns.Count).ToList();
            var heads = order.Select(i => _dt.Columns[i].ColumnName).ToList();
            var rows = _dt.DefaultView.Cast<DataRowView>().Where(rv => rv.Row.RowState != DataRowState.Deleted).Select(rv => order.Select(i => rv.Row[i]).ToArray());
            _txtData.Text = TextGrid.Format(heads, rows, k => order[k] < cols.Count && IsNumType(cols[order[k]].DataType), CellText, _txtData.Font);
        }

        async Task SaveBlob()
        {
            var cell = _grid.CurrentCell;
            if (cell == null || !IsBlobCol(cell.ColumnIndex)) { Ui.Info(this, Tr.T("คลิกเลือกช่อง BLOB ก่อน", "Select a BLOB cell first")); return; }
            try
            {
                var b = await BlobBytes(cell.RowIndex, cell.ColumnIndex);
                if (b == null) { Ui.Info(this, Tr.T("ช่องนี้เป็น NULL", "This cell is NULL")); return; }
                string ext = BlobExt(b);
                using var dlg = new SaveFileDialog { FileName = _tbl + "_" + _cols[cell.ColumnIndex].Name + ext, Filter = Tr.T($"ไฟล์ {ext}|*{ext}|ทุกไฟล์|*.*", $"{ext} file|*{ext}|All files|*.*") };
                if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(dlg.FileName, b);
            }
            catch (Exception ex) { Ui.Error(this, Err(ex)); }
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!e.Cancel && HasPending)
            {
                var r = AskPending();
                if (r == DialogResult.Cancel) e.Cancel = true;
                else if (r == DialogResult.Yes) { e.Cancel = true; _ = SaveThen(Close); }
                else _dt!.RejectChanges();
            }
            base.OnFormClosing(e);
        }

    }
}
