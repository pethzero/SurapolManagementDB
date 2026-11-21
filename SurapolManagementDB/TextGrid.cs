using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace SurapolSQL
{
    /// <summary>
    /// แปลงตารางข้อมูลเป็นข้อความแบบ MySQL command line (โหมดดูแบบ Text)
    ///   +-------+-----------+
    ///   | ROWID | DATA_TEXT |
    ///   +-------+-----------+
    ///   |     1 | ข้อความ    |
    /// ระบุ font = จัดคอลัมน์ตามความกว้างจริงบนจอ (พิกเซล) — ภาษาไทยที่ฟอนต์ความกว้างคงที่ไม่มี
    /// จะถูกแสดงด้วยฟอนต์อื่นที่กว้างไม่เท่ากัน จึงต้องวัดจริงแล้วเติมช่องว่างให้ตรงตำแหน่ง
    /// ไม่ระบุ font = นับจำนวนตัวอักษร (สระบน / ล่าง วรรณยุกต์ = 0)
    /// </summary>
    internal static class TextGrid
    {
        /// <summary>ความกว้างสูงสุดของแต่ละช่อง (ตัวอักษร) ข้อความยาวกว่านี้ถูกตัดเป็น …</summary>
        public const int MaxCell = 60;

        /// <summary>จำนวนแถวสูงสุดที่แสดง (กันหน้าจอค้างเมื่อผลลัพธ์ใหญ่มาก)</summary>
        public const int MaxRows = 5000;

        /// <param name="headers">ชื่อคอลัมน์</param>
        /// <param name="rows">ค่าในแต่ละแถว</param>
        /// <param name="right">คอลัมน์ที่ชิดขวา (ตัวเลข)  null = ชิดซ้ายทั้งหมด</param>
        /// <param name="text">แปลงค่าเป็นข้อความ</param>
        /// <param name="font">ฟอนต์ที่ใช้แสดง (วัดความกว้างจริง)  null = นับตัวอักษร</param>
        public static string Format(IList<string> headers, IEnumerable<object?[]> rows, Func<int, bool>? right, Func<object?, string> text, Font? font = null)
        {
            int n = headers.Count;
            if (n == 0) return "";
            var head = new string[n];
            for (int c = 0; c < n; c++) head[c] = Clean(headers[c]);
            var cells = new List<string[]>();
            int total = 0;
            foreach (var r in rows)
            {
                total++;
                if (cells.Count >= MaxRows) continue;
                var line = new string[n];
                for (int c = 0; c < n; c++) line[c] = Clean(text(c < r.Length ? r[c] : null));
                cells.Add(line);
            }

            // ---- การวัดความกว้าง: พิกเซล (มีฟอนต์) หรือจำนวนตัวอักษร
            var cache = new Dictionary<string, double>();
            double M(string s)
            {
                if (s.Length == 0) return 0;
                if (font == null) return Width(s);
                if (cache.TryGetValue(s, out double v)) return v;
                const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                return cache[s] = TextRenderer.MeasureText(s, font, new Size(int.MaxValue, int.MaxValue), f).Width;
            }
            // ช่องว่าง / ขีด วัดจากหลายตัวแล้วหาร (ได้ค่าทศนิยมที่แม่นกว่า)
            double sp = font == null ? 1 : (M("x" + new string(' ', 20) + "x") - M("xx")) / 20.0;
            double dash = font == null ? 1 : M(new string('-', 20)) / 20.0;
            if (sp <= 0) sp = 1;
            if (dash <= 0) dash = 1;

            var w = new double[n];
            for (int c = 0; c < n; c++) w[c] = M(head[c]);
            foreach (var line in cells) for (int c = 0; c < n; c++) w[c] = Math.Max(w[c], M(line[c]));

            // ตำแหน่งเริ่มของข้อความในแต่ละคอลัมน์ (คงที่ทุกแถว ไม่สะสมความคลาดเคลื่อน)
            double bar = M("|"), plus = M("+");
            var x0 = new double[n];
            double pos = bar + sp;
            for (int c = 0; c < n; c++) { x0[c] = pos; pos += w[c] + sp + bar + sp; }

            var sb = new StringBuilder();
            double x = 0;
            void Put(string s) { sb.Append(s); x += M(s); }
            void FillTo(double target, char ch, double chW, int min = 0)
            {
                int k = Math.Max(min, (int)Math.Round((target - x) / chW));
                if (k <= 0) return;
                sb.Append(ch, k);
                x += k * chW;
            }
            void Border()
            {
                x = 0;
                Put("+");
                for (int c = 0; c < n; c++) { FillTo(x0[c] + w[c] + sp, '-', dash); Put("+"); }
                sb.Append("\r\n");
            }
            void Row(string[] v, bool header)
            {
                x = 0;
                Put("|");
                for (int c = 0; c < n; c++)
                {
                    bool r = !header && right != null && right(c);
                    FillTo(r ? x0[c] + w[c] - M(v[c]) : x0[c], ' ', sp, 1);
                    Put(v[c]);
                    FillTo(x0[c] + w[c] + sp, ' ', sp, 1);
                    Put("|");
                }
                sb.Append("\r\n");
            }
            Border();
            Row(head, true);
            Border();
            foreach (var v in cells) Row(v, false);
            if (cells.Count > 0) Border();
            sb.Append(total > cells.Count
                ? Tr2($"แสดง {cells.Count:N0} จาก {total:N0} แถว", $"{cells.Count:N0} of {total:N0} rows shown")
                : Tr2($"{total:N0} แถว", $"{total:N0} rows")).Append("\r\n");
            return sb.ToString();
        }

        static string Tr2(string th, string en) => SurapolSQL.Core.Tr.T(th, en);

        /// <summary>DataTable → ข้อความ (ใช้ Caption เป็นหัวคอลัมน์ถ้ามี, ตามลำดับการเรียงของ DefaultView)</summary>
        public static string Format(DataTable t, Func<int, bool>? right, Func<object?, string> text, Font? font = null)
        {
            var heads = new List<string>();
            foreach (DataColumn c in t.Columns) heads.Add(string.IsNullOrEmpty(c.Caption) ? c.ColumnName : c.Caption);
            return Format(heads, Rows(t), right, text, font);
        }

        static IEnumerable<object?[]> Rows(DataTable t)
        {
            foreach (DataRowView rv in t.DefaultView)
            {
                if (rv.Row.RowState == DataRowState.Deleted) continue;
                yield return rv.Row.ItemArray;
            }
        }

        /// <summary>ข้อความ 1 บรรทัด ไม่เกิน MaxCell (ขึ้นบรรทัดใหม่ → ↵, tab → เว้นวรรค)</summary>
        static string Clean(string s)
        {
            if (s.Length > MaxCell * 3) s = s.Substring(0, MaxCell * 3);
            if (s.IndexOfAny(new[] { '\r', '\n', '\t', '\0' }) >= 0)
                s = s.Replace("\r\n", "↵").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', ' ').Replace('\0', ' ');
            if (Width(s) <= MaxCell) return s;
            var sb = new StringBuilder();
            int w = 0;
            foreach (char ch in s)
            {
                int cw = CharWidth(ch);
                if (w + cw > MaxCell - 1) break;
                sb.Append(ch);
                w += cw;
            }
            return sb.Append('…').ToString();
        }

        /// <summary>จำนวนตัวอักษรที่กินที่บนจอ: ตัวที่ซ้อนบน / ล่าง (สระ วรรณยุกต์) = 0</summary>
        public static int Width(string s)
        {
            int w = 0;
            foreach (char ch in s) w += CharWidth(ch);
            return w;
        }

        static int CharWidth(char ch)
        {
            if (ch < 0x0300) return 1;
            var cat = char.GetUnicodeCategory(ch);
            return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0 : 1;
        }
    }
}
