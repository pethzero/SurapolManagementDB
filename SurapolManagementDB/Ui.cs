using System;
using SurapolSQL.Core;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SurapolSQL
{
    /// <summary>ของใช้ร่วมกันของหน้าจอ: ฟอนต์ ไอคอน และการจัดรูปแบบค่าที่แสดง</summary>
    internal static class Ui
    {
        public const string AppName = "SurapolManagementDB Demo";
        public const string AppVersion = "1.0.0";

        private static string? _dataDir;

        /// <summary>
        /// โฟลเดอร์เก็บ log / ค่าที่จำไว้: %LOCALAPPDATA%\SurapolManagementDB
        /// ครั้งแรกที่ใช้ชื่อใหม่ จะย้ายของเดิมจาก %LOCALAPPDATA%\SurapolSQL มาให้
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = System.IO.Path.Combine(root, AppName);
                return _dataDir = dir;
            }
        }

        private static string SettingsFile => System.IO.Path.Combine(DataDir, "settings.txt");

        /// <summary>อ่านค่าที่ตั้งไว้ (settings.txt แบบ key=value)</summary>
        public static string GetSetting(string key, string def = "")
        {
            try
            {
                if (System.IO.File.Exists(SettingsFile))
                    foreach (var l in System.IO.File.ReadAllLines(SettingsFile))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0 && l.Substring(0, i).Trim() == key) return l.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return def;
        }

        /// <summary>ขนาด max_allowed_packet ที่เลือกได้ (MB)</summary>
        public static readonly int[] PacketMbs = { 64, 128, 256, 512, 1024 };

        /// <summary>ค่า max_allowed_packet ที่ใช้ล่าสุด (ค่าเริ่มต้น 128 MB) → ลำดับใน PacketMbs</summary>
        public static int PacketIndex
        {
            get
            {
                int i = Array.IndexOf(PacketMbs, int.TryParse(GetSetting("packet_mb", "128"), out int mb) ? mb : 128);
                return i >= 0 ? i : 1;
            }
            set { if (value >= 0 && value < PacketMbs.Length) SetSetting("packet_mb", PacketMbs[value].ToString()); }
        }

        /// <summary>ติ๊ก "ตั้ง max_allowed_packet" ไว้หรือไม่ (ค่าเริ่มต้น: ติ๊ก)</summary>
        public static bool PacketOn
        {
            get => GetSetting("packet_on", "1") != "0";
            set => SetSetting("packet_on", value ? "1" : "0");
        }

        public static void SetSetting(string key, string value)
        {
            try
            {
                var lines = System.IO.File.Exists(SettingsFile) ? System.IO.File.ReadAllLines(SettingsFile).ToList() : new List<string>();
                lines.RemoveAll(l => l.Split('=')[0].Trim() == key);
                lines.Add(key + "=" + value);
                System.IO.Directory.CreateDirectory(DataDir);
                System.IO.File.WriteAllLines(SettingsFile, lines);
            }
            catch { }
        }

        private static readonly HashSet<string> Installed =
            new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        public static readonly Font Font = MakeFont(new[] { "Leelawadee UI", "Segoe UI", "Tahoma" }, 9.75f, FontStyle.Regular);
        public static readonly Font Bold = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        public static readonly Font Big = new Font(Font.FontFamily, 15f, FontStyle.Regular);
        public static readonly Font Mono = MakeFont(new[] { "Consolas", "Courier New" }, 10f, FontStyle.Regular);

        private static Font MakeFont(string[] families, float size, FontStyle style)
        {
            foreach (var f in families)
                if (Installed.Contains(f)) return new Font(f, size, style);
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        private static Icon? _icon;
        public static Icon? AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                }
                return _icon;
            }
        }

        /// <summary>วาดไอคอนปุ่มจากฟอนต์ไอคอนของ Windows (ถ้าไม่มีฟอนต์ คืน null = ปุ่มมีแต่ข้อความ)</summary>
        public static Bitmap? Glyph(string glyph, int size, Color color)
        {
            string? fam = Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                        : Installed.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null;
            if (fam == null) return null;
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var f = new Font(fam, size * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var br = new SolidBrush(color);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, f, br, new RectangleF(0, 1, size, size), sf);
            return bmp;
        }

        public static int Px(Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);

        public static string HumanSize(long n)
        {
            if (n < 1024) return n + " B";
            double v = n / 1024.0;
            foreach (var u in new[] { "KB", "MB", "GB" })
            {
                if (v < 1024 || u == "GB") return v.ToString("0.0") + " " + u;
                v /= 1024;
            }
            return v.ToString("0.0") + " GB";
        }

        private static readonly char[] Ctl = { '\r', '\n', '\t', '\0' };

        /// <summary>ข้อความสั้น ๆ สำหรับแสดงในช่องตาราง</summary>
        public static string DisplayValue(object? v, int limit = 300)
        {
            if (v == null) return "NULL";
            if (v is byte[] b) return "<BLOB" + BlobKind(b).Label + " " + HumanSize(b.Length) + ">";
            string s = (string)v;
            bool cut = s.Length > limit;
            if (cut) s = s.Substring(0, limit);
            if (s.IndexOfAny(Ctl) >= 0)
                s = s.Replace("\r", " ").Replace("\n", " \\n ").Replace("\t", " ").Replace("\0", " ");
            return cut ? s + "…" : s;
        }

        public static bool IsImageExt(string ext) => ext == ".jpg" || ext == ".png" || ext == ".bmp" || ext == ".gif" || ext == ".tif";

        /// <summary>เดาชนิดไฟล์จาก byte แรก ๆ</summary>
        public static (string Label, string Ext) BlobKind(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return (" JPEG", ".jpg");
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return (" PNG", ".png");
            if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4) return (" ZIP", ".zip");
            if (b.Length >= 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F') return (" PDF", ".pdf");
            if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return (" GIF", ".gif");
            if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42))) return (" TIFF", ".tif");
            if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return (" WEBP", ".webp");
            if (b.Length >= 2 && b[0] == 0x1F && b[1] == 0x8B) return (" GZIP", ".gz");
            if (b.Length >= 6 && b[0] == 'B' && b[1] == 'M' && BitConverter.ToUInt32(b, 2) == (uint)b.Length) return (" BMP", ".bmp");
            return ("", ".bin");
        }

        /// <summary>แสดง byte แบบ hex dump (ใช้กับ BLOB ที่ไม่ใช่รูป)</summary>
        public static string HexDump(byte[] b, int max)
        {
            int n = Math.Min(b.Length, max);
            var sb = new StringBuilder(n * 4 + 64);
            for (int off = 0; off < n; off += 16)
            {
                sb.Append(off.ToString("X8")).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (off + i < n) sb.Append(b[off + i].ToString("X2")).Append(' ');
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(' ');
                for (int i = 0; i < 16 && off + i < n; i++)
                {
                    byte c = b[off + i];
                    sb.Append(c >= 32 && c < 127 ? (char)c : '.');
                }
                sb.Append("\r\n");
            }
            if (b.Length > n) sb.Append(Tr.T("… (แสดง ", "… (showing ")).Append(n.ToString("N0")).Append(Tr.T(" จาก ", " of ")).Append(b.Length.ToString("N0")).Append(" bytes)\r\n");
            return sb.ToString();
        }

        public static Button MakeButton(string text, Action onClick, bool primary = false)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(primary ? 12 : 6, 3, primary ? 12 : 6, 3), Margin = new Padding(0, 0, 6, 0), Tag = primary ? "primary" : null };
            b.Click += (s, e) => onClick();
            return b;
        }

        public static void Error(IWin32Window owner, string message) =>
            MessageBox.Show(owner, message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);

        public static void Info(IWin32Window owner, string message) =>
            MessageBox.Show(owner, message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
