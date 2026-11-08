namespace MemberManager.Common
{
    /// <summary>
    /// C# 端仍在使用的界面文案常量（简体中文）。界面禁止出现英文。
    /// 说明：UI 已迁移到 WebView2 + React，前端文案硬编码在 tsx；
    /// 本类只保留 BLL 校验消息 / Shell 外壳仍引用的常量（36 个）。
    /// 新增 C# 端文案请加到这里；纯前端文案直接写 tsx。
    /// </summary>
    public static class UiText
    {
        // ---- 通用 ----
        public const string AppName = "收银系统";

        // ---- 登录 ----
        public const string LoginErrorWrongPwd = "密码错误，请重试";
        public const string LoginErrorDisabled = "该账号已停用";
        public const string LoginErrorEmpty = "请输入密码";

        // ---- 主导航 ----
        public const string NavDashboard = "主工作台";
        public const string NavPos = "收银开单";
        public const string NavMembers = "会员管理";
        public const string NavSettings = "系统设置";

        // ---- 状态栏 ----
        public const string StatusBackupNone = "自动备份：暂无备份";
        public const string StatusBackupPrefix = "自动备份：";

        // ---- 主工作台 ----
        public const string DashSleepSuffix = " 天未到店";
        public const string DashNeverVisit = "从未到店";

        // ---- 会员管理 ----
        public const string MemberFilterAllStatus = "全部状态";
        public const string MemberErrName = "姓名不能为空";
        public const string MemberErrPhoneFormat = "手机号必须为 11 位数字且以 1 开头";
        public const string MemberErrPhoneDup = "该手机号已存在，请走合并/余额转移流程";
        public const string MemberErrAmount = "首次充值金额必须为非负数字";
        public const string MemberErrBirthday = "生日格式应为 MM-dd 或 YYYY-MM-DD，且不能晚于今天";
        public const string MemberErrCardNo = "卡号已被占用，请重试";
        public const string MemberActionFreeze = "冻结";
        public const string MemberActionUnfreeze = "解冻";
        public const string MemberActionReportLoss = "挂失";
        public const string MemberActionUnloss = "解挂";
        public const string MemberActionEdit = "编辑";

        // ---- 收银开单 ----
        public const string PosWalkIn = "散客";
        public const string PosErrBalanceShort = "余额不足：{0} 当前余额 {1}，本项最多支付 {2}";
        public const string PosErrCashShort = "实收不足：还差 {0}，请补全「余额 / 现金」金额";
        public const string PosErrCardShort = "次卡余次不足：本单需 {0} 次，当前仅剩 {1} 次";
        public const string PosErrCartEmpty = "账单为空，请先选择服务项目";
        public const string PosErrQty = "数量必须为正整数";
        public const string PosErrMemberStatus = "该会员状态异常（{0}），不可开单";
        public const string PosErrWalkInBalance = "散客无会员余额，余额支付金额必须为 0";
        public const string PosErrWalkInCard = "散客无次卡，不可使用次卡抵扣";
        public const string PosErrItemDisabled = "项目「{0}」已停用";
        public const string PosErrHoldEmpty = "挂单失败：账单为空";
        public const string PosErrHeldNotFound = "挂单不存在或已取单";
        public const string PosErrHeldVoided = "这张挂单已作废，不能再取单或结账";

        /// <summary>op_logs 的 logs_no_delete 触发器提示语。DDL 里是硬编码同一句，
        /// 日志清理（LogService.Purge）重建触发器时用这里，保证两处文案一致。</summary>
        public const string LogNoDelete = "日志禁止删除";
    }
}
