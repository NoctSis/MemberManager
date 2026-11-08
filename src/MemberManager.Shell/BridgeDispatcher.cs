using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MemberManager.BLL;
using MemberManager.Common;
using Newtonsoft.Json.Linq;

namespace MemberManager.Shell
{
    /// <summary>
    /// JS 桥分发器：把前端请求 {id, svc, method, args[]} 反射路由到 BLL。
    /// 服务与前端页面一一对应，返回 {id, ok, data|error}。
    /// BLL 全部无状态，按需实例化；异常统一转 error 文本，不跨桥抛出。
    /// </summary>
    public class BridgeDispatcher
    {
        private static readonly Dictionary<string, Type> Services = new Dictionary<string, Type>
        {
            { "auth", typeof(AuthService) },
            { "members", typeof(MemberService) },
            { "dashboard", typeof(DashboardService) },
            { "pos", typeof(PosService) },
            { "system", typeof(SystemInfoService) },
            { "projects", typeof(ProjectService) },
            { "storedvalue", typeof(StoredValueService) },
            { "cards", typeof(CardService) },
            { "shift", typeof(ShiftService) },
            { "commission", typeof(CommissionService) },
            { "perm", typeof(PermissionService) },
            { "backup", typeof(BackupService) },
            { "settings", typeof(SettingsService) },
            { "logs", typeof(LogService) },
            { "points", typeof(PointService) },
            { "levels", typeof(LevelService) },
            { "reports", typeof(ReportService) },
            { "records", typeof(RecordService) },
        };

        /// <summary>
        /// M10（店主 10-18）敏感写操作 → 所需权限模块。命中即在 <see cref="Handle"/> 里
        /// 调 <c>PermissionService.RequireModule(module)</c> 做服务端二次校验。
        /// 前端 UI 已按权限隐藏入口，但接口可被脚本直调越权——这里用服务端 Session
        /// （真登录身份，不信前端 operator）再拦一道。模块归属与前端 perm.ts MODULE_OF 对齐。
        /// 键 = "svc.method"，value = 后端 10 项权限模块之一（见 PermissionService.ModuleNames）。
        /// 破坏性写（Refund/DeleteMember/WriteOffBalance）模块值只是第二道，其自身另有 IsManager 硬兜底。
        /// </summary>
        private static readonly Dictionary<string, string> SensitiveWrites =
            new Dictionary<string, string>
        {
            // 会员管理
            { "members.CreateMember", "会员管理" },
            { "members.ChangeStatus", "会员管理" },
            { "members.UpdateMember", "会员管理" },
            { "members.WriteOffBalance", "会员管理" },
            { "members.DeleteMember", "会员管理" },
            // 收银开单（含充值、售卡）：店主 11-05 定「关于充值的收银员一定要有」
            // 「售卡也属于收银类」——会员充值、卖次卡都是收银台的交易动作，不是储值管理。
            // 全部归「收银开单」：能进收银台就一定能充值、卖卡，店主按人关掉
            // 「储值与次卡」也不会把这两个日常动作一起关掉。
            { "pos.HoldOrder", "收银开单" },
            { "pos.TakeOrder", "收银开单" },
            { "pos.VoidHeldOrder", "收银开单" },
            { "storedvalue.Recharge", "收银开单" },
            { "storedvalue.RechargeByStaff", "收银开单" },
            { "cards.SellCard", "收银开单" },
            // 11-05（店主定）：收银员默认仍拿到「储值与次卡」（角色默认与老库迁移保留），
            // 但方案、卡模板、次卡冻结解冻、项目价目维护都是店长管理动作，一律改绑
            // 「系统设置」（收银员默认无），防止绕过前端直调管理接口。
            // 前端 projects / stored-value 两页同时保持 MANAGER_ONLY，双保险。
            { "storedvalue.SavePackage", "系统设置" },
            { "storedvalue.DeletePackage", "系统设置" },
            { "cards.SaveTemplate", "系统设置" },
            { "cards.SetCardStatus", "系统设置" },
            { "projects.SaveItem", "系统设置" },
            { "projects.SetEnabled", "系统设置" },
            { "projects.DeleteItem", "系统设置" },
            // 退款 / 改价（店长专属；模块值第二道，Refund 自身另有 IsManager 硬兜底）
            { "storedvalue.Refund", "退款/改价" },
            // 报表导出（导出文件类）
            { "storedvalue.ExportRecords", "报表导出" },
            { "storedvalue.ExportRecordsRange", "报表导出" },
            { "commission.ExportCommissionExcel", "报表导出" },
            { "reports.ExportStaffDetailExcel", "报表导出" },
            // 员工提成（结算 / 比例 / 模式 / 逐项）
            { "commission.SettleByStaff", "员工提成" },
            { "commission.SettleAll", "员工提成" },
            { "commission.SaveRates", "员工提成" },
            { "commission.SaveCardCommissionMode", "员工提成" },
            { "commission.SaveStaffItemRates", "员工提成" },
            // 权限设置（账号 / 权限矩阵 / 密码 / 停用）
            { "perm.SaveStaff", "权限设置" },
            { "perm.DeleteStaff", "权限设置" },
            { "perm.SaveAccount", "权限设置" },
            { "perm.SetPerm", "权限设置" },
            { "perm.SetAccountPerms", "权限设置" },
            { "perm.ToggleStaff", "权限设置" },
            { "perm.ResetPwd", "权限设置" },
            // 数据备份（备份 / 恢复 / 删除 / 自动备份设置）
            { "backup.BackupNow", "数据备份" },
            { "backup.Restore", "数据备份" },
            { "backup.Delete", "数据备份" },
            { "backup.SetAuto", "数据备份" },
            { "backup.AutoTick", "数据备份" },
            // 店主 11-05：按勾选导出全库数据、从任意文件导入 —— 都是数据备份级操作
            { "backup.ExportSelected", "数据备份" },
            { "backup.ExportVersions", "数据备份" },
            { "backup.Import", "数据备份" },
            { "backup.Rotate", "数据备份" },
            // 系统设置（系统参数 / 店名店号 / 积分规则与兑换 / 等级制度）
            { "settings.Save", "系统设置" },
            // 店主 11-05：日志清理是删数据（只删操作日志，不碰业务流水），归系统设置
            { "logs.Purge", "系统设置" },
            { "system.SaveShop", "系统设置" },
            { "points.SavePointConfig", "系统设置" },
            // 店主 10-24：兑换是商城核心动作，归「积分商城」模块（收银员可做）；
            // 规则/礼品/手工调整仍属「系统设置」（店长专属）。
            { "points.Exchange", "积分商城" },
            { "points.SaveGift", "系统设置" },
            { "points.DeleteGift", "系统设置" },
            { "points.AdjustPoints", "系统设置" },
            { "levels.Save", "系统设置" },
            { "levels.Delete", "系统设置" },
        };

        private readonly System.Windows.Forms.Control _ui;

        public BridgeDispatcher(System.Windows.Forms.Control ui) { _ui = ui; }

        public string Handle(string reqJson)
        {
            long id = -1;
            try
            {
                var req = JObject.Parse(reqJson);
                id = req.Value<long?>("id") ?? -1;
                string svc = req.Value<string>("svc") ?? "";
                string method = req.Value<string>("method") ?? "";
                var args = req["args"] as JArray ?? new JArray();

                // 打印是「壳层」功能（必须操作 WebView2 控件本体，不走 BLL），单独路由到 ShellForm。
                // ShowPrintUI 必须在 UI 线程调用；Handle 跑在线程池，故用 Invoke 切回 UI 线程。
                if (svc == "shell" && method == "Print")
                {
                    var form = _ui as ShellForm;
                    if (form != null) _ui.Invoke(new Action(form.PrintReceipt));
                    return new JObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["data"] = JValue.CreateNull(),
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                // 开机自启同样是「壳层」功能：读写 HKCU 的 Run 键，不经过 BLL（不需要管理员权限）。
                if (svc == "shell" && method == "GetAutostart")
                {
                    return new JObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["data"] = AutoStart.IsEnabled(),
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (svc == "shell" && method == "SetAutostart")
                {
                    bool on = args.Count > 0 && args[0].Type == JTokenType.Boolean && args[0].Value<bool>();
                    bool ok = on ? AutoStart.Enable() : AutoStart.Disable();
                    if (!ok) return Err(id, "设置开机自启动失败，请查看运行日志");
                    return new JObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["data"] = AutoStart.IsEnabled(),   // 回读注册表实际状态，不让前端凭猜
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                Type type;
                if (!Services.TryGetValue(svc, out type))
                    return Err(id, "未知服务：" + svc);

                // 用 GetAllMethods 避免 GetMethod 的 5 参重载对 null types 数组的校验
                var allMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                MethodBase mi = null;
                for (int i = 0; i < allMethods.Length; i++)
                {
                    if (allMethods[i].Name == method && !allMethods[i].IsSpecialName)
                    {
                        mi = allMethods[i];
                        break;
                    }
                }
                if (mi == null)
                {
                    var allNames = string.Join(",", type
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => m.Name).Distinct().ToArray());
                    Logger.Warn("桥找不到方法 id=" + id + " svc=" + svc + " method=" + method +
                        " | 可用: " + allNames);
                    return Err(id, "未知方法：" + svc + "." + method +
                        "（可用：" + allNames + "）");
                }

                var ps = mi.GetParameters();
                if (ps.Length != args.Count)
                    return Err(id, "参数个数不匹配：" + svc + "." + method +
                        "（期望 " + ps.Length + "，实际 " + args.Count + "）");

                var argv = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    // 强转：string/int/decimal/bool 直接取值；对象/数组走 ToObject
                    var paramType = ps[i].ParameterType;
                    var token = args[i];
                    if (paramType == typeof(string))
                        argv[i] = token.Type == JTokenType.Null ? null : (string)token;
                    else if (paramType == typeof(int))
                        argv[i] = token.Type == JTokenType.Null ? 0 : (int)token;
                    else if (paramType == typeof(decimal))
                        argv[i] = token.Type == JTokenType.Null ? 0m : (decimal)token;
                    else if (paramType == typeof(bool))
                        argv[i] = token.Type == JTokenType.Null ? false : (bool)token;
                    else if (paramType == typeof(double))
                        argv[i] = token.Type == JTokenType.Null ? 0.0 : (double)token;
                    else if (paramType == typeof(long))
                        argv[i] = token.Type == JTokenType.Null ? 0L : (long)token;
                    else
                        argv[i] = token.ToObject(paramType);
                }

                object inst = mi.IsStatic ? null : Activator.CreateInstance(type);

                // M10：敏感写操作的服务端权限校验。用服务端 Session（真登录身份，不信前端传的 operator），
                // 拦「已登录的非店长账号绕过前端 UI、直接构造脚本调敏感写接口」。
                // 未登录 / 店长在 RequireModule 内放行（冒烟反射直调 BLL 不经本 Bridge；后台自动调度亦不登录）。
                string neededModule;
                if (SensitiveWrites.TryGetValue(svc + "." + method, out neededModule))
                    new PermissionService().RequireModule(neededModule);

                // Pick* 系列（PickFolder / PickSaveFile / PickOpenFile）
                // 都要在 UI 线程弹 WinForms 对话框，否则跨线程会抛 InvalidOperationException
                // system.Restart 同理：Application.Exit 必须在 UI 线程执行，否则窗体和 WebView2 退不干净
                bool needUiThread = method.StartsWith("Pick", StringComparison.Ordinal)
                    || (svc == "system" && method == "Restart");
                if (needUiThread && _ui != null && _ui.InvokeRequired)
                {
                    object uiResult = _ui.Invoke(new Func<object>(() => mi.Invoke(inst, argv)));
                    return new JObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["data"] = uiResult == null ? JValue.CreateNull() : JToken.FromObject(uiResult),
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }
                
                var result = mi.Invoke(inst, argv);

                return new JObject
                {
                    ["id"] = id,
                    ["ok"] = true,
                    ["data"] = result == null ? JValue.CreateNull() : JToken.FromObject(result),
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (TargetInvocationException tie)
            {
                var real = tie.InnerException ?? tie;
                Logger.Error("桥调用失败 id=" + id, real);
                try { System.IO.File.AppendAllText(
                    System.IO.Path.Combine(MemberManager.Common.Logger.LogDir, "stack_" + id + ".txt"),
                    real.ToString() + "\r\n", new System.Text.UTF8Encoding(false)); } catch { }
                return Err(id, real.Message);
            }
            catch (Exception ex)
            {
                Logger.Error("桥调用失败 id=" + id, ex);
                try { System.IO.File.AppendAllText(
                    System.IO.Path.Combine(MemberManager.Common.Logger.LogDir, "stack_" + id + ".txt"),
                    ex.ToString() + "\r\n", new System.Text.UTF8Encoding(false)); } catch { }
                return Err(id, ex.Message);
            }
        }

        private static string Err(long id, string message)
        {
            return new JObject { ["id"] = id, ["ok"] = false, ["error"] = message }
                .ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
