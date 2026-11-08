using System;

namespace MemberManager.Models
{
    /// <summary>会员（members 表）。卡号 VIP0001 起顺序发号。</summary>
    public class Member
    {
        public int Id { get; set; }                    // id
        public string CardNo { get; set; }             // card_no
        public string Name { get; set; }               // name
        public string Phone { get; set; }              // phone（可空）
        public string Level { get; set; }              // level 默认 普卡
        public string Tags { get; set; }               // tags 逗号分隔
        public decimal Balance { get; set; }           // balance
        public int Points { get; set; }                // points
        public string Status { get; set; }             // status：正常/冻结/挂失
        public string Birthday { get; set; }           // birthday（MM-dd）
        public string Remark { get; set; }             // remark
        public DateTime CreatedAt { get; set; }        // created_at
    }

    /// <summary>会员资金流水（member_flows 表，触发器保护：只增不改不删）。</summary>
    public class MemberFlow
    {
        public long Id { get; set; }                   // id
        public int MemberId { get; set; }              // member_id
        public string BizDate { get; set; }            // biz_date 所属营业日 yyyy-MM-dd
        public string Type { get; set; }               // type：充值/赠送/消费/退款/调整/红冲
        public decimal Amount { get; set; }            // amount 带符号：入账为正、扣减为负
        public decimal BalanceAfter { get; set; }      // balance_after 本笔后余额
        public int? OrderId { get; set; }              // order_id
        public string OperatorName { get; set; }       // operator
        public string Note { get; set; }               // note
        public DateTime CreatedAt { get; set; }        // created_at
    }
}
