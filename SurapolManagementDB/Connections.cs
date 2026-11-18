using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using SurapolSQL.Core;

namespace SurapolSQL
{
    /// <summary>การเชื่อมต่อที่บันทึกไว้ (MySQL / MariaDB)</summary>
    public sealed class SavedConn
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string User { get; set; } = "root";
        /// <summary>รหัสผ่านที่เข้ารหัสด้วย Windows DPAPI (ถอดได้เฉพาะ user Windows คนนี้)  "" = ไม่ได้จำ</summary>
        public string PasswordEnc { get; set; } = "";
        public bool SavePassword { get; set; } = true;
        /// <summary>ฐานข้อมูลเริ่มต้น (ไม่บังคับ)</summary>
        public string Database { get; set; } = "";

        public string Display => Name != "" ? Name : $"{User}@{Host}:{Port}";

        public DbConnInfo ToInfo(string password, string? database = null) =>
            new DbConnInfo { Pg = false, Host = Host, Port = Port, User = User, Password = password, Database = database ?? Database, CreateDatabase = false };
    }

    /// <summary>อ่าน/บันทึกรายการการเชื่อมต่อ (%LOCALAPPDATA%\SurapolManagementDB\connections.json)</summary>
    public static class ConnStore
    {
        static string FilePath => Path.Combine(Ui.DataDir, "connections.json");

        public static List<SavedConn> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<SavedConn>>(File.ReadAllText(FilePath)) ?? new List<SavedConn>();
            }
            catch { }
            return new List<SavedConn>();
        }

        public static void Save(List<SavedConn> list)
        {
            Directory.CreateDirectory(Ui.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ---- Windows DPAPI (CryptProtectData) ไม่ต้องใช้ package เพิ่ม
        [StructLayout(LayoutKind.Sequential)]
        struct Blob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob input, string? descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptUnprotectData(ref Blob input, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr h);

        static byte[]? Dpapi(byte[] data, bool protect)
        {
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
                var output = new Blob();
                bool ok = protect ? CryptProtectData(ref input, Ui.AppName, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                                  : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
                if (!ok) return null;
                var res = new byte[output.Size];
                Marshal.Copy(output.Data, res, 0, output.Size);
                LocalFree(output.Data);
                return res;
            }
            finally { h.Free(); }
        }

        public static string Protect(string s) =>
            s == "" ? "" : Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(s), true) ?? Array.Empty<byte>());

        public static string Unprotect(string s)
        {
            if (s == "") return "";
            try
            {
                var b = Dpapi(Convert.FromBase64String(s), false);
                return b == null ? "" : Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }

    /// <summary>หน้าต่างสร้าง / แก้ไขการเชื่อมต่อ (แบบ New Connection ของ DBeaver)</summary>
    public sealed class ConnDialog : Form
    {
        readonly TextBox _txtName, _txtHost, _txtUser, _txtPass;
        readonly NumericUpDown _numPort;
        readonly ComboBox _cmbDb;
        readonly CheckBox _chkSave, _chkShow;
        readonly Label _lblTest;
        readonly Button _btnTest;
        public SavedConn Result { get; private set; }
        public string Password => _txtPass.Text;

        public ConnDialog(SavedConn? edit, string password)
        {
            Result = edit ?? new SavedConn();
            Text = edit == null ? Tr.T("เชื่อมต่อใหม่ - MySQL / MariaDB", "New connection - MySQL / MariaDB") : Tr.T("แก้ไขการเชื่อมต่อ", "Edit connection");
            Font = Ui.Font;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (Ui.AppIcon != null) Icon = Ui.AppIcon;

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Padding = new Padding(14, 12, 14, 10) };
            Label L(string s) => new Label { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
            int W(int px) => Ui.Px(this, px);

            _txtName = new TextBox { Width = W(420), Margin = new Padding(0, 3, 0, 3) };
            t.Controls.Add(L(Tr.T("ชื่อการเชื่อมต่อ:", "Connection name:")), 0, 0);
            t.Controls.Add(_txtName, 1, 0);
            t.SetColumnSpan(_txtName, 3);

            _txtHost = new TextBox { Width = W(220), Margin = new Padding(0, 3, 16, 3) };
            _numPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = W(90), Margin = new Padding(0, 3, 0, 3) };
            t.Controls.Add(L("Server Host:"), 0, 1);
            t.Controls.Add(_txtHost, 1, 1);
            t.Controls.Add(L("Port:"), 2, 1);
            t.Controls.Add(_numPort, 3, 1);

            _txtUser = new TextBox { Width = W(220), Margin = new Padding(0, 3, 16, 3) };
            _txtPass = new TextBox { Width = W(150), UseSystemPasswordChar = true, Margin = new Padding(0, 3, 6, 3) };
            _chkShow = new CheckBox { Text = Tr.T("แสดง", "Show"), AutoSize = true, Margin = new Padding(0, 5, 0, 3) };
            _chkShow.CheckedChanged += (s, e) => _txtPass.UseSystemPasswordChar = !_chkShow.Checked;
            var fp = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            fp.Controls.AddRange(new Control[] { _txtPass, _chkShow });
            t.Controls.Add(L("Username:"), 0, 2);
            t.Controls.Add(_txtUser, 1, 2);
            t.Controls.Add(L("Password:"), 2, 2);
            t.Controls.Add(fp, 3, 2);

            _cmbDb = new ComboBox { Width = W(220), DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(0, 3, 16, 3) };
            _cmbDb.DropDown += async (s, e) => { if (_cmbDb.Items.Count == 0) await TestAsync(); };
            _chkSave = new CheckBox { Text = Tr.T("จำรหัสผ่าน", "Save password"), AutoSize = true, Margin = new Padding(0, 5, 0, 3) };
            new ToolTip().SetToolTip(_chkSave, Tr.T("เข้ารหัสด้วย Windows (DPAPI) เปิดได้เฉพาะ user Windows นี้", "Encrypted with Windows DPAPI — only this Windows user can read it"));
            t.Controls.Add(L(Tr.T("Database (ไม่บังคับ):", "Database (optional):")), 0, 3);
            t.Controls.Add(_cmbDb, 1, 3);
            t.Controls.Add(_chkSave, 2, 3);
            t.SetColumnSpan(_chkSave, 2);

            _btnTest = Ui.MakeButton(Tr.T("ทดสอบการเชื่อมต่อ", "Test connection"), () => _ = TestAsync());
            _btnTest.Margin = new Padding(0, 8, 8, 4);
            t.Controls.Add(_btnTest, 0, 4);
            t.SetColumnSpan(_btnTest, 2);
            _lblTest = new Label { AutoSize = true, MaximumSize = new Size(W(560), 0), Margin = new Padding(0, 2, 0, 6) };
            t.Controls.Add(_lblTest, 0, 5);
            t.SetColumnSpan(_lblTest, 4);

            var fb = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
            var btnCancel = Ui.MakeButton(Tr.T("ยกเลิก", "Cancel"), () => { DialogResult = DialogResult.Cancel; Close(); });
            var btnOk = Ui.MakeButton(Tr.T("บันทึก", "Save"), OnOk, true);
            fb.Controls.AddRange(new Control[] { btnCancel, btnOk });
            t.Controls.Add(fb, 0, 6);
            t.SetColumnSpan(fb, 4);
            Controls.Add(t);
            AcceptButton = btnOk;
            btnOk.Tag = "primary";
            Theme.Apply(this);
            Shown += (s, e) => Theme.TitleBar(this);
            CancelButton = btnCancel;

            _txtName.Text = Result.Name;
            _txtHost.Text = Result.Host;
            _numPort.Value = Math.Max(1, Math.Min(65535, Result.Port));
            _txtUser.Text = Result.User;
            _txtPass.Text = password;
            _cmbDb.Text = Result.Database;
            _chkSave.Checked = edit == null || Result.SavePassword;
        }

        SavedConn Current() => new SavedConn
        {
            Name = _txtName.Text.Trim(),
            Host = _txtHost.Text.Trim(),
            Port = (int)_numPort.Value,
            User = _txtUser.Text.Trim(),
            Database = _cmbDb.Text.Trim(),
            SavePassword = _chkSave.Checked,
        };

        async Task TestAsync()
        {
            var sc = Current();
            if (sc.Host == "" || sc.User == "") { SetTest("✖ " + Tr.T("กรอก Server Host และ Username ก่อน", "Enter Server Host and Username first"), Theme.Err); return; }
            var c = sc.ToInfo(_txtPass.Text, "");
            _btnTest.Enabled = false;
            SetTest(Tr.T("กำลังเชื่อมต่อ ", "Connecting to ") + sc.Host + ":" + sc.Port + " …", Theme.Muted);
            try
            {
                var r = await Task.Run(() => Db.Test(c));
                string cur = _cmbDb.Text;
                _cmbDb.Items.Clear();
                foreach (var d in r.Databases) _cmbDb.Items.Add(d);
                _cmbDb.Text = cur;
                SetTest("✔ " + Tr.T("เชื่อมต่อสำเร็จ — ", "Connected — ") + r.ServerVersion + Tr.T($"  ({r.Databases.Count:N0} ฐานข้อมูล)", $"  ({r.Databases.Count:N0} databases)"), Theme.Ok);
            }
            catch (Exception ex) { SetTest("✖ " + Tr.T("เชื่อมต่อไม่ได้: ", "Cannot connect: ") + (ex.InnerException ?? ex).Message, Theme.Err); }
            finally { _btnTest.Enabled = true; }
        }

        void SetTest(string s, Color c)
        {
            _lblTest.Text = s;
            _lblTest.ForeColor = c;
        }

        void OnOk()
        {
            var sc = Current();
            if (sc.Host == "" || sc.User == "")
            {
                Ui.Info(this, Tr.T("กรอก Server Host และ Username ก่อน", "Enter Server Host and Username first"));
                return;
            }
            if (sc.Name == "") sc.Name = $"{sc.User}@{sc.Host}:{sc.Port}";
            sc.PasswordEnc = sc.SavePassword ? ConnStore.Protect(_txtPass.Text) : "";
            Result = sc;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
