using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MemberManager.Common;

namespace MemberManager.Shell
{
    /// <summary>
    /// 单实例守卫（店主 11-08：「双开」是真的会遇到）。
    ///
    /// 为什么必须拦：两个人各开一个窗口，各自读一份库存/挂单列表，同一笔单可能被结两次；
    /// 虽然 SQLite WAL 不会把库写坏，但**账会重**，对账时极难查。
    ///
    /// 规则：
    ///   1) 锁按「数据目录」派生，不是按 exe。同一个数据目录只允许一个进程；
    ///      不同目录的两份便携版（比如老版留作对照）互不影响。
    ///   2) 重复启动时把已有窗口还原并置前，然后新进程直接退出 —— 不去弹一堆错误框。
    ///   3) 建不出互斥体（极端权限问题）时选择放行：宁可有可能双开，也不能让人开不了软件收银。
    ///   4) 探针模式（--probe）与安装包开关（--enable-autostart 等）不参与，否则冒烟与卸载脚本会被误拦。
    /// </summary>
    internal static class SingleInstance
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        private static Mutex _mutex;

        /// <summary>
        /// 「程序正在运行」的固定标记，专门给安装包看（setup.iss 的 AppMutex）。
        /// <para>
        /// 为什么要单独一个名字：单实例锁是按数据目录哈希派生的，安装程序无法预知哈希，
        /// 也就没法用 AppMutex 去等它。而升级安装必须先关掉程序——它握着 exe/dll 的句柄，
        /// 还有 data\member.db；不关就装会在复制文件时报「正由另一进程使用」，
        /// 店主看到的是任务管理器里一堆 msedgewebview2.exe，不知道该结束哪个。
        /// </para>
        /// <para>
        /// 同时建 Global\ 与 Local\：Global\ 覆盖「另一个 Windows 用户也开着」的情况，
        /// Local\ 是 Global\ 建不出来时的退路。安装包两个名字都登记，命中任一即提示。
        /// 这个互斥体不 WaitOne，只要句柄活着对象就存在，进程退出时内核自动回收。
        /// </para>
        /// </summary>
        private static Mutex _running;

        private const string RunningGlobal = "Global\\MemberManager.Running";
        private const string RunningLocal = "Local\\MemberManager.Running";

        /// <summary>
        /// 尝试取得独占权。waitMs = 0 表示立即返回；&gt;0 用于「重启软件」场景，等旧进程体面退出。
        /// 返回 false 表示已有实例在跑。
        /// </summary>
        public static bool TryAcquire(int waitMs)
        {
            string name = BuildName();
            try
            {
                _mutex = new Mutex(false, name);
            }
            catch (Exception ex)
            {
                // 拿不到锁就当没有锁：不能因为一个守卫让店主开不了软件
                Logger.Warn("单实例锁创建失败，本次不做重复启动拦截：" + ex.Message);
                return true;
            }

            int waited = 0;
            while (true)
            {
                try
                {
                    int slice = waitMs > waited ? Math.Min(250, waitMs - waited) : 0;
                    if (_mutex.WaitOne(slice)) return true;
                }
                catch (AbandonedMutexException)
                {
                    // 上一个进程崩溃时没释放，内核把所有权交给等待者 —— 这就是我们的了
                    Logger.Warn("上一个实例非正常退出，已接管单实例锁");
                    return true;
                }

                if (waited >= waitMs) return false;
                waited += 250;
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// 把已经在运行的那个窗口还原到前台。找不到窗口也返回 false，由调用方给文字提示。
        /// </summary>
        public static bool ActivateExisting()
        {
            try
            {
                Process[] all = Process.GetProcessesByName("MemberManager");
                foreach (Process p in all)
                {
                    try
                    {
                        if (p.Id == Process.GetCurrentProcess().Id) continue;
                        IntPtr h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;   // 还在启动中或最小化到托盘
                        ShowWindowAsync(h, SW_RESTORE);
                        SetForegroundWindow(h);
                        return true;
                    }
                    catch { /* 单个进程访问被拒不影响继续找 */ }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("激活已有窗口失败：" + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 立「程序正在运行」的标记，让安装包知道该先提示关闭。
        /// 必须在确认能继续运行之后调用（重复启动的那次不调，避免第二个进程替第一个背标记）。
        /// 建不出来只记日志：装的时候最多退回到「靠文件占用报错」，不影响软件本身。
        /// </summary>
        public static void MarkRunning()
        {
            if (_running != null) return;
            try
            {
                _running = new Mutex(false, RunningGlobal);
            }
            catch (Exception ex)
            {
                Logger.Warn("运行标记（Global）创建失败，改用 Local：" + ex.Message);
                try { _running = new Mutex(false, RunningLocal); }
                catch (Exception ex2)
                {
                    Logger.Warn("运行标记（Local）也创建失败，安装时将无法提前识别本程序在运行：" + ex2.Message);
                }
            }
        }

        /// <summary>释放锁（进程退出时内核也会自动释放，这里只是让「重启」路径更快放开）。</summary>
        public static void Release()
        {
            if (_running != null)
            {
                try { _running.Dispose(); } catch { }
                _running = null;
            }
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch { /* 未持有或已释放，忽略 */ }
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }

        /// <summary>
        /// 锁名 = 数据根目录的哈希。用 Global\ 前缀（店主 11-08 修正）：
        /// 原来用 Local\ 只在当前登录会话内唯一，**同一台电脑上另一个 Windows 用户
        /// 拿不到这把锁**。安装版各用各的 %LocalAppData%\MemberManager 目录，确实互不影响；
        /// 但便携版放在 D:\ 公共目录或 U 盘时，两个用户指向的是**同一个库**，
        /// 于是能各开一个实例同时写，账会重、库也可能坏。
        /// 锁名已含数据目录哈希，改成 Global\ 不会误伤不同目录的两份程序。
        /// 互斥体在 Global\ 下普通用户可创建；万一创建失败，TryAcquire 会放行，不会让人开不了软件。
        /// </summary>
        private static string BuildName()
        {
            string root;
            try { root = AppPaths.RootDir; }
            catch { root = AppDomain.CurrentDomain.BaseDirectory; }
            root = (root ?? "").TrimEnd('\\', '/').ToLowerInvariant();

            string hash;
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(root));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                hash = sb.ToString();
            }
            return "Global\\MemberManager.Single." + hash;
        }
    }
}
