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

        bool HasPending => _dt != null && _dt.GetChanges() != null;

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
            _offset = Math.Max(0, _offset + dir * PageSize);
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

        async Task ReloadPage(bool ask)
        {
            if (_sc == null || _busy) return;
            if (ask && !await OkToLeave()) return;
            var c = Conn(_sc, _db);
            if (c == null) return;
            string sel = string.Join(", ", _cols.Select(cm => cm.Lazy ? "LENGTH(" + Q(cm.Name) + ") AS " + Q(cm.Name) : Q(cm.Name)));
            string sql = "SELECT " + sel + " FROM " + Q(_tbl) + (_where != "" ? " WHERE " + _where : "") +
                         (_orderCol != null ? " ORDER BY " + Q(_orderCol) + (_orderDesc ? " DESC" : " ASC") : "") +
                         $" LIMIT {PageSize + 1} OFFSET {_offset}";
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
                        if (++n > PageSize) { m = true; break; }
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
                    if (cm.Key == "PRI") gc.HeaderText = "🔑 " + cm.Name;
                }
                _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
                foreach (DataGridViewColumn gc in _grid.Columns) if (gc.Width > Ui.Px(this, 320)) gc.Width = Ui.Px(this, 320);
                _grid.ReadOnly = true;
                _grid.AllowUserToDeleteRows = false;
                _btnPrev.Enabled = _offset > 0;
                _btnNext.Enabled = more;
                _lblPage.Text = dt.Rows.Count == 0 ? "0" : $"{_offset + 1:N0}–{_offset + dt.Rows.Count:N0}";
                UpdateDirty();
                SetStatus($"{_db}.{_tbl}  —  " + Tr.T("อ่านอย่างเดียว (Demo)", "Read-only (Demo)"));
            }
            catch (Exception ex)
            {
                SetStatus("✖ " + Err(ex));
                _lblData.Text = "✖ " + Err(ex);
            }
            finally { _busy = false; UseWaitCursor = false; }
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
            string info = Tr.T($"{_dt.Rows.Count - del:N0} แถวในหน้านี้", $"{_dt.Rows.Count - del:N0} rows on this page") + (_hasMore ? Tr.T(" (มีหน้าถัดไป)", " (more pages)") : "");
            if (dirty) info += Tr.T($"   —   ยังไม่บันทึก: เพิ่ม {add}, แก้ {mod}, ลบ {del}  (กด บันทึก หรือ Ctrl+S)", $"   —   unsaved: {add} added, {mod} changed, {del} deleted  (Save or Ctrl+S)");
            info += Tr.T("   ·   ดับเบิลคลิกช่อง BLOB เพื่อเปิดดู, คลิกขวาเพื่อบันทึกเป็นไฟล์", "   ·   double-click a BLOB cell to open it, right-click to save it to a file");
            _lblData.Text = info;
            _lblData.ForeColor = dirty ? Theme.Warn : Theme.Muted;
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
        async Task<byte[]?> BlobBytes(int gridRow, int col)
        {
            var r = RowAt(gridRow);
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

        async Task OpenBlob(int gridRow, int col)
        {
            try
            {
                var b = await BlobBytes(gridRow, col);
                if (b == null) return;
                string path = Path.Combine(Path.GetTempPath(), Ui.AppName + "_" + _tbl + "_" + _cols[col].Name + "_" + gridRow + BlobExt(b));
                File.WriteAllBytes(path, b);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Ui.Error(this, Err(ex)); }
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
