using System;

namespace MemberManager.Common
{
    /// <summary>统一格式化：金额、营业日换算（日切点规则）。</summary>
    public static class Fmt
    {
        /// <summary>金额带人民币符号。</summary>
        public static string MoneyYuan(decimal v)
        {
            return "¥" + v.ToString("N2");
        }

        /// <summary>
        /// 营业日换算：日切点（如 02:00）之前的时刻归属前一营业日。
        /// 例：02:00 切点下，09-22 01:30 → 09-21；09-22 02:00:00 → 09-22。
        /// </summary>
        public static string BizDateOf(DateTime moment, int cutHour)
        {
            DateTime shifted = moment.AddHours(-cutHour);
            return shifted.ToString("yyyy-MM-dd");
        }

        /// <summary>解析日切点设置值（"02:00" → 2）。非法值回退 2。</summary>
        public static int ParseCutHour(string dayCut)
        {
            if (string.IsNullOrEmpty(dayCut)) return 2;
            string[] parts = dayCut.Split(':');
            int h;
            if (parts.Length > 0 && int.TryParse(parts[0], out h) && h >= 0 && h <= 23) return h;
            return 2;
        }
    }
}
