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

            // 店长点「重启软件」后，新进程带 --restarted 起来：等旧进程真正放开单实例锁再继续
            // （下面的 TryAcquire 会带 10 秒等待），否则两边同时打开同一个库会抢写锁。
            bool restarted = args.Length >= 1 && args[0] == "--restarted";
            bool probeMode = args.Length >= 2 && args[0] == "--probe";

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

            // ---- 单实例守卫（店主 11-08：双开会让同一笔单被结两次，账就重了）----
            // 探针模式必须豁免：冒烟的用法本来就是「软件开着也拍一张图」，拦了就跑不动。
            // 开机自启开关在上面已经提前 return，不会走到这里。
            if (!probeMode)
            {
                if (!SingleInstance.TryAcquire(restarted ? 10000 : 0))
                {
                    bool activated = SingleInstance.ActivateExisting();
                    Logger.Info("已有实例在运行，本次启动退出（" + (activated ? "已切换到已有窗口" : "未找到可激活的窗口") + "）");
                    if (!activated)
                    {
                        MessageBox.Show(
                            "会员管家已经在运行了，请不要重复打开。\n\n" +
                            "如果找不到它的窗口，请先在任务栏或屏幕右下角找一找；" +
                            "确认没有窗口，再打开「任务管理器」结束 MemberManager 进程后重新启动。",
                            "已经开着了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }
                Logger.Info("单实例锁已取得（数据目录：" + AppPaths.RootDir + "）");
                // 给安装包（setup.iss 的 AppMutex）留一个固定名的记号，
                // 升级时它才能提示「请先关闭会员管家」，而不是复制文件到一半报占用。
                SingleInstance.MarkRunning();
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

            // ---- 每日自动备份（店主 11-08）----
            // 此前只有「数据备份」页面挂载时才会调 AutoTick，店主一天不进那一页就等于没有自动备份，
            // 而备份是误删/坏库唯一能救命的东西。这里放到主程序后台：启动 30 秒后查一次
            // （补上昨天错过的），之后每小时一次，软件开着到点就会备份。
            // 页面那处调用保留：两条路径都靠 backup_last_date 去重，同一天只会真正备份一次。
            // 失败只记日志、不弹框——自动备份绝不能拦着店主收银。
            StartAutoBackupTimer();

            Application.Run(new ShellForm());
            Logger.Info("程序退出");
        }

        /// <summary>后台定时器的引用必须自己持有，否则可能被 GC 回收导致定时不再触发。</summary>
        private static System.Threading.Timer _autoBackupTimer;

        /// <summary>启动每日自动备份的后台轮询（每小时一次；首次延迟 30 秒避开启动高峰）。</summary>
        private static void StartAutoBackupTimer()
        {
            try
            {
                _autoBackupTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        string done = new BackupService().AutoTick("系统自动");
                        if (!string.IsNullOrEmpty(done)) Logger.Info("自动备份完成：" + done);
                    }
                    catch (Exception ex)
                    {
                        // 体检失败、磁盘满、加密取不到密码等都在这里兜住：不推进日期，1 小时后重试
                        Logger.Warn("自动备份失败（不弹框、不影响收银）：" + ex.Message);
                    }
                }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                Logger.Warn("自动备份定时器启动失败（会由备份页面兜底触发）：" + ex.Message);
            }
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
