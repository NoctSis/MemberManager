using System;
using System.IO;
using System.Windows.Forms;
using MemberManager.BLL;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.Shell
{
    /// <summary>
    /// 程序入口：DPI → 自检建库 → WebView2 壳窗体。
    /// 界面层 = web\ 前端（React 设计稿构建产物），经 JS 桥调用本地 BLL。
    /// </summary>
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new System.IntPtr(-4)); } catch { }

            // 店长点「重启软件」后，新进程带 --restarted 起来：先等旧进程真正退出，
            // 否则两个进程同时打开同一个数据库，WAL 下会互相抢写锁（表现为启动卡住或 database is locked）。
            if (args.Length >= 1 && args[0] == "--restarted")
            {
                System.Threading.Thread.Sleep(1500);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Logger.Info("========== 程序启动（Shell/WebUI） ==========");

            // ---- 安装包专用：命令行开关开机自启（改完即退出，不建库、不弹界面）----
            // 用法：MemberManager.exe --enable-autostart / --disable-autostart
            // 放在建库之前，使安装程序只改注册表、不产生任何数据副作用。
            if (args.Length >= 1 && (args[0] == "--enable-autostart" || args[0] == "--disable-autostart"))
            {
                bool on = args[0] == "--enable-autostart";
                bool changed = on ? AutoStart.Enable() : AutoStart.Disable();
                Logger.Info("命令行" + (on ? "启用" : "关闭") + "开机自启：" + (changed ? "成功" : "失败"));
                Environment.ExitCode = changed ? 0 : 1;
                return;
            }

            // ---- 启动自检：数据库初始化 ----
            try
            {
                bool seeded = DatabaseInitializer.EnsureDatabase();
                Logger.Info("数据库初始化完成：" + Db.DbPath + (seeded ? "（首次建库）" : "（已有数据）"));
            }
            catch (Exception ex)
            {
                Logger.Error("数据库初始化失败", ex);
                MessageBox.Show("数据库初始化失败：" + ex.Message, "启动失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ---- 无头探针模式：--probe <png路径> [宽 高] [页签key] [点击按钮文本] [登录账号名] 渲染后截图退出 ----
            if (args.Length >= 2 && args[0] == "--probe")
            {
                IsProbeMode = true;
                int w = args.Length >= 4 ? int.Parse(args[2]) : 0;
                int h = args.Length >= 4 ? int.Parse(args[3]) : 0;
                if (args.Length >= 5) ProbePage = args[4];
                if (args.Length >= 6) ProbeClickText = args[5];
                if (args.Length >= 7) ProbeUser = args[6];
                RunProbe(args[1], w, h);
                return;
            }

            // 便携版换目录/改盘符后，Run 键里的老路径会指向不存在的文件 ——
            // 仅在「已启用」时改写成当前 exe 位置；未启用则什么都不做（探针模式在上面已提前返回）。
            AutoStart.Reconcile();

            // ---- 导出目录收回安装目录（店主 11-05）----
            // 早年「选择文件夹」对话框默认停在桌面，店主直接点确定就存进了 export_dir；
            // 这里把指向桌面/文档/下载的那类值改写成 安装目录\exports，别让报表散落在用户目录里。
            // 店主自己选的其它盘符目录不动（那是明确意图）。失败只记日志。
            try
            {
                string moved = SystemInfoService.EnsureExportDirInApp();
                if (moved != null) Logger.Info("导出目录已收回到安装目录（原：" + moved + "）");
            }
            catch (Exception ex)
            {
                Logger.Warn("整理导出目录失败（不影响使用）：" + ex.Message);
            }

            // ---- 操作日志保留期清理（店主 11-05）----
            // 按 settings.log_keep_days（默认 180，最小 30）删掉最老的操作日志，每天最多跑一次。
            // 刻意放在探针/截图分支之后：截图与冒烟不该顺手改库。
            // 只删 op_logs，业务流水一条不动；失败只记 app 日志，绝不拦着开软件收银。
            try
            {
                int purged = LogService.AutoTick();
                if (purged > 0) Logger.Info("启动时清理操作日志：" + purged + " 条");
            }
            catch (Exception ex)
            {
                Logger.Warn("启动时清理操作日志失败（不影响使用）：" + ex.Message);
            }

            Application.Run(new ShellForm());
            Logger.Info("程序退出");
        }

        /// <summary>无头探针模式下静默处理初始化失败（不弹框）。</summary>
        public static bool IsProbeMode { get; private set; }

        /// <summary>无头探针模式指定初始页签（hash 深链，如 stored-value）。</summary>
        public static string ProbePage { get; private set; } = "";

        /// <summary>无头探针模式截图前点击含此文本的按钮（打开弹窗等）。</summary>
        public static string ProbeClickText { get; private set; } = "";

        /// <summary>无头探针模式指定自动登录的账号名（用于验证不同角色的权限视图）。</summary>
        public static string ProbeUser { get; private set; } = "";

        private static void RunProbe(string pngPath, int w, int h)
        {
            var form = new ShellForm();
            if (w > 100 && h > 100) form.Size = new System.Drawing.Size(w, h);
            form.Shown += async (s, e) =>
            {
                string result;
                try
                {
                    result = await form.WaitAndCaptureAsync(pngPath, 30);
                    Logger.Info("探针截图完成：" + pngPath + "  " + result);
                }
                catch (Exception ex)
                {
                    result = "EXC:" + ex.Message;
                    Logger.Error("探针异常", ex);
                }
                try { System.IO.File.WriteAllText(pngPath + ".res", result); } catch { }
                form.Close();
            };
            Application.Run(form);
        }

        /// <summary>dist\web 前端资源目录（exe 同级的 web 文件夹）。</summary>
        public static string WebRoot
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(baseDir, "web");
            }
        }
    }
}
