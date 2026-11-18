using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MySqlConnector;
using Npgsql;

namespace SurapolSQL.Core
{

    /// <summary>ข้อมูลการเชื่อมต่อฐานข้อมูลปลายทาง</summary>
    public sealed class DbConnInfo
    {
        public bool Pg;
        public string Host = "localhost";
        public int Port = 3306;
        public string User = "root";
        public string Password = "";
        public string Database = "";
        /// <summary>สร้างฐานข้อมูลให้ถ้ายังไม่มี</summary>
        public bool CreateDatabase = true;
        /// <summary>charset / collation ตอนสร้างฐานข้อมูลใหม่ (MySQL / MariaDB)  collation "" = ค่าเริ่มต้นของ server</summary>
        public string DbCharset = "utf8mb4", DbCollation = "";

        public string Display => $"{User}@{Host}:{Port}/{Database}";

        public DbConnInfo Clone() => (DbConnInfo)MemberwiseClone();
    }

    /// <summary>ผลการทดสอบการเชื่อมต่อ</summary>
    public sealed class DbTestResult
    {
        public string ServerVersion = "";
        public List<string> Databases = new List<string>();
        public bool DatabaseExists;
        /// <summary>จำนวนตารางที่มีอยู่แล้วในฐานข้อมูลปลายทาง (-1 = ไม่รู้)</summary>
        public long ExistingTables = -1;
        /// <summary>max_allowed_packet ของ server (MySQL/MariaDB เท่านั้น, 0 = ไม่รู้)</summary>
        public long MaxPacket;
    }


    public static class Db
    {
        static readonly HashSet<string> MySqlSystemDbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "information_schema", "performance_schema", "mysql", "sys" };

        public static string MySqlCs(DbConnInfo c, bool withDb) => new MySqlConnectionStringBuilder
        {
            Server = c.Host,
            Port = (uint)c.Port,
            UserID = c.User,
            Password = c.Password,
            Database = withDb ? c.Database : "",
            AllowUserVariables = true,      // header ใช้ @OLD_FOREIGN_KEY_CHECKS ฯลฯ
            AllowPublicKeyRetrieval = true, // MySQL 8 (caching_sha2_password) แบบไม่มี SSL
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 0,
            Pooling = false,
            CharacterSet = "utf8mb4",
        }.ConnectionString;

        public static string PgCs(DbConnInfo c, string database) => new NpgsqlConnectionStringBuilder
        {
            Host = c.Host,
            Port = c.Port,
            Username = c.User,
            Password = c.Password,
            Database = database,
            Timeout = 15,
            CommandTimeout = 0,
            Pooling = false,
            IncludeErrorDetail = true,
        }.ConnectionString;

        static DbConnection OpenMySql(DbConnInfo c, bool withDb)
        {
            var conn = new MySqlConnection(MySqlCs(c, withDb));
            try { conn.Open(); return conn; }
            catch { conn.Dispose(); throw; }
        }

        static DbConnection OpenPg(DbConnInfo c, string database)
        {
            var conn = new NpgsqlConnection(PgCs(c, database));
            try { conn.Open(); return conn; }
            catch { conn.Dispose(); throw; }
        }

        static object? Scalar(DbConnection conn, string sql, params (string, object)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = n;
                p.Value = v;
                cmd.Parameters.Add(p);
            }
            return cmd.ExecuteScalar();
        }


        static List<string> Strings(DbConnection conn, string sql)
        {
            var l = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) l.Add(r.GetString(0));
            return l;
        }

        /// <summary>ทดสอบการเชื่อมต่อ ดึงรายชื่อฐานข้อมูล และดูว่าฐานข้อมูลปลายทางมีอยู่แล้วหรือยัง</summary>
        public static DbTestResult Test(DbConnInfo c, string pgSchema = "public")
        {
            var res = new DbTestResult();
            if (!c.Pg)
            {
                using var conn = OpenMySql(c, false);
                res.ServerVersion = Convert.ToString(Scalar(conn, "SELECT VERSION()")) ?? "";
                res.ServerVersion = (res.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "MariaDB " : "MySQL ") + res.ServerVersion;
                res.Databases = Strings(conn, "SHOW DATABASES");
                res.Databases.RemoveAll(MySqlSystemDbs.Contains);
                res.MaxPacket = MaxPacket(conn);
                if (c.Database != "")
                {
                    res.DatabaseExists = res.Databases.Exists(d => string.Equals(d, c.Database, StringComparison.OrdinalIgnoreCase));
                    if (res.DatabaseExists)
                        res.ExistingTables = Convert.ToInt64(Scalar(conn,
                            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @db", ("@db", c.Database)));
                }
                return res;
            }
            // PostgreSQL: เชื่อมต่อฐาน postgres ก่อน (ถ้าเข้าไม่ได้ ใช้ฐานที่ระบุ)
            DbConnection pc;
            try { pc = OpenPg(c, "postgres"); }
            catch (PostgresException ex) when (ex.SqlState == "3D000" && c.Database != "") { pc = OpenPg(c, c.Database); }
            using (pc)
            {
                res.ServerVersion = "PostgreSQL " + ((NpgsqlConnection)pc).PostgreSqlVersion;
                res.Databases = Strings(pc, "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY datname");
            }
            if (c.Database != "")
            {
                res.DatabaseExists = res.Databases.Contains(c.Database);
                if (res.DatabaseExists)
                {
                    using var conn = OpenPg(c, c.Database);
                    res.ExistingTables = Convert.ToInt64(Scalar(conn,
                        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE'",
                        ("@s", string.IsNullOrEmpty(pgSchema) ? "public" : pgSchema)));
                }
            }
            return res;
        }


        /// <summary>ค่าสูงสุดที่ MySQL/MariaDB ยอมให้ตั้ง max_allowed_packet (1 GB)</summary>
        public const long MaxPacketLimit = 1L << 30;


        public static string Mb(long bytes) =>
            bytes >= MaxPacketLimit && bytes % MaxPacketLimit == 0 ? (bytes / MaxPacketLimit) + " GB" : (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";

        /// <summary>max_allowed_packet ของ MySQL/MariaDB (bytes)  0 = ไม่รู้</summary>
        public static long MaxPacket(DbConnection conn)
        {
            if (conn is not MySqlConnection) return 0;
            try { return Convert.ToInt64(Scalar(conn, "SELECT @@max_allowed_packet")); }
            catch { return 0; }
        }
    }

}
