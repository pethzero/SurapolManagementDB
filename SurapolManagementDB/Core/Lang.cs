namespace SurapolSQL.Core
{
    /// <summary>
    /// ภาษาของโปรแกรม (ไทย / English)
    /// ข้อความทุกจุดเขียนเป็นคู่  Tr.T("ไทย", "English")  แล้วเลือกตาม Tr.En
    /// หน้าจอ: ตั้งจากเมนู ภาษา / Language (บันทึกไว้ใน settings.txt)   command line: --lang en หรือ env SURAPOL_LANG=en
    /// </summary>
    public static class Tr
    {
        public static bool En;

        public static string T(string th, string en) => En ? en : th;
    }
}
