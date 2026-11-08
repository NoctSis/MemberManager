using System;
using System.IO;
using System.Text;

namespace MemberManager.Common
{
    /// <summary>
    /// 运行日志：&lt;数据根目录&gt;\logs\app-yyyyMMdd.log，UTF-8，按天滚动，只增。
    /// 目录由 AppPaths 判定（便携模式跟 exe 走）。
    /// 多线程安全（进程内加锁）；写失败静默降级（不让日志拖垮主流程）。
    /// </summary>
    public static class Logger
    {
        private static readonly object Sync = new object();

        public static string LogDir
        {
            get { return AppPaths.LogDir; }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex == null ? "" : " | " + ex.GetType().Name + ": " + ex.Message));
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDir);
                    string file = Path.Combine(LogDir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志写入失败不影响主流程
            }
        }
    }
}
