using System;

namespace MemberManager.Models
{
    /// <summary>服务/商品项目（service_items 表）。含按项目配置的消费提成比例（变更单 #2）。</summary>
    public class ServiceItem
    {
        public int Id { get; set; }                    // id
        public string Name { get; set; }               // name
        public string Category { get; set; }           // category：美发/美容/商品
        public decimal Price { get; set; }             // price
        public string Unit { get; set; }               // unit 默认 次
        public bool IsCardItem { get; set; }           // is_card_item 是否次卡项目
        public bool Enabled { get; set; }              // enabled
        public decimal CommissionRate { get; set; }    // commission_rate（变更单 #2，默认 0.05）
    }

    /// <summary>单据（orders 表）。kind：消费/充值/售卡；status：正常/挂单/已退款。</summary>
    public class Order
    {
        public long Id { get; set; }                   // id
        public string OrderNo { get; set; }            // order_no：D日期-序号 / C充值 / H挂单
        public string BizDate { get; set; }            // biz_date
        public int? MemberId { get; set; }             // member_id（NULL=散客）
        public int StaffId { get; set; }               // staff_id 开单人
        public decimal Subtotal { get; set; }          // subtotal 小计
        public decimal CardCovered { get; set; }       // card_covered 次卡抵扣合计
        public decimal Receivable { get; set; }        // receivable 应收
        public string Kind { get; set; }               // kind
        public string Status { get; set; }             // status
        public DateTime CreatedAt { get; set; }        // created_at
    }

    /// <summary>单据行（order_items 表）。含行级服务技师（变更单 #3，可空）。</summary>
    public class OrderItem
    {
        public long Id { get; set; }                   // id
        public long OrderId { get; set; }              // order_id
        public string ItemName { get; set; }           // item_name
        public decimal Price { get; set; }             // price
        public int Qty { get; set; }                   // qty
        public bool ByCard { get; set; }               // by_card 次卡抵扣
        public int? StaffId { get; set; }              // staff_id 该行服务技师（变更单 #3，可空）
    }

    /// <summary>支付方式明细（payments 表）。method：会员余额/现金/次卡抵扣/微信/支付宝。</summary>
    public class Payment
    {
        public long Id { get; set; }                   // id
        public long OrderId { get; set; }              // order_id
        public string Method { get; set; }             // method
        public decimal Amount { get; set; }            // amount >=0
    }
}
