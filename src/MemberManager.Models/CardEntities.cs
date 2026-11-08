using System;

namespace MemberManager.Models
{
    /// <summary>次卡模板（card_templates 表）。</summary>
    public class CardTemplate
    {
        public int Id { get; set; }                    // id
        public string Name { get; set; }               // name
        public string ItemName { get; set; }           // item_name 对应服务项目
        public int Times { get; set; }                 // times 总次数
        public decimal Price { get; set; }             // price
        public int ValidMonths { get; set; }           // valid_months 默认 12
        public bool Enabled { get; set; }              // enabled 在售
    }

    /// <summary>会员卡（次卡实例，member_cards 表）。status：正常/已用完/已过期/冻结。</summary>
    public class MemberCard
    {
        public long Id { get; set; }                   // id
        public int MemberId { get; set; }              // member_id
        public int TemplateId { get; set; }            // template_id
        public int Total { get; set; }                 // total 总次数
        public int Remain { get; set; }                // remain 剩余次数 >=0
        public string ExpireDate { get; set; }         // expire_date yyyy-MM-dd
        public string Status { get; set; }             // status
    }

    /// <summary>次卡核销记录（card_uses 表）。</summary>
    public class CardUse
    {
        public long Id { get; set; }                   // id
        public long MemberCardId { get; set; }         // member_card_id
        public long? OrderId { get; set; }             // order_id
        public int RemainAfter { get; set; }           // remain_after 核销后剩余
        public string OperatorName { get; set; }       // operator
        public DateTime CreatedAt { get; set; }        // created_at
    }
}
