using System.Drawing;

namespace MemberManager.Common
{
    /// <summary>
    /// 视觉规范单（《界面交接包》§1）落地。
    /// WinForms 端仅保留 Body 字体（ShellForm 默认字体）；
    /// 颜色/尺寸/圆角等常量已随 UI 迁移到 WebView2 + Tailwind，不再由 C# 持有。
    /// </summary>
    public static class Theme
    {
        public static readonly Font Body = new Font("微软雅黑", 10f, FontStyle.Regular); // 正文/表格
    }
}
