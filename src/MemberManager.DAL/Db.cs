using System;
using System.Data.SQLite;
using System.IO;
using MemberManager.Common;

namespace MemberManager.DAL
{
    /// <summary>
    /// 数据访问入口。根目录由 AppPaths 判定：便携模式跟 exe 走，否则当前用户的 %LocalAppData%\MemberManager\。
    /// 连接统一开 WAL + Synchronous=FULL + BusyTimeout + 外键。
    /// </summary>
    public static class Db
    {
        private static string _dbPath;

        /// <summary>数据根目录（便携模式下即 exe 同目录）。</summary>
        public static string RootDir { get { return AppPaths.RootDir; } }

        /// <summary>是否便携模式（数据跟着程序文件夹走，可整体拷走）。</summary>
        public static bool Portable { get { return AppPaths.Portable; } }

        public static string DataDir { get { return AppPaths.DataDir; } }
        public static string BackupDir { get { return AppPaths.BackupDir; } }

        public static string DbPath
        {
            get
            {
                if (_dbPath == null) _dbPath = AppPaths.DbPath;
                return _dbPath;
            }
        }

        /// <summary>确保三个目录存在（数据/日志/备份）。</summary>
        public static void EnsureDirectories()
        {
            AppPaths.EnsureDirectories();
        }

        /// <summary>
        /// 打开一个新连接并套用 PRAGMA（WAL/FULL/BusyTimeout/外键）。
        /// 调用方负责 using 释放。任何失败上抛中文业务异常。
        /// </summary>
        public static SQLiteConnection OpenConnection()
        {
            try
            {
                var csb = new SQLiteConnectionStringBuilder();
                csb.DataSource = DbPath;
                csb.JournalMode = SQLiteJournalModeEnum.Wal;
                csb.SyncMode = SynchronizationModes.Full;
                csb.BusyTimeout = 5000;
                csb.ForeignKeys = true;
                var conn = new SQLiteConnection(csb.ConnectionString);
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                    cmd.ExecuteNonQuery();
                }
                return conn;
            }
            catch (Exception ex)
            {
                Logger.Error("打开数据库连接失败", ex);
                throw new BizException("数据库连接失败，请稍后重试或重启程序", ex);
            }
        }

        /// <summary>探测当前日志模式（状态栏「数据库正常（WAL）」用）。</summary>
        public static string QueryJournalMode()
        {
            using (var conn = OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                object v = cmd.ExecuteScalar();
                return v == null ? "" : v.ToString();
            }
        }
    }
}
