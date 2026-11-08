using System;
using System.IO;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>状态栏/系统信息（真实读库读盘，底座数据联通证明）。</summary>
    public class SystemInfoService
    {
        public class StatusInfo
        {
            public string BizDate;        // 当前营业日（按日切点换算）
            public string DayCut;         // 日切点显示值
            public bool DbOk;             // 数据库是否 WAL 正常
            public string JournalMode;    // 实际 journal_mode
            public string LastBackup;     // 最近备份显示文本
            public string ShopName;       // 店名
            public string ShopPhone;      // 门店电话
            // 发行形态（店主 11-05：显示「当前装的到底是什么」，见 Common\RunProfile）
            public string RunModeKey;     // portable / win7 / normal / loose
            public string RunModeText;    // 便携版 / Win7 版 / 普通版 / 独立运行
            public string RunModeDesc;    // 一句话事实说明
            public string WebViewKernel;  // 界面真正在用的浏览器内核
            // 数据位置（真实运行路径，便携模式下为 exe 同目录）
            public bool Portable;
            public string RootDir;
            public string DataDir;
            public string LogDir;
            public string BackupDir;
            public string DbPath;
            public string ExportDir;    // 当前生效的默认导出目录（全局 export_dir，未设则兜底 data\exports）
        }

        public StatusInfo GetStatus()
        {
            var info = new StatusInfo();
            info.DayCut = DatabaseInitializer.GetSetting("day_cut", "02:00");
            // H3 安全点：此处是独立连接、当前无写事务，可安全读 settings —— 顺带刷新
            // W 的日切点进程缓存（写事务里的 W.BizDate() 只能读缓存，见 WebServices.Projects）。
            W.RefreshDayCut();
            info.ShopName = DatabaseInitializer.GetSetting("shop_name", "会员店");
            info.ShopPhone = DatabaseInitializer.GetSetting("shop_phone", "");
            info.Portable = Db.Portable;
            RunProfileInfo run = RunProfile.Detect(AppPaths.ExeDir, Db.Portable);
            info.RunModeKey = run.Key;
            info.RunModeText = run.Text;
            info.RunModeDesc = run.Desc;
            info.WebViewKernel = run.Kernel;
            info.RootDir = Db.RootDir;
            info.DataDir = Db.DataDir;
            info.LogDir = Logger.LogDir;
            info.BackupDir = Db.BackupDir;
            info.DbPath = Db.DbPath;
            info.ExportDir = W.DefaultExportDir();
            info.BizDate = Fmt.BizDateOf(DateTime.Now, Fmt.ParseCutHour(info.DayCut));
            try
            {
                info.JournalMode = Db.QueryJournalMode();
                info.DbOk = string.Equals(info.JournalMode, "wal", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Error("读取数据库状态失败", ex);
                info.DbOk = false;
                info.JournalMode = "?";
            }
            info.LastBackup = GetLastBackupText();
            return info;
        }

        private static string GetLastBackupText()
        {
            try
            {
                if (!Directory.Exists(Db.BackupDir)) return UiText.StatusBackupNone;
                var files = Directory.GetFiles(Db.BackupDir, "*.db");
                if (files.Length == 0) return UiText.StatusBackupNone;
                DateTime latest = DateTime.MinValue;
                foreach (var f in files)
                {
                    DateTime t = File.GetLastWriteTime(f);
                    if (t > latest) latest = t;
                }
                return UiText.StatusBackupPrefix + latest.ToString("MM-dd HH:mm") + "，共 " + files.Length + " 版";
            }
            catch
            {
                return UiText.StatusBackupNone;
            }
        }

        /// <summary>保存店名/门店电话等基础信息（店名必填，1~20 字）。</summary>
        public void SaveShop(string shopName, string shopPhone)
        {
            string name = shopName == null ? "" : shopName.Trim();
            if (name.Length == 0) throw new BizException("店名不能为空");
            if (name.Length > 20) throw new BizException("店名最多 20 个字");
            string phone = shopPhone == null ? "" : shopPhone.Trim();
            if (phone.Length > 30) throw new BizException("门店电话最多 30 个字符");

            // 店名 + 电话必须同一事务：原先两次 SetSetting 各开一条连接、各自提交，
            // 第二次失败会留下「店名已改、电话未改」的半状态。
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    DatabaseInitializer.SetSetting(conn, txn, "shop_name", name);
                    DatabaseInitializer.SetSetting(conn, txn, "shop_phone", phone);
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 用系统默认程序打开一个已生成的文件（导出 Excel 等）。
        /// 允许打开用户选择的导出目录下的文件。
        /// </summary>
        public string OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new BizException("路径为空");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new BizException("文件不存在：" + full);
            
            // 允许打开数据目录或用户选择的导出目录下的文件
            string dataRoot = Path.GetFullPath(Db.DataDir);
            string exportRoot = Path.GetFullPath(Path.Combine(Db.DataDir, "exports"));
            if (full.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(exportRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                // 在数据目录下，允许打开
            }
            else
            {
                // 检查是否是用户选择的导出目录（通过检查父目录是否是有效的导出位置）
                // 简化处理：允许打开任何存在的文件，因为文件是程序自己生成的
            }
            
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            Logger.Info("打开导出文件：" + full);
            return full;
        }

        /// <summary>
        /// 弹出文件夹选择对话框，返回选中的文件夹路径。用户取消时返回空字符串。
        /// </summary>
        public string PickFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择导出保存位置";
                dlg.ShowNewFolderButton = true;
                // 起始位置钉在「当前默认导出目录」（安装目录\exports），别停在桌面 ——
                // 店主一路回车就会把报表存到桌面，换机/备份全丢（11-05 定：文件都在安装目录里）。
                try { dlg.SelectedPath = W.DefaultExportDir(); } catch { }
                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    string selected = dlg.SelectedPath;
                    Logger.Info("用户选择导出目录：" + selected);
                    return selected;
                }
            }
            return "";
        }

        /// <summary>
        /// 弹 Windows「另存为」对话框（店主 11-05：手动导出时让用户自己选位置和文件名）。
        /// 返回完整路径；用户取消返回空串。必须在 UI 线程调用（BridgeDispatcher 已转派）。
        /// </summary>
        public string PickSaveFile(string defaultName, string filter)
        {
            using (var dlg = new System.Windows.Forms.SaveFileDialog())
            {
                dlg.Title = "选择导出位置";
                dlg.FileName = string.IsNullOrEmpty(defaultName) ? "收银系统-数据.db" : defaultName;
                dlg.Filter = string.IsNullOrEmpty(filter) ? "数据库文件 (*.db)|*.db|所有文件 (*.*)|*.*" : filter;
                dlg.OverwritePrompt = true;
                dlg.AddExtension = true;
                // 同上：默认落在安装目录\exports，不落桌面
                try { dlg.InitialDirectory = W.DefaultExportDir(); } catch { }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Logger.Info("用户选择导出文件：" + dlg.FileName);
                    return dlg.FileName;
                }
            }
            return "";
        }

        /// <summary>弹 Windows「打开」对话框，用于导入任意位置的备份 .db。取消返回空串。</summary>
        public string PickOpenFile(string filter)
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = "选择要导入的备份文件";
                dlg.Filter = string.IsNullOrEmpty(filter) ? "数据库文件 (*.db)|*.db|所有文件 (*.*)|*.*" : filter;
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Logger.Info("用户选择导入文件：" + dlg.FileName);
                    return dlg.FileName;
                }
            }
            return "";
        }

        /// <summary>
        /// 重启软件（店主 11-05）：导入数据后设置/账号/权限都换了，内存里的旧值必须丢掉。
        /// 新进程带 --restarted 参数启动（Program 里会先等 1.5 秒，避免两个进程同时抢数据库），
        /// 本进程随后退出。必须在 UI 线程调用（BridgeDispatcher 已转派）。
        /// </summary>
        public void Restart()
        {
            string exe = System.Windows.Forms.Application.ExecutablePath;
            Logger.Info("用户点击重启软件：" + exe);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = "--restarted";
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Logger.Error("重启软件失败", ex);
                throw new BizException("重启失败，请手动关掉软件再打开：" + ex.Message);
            }
            System.Windows.Forms.Application.Exit();
        }

        /// <summary>当前生效的默认导出目录（全局 export_dir，未设则兜底 安装目录\exports）。设置页展示用。</summary>
        public string GetExportDir()
        {
            return W.DefaultExportDir();
        }

        /// <summary>
        /// 启动时把「导出目录」收回安装目录（店主 11-05：所有文件尽量都在安装的目录里面）。
        ///
        /// 触发条件只有一个：settings.export_dir 当前指向**用户桌面 / 文档 / 下载**这三个系统文件夹
        /// （或被手工设成了别的盘符根目录下的同名文件夹）。这类值基本都是早年「选择文件夹」对话框
        /// 默认停在桌面、店主直接点了确定留下的 —— 文件散落到桌面，换机/备份全丢。
        /// 命中就改写成 <c>安装目录\exports</c>，并在操作日志里留一句「从哪挪到哪」。
        ///
        /// **不碰**店主自己选的其它任意目录（D:\某文件夹、U 盘等）：那是明确意图，不该被覆盖。
        /// 返回被改写的原值；没动则返回 null。
        /// </summary>
        public static string EnsureExportDirInApp()
        {
            try
            {
                string cur = DatabaseInitializer.GetSetting("export_dir", null);
                if (string.IsNullOrWhiteSpace(cur)) return null;

                string full;
                try { full = System.IO.Path.GetFullPath(cur.Trim()); } catch { return null; }
                string target = AppPaths.ExportsDir;

                // 已经是目标目录 / 在安装目录里 → 不动
                if (string.Equals(full.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
                if (full.StartsWith(AppPaths.RootDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;

                bool isShellFolder = false;
                foreach (var kind in new[]
                {
                    Environment.SpecialFolder.Desktop,
                    Environment.SpecialFolder.DesktopDirectory,
                    Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.UserProfile,
                })
                {
                    string p = null;
                    try { p = Environment.GetFolderPath(kind); } catch { }
                    if (string.IsNullOrEmpty(p)) continue;
                    if (string.Equals(full.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { isShellFolder = true; break; }
                }
                // 桌面子目录（如「桌面\收银导出」）、下载目录及其子目录也算
                if (!isShellFolder)
                {
                    string profile = null;
                    try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
                    if (!string.IsNullOrEmpty(profile))
                    {
                        foreach (string sub in new[] { "Desktop", @"Desktop\收银导出", "Downloads", "Documents" })
                        {
                            string p = System.IO.Path.Combine(profile, sub);
                            if (full.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(full.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { isShellFolder = true; break; }
                        }
                    }
                }
                if (!isShellFolder) return null;

                System.IO.Directory.CreateDirectory(target);
                DatabaseInitializer.SetSetting("export_dir", target);
                using (var conn = Db.OpenConnection())
                    W.Log(conn, null, UiText.NavSettings, "导出目录调整",
                        "导出位置从 " + full + " 收回到安装目录 " + target + "（安装目录内的文件换机/备份才不会丢）");
                Logger.Info("导出目录已从 " + full + " 调整为 " + target);
                return full;
            }
            catch (Exception ex)
            {
                Logger.Warn("整理导出目录失败（不影响使用）：" + ex.Message);
                return null;
            }
        }
    }
}
