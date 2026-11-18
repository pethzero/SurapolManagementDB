using System;
using SurapolSQL.Core;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SurapolSQL
{
    internal static class Program
    {
        // ใช้งาน:  SurapolManagementDB-Demo.exe   (--lang en|th เลือกภาษา)
        [STAThread]
        private static void Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // ภาษา: ตามที่เลือกไว้ในเมนู ภาษา / Language  (--lang en|th ใช้ตอนทดสอบ ไม่บันทึก)
            Tr.En = Ui.GetSetting("lang") == "en";
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), Ui.AppName + Tr.T(" - เกิดข้อผิดพลาด", " - Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);

            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--lang") Tr.En = args[i + 1] == "en";
            // ตัว demo: หน้าต่างฐานข้อมูล (เชื่อมต่อ · SQL Editor · ดูข้อมูลตาราง) เป็นหน้าต่างหลัก
            Application.Run(new DbForm(true));
        }
    }
}
