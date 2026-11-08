using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MemberManager.Common
{
    /// <summary>当前这一份程序「是什么形态」的探测结果（全部字段真实取自磁盘/注册表）。</summary>
    public sealed class RunProfileInfo
    {
        public string Key;            // portable / win7 / normal / loose
        public string Text;           // 便携版 / Win7 版 / 普通版 / 独立运行
        public string Desc;           // 一句话事实说明（数据在哪、靠什么跑）
        public string Kernel;         // 界面真正在用的浏览器内核
        public string OsName;         // Windows 7 / 10 / 11 …
        public bool Installed;        // 有卸载程序或安装登记
        public bool BundledRuntime;   // 程序目录自带 runtime\
    }

    /// <summary>
    /// 发行形态探测（店主 11-05：运行模式显示「当前装的到底是哪一种」，不许再靠配置或猜）。
    ///
    /// 为什么必须看磁盘而不是看 portable 标志：
    ///   安装包会把 portable.txt 一起装进 {app}（数据留在自己目录，卸载时才问要不要删），
    ///   于是「便携标志」在安装版上同样为真 —— 早期界面据此显示「绿色免安装」，
    ///   实际是从安装包装出来的，等于在页面上说假话。
    ///
    /// 判定顺序（全部是既成事实）：
    ///   ① 装过安装包？  = {exe}\unins000.exe 存在，或注册表 {AppId}_is1 登记且 InstallLocation 指向当前目录
    ///   ② 系统版本？    = Win10 以下（6.x）没有系统级 WebView2，必须靠随包内核 → Win7 版
    ///   ③ 自带内核？    = {exe}\runtime\msedgewebview2.exe 存在（ShellForm.ResolveRuntimeFolder 优先用它）
    ///
    /// 组合结果：没装 + 数据跟目录走 = 便携版；没装 + 数据在用户目录 = 独立运行；
    ///           装了 + 老系统 = Win7 版；装了 + Win10 以上 = 普通版。
    /// </summary>
    public static class RunProfile
    {
        public const string KeyPortable = "portable";
        public const string KeyWin7 = "win7";
        public const string KeyNormal = "normal";
        public const string KeyLoose = "loose";

        /// <summary>Inno Setup 的卸载登记名（{AppId}_is1 是 Inno 的固定后缀，见 tools\_inno\setup.iss）。</summary>
        private const string UninstKeyName = "{7B3E9C21-5A48-4D6F-9C1B-2E8F4A7D3C65}_is1";
        private const string UninstPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstKeyName;

        /// <summary>当前进程的形态（exe 目录取 AppPaths，便携标志取 AppPaths）。</summary>
        public static RunProfileInfo Detect()
        {
            string dir = null;
            try { dir = AppPaths.ExeDir; } catch { }
            return DetectCore(dir, null);
        }

        /// <summary>指定程序目录的形态；便携标志仍按 AppPaths 判定。</summary>
        public static RunProfileInfo Detect(string exeDir)
        {
            return DetectCore(exeDir, null);
        }

        /// <summary>
        /// 指定目录 + 指定便携标志的形态。冒烟脚本用这个签名在临时目录里造样本
        /// （装个空的 unins000.exe 就能冒充安装版），所以刻意不用可空参数。
        /// </summary>
        public static RunProfileInfo Detect(string exeDir, bool portable)
        {
            return DetectCore(exeDir, portable);
        }

        private static RunProfileInfo DetectCore(string exeDir, bool? portable)
        {
            var info = new RunProfileInfo();
            if (string.IsNullOrEmpty(exeDir))
            {
                try { exeDir = Directory.GetCurrentDirectory(); } catch { exeDir = "."; }
            }
            exeDir = exeDir.TrimEnd('\\', '/');

            bool isPortable;
            if (portable.HasValue) isPortable = portable.Value;
            else
            {
                try { isPortable = AppPaths.Portable; } catch { isPortable = false; }
            }

            info.Installed = HasUninstaller(exeDir);
            info.BundledRuntime = File.Exists(Path.Combine(Path.Combine(exeDir, "runtime"), "msedgewebview2.exe"));
            info.OsName = OsName();
            info.Kernel = KernelText(exeDir, info.BundledRuntime);

            if (!info.Installed)
            {
                if (isPortable)
                {
                    info.Key = KeyPortable;
                    info.Text = "便携版";
                    info.Desc = "未安装 · 数据在程序目录，整个文件夹可拷走";
                }
                else
                {
                    info.Key = KeyLoose;
                    info.Text = "独立运行";
                    info.Desc = "未安装 · 数据在用户目录，重装系统前请先备份";
                }
            }
            else if (IsLegacyWindows())
            {
                // Win10 以下没有系统级 WebView2，界面能开就一定是随包内核在工作
                info.Key = KeyWin7;
                info.Text = "Win7 版";
                info.Desc = "安装版 · " + info.OsName + "，靠随包内核运行";
            }
            else
            {
                info.Key = KeyNormal;
                info.Text = "普通版";
                info.Desc = "安装版 · " + info.OsName;
            }
            return info;
        }

        /// <summary>
        /// 是否装过安装包。先看本目录的卸载程序（最直接的证据），再看注册表登记；
        /// 注册表里记的 InstallLocation 必须指向当前目录 —— 否则只是「这台机器装过」，
        /// 现在跑的可能是从别处拷来的便携副本。
        /// </summary>
        private static bool HasUninstaller(string exeDir)
        {
            try
            {
                if (File.Exists(Path.Combine(exeDir, "unins000.exe"))) return true;
            }
            catch { }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstPath))
                {
                    if (Matches(k, exeDir)) return true;
                    if (k != null) { /* 登记在别处：不算 */ }
                }
            }
            catch { }

            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                {
                    using (RegistryKey k = root.OpenSubKey(UninstPath))
                    {
                        if (Matches(k, exeDir)) return true;
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey k = root.OpenSubKey(UninstPath))
                    {
                        if (Matches(k, exeDir)) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool Matches(RegistryKey k, string exeDir)
        {
            if (k == null) return false;
            string loc = null;
            try { loc = k.GetValue("InstallLocation") as string; } catch { }
            if (string.IsNullOrWhiteSpace(loc)) return true;   // 老安装包没写位置，按已安装处理
            string a = loc.Trim().TrimEnd('\\', '/');
            string b = exeDir.Trim().TrimEnd('\\', '/');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Win10 以下（6.1/6.2/6.3）：系统不带 WebView2，必须靠随包内核。</summary>
        private static bool IsLegacyWindows()
        {
            try { return Environment.OSVersion.Version.Major < 10; }
            catch { return false; }
        }

        private static string OsName()
        {
            try
            {
                Version v = Environment.OSVersion.Version;
                if (v.Major >= 10) return v.Build >= 22000 ? "Windows 11" : "Windows 10";
                if (v.Major == 6 && v.Minor == 1) return "Windows 7";
                if (v.Major == 6 && v.Minor == 2) return "Windows 8";
                if (v.Major == 6 && v.Minor == 3) return "Windows 8.1";
                return "Windows " + v.Major + "." + v.Minor;
            }
            catch { return "Windows"; }
        }

        /// <summary>
        /// 界面真正在用的内核。取用顺序与 ShellForm.ResolveRuntimeFolder() 一致：
        /// 程序目录的 runtime\ 优先，其次系统安装位置（取版本号最大的那个）。
        /// </summary>
        private static string KernelText(string exeDir, bool bundled)
        {
            if (bundled)
            {
                string ver = ExeVersion(Path.Combine(Path.Combine(exeDir, "runtime"), "msedgewebview2.exe"));
                return string.IsNullOrEmpty(ver) ? "随包内核" : "随包 " + ver;
            }
            string sys = SystemRuntimeVersion();
            if (!string.IsNullOrEmpty(sys)) return "系统 " + sys;
            return "启动时自动发现";
        }

        private static string ExeVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string v = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (string.IsNullOrEmpty(v)) return null;
                int sp = v.IndexOf(' ');
                if (sp > 0) v = v.Substring(0, sp);
                return v.Trim();
            }
            catch { return null; }
        }

        private static string SystemRuntimeVersion()
        {
            var roots = new List<string>();
            try
            {
                string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (!string.IsNullOrEmpty(pf86)) roots.Add(pf86);
                string pf = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(pf) && !roots.Contains(pf)) roots.Add(pf);
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(lad) && !roots.Contains(lad)) roots.Add(lad);
            }
            catch { }

            Version best = null;
            string bestText = null;
            foreach (string r in roots)
            {
                string app = Path.Combine(Path.Combine(r, "Microsoft"), Path.Combine("EdgeWebView", "Application"));
                string[] dirs;
                try
                {
                    if (!Directory.Exists(app)) continue;
                    dirs = Directory.GetDirectories(app);
                }
                catch { continue; }

                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    Version v;
                    if (!Version.TryParse(name, out v)) continue;
                    if (best == null || v > best) { best = v; bestText = name; }
                }
            }
            return bestText;
        }
    }
}
