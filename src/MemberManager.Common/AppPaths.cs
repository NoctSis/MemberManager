using System;
using System.IO;
using System.Reflection;

namespace MemberManager.Common
{
    /// <summary>
    /// 应用数据根目录判定（便携优先）。
    ///
    /// 判定顺序（先命中先用）：
    ///   1) 环境变量 MEMBERMANAGER_ROOT 存在且是可写目录 → 直接用它（权威指定，跳过便携探测）。
    ///      用途：PowerShell 冒烟脚本的 AppDomain.BaseDirectory 是 System32，便携探测会退回到
    ///      用户目录，导致 exe 与冒烟脚本各写各的库；脚本显式指定即可指向同一份库。
    ///   2) 便携模式（绿色版）：exe 同目录下存在 portable.txt，或已存在 data\ 目录时，
    ///      数据/日志/备份全部放在 exe 同目录 —— 整个文件夹拷到 U 盘或另一台电脑即可带走。
    ///   3) 否则退回 %LocalAppData%\MemberManager\（当前用户自己的目录，仅当 exe 装在
    ///      Program Files 这类只读位置时才会用到）。
    ///
    /// ★ 刻意不使用 %ProgramData%\MemberManager\（全机共享的系统公共目录）：
    ///   早先非便携分支走那里，并且「首次便携启动」还会把那里的旧库自动搬进 exe 目录。
    ///   后果是单机数据落到不可见处，且可能把测试脚本误写进去的库悄悄混进真实数据
    ///   （实测那份库 shop_name=666、仅 7 个会员，全是冒烟垃圾）。
    ///   现已整体移除：既不往那里写，也不从那里搬数据。
    /// </summary>
    public static class AppPaths
    {
        private static readonly object Sync = new object();
        private static string _rootDir;
        private static bool _portable;
        private static bool _resolved;

        /// <summary>是否便携模式（数据跟 exe 走）。</summary>
        public static bool Portable
        {
            get { Resolve(); return _portable; }
        }

        /// <summary>数据根目录。</summary>
        public static string RootDir
        {
            get { Resolve(); return _rootDir; }
        }

        public static string DataDir { get { return Path.Combine(RootDir, "data"); } }
        public static string LogDir { get { return Path.Combine(RootDir, "logs"); } }
        public static string BackupDir { get { return Path.Combine(RootDir, "backups"); } }
        public static string DbPath { get { return Path.Combine(DataDir, "member.db"); } }

        /// <summary>
        /// 默认导出目录（店主 11-05：所有文件尽量都在安装的目录里面）。
        /// 便携模式 = exe 旁的 exports\，整包拷走时报表/导出跟着走；
        /// 非便携 = %LocalAppData%\MemberManager\exports\。
        /// 放 data\ 外面是刻意的：这是给店主自己找、自己发微信的文件，不该埋进数据目录。
        /// </summary>
        public static string ExportsDir { get { return Path.Combine(RootDir, "exports"); } }

        /// <summary>exe 所在目录（末尾无分隔符）。</summary>
        public static string ExeDir
        {
            get
            {
                string dir = null;
                try { dir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
                }
                if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
                return dir.TrimEnd('\\', '/');
            }
        }

        /// <summary>
        /// 尝试用环境变量 MEMBERMANAGER_ROOT 显式指定数据根目录。
        /// 命中（值非空、目录存在且可写）时返回 true 并输出该目录；否则返回 false 走正常探测。
        /// </summary>
        private static bool TryExplicitRoot(out string root)
        {
            root = null;
            try
            {
                string env = Environment.GetEnvironmentVariable("MEMBERMANAGER_ROOT");
                if (string.IsNullOrWhiteSpace(env)) return false;
                string dir = env.Trim().TrimEnd('\\', '/');
                if (!Directory.Exists(dir)) return false;
                if (!IsWritable(dir)) return false;
                root = dir;
                return true;
            }
            catch { return false; }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            lock (Sync)
            {
                if (_resolved) return;

                // 1) 显式指定优先（冒烟脚本用）：命中即定，跳过便携探测与迁移
                if (TryExplicitRoot(out string explicitRoot))
                {
                    _portable = true;
                    _rootDir = explicitRoot;
                    _resolved = true;
                    Logger.Info("数据根目录由环境变量 MEMBERMANAGER_ROOT 显式指定：" + explicitRoot);
                    return;
                }

                string exeDir = ExeDir;

                // 非便携分支的落点：当前用户自己的 %LocalAppData%。
                // ★ 不用 %ProgramData%（全机共享的系统公共目录）——见类注释。
                string localAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MemberManager");

                bool portable = false;
                try
                {
                    // 判定顺序：显式标记 portable.txt > 已有 data 目录
                    if (File.Exists(Path.Combine(exeDir, "portable.txt"))) portable = true;
                    else if (Directory.Exists(Path.Combine(exeDir, "data"))) portable = true;
                }
                catch { }

                if (portable)
                {
                    // exe 装在 Program Files 这类只读位置时不能便携，退回用户目录
                    if (!IsWritable(exeDir)) portable = false;
                }

                _portable = portable;
                _rootDir = portable ? exeDir : localAppData;
                _resolved = true;
            }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".mm_write_test.tmp");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>确保四个目录存在（数据/日志/备份/导出）。</summary>
        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(LogDir);
            try { Directory.CreateDirectory(ExportsDir); } catch { }
        }
    }
}
