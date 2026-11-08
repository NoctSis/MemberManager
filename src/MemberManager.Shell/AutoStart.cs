using System;
using System.Windows.Forms;
using Microsoft.Win32;
using MemberManager.Common;

namespace MemberManager.Shell
{
    /// <summary>
    /// 开机自启动：写「当前用户」的 Run 键（HKCU），**不需要管理员权限**，
    /// 兼容 Windows 7 / 10 / 11，也不依赖安装程序（绿色便携版同样可用）。
    ///
    /// 设计要点：
    ///  · **注册表是唯一真源**，不落数据库 —— 避免「库里开着、注册表没开」的分裂状态；
    ///    设置页的开关读的就是这里。
    ///  · 便携版换目录/改盘符后，Run 键里的老路径会指向不存在的文件 ——
    ///    启动时 <see cref="Reconcile"/> 会把命令改写成当前 exe 位置（仅在已启用时）。
    ///  · 任何一步失败都只记日志、绝不抛异常影响启动。
    /// </summary>
    internal static class AutoStart
    {
        /// <summary>HKCU 下的自启键（普通权限可写，不触发 UAC）。</summary>
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Run 键下的值名；同一台机器只应存在一个我们的自启项。</summary>
        private const string ValueName = "MemberManager";

        /// <summary>要写进 Run 键的命令行：exe 路径带引号（路径可能含空格与中文）。</summary>
        private static string Command()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        /// <summary>当前是否已启用自启（以注册表为准）。异常一律按「未启用」处理，绝不卡启动。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var v = key.GetValue(ValueName) as string;
                    return !string.IsNullOrWhiteSpace(v);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取开机自启状态失败（按未启用处理）：" + ex.Message);
                return false;
            }
        }

        /// <summary>启用自启（幂等）。失败返回 false，不抛异常。</summary>
        public static bool Enable()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    key.SetValue(ValueName, Command(), RegistryValueKind.String);
                }
                Logger.Info("已启用开机自启：" + Command());
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("启用开机自启失败", ex);
                return false;
            }
        }

        /// <summary>关闭自启（幂等：键或值本来就不存在也算成功）。</summary>
        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return true;
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
                Logger.Info("已关闭开机自启");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("关闭开机自启失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 启动时校准：**仅在已启用时**把命令路径改写为当前 exe 位置；未启用则什么都不做
        /// （绝不擅自帮用户打开自启）。路径本来就正确时不写注册表。
        /// </summary>
        public static void Reconcile()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return;
                    var cur = key.GetValue(ValueName) as string;
                    if (string.IsNullOrWhiteSpace(cur)) return;    // 未启用 -> 保持未启用
                    if (cur == Command()) return;                   // 已指向当前 exe -> 无副作用

                    Logger.Warn("开机自启项路径已过期，改写为当前位置：" + cur + " -> " + Command());
                    using (var w = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (w != null) w.SetValue(ValueName, Command(), RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("校准开机自启失败（忽略）：" + ex.Message);
            }
        }
    }
}
