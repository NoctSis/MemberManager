using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MemberManager.Common;
using MemberManager.DAL;
using Microsoft.Web.WebView2.Core;

namespace MemberManager.Shell
{
    /// <summary>
    /// WebView2 壳窗体：全屏加载 dist\web 前端（React 设计稿），
    /// 经 window.chrome.webview.postMessage 与本地 BLL 通信。
    /// </summary>
    public class ShellForm : Form
    {
        private Microsoft.Web.WebView2.WinForms.WebView2 _web;
        private BridgeDispatcher _bridge;
        private bool _minSizeApplied;

        /// <summary>
        /// 最小窗口宽（店主定：调整窗口不调整内容，内容不许被压缩）。
        /// 收银开单是最宽的页面，按它的自然宽度反推（窗口 1280 时）：
        ///   视口 1264 = 窗口 1280 - 窗框 16
        ///   内容 1048 = 视口 - 导航 176 - 页面内边距 40
        ///   项目列 512 = 内容 - 账单 312 - 挂单 192 - 列间距 32
        /// 项目列 512 时「头像 40 + 会员搜索框 190 + 新会员建档按钮 107 + 散客提示 104 + 间隙」刚好排一行，
        /// 会员搜索框不会被挤成只剩图标、分类胶囊不会竖排、项目卡不会逐字折断。
        /// </summary>
        private const int MinWidth = 1280;

        /// <summary>左侧导航宽（与前端 AppShell 的 w-44 对齐）。</summary>
        private const int NavWidth = 176;

        public ShellForm()
        {
            Text = UiText.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            // 最小窗口（店主定：调整窗口不调整内容，内容不允许被压缩）：
            // 宽 = MinWidth —— 收银开单是最宽的页面（左项目列 + 账单 312 + 挂单 192 + 间距），
            //       此宽度下项目列仍有 ~512px，搜索框/分类胶囊/项目卡都不会被挤变形；
            // 高 = 首页（主工作台）刚好无竖向滚动的尺寸，首载渲染稳定后实测钉死（ApplyMinSizeAsync）。
            MinimumSize = new System.Drawing.Size(MinWidth, 800);
            Size = new System.Drawing.Size(1440, 940);
            // 店主 10-07：默认全屏（最大化）；之后记住上次的窗口状态与大小位置（见 ApplySavedWindowGeometry）
            ApplySavedWindowGeometry();
            Font = Theme.Body;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _bridge = new BridgeDispatcher(this);

            _web = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            Load += new EventHandler(OnLoad);
            // 拖动结束后与关闭时各写一次（最小化不覆盖；探针模式内部会跳过）
            ResizeEnd += new EventHandler(delegate { SaveWindowGeometry(); });
            FormClosing += new FormClosingEventHandler(delegate { SaveWindowGeometry(); });
        }

        // ---------- 窗口几何记忆（店主 10-07：默认全屏；记住上次窗口大小） ----------

        private const string KeyWindowState = "window_state";     // Normal | Maximized
        private const string KeyWindowBounds = "window_bounds";   // x,y,w,h

        /// <summary>
        /// 恢复上次窗口状态与大小位置；**首次运行（无记录）默认最大化**。
        /// 读到的坐标必须与某块屏幕有足够重叠，否则不回填（换显示器/拔掉扩展屏后不会跑到看不见的地方）。
        /// 探针模式跳过：外部会指定尺寸截图，几何必须保持确定性。
        /// </summary>
        private void ApplySavedWindowGeometry()
        {
            if (Program.IsProbeMode) return;
            try
            {
                string state = DatabaseInitializer.GetSetting(KeyWindowState, "");
                var bounds = ParseBounds(DatabaseInitializer.GetSetting(KeyWindowBounds, ""));
                if (bounds.HasValue && OverlapsAnyScreen(bounds.Value))
                {
                    // 必须先切 Manual，否则 CenterScreen 会在显示时覆盖 Bounds
                    StartPosition = FormStartPosition.Manual;
                    Bounds = bounds.Value;
                }
                // 首次运行（无记录）或上次就是最大化 -> 最大化
                if (state.Length == 0 || state == "Maximized") WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex)
            {
                Logger.Warn("恢复窗口几何失败（改用默认）：" + ex.Message);
            }
        }

        /// <summary>
        /// 写回窗口状态与大小位置。最大化时存 RestoreBounds（还原后的大小），
        /// 最小化时直接跳过（别用最小化状态覆盖上次可用的状态）。
        /// </summary>
        private void SaveWindowGeometry()
        {
            if (Program.IsProbeMode) return;
            try
            {
                if (WindowState == FormWindowState.Minimized) return;
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                DatabaseInitializer.SetSetting(KeyWindowState,
                    WindowState == FormWindowState.Maximized ? "Maximized" : "Normal");
                DatabaseInitializer.SetSetting(KeyWindowBounds,
                    r.X + "," + r.Y + "," + r.Width + "," + r.Height);
            }
            catch (Exception ex)
            {
                Logger.Warn("保存窗口几何失败（忽略）：" + ex.Message);
            }
        }

        private static System.Drawing.Rectangle? ParseBounds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Split(',');
            if (parts.Length != 4) return null;
            int x, y, w, h;
            if (!int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y) ||
                !int.TryParse(parts[2], out w) || !int.TryParse(parts[3], out h)) return null;
            if (w < 200 || h < 200) return null;
            return new System.Drawing.Rectangle(x, y, w, h);
        }

        /// <summary>窗口区域是否与某块屏幕有足够重叠（防止沿用已拔掉的显示器坐标）。</summary>
        private static bool OverlapsAnyScreen(System.Drawing.Rectangle b)
        {
            foreach (var s in Screen.AllScreens)
            {
                var i = System.Drawing.Rectangle.Intersect(s.WorkingArea, b);
                if (i.Width >= 200 && i.Height >= 100) return true;
            }
            return false;
        }

        /// <summary>
        /// 首载后按首页（主工作台）内容实测钉死最小窗口（店主 09-22 定）：
        /// 临时把 main 限宽到「最小窗口宽 MinWidth 对应的内容宽」量竖向足迹（避免宽窗口量出来偏矮），
        /// MinimumSize 高 = 足迹 + 顶栏52 + 状态栏28 + 窗体镶边，宽 = MinWidth（收银页不再压缩的临界宽）。
        /// 其他页面高度超了就正常滚动，不参与定标。
        /// </summary>
        private async Task ApplyMinSizeAsync(int delayMs)
        {
            var core = _web.CoreWebView2;
            if (core == null || _minSizeApplied) return;
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                int chromeW = Width - ClientSize.Width;
                int chromeH = Height - ClientSize.Height;
                int mainW = MinWidth - chromeW - NavWidth;   // 最小窗口宽下 main 的内容宽
                if (mainW < 400) mainW = 400;

                var m = await core.ExecuteScriptAsync(
                    "(function(){var el=document.querySelector('main');if(!el)return '0';" +
                    "el.style.maxWidth='" + mainW + "px';var h=el.scrollHeight;" +
                    "el.style.maxWidth='';return String(h);})()");
                int sh;
                if (!int.TryParse(m.Trim('"'), out sh) || sh < 400) return;
                _minSizeApplied = true;

                int need = sh + 80 + chromeH + 2;
                MinimumSize = new System.Drawing.Size(MinWidth, need);
            }
            catch { }
        }

        /// <summary>
        /// 定位 WebView2 运行时目录；返回 null 表示交给加载器自动发现。
        /// <para>
        /// 为什么要显式指定：Win7 上只能跑 Runtime 109，而 109 只能手工放置（官方 Evergreen
        /// 装上去的是最新版，在 Win7 必炸）。手工放置只写了注册表 pv，加载器的"自动发现"在
        /// 没有 Edge 浏览器可回退的机器上（Win7 就是）会失败，报
        /// "Couldn't find a compatible Webview2 Runtime installation"。实测把同一个目录
        /// 显式传给 CreateAsync 就能正常起来（GetAvailableBrowserVersionString(目录) 返回
        /// 109.0.1518.140）。所以这里自己找目录、显式传，不再依赖自动发现。
        /// </para>
        /// 查找顺序：随包 runtime\ → 标准 EdgeWebView\Application\下各版本 → 环境变量覆盖。
        /// </summary>
        private static string ResolveRuntimeFolder()
        {
            var candidates = new List<string>();

            // 1) 随包携带的运行时（绿色版：客户机什么都不用装）
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                candidates.Add(Path.Combine(dir, "runtime"));
                candidates.Add(Path.Combine(dir, "WebView2Runtime"));
            }
            catch { }

            // 2) 标准安装位置下已存在的任意版本（含 Win7 上手工放置的 109）
            var roots = new List<string>();
            try
            {
                string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (string.IsNullOrEmpty(pf86))
                    pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86)) roots.Add(pf86);
                string pf = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(pf) && !roots.Contains(pf)) roots.Add(pf);
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(lad)) roots.Add(lad);
            }
            catch { }

            foreach (string r in roots)
            {
                string app = Path.Combine(r, "Microsoft", "EdgeWebView", "Application");
                if (!Directory.Exists(app)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(app); } catch { continue; }
                var list = new List<string>(dirs);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                list.Reverse();                       // 版本号倒序，优先用较新的
                candidates.AddRange(list);
            }

            // 3) 手工覆盖（排查用）：MEMBERMANAGER_WEBVIEW2_FOLDER
            try
            {
                string over = Environment.GetEnvironmentVariable("MEMBERMANAGER_WEBVIEW2_FOLDER");
                if (!string.IsNullOrEmpty(over)) candidates.Add(over);
            }
            catch { }

            // 第一轮：加载器认可的目录
            foreach (string p in candidates)
            {
                if (IsRuntimeFolder(p, true)) return p;
            }
            // 第二轮：退化判断——只要 msedgewebview2.exe 在就用它。
            // （GetAvailableBrowserVersionString 在某些机器上会抛异常，不能让它一票否决）
            foreach (string p in candidates)
            {
                if (IsRuntimeFolder(p, false)) return p;
            }
            return null;
        }

        /// <summary>目录里有可用的 WebView2 运行时吗。</summary>
        /// <param name="askLoader">true=还要让加载器确认（GetAvailableBrowserVersionString）。</param>
        private static bool IsRuntimeFolder(string p, bool askLoader)
        {
            try
            {
                if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return false;
                if (!File.Exists(Path.Combine(p, "msedgewebview2.exe"))) return false;
                if (!askLoader) return true;
                string v = CoreWebView2Environment.GetAvailableBrowserVersionString(p);
                return !string.IsNullOrEmpty(v);
            }
            catch { return false; }
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MemberManager", "WebView2");
                Directory.CreateDirectory(userData);

                string runtime = ResolveRuntimeFolder();
                Logger.Info("WebView2 运行时目录：" + (runtime ?? "<未找到，回退自动发现>"));
                var env = await CoreWebView2Environment.CreateAsync(runtime, userData);
                await _web.EnsureCoreWebView2Async(env);

                string webRoot = Program.WebRoot;
                if (!Directory.Exists(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
                {
                    MessageBox.Show("未找到界面资源目录 web\\，请确认安装完整。", "启动失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }

                var core = _web.CoreWebView2;
                // 单机版界面固定浅色主题（与效果图一致）；生产版禁用浏览器右键菜单与开发者工具。
                // 注意：这里**不能**用 core.Profile.PreferredColorScheme —— 它属 ICoreWebView2_13，
                // 需 SDK ≥1.0.1774.30，而 Win7 上限是 1.0.1518.46（10-05 为 Win7 适配统一降 SDK）。
                // 等价做法：用旧 SDK 就有的 DefaultBackgroundColor 保证页面加载前是白底；
                // 页面内的主题由 index.css 的 :root{color-scheme:light} + 固定浅色 token 决定。
                _web.DefaultBackgroundColor = System.Drawing.Color.White;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.SetVirtualHostNameToFolderMapping("app.local", webRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += new EventHandler<CoreWebView2WebMessageReceivedEventArgs>(OnWebMessage);
                core.NavigationCompleted += new EventHandler<CoreWebView2NavigationCompletedEventArgs>((s2, e2) =>
                {
                    var ignored = ApplyMinSizeAsync(1200);
                });
                string page = Program.ProbePage;
                string query = Program.IsProbeMode ? "?probe=1" : "";
                if (Program.IsProbeMode && Program.ProbeUser.Length > 0)
                    query += "&probeUser=" + System.Uri.EscapeDataString(Program.ProbeUser);
                // 探针专用：置 MM_PROBE_PWD_TIP=1 时强制弹出「建议改密码」提醒，
                // 便于单独截图验证该弹窗（生产不置该变量，行为不受影响）。
                if (Program.IsProbeMode && Environment.GetEnvironmentVariable("MM_PROBE_PWD_TIP") == "1")
                    query += "&probePwdTip=1";
                core.Navigate("https://app.local/index.html" + query + (page.Length > 0 ? "#" + page : ""));
                Logger.Info("WebView2 已加载前端：" + webRoot);
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 初始化失败", ex);
                if (Program.IsProbeMode) { Close(); return; }
                MessageBox.Show(
                    "界面组件（WebView2 Runtime）初始化失败：" + ex.Message +
                    "\n\n请在电脑上安装 Microsoft Edge WebView2 Runtime 后重试。" +
                    "\n下载地址：https://developer.microsoft.com/microsoft-edge/webview2/",
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // 收到请求即丢到线程池跑 BLL，完成后回 UI 线程投递结果
            string reqJson = e.TryGetWebMessageAsString();
            Task.Run(new Action(() =>
            {
                string resp = _bridge.Handle(reqJson);
                BeginInvoke(new Action(() =>
                {
                    try { _web.CoreWebView2.PostWebMessageAsJson(resp); }
                    catch (ObjectDisposedException) { }
                }));
            }));
        }

        /// <summary>
        /// 打印收银小票。策略（店主 10-02 定：一键直打默认打印机）：
        ///   · 默认打印机是**实体打印机** → WebView2 原生 PrintAsync 静默直打（不弹对话框，规避被抢焦点关闭）；
        ///   · 默认打印机是 PDF/XPS/OneNote/Fax 之类虚拟打印机，或没有打印机 → 直接静默生成小票 PDF 并打开。
        ///   原因：虚拟打印机（如 Microsoft Print to PDF）在 PrintAsync 下会弹「另存为」对话框并阻塞，反而不如直接出 PDF。
        /// 打印内容由 @media print 决定，只显示 .receipt-print 小票区（见 web/src/index.css）。
        /// 必须在 UI 线程调用；PrintAsync/PrintToPdfStreamAsync 需 SDK ≥ 1.0.1518.46（本项目 1.0.4191）。
        /// </summary>
        public async void PrintReceipt()
        {
            try
            {
                var core = _web == null ? null : _web.CoreWebView2;
                if (core == null) { Logger.Info("打印小票：WebView2 未就绪，跳过"); return; }
                Logger.Info("打印小票：开始");

                string real = RealPrinterName();
                if (real != null)
                {
                    Logger.Info("打印小票：默认打印机（实体）= " + real + "，静默打印");
                    CoreWebView2PrintStatus status;
                    try { status = await core.PrintAsync(null); }
                    catch (Exception ex) { Logger.Info("打印小票：调用异常 " + ex.Message); status = CoreWebView2PrintStatus.OtherError; }

                    if (status == CoreWebView2PrintStatus.Succeeded)
                    {
                        Logger.Info("打印小票：已发送到打印机 " + real);
                        return;
                    }
                    Logger.Info("打印小票：打印未成功（state=" + status + "），改为生成 PDF");
                }
                else
                {
                    Logger.Info("打印小票：无实体打印机（默认=" + (ReadDefaultPrinter() ?? "无") + "），改为生成 PDF");
                }

                await SaveReceiptPdf(core);
            }
            catch (Exception ex)
            {
                Logger.Error("打印失败", ex);
            }
        }

        /// <summary>当前默认打印机名（无则 null）。</summary>
        private static string ReadDefaultPrinter()
        {
            try { return new System.Drawing.Printing.PrintDocument().PrinterSettings.PrinterName; }
            catch { return null; }
        }

        /// <summary>「实体」打印机名：默认打印机是 PDF/XPS/OneNote/Fax 等虚拟打印机时返回 null（此时应直接出 PDF）。</summary>
        private static string RealPrinterName()
        {
            string def = ReadDefaultPrinter();
            if (string.IsNullOrEmpty(def)) return null;
            string u = def.ToUpperInvariant();
            if (u.Contains("PDF") || u.Contains("XPS") || u.Contains("ONENOTE") || u.Contains("FAX")) return null;
            return def;
        }

        /// <summary>
        /// 小票纸实际宽度（毫米）。读默认打印机的默认纸张宽度；不可得或异常值则回落 80mm。
        /// 这样不同店用 50/58/80mm 热敏纸都不用手动量、也不裁字。
        /// </summary>
        private static double ReceiptPaperWidthMm()
        {
            try
            {
                using (var pd = new System.Drawing.Printing.PrintDocument())
                {
                    var ps = pd.PrinterSettings.DefaultPageSettings.PaperSize;
                    if (ps != null && ps.Width > 0)
                    {
                        double mm = ps.Width / 100.0 * 25.4;   // PaperSize.Width 单位：1/100 英寸
                        // 只接受热敏小票合理区间 40~100mm，避免 A4/Letter 等把收据撑成宽幅
                        if (mm >= 40 && mm <= 100) return mm;
                        Logger.Info("小票纸宽检测值 " + mm.ToString("F1") + "mm 超出热敏区间，回落 80mm");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读默认打印机纸宽失败，回落 80mm：" + ex.Message);
            }
            return 80.0;
        }

        /// <summary>
        /// 把小票打印成 PDF（按默认打印机实际纸宽，自适应 50/58/80mm）并用系统默认阅读器打开。无实体打印机时的兜底。
        /// 修复：旧逻辑直接拿全宽应用页（WebView2 视口 ~1280px）去打 80mm 纸，打印引擎把整页
        /// 缩到 ~30% 且下移。现改为——把 .receipt-print 小票区抽出来，放进一个等于实际纸宽（CSS px）
        /// 的独立临时 WebView2 里渲染，再打印，内容与纸宽 1:1，不缩放、不漂移、不被裁。
        /// 抽不到小票（例如非收银台路径未挂 .receipt-print）时回退到整页打印，保底不出错。
        /// </summary>
        private async Task SaveReceiptPdf(CoreWebView2 core)
        {
            string pdfPath = null;
            try
            {
                double mm = ReceiptPaperWidthMm();
                Logger.Info("小票 PDF：按默认打印机纸宽 " + mm.ToString("F1") + "mm 出 PDF");
                string html = await GetReceiptHtmlAsync(core);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    string docPath = BuildReceiptTempDoc(html, mm);
                    if (!string.IsNullOrWhiteSpace(docPath))
                    {
                        pdfPath = await PrintTempDocToPdf(core, docPath, mm);
                        try { File.Delete(docPath); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("生成小票 PDF（独立页）失败，回退整页打印", ex);
            }

            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                await SaveReceiptPdfLegacy(core);
                return;
            }

            Logger.Info("小票 PDF 已生成：" + pdfPath + "（" + new FileInfo(pdfPath).Length + " 字节）");
            try { System.Diagnostics.Process.Start(pdfPath); }
            catch (Exception ex) { Logger.Info("打开小票 PDF 失败：" + ex.Message); }
        }

        /// <summary>旧逻辑：直接拿主 WebView2 整页打印成 PDF（会缩约 30%）。仅作兜底回退。纸宽随默认打印机实际宽度。</summary>
        private async Task SaveReceiptPdfLegacy(CoreWebView2 core)
        {
            try
            {
                double mm = ReceiptPaperWidthMm();
                var ps = core.Environment.CreatePrintSettings();
                ps.PageWidth = mm / 25.4;      // 实际纸宽（英寸）
                ps.PageHeight = 11.0;
                ps.MarginTop = 0; ps.MarginBottom = 0; ps.MarginLeft = 0; ps.MarginRight = 0;
                ps.ShouldPrintBackgrounds = true;

                var stream = await core.PrintToPdfStreamAsync(ps);
                string dir = Path.Combine(AppPaths.DataDir, "exports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "小票-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".pdf");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.CopyTo(fs);
                Logger.Info("小票 PDF（整页兜底）已生成：" + path + "（" + new FileInfo(path).Length + " 字节）");
                try { System.Diagnostics.Process.Start(path); }
                catch (Exception ex) { Logger.Info("打开小票 PDF 失败：" + ex.Message); }
            }
            catch (Exception ex)
            {
                Logger.Error("生成小票 PDF（整页兜底）失败", ex);
            }
        }

        /// <summary>抓主页面当前的小票 HTML（仅打印时 .receipt-print 存在于 DOM）。</summary>
        private static async Task<string> GetReceiptHtmlAsync(CoreWebView2 core)
        {
            try
            {
                const string js = "document.querySelector('.receipt-print')?document.querySelector('.receipt-print').outerHTML:''";
                string res = await core.ExecuteScriptAsync(js);
                if (string.IsNullOrEmpty(res)) return "";
                // ExecuteScriptAsync 返回 JSON 字符串字面量，反序列化拿原始 HTML
                string s = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(res);
                return s ?? "";
            }
            catch (Exception ex)
            {
                Logger.Warn("取小票 HTML 失败：" + ex.Message);
                return "";
            }
        }

        /// <summary>把小票 HTML 拼成独立的、内联了已构建 CSS 的临时页。视口宽 = 实际纸宽 mm，内容填满任意纸（50/58/80mm），不裁。</summary>
        private static string BuildReceiptTempDoc(string receiptHtml, double paperMm)
        {
            try
            {
                string mm = paperMm.ToString("F1");
                string css = LoadBuiltCss();
                string doc =
                    "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
                    "<style>" + (css ?? "") + "</style>" +
                    "<style>" +
                    "@page{size:" + mm + "mm auto;margin:0!important}" +
                    "html,body{margin:0!important;padding:0!important;background:#fff!important;min-height:100%!important}" +
                    "body{display:flex!important;flex-direction:column!important;align-items:center!important}" +
                    ".receipt-print{width:100%!important;display:block!important;position:static!important}" +
                    ".receipt-print>div{max-width:" + mm + "mm!important;box-sizing:border-box!important}" +"</style></head><body>" + receiptHtml + "</body></html>";
                string dir = Path.Combine(Path.GetTempPath(), "mm_receipt");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "mm_receipt_" + Guid.NewGuid().ToString("N") + ".html");
                File.WriteAllText(path, doc, System.Text.Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Warn("拼小票临时页失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>读已经构建好的前端 CSS（含 Tailwind 工具类，小票里的 w-[300px]/text-[12px] 等靠它）。</summary>
        private static string _builtCssCache;
        private static string LoadBuiltCss()
        {
            if (_builtCssCache != null) return _builtCssCache;
            try
            {
                string webRoot = Program.WebRoot;
                var files = Directory.GetFiles(webRoot, "index-*.css", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    Array.Sort(files);
                    _builtCssCache = File.ReadAllText(files[files.Length - 1], System.Text.Encoding.UTF8);
                    return _builtCssCache;
                }
            }
            catch (Exception ex) { Logger.Warn("读内置 CSS 失败：" + ex.Message); }
            return "";
        }

        /// <summary>在等于实际纸宽的独立隐藏 WebView2 里渲染临时页，打印成 1:1 的小票 PDF（不缩放、不漂移、不裁）。</summary>
        private async Task<string> PrintTempDocToPdf(CoreWebView2 core, string docPath, double paperMm)
        {
            CoreWebView2Controller ctrl = null;
            System.Windows.Forms.Form host = null;
            try
            {
                // 临时 WebView2 视口 = 纸宽（CSS px），打印时 1:1 不缩放
                int w = (int)Math.Round(paperMm / 25.4 * 96) + 1;
                host = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Width = w,
                    Height = 640,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-10000, -10000),
                };
                host.Show();

                ctrl = await core.Environment.CreateCoreWebView2ControllerAsync(host.Handle);
                ctrl.DefaultBackgroundColor = System.Drawing.Color.White;
                try
                {
                    ctrl.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    ctrl.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    ctrl.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                }
                catch { }

                // 用虚拟主机映射打开临时页（避免 file:// 限制，与 app.local 同源做法）
                string tempDir = Path.GetDirectoryName(docPath);
                ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping("receipt.local", tempDir, CoreWebView2HostResourceAccessKind.Allow);
                var navTcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (s, e) => navTcs.TrySetResult(e.IsSuccess);
                ctrl.CoreWebView2.NavigationCompleted += handler;
                ctrl.CoreWebView2.Navigate("https://receipt.local/" + Path.GetFileName(docPath));
                await navTcs.Task;
                await Task.Delay(300);   // 等字体/布局稳定

                // 按内容实际高度定纸高（紧致小票，不空一大截）
                double pageH = 11.0;
                try
                {
                    string hJs = await ctrl.CoreWebView2.ExecuteScriptAsync("document.body.scrollHeight");
                    double hCss;
                    if (double.TryParse(hJs.Trim('"'), out hCss) && hCss > 0) pageH = hCss / 96.0;
                }
                catch { }

                var ps = core.Environment.CreatePrintSettings();
                ps.PageWidth = paperMm / 25.4;   // 实际纸宽（英寸）
                ps.PageHeight = pageH;
                ps.MarginTop = 0; ps.MarginBottom = 0; ps.MarginLeft = 0; ps.MarginRight = 0;
                ps.ShouldPrintBackgrounds = true;

                var stream = await ctrl.CoreWebView2.PrintToPdfStreamAsync(ps);
                string dir = Path.Combine(AppPaths.DataDir, "exports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "小票-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".pdf");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.CopyTo(fs);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error("独立页打印小票 PDF 失败", ex);
                return null;
            }
            finally
            {
                try { if (ctrl != null) ctrl.Close(); }
                catch { }
                try { if (host != null) host.Close(); }
                catch { }
                try { if (host != null) host.Dispose(); }
                catch { }
            }
        }

    /// <summary>
    /// 等待 WebView2 初始化并页面加载后截图（无头探针用，须在 UI 线程调用）。
    /// 返回 res 文本：OK;指标 或 ERR:错误文本。
    /// 指标含 main 滚动盒尺寸（sh=scrollHeight/ch=clientHeight/sw=scrollWidth/cw=clientWidth）
    /// 与视口 ih/iw——用于测算各页竖向足迹、钉 MinimumSize。
    /// </summary>
    public async Task<string> WaitAndCaptureAsync(string pngPath, int timeoutSeconds)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (_web.CoreWebView2 == null && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            var core = _web.CoreWebView2;
            if (core == null) return "ERR:WebView2 初始化超时";

            // 等导航完成（.Source 稳定为 app.local 且再给渲染留 5 秒）
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                var src = core.Source ?? "";
                if (src.Contains("app.local")) break;
            }
            if (DateTime.UtcNow >= deadline) return "ERR:页面加载超时";
            await Task.Delay(5000);

            // 等页面文本稳定（连续两次一致且非空），避免截到过渡空白帧
            string last = null;
            while (DateTime.UtcNow < deadline)
            {
                var txt = await core.ExecuteScriptAsync("document.body.innerText.length");
                if (txt == last && txt != "0") break;
                last = txt;
                await Task.Delay(500);
            }

            // 先按首页内容钉最小窗口（窗口不够高会自动抬到临界值），再量指标
            await ApplyMinSizeAsync(0);
            await Task.Delay(400);

            // 布局指标：main 是唯一滚动容器（root h-screen overflow-hidden）
            string metrics = "";
            if (Program.ProbePage == "__all__")
            {
                // 逐页量指标：page:sw|cw|sh|ch|dxsw|dxcw —— sw>cw 即该页在当前窗口宽下横向溢出（被挤扁）
                string[] pages = { "dashboard", "pos", "projects", "records", "members",
                    "stored-value", "points", "staff", "reports", "permission", "backup", "settings" };
                var sb = new System.Text.StringBuilder();
                try
                {
                    foreach (string p in pages)
                    {
                        await core.ExecuteScriptAsync("location.hash='" + p + "'");
                        await Task.Delay(800);
                        var m = await core.ExecuteScriptAsync(
                            "(function(){var el=document.querySelector('main');var dx=document.documentElement;" +
                            "if(!el)return 'X';return el.scrollWidth+'|'+el.clientWidth+'|'+el.scrollHeight+'|'+el.clientHeight+" +
                            "'|'+dx.scrollWidth+'|'+dx.clientWidth;})()");
                        sb.Append(p).Append(":").Append(m.Trim('"')).Append(";");
                    }
                }
                catch { }
                metrics = ";" + sb.ToString();
            }
            else
            {
                try
                {
                    // sh/ch/sw/cw = main 容器；tw/tw2 = 页面内表格 wrapper 与其 table 的
                    // scrollWidth / clientWidth。tw > tw2 说明表格自身横向溢出（出现横条），
                    // 这是只看 main 看不出来的（表格是 main 内独立的横向滚动容器）。
                    var m = await core.ExecuteScriptAsync(
                        "(function(){var el=document.querySelector('main');var wr=document.querySelector('main table');" +
                        "var wc=wr?wr.parentElement:null;var ls={};for(var i=0;i<localStorage.length;i++){var k=localStorage.key(i);ls[k]=localStorage.getItem(k);}" +
                        "return JSON.stringify({sh:el?el.scrollHeight:-1,ch:el?el.clientHeight:-1," +
                        "sw:el?el.scrollWidth:-1,cw:el?el.clientWidth:-1,ih:window.innerHeight,iw:window.innerWidth," +
                        "tw:wc?wc.scrollWidth:-1,tw2:wc?wc.clientWidth:-1,ls:ls});})()");
                    metrics = ";" + m.Trim('"').Replace("\\\"", "\"");
                }
                catch { }
            }

            // 可选：点击含指定文本的按钮（打开弹窗等交互态），等弹窗动画完成。
            // 用 '|' 分隔可依次点多个目标（如 详情|编辑档案），每点一次都等动画走完。
            // 目标以 '!' 开头则改成预置本地存储（!key=value），用于验证「记住上次选择」这类本地状态。
            foreach (string one in Program.ProbeClickText.Split('|'))
            {
                string want = one.Trim();
                if (want.Length == 0) continue;

                if (want.StartsWith("!"))
                {
                    string[] kv = want.Substring(1).Split(new char[] { '=' }, 2);
                    if (kv.Length == 2)
                    {
                        string setJs = "(function(){localStorage.setItem(" +
                            Newtonsoft.Json.JsonConvert.SerializeObject(kv[0].Trim()) + "," +
                            Newtonsoft.Json.JsonConvert.SerializeObject(kv[1].Trim()) +
                            ");return '1';})()";
                        var setR = await core.ExecuteScriptAsync(setJs);
                        string setMsg = "探针预置本地存储 '" + kv[0].Trim() + "=" + kv[1].Trim() + "' : " + setR;
                        Logger.Info(setMsg);
                        await Task.Delay(300);
                    }
                    continue;
                }

                string js = "(function(){var t=" +
                            Newtonsoft.Json.JsonConvert.SerializeObject(want) +
                            ";var bs=document.querySelectorAll('button');for(var i=0;i<bs.length;i++){if(bs[i].textContent.indexOf(t)>=0){bs[i].click();return '1';}}return '0';})()";
                var clicked = await core.ExecuteScriptAsync(js);
                Logger.Info("探针点击 '" + want + "': " + clicked);
                await Task.Delay(1500);
            }

            // 点击后再采一次状态：本地存储快照 + 页面上每个下拉的选中值与选项清单，
            // 用来验证「默认上一次开单的人」这类依赖本地状态的默认值——不用靠眼看截图猜。
            string post = "";
            try
            {
                var pm = await core.ExecuteScriptAsync(
                    "(function(){var ls={};for(var i=0;i<localStorage.length;i++){var k=localStorage.key(i);ls[k]=localStorage.getItem(k);}" +
                    "var sels=[];var ss=document.querySelectorAll('select');for(var j=0;j<ss.length;j++){" +
                    "var op=[];for(var t=0;t<ss[j].options.length;t++){op.push(ss[j].options[t].value+':'+ss[j].options[t].textContent);}" +
                    "sels.push({v:ss[j].value,op:op});}" +
                    "return JSON.stringify({ls:ls,sels:sels});})()");
                post = ";" + pm.Trim('"').Replace("\\\"", "\"");
                Logger.Info("探针点击后状态 " + post.Substring(1));
            }
            catch { }

            using (var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }

            // 布局审计（QA）：在真实 WebView2 环境里量元素几何，程序化找排版问题。
            // 只报「用户能感知」的问题，宁少勿滥——误报比漏报更浪费时间。
            string audit = "";
            try
            {
                string auditJs = @"
(function(){
  var issues=[];
  function add(t,el,d){var r=el.getBoundingClientRect();
    issues.push({t:t,d:d,tag:el.tagName.toLowerCase(),
      cls:(el.className||'').toString().slice(0,80),
      txt:(el.innerText||el.textContent||'').trim().replace(/\s+/g,' ').slice(0,50),
      x:Math.round(r.x),y:Math.round(r.y),w:Math.round(r.width),h:Math.round(r.height)});}

  var all=document.querySelectorAll('body *');
  for(var i=0;i<all.length;i++){
    var el=all[i], cs=getComputedStyle(el);
    if(cs.display==='none'||cs.visibility==='hidden'||cs.opacity==='0') continue;
    var r=el.getBoundingClientRect();
    if(r.width<1||r.height<1) continue;

    // 1) 叶子文本被省略号截断（用户看不到完整信息）
    //    例外：挂了 title 的（hover 可看全文）属设计内的弹性列，不算问题。
    if(el.children.length===0){
      var txt=(el.textContent||'').trim();
      var hasTip=el.getAttribute('title')||(el.parentElement&&el.parentElement.getAttribute('title'));
      if(txt && cs.textOverflow==='ellipsis' && el.scrollWidth>el.clientWidth+1 && !hasTip){
        add('TEXT_CLIPPED',el,'sw='+el.scrollWidth+' cw='+el.clientWidth);
      }
      // 2) 文字行高过紧（挤压）
      var fs=parseFloat(cs.fontSize)||0;
      if(txt && fs>0 && r.height<fs*0.9 && cs.display!=='inline'){
        add('LINE_TIGHT',el,'h='+Math.round(r.height)+' fs='+fs);
      }
    }

    // 3) 元素超出视口右侧
    if(r.right>window.innerWidth+2) add('OUT_RIGHT',el,'right='+Math.round(r.right)+' vw='+window.innerWidth);
  }

  // 4) 同容器内控件高度不一致（按钮/输入框参差）
  var groups=new Map();
  var ctrls=document.querySelectorAll('button,input,select,textarea');
  for(var j=0;j<ctrls.length;j++){
    var c=ctrls[j], ccs=getComputedStyle(c);
    if(ccs.display==='none'||ccs.visibility==='hidden') continue;
    var p=c.parentElement; if(!p) continue;
    if(!groups.has(p)) groups.set(p,[]);
    groups.get(p).push(c);
  }
  groups.forEach(function(kids){
    if(kids.length<2) return;
    var hs=[]; for(var k=0;k<kids.length;k++){var h=Math.round(kids[k].getBoundingClientRect().height); if(h>0) hs.push(h);}
    if(hs.length<2) return;
    var mn=Math.min.apply(null,hs), mx=Math.max.apply(null,hs);
    if(mx-mn>4) add('CTRL_HEIGHT_DIFF',kids[0],'hs='+hs.join(','));
  });

  // 5) 弹窗专项
  var dlg=document.querySelector('[role=dialog]');
  var dlgInfo='none';
  if(dlg){
    var dr=dlg.getBoundingClientRect();
    var inner=dlg.firstElementChild;
    var ir=inner?inner.getBoundingClientRect():dr;
    dlgInfo=JSON.stringify({w:Math.round(dr.width),h:Math.round(dr.height),
      innerH:Math.round(ir.height),innerW:Math.round(ir.width),
      vpOver:dr.right>window.innerWidth+2||dr.bottom>window.innerHeight+2||dr.left<-2||dr.top<-2,
      contentOver:ir.height>dr.height+2});
    if(dr.right>window.innerWidth+2||dr.bottom>window.innerHeight+2) add('DIALOG_OUT_VP',dlg,'r='+Math.round(dr.right)+' b='+Math.round(dr.bottom));
    if(ir.height>dr.height+2) add('DIALOG_SCROLL',dlg,'innerH='+Math.round(ir.height)+' dlgH='+Math.round(dr.height));
  }

  return JSON.stringify({vp:window.innerWidth+'x'+window.innerHeight,
    docOver:document.documentElement.scrollWidth>document.documentElement.clientWidth+1,
    dlg:dlgInfo, n:issues.length, issues:issues.slice(0,20)});
})()
";
                var ar = await core.ExecuteScriptAsync(auditJs);
                audit = ";AUDIT:" + ar.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");
                Logger.Info("布局审计 " + audit);
            }
            catch (Exception ex) { audit = ";AUDIT:ERR " + ex.Message; }

            return "OK" + metrics + post + audit;
        }
        catch (Exception ex)
        {
            return "ERR:" + ex.Message;
        }
    }
    }
}
