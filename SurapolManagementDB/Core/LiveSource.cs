using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MySqlConnector;

namespace SurapolSQL.Core
{

    /// <summary>แปลงค่าจาก MySQL / MariaDB เป็นข้อความ / token</summary>
    public static class LiveValue
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// connection string สำหรับอ่านข้อมูลให้ได้ค่าตรงตามที่เก็บ:
        /// tinyint(1) เป็นตัวเลข (ไม่ใช่ bool), วันที่ 0000-00-00 อ่านได้, char(36) เป็นข้อความ (ไม่ใช่ Guid)
        /// </summary>
        public static string Cs(DbConnInfo c, string? database, bool pooling = true) =>
            new MySqlConnectionStringBuilder(Db.MySqlCs(c, false))
            {
                Database = database ?? "",
                TreatTinyAsBoolean = false,
                AllowZeroDateTime = true,
                GuidFormat = MySqlGuidFormat.None,
                Pooling = pooling,
            }.ConnectionString;

        public static MySqlConnection Open(DbConnInfo c, string? database)
        {
            var conn = new MySqlConnection(Cs(c, database));
            conn.Open();
            return conn;
        }

        public static string Q(string name) => "`" + name.Replace("`", "``") + "`";

        /// <summary>ค่าของคอลัมน์ที่ i: null, string หรือ byte[]</summary>
        public static object? Read(MySqlDataReader r, int i)
        {
            if (r.IsDBNull(i)) return null;
            string type = r.GetDataTypeName(i).ToLowerInvariant();
            if (type.Contains("decimal"))
            {
                try { return r.GetMySqlDecimal(i).ToString(); } catch { return Convert.ToString(r.GetValue(i), Inv); }
            }
            object v = r.GetValue(i);
            return v switch
            {
                byte[] b => b,
                string s => s,
                MySqlDateTime d => DateText(d, type),
                DateTime d => DateText(new MySqlDateTime(d), type),
                TimeSpan t => TimeText(t),
                bool bo => bo ? "1" : "0",
                ulong u when type == "bit" => u.ToString(Inv),
                float f => f.ToString("R", Inv),
                double d => d.ToString("R", Inv),
                IFormattable x => x.ToString(null, Inv),
                _ => v.ToString() ?? "",
            };
        }

        public static string DateText(MySqlDateTime d, string type)
        {
            string s = $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
            if (type == "date") return s;
            s += $" {d.Hour:D2}:{d.Minute:D2}:{d.Second:D2}";
            if (d.Microsecond > 0) s += "." + d.Microsecond.ToString("D6", Inv).TrimEnd('0');
            return s;
        }

        public static string TimeText(TimeSpan t)
        {
            string sign = t < TimeSpan.Zero ? "-" : "";
            if (t < TimeSpan.Zero) t = t.Negate();
            string s = $"{sign}{(long)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
            long micro = (t.Ticks % TimeSpan.TicksPerSecond) / 10;
            if (micro > 0) s += "." + micro.ToString("D6", Inv).TrimEnd('0');
            return s;
        }

        /// <summary>ข้อความ → literal ของ MySQL ('...' พร้อม escape)</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('\'');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\0': sb.Append("\\0"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\x1a': sb.Append("\\Z"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('\'');
            return sb.ToString();
        }

        /// <summary>ค่าของคอลัมน์ → token แบบ Navicat</summary>
        public static string Token(MySqlDataReader r, int i)
        {
            if (r.IsDBNull(i)) return "NULL";
            string type = r.GetDataTypeName(i).ToLowerInvariant();
            object? v = Read(r, i);
            switch (v)
            {
                case null: return "NULL";
                case byte[] b: return b.Length == 0 ? "X''" : "0x" + Convert.ToHexString(b);
                case string s:
                    if (type == "bit") return "b'" + Convert.ToString((long)ulong.Parse(s, Inv), 2) + "'";
                    var cls = r.GetFieldType(i);
                    bool num = cls != typeof(string) && cls != typeof(DateTime) && cls != typeof(TimeSpan) && cls != typeof(MySqlDateTime) && cls != typeof(byte[]);
                    return num || type.Contains("decimal") ? s : Quote(s);
                default: return Quote(v.ToString() ?? "");
            }
        }
    }

}
