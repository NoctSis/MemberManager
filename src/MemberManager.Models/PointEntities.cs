using System;

namespace MemberManager.Models
{
    /// <summary>积分流水（point_flows 表，只增不改）。</summary>
    public class PointFlow
    {
        public long Id { get; set; }                   // id
        public int MemberId { get; set; }              // member_id
        public int Change { get; set; }                // change 带符号，兑换为负
        public string Reason { get; set; }             // reason
        public int BalanceAfter { get; set; }          // balance_after
        public DateTime CreatedAt { get; set; }        // created_at
    }

    /// <summary>积分商城礼品（point_gifts 表）。</summary>
    public class PointGift
    {
        public int Id { get; set; }                    // id
        public string Name { get; set; }               // name
        public int Points { get; set; }                // points 所需积分
        public int Stock { get; set; }                 // stock >=0
    }

    /// <summary>礼品兑换单（gift_exchanges 表）。</summary>
    public class GiftExchange
    {
        public long Id { get; set; }                   // id
        public int MemberId { get; set; }              // member_id
        public int GiftId { get; set; }                // gift_id
        public int Points { get; set; }                // points 消耗积分
        public string OperatorName { get; set; }       // operator
        public DateTime CreatedAt { get; set; }        // created_at
    }
}
