using System;

namespace MemberManager.Models
{
    /// <summary>日结记录（daily_settlements 表）。pay_snapshot 为各支付方式金额 JSON。</summary>
    public class DailySettlement
    {
        public long Id { get; set; }                   // id
        public string BizDate { get; set; }            // biz_date 唯一
        public decimal TotalRevenue { get; set; }      // total_revenue
        public string PaySnapshot { get; set; }        // pay_snapshot JSON
        public decimal CashSystem { get; set; }        // cash_system 账面现金
        public decimal CashActual { get; set; }        // cash_actual 实点现金
        public decimal CashDiff { get; set; }          // cash_diff 差异
        public string DiffReason { get; set; }         // diff_reason
        public string OperatorName { get; set; }       // operator
        public string Note { get; set; }               // note
        public DateTime SettledAt { get; set; }        // settled_at
    }

    /// <summary>交接班记录（handover_logs 表）。</summary>
    public class HandoverLog
    {
        public long Id { get; set; }                   // id
        public string FromStaff { get; set; }          // from_staff 移交人
        public string ToStaff { get; set; }            // to_staff 接班人
        public decimal CashDiff { get; set; }          // cash_diff
        public string Reason { get; set; }             // reason
        public DateTime CreatedAt { get; set; }        // created_at
    }

    /// <summary>提成明细（commissions 表）。type：充值/消费；settled 结算后不可撤销。</summary>
    public class Commission
    {
        public long Id { get; set; }                   // id
        public long OrderId { get; set; }              // order_id
        public int StaffId { get; set; }               // staff_id 提成归属（消费单=行技师，充值单=操作员）
        public string Type { get; set; }               // type
        public decimal Amount { get; set; }            // amount 业绩额
        public decimal Rate { get; set; }              // rate 比例（界面中文百分比展示）
        public decimal CommissionValue { get; set; }   // commission 提成金额
        public string Share { get; set; }              // share 默认 100%
        public bool Settled { get; set; }              // settled
        public DateTime CreatedAt { get; set; }        // created_at
    }
}
