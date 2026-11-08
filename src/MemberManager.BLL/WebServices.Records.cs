using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 桥接服务集（四）：消费记录（全局单据查询）。
    /// 设计目的：店主需要「逐笔可查」而不是只看汇总数字——
    /// 有了这一页，日结就没有存在价值（日结唯一多出的是人工盘现金的字段）。
    /// 只读查询，不写库。
    /// </summary>
    public class RecordService
    {
        public class OrderRecord
        {
            public int Id;
            public string OrderNo;
            public string Time;
            public string BizDate;
            public string MemberName;    // 散客单显示「散客」
            public string MemberCard;
            public string Level;         // 会员等级（散客为空）
            public string Kind;          // 消费 / 充值 / 售卡
            public decimal Subtotal;
            public decimal CardCovered;  // 次卡抵扣
            public decimal Receivable;   // 应收
            public string Items;         // 项目明细，如「洗剪吹×1、头部护理×1」
            public string Pays;          // 支付方式，如「会员余额¥58.00 + 现金¥100.00」
            public string Operator;      // 开单人
            public string Status;        // 正常 / 挂单 / 已退款
        }

        public class Totals
        {
            public int Count;
            public decimal Receivable;
            public decimal CardCovered;
            public decimal Balance;   // 会员余额支付
            public decimal Cash;      // 现金
            public decimal Other;     // 微信 / 支付宝 等
        }

        public class QueryResult
        {
            public List<OrderRecord> Rows = new List<OrderRecord>();
            public Totals Total = new Totals();
        }

        public class ItemRow
        {
            public string ItemName;
            public decimal Price;
            public int Qty;
            public bool ByCard;
            public string Staff;     // 谁做的（order_items.staff_id）
        }

        public class PayRow
        {
            public string Method;
            public decimal Amount;
        }

        public class OrderDetail
        {
            public int Id;
            public string OrderNo;
            public string Time;
            public string MemberName;
            public string Kind;
            public decimal Subtotal;
            public decimal CardCovered;
            public decimal Receivable;
            public string Status;
            public List<ItemRow> Items = new List<ItemRow>();
            public List<PayRow> Pays = new List<PayRow>();
        }

        private const string FromSql =
            " FROM orders o" +
            " LEFT JOIN members m ON m.id=o.member_id" +
            " LEFT JOIN staff s ON s.id=o.staff_id";

        /// <summary>
        /// 拼接筛选条件。全部走参数化，杜绝 SQL 注入。
        /// </summary>
        private static string BuildWhere(
            string fromDate, string toDate, string keyword,
            string kind, string status, string operatorName,
            string level, string customer,
            SQLiteCommand cmd)
        {
            var w = new List<string>();
            // 作废的挂单不再出现在消费记录里（店主 11-05：「清空」= 彻底作废）。
            // 行仍留在库中，只在查询层过滤；DDL 冻结，status CHECK 里塞不进「作废」。
            w.Add("IFNULL(o.voided_at,'')=''");

            if (!string.IsNullOrWhiteSpace(fromDate))
            {
                w.Add("o.biz_date>=@f");
                AddP(cmd, "@f", fromDate.Trim());
            }
            if (!string.IsNullOrWhiteSpace(toDate))
            {
                w.Add("o.biz_date<=@t");
                AddP(cmd, "@t", toDate.Trim());
            }
            string kw = (keyword ?? "").Trim();
            if (kw.Length > 0)
            {
                w.Add("(m.name LIKE @k OR IFNULL(m.phone,'') LIKE @k OR IFNULL(m.card_no,'') LIKE @k OR o.order_no LIKE @k)");
                AddP(cmd, "@k", "%" + kw + "%");
            }
            if (!string.IsNullOrWhiteSpace(kind) && kind != "全部类型")
            {
                w.Add("o.kind=@kind");
                AddP(cmd, "@kind", kind.Trim());
            }
            if (!string.IsNullOrWhiteSpace(status) && status != "全部状态")
            {
                w.Add("o.status=@st");
                AddP(cmd, "@st", status.Trim());
            }
            if (!string.IsNullOrWhiteSpace(operatorName) && operatorName != "全部员工")
            {
                w.Add("IFNULL(s.name,'')=@op");
                AddP(cmd, "@op", operatorName.Trim());
            }
            if (!string.IsNullOrWhiteSpace(level) && level != "全部等级")
            {
                w.Add("m.level=@lv");
                AddP(cmd, "@lv", level.Trim());
            }
            string cust = (customer ?? "").Trim();
            if (cust == "散客")
            {
                w.Add("o.member_id IS NULL");
            }
            else if (cust == "会员")
            {
                w.Add("o.member_id IS NOT NULL");
            }
            return w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w.ToArray());
        }

        private static void AddP(SQLiteCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        /// <summary>
        /// 店主 11-05：「挂单暂时不算就行了」「计算逻辑还是要改为结账后」。
        /// 口径 = **明细照常列，合计只算结账后**：
        ///   · 列表里挂单照常出现（收银员能在消费记录里找到它，status 列写着「挂单」）；
        ///   · 合计（笔数 / 应收 / 支付方式拆分）一律不含挂单 —— 挂单没收过钱，算进去就是虚账，
        ///     还会让「N 笔 × 金额」和实际收银对不上。
        /// 店主主动把状态筛成「挂单」时照原样算 —— 那是他专门在看挂单。
        /// </summary>
        private static string HideHeld(string where, string status)
        {
            if (!string.IsNullOrWhiteSpace(status) && status != "全部状态") return where;
            return where.Length == 0 ? " WHERE o.status<>'挂单'" : where + " AND o.status<>'挂单'";
        }

        /// <summary>全局单据查询（含汇总）。只读。Rows 按 offset/limit 分页，Total 是命中筛选条件的全量汇总（不受分页影响）。</summary>
        public QueryResult Query(
            string fromDate, string toDate, string keyword,
            string kind, string status, string operatorName,
            string level, string customer, int limit, int offset)
        {
            if (limit <= 0 || limit > 2000) limit = 200;
            if (offset < 0) offset = 0;
            var res = new QueryResult();

            using (var conn = Db.OpenConnection())
            {
                // ---- 明细 ----
                using (var cmd = conn.CreateCommand())
                {
                    string where = BuildWhere(fromDate, toDate, keyword, kind, status, operatorName, level, customer, cmd);
                    cmd.CommandText =
                        "SELECT o.id,o.order_no,o.created_at,o.biz_date," +
                        " IFNULL(m.name,'散客'),IFNULL(m.card_no,''),IFNULL(m.level,'')," +
                        " o.kind,o.subtotal,o.card_covered,o.receivable," +
                        " (SELECT IFNULL(GROUP_CONCAT(oi.item_name||'×'||oi.qty,'、'),'') FROM order_items oi WHERE oi.order_id=o.id)," +
                        " (SELECT IFNULL(GROUP_CONCAT(p.method||'¥'||printf('%.2f',p.amount),' + '),'') FROM payments p WHERE p.order_id=o.id)," +
                        " IFNULL(s.name,'—')," +
                        " o.status" +
                        FromSql + where +
                        " ORDER BY o.id DESC LIMIT @lim OFFSET @off";
                    AddP(cmd, "@lim", limit);
                    AddP(cmd, "@off", offset);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            res.Rows.Add(new OrderRecord
                            {
                                Id = r.GetInt32(0),
                                OrderNo = r.GetString(1),
                                Time = r.GetString(2),
                                BizDate = r.GetString(3),
                                MemberName = r.GetString(4),
                                MemberCard = r.GetString(5),
                                Level = r.GetString(6),
                                Kind = r.GetString(7),
                                Subtotal = Convert.ToDecimal(r.GetValue(8)),
                                CardCovered = Convert.ToDecimal(r.GetValue(9)),
                                Receivable = Convert.ToDecimal(r.GetValue(10)),
                                Items = r.IsDBNull(11) ? "" : r.GetString(11),
                                Pays = r.IsDBNull(12) ? "" : r.GetString(12),
                                Operator = r.GetString(13),
                                Status = r.GetString(14),
                            });
                        }
                    }
                }

                // ---- 汇总 ----
                // M4（店主 10-14）：钱必须走实收口径——挂单未收款、已退款不计，
                // 与支付方式拆分、日结、业绩同口径，恒守「应收合计=支付合计」。
                // 笔数仍跟筛选条件走（描述列表本身），不受实收口径影响。
                // 注意：BuildWhere 会把筛选参数挂到传入的 cmd 上，每个查询必须用自己的 cmd 调一次。
                using (var cmd = conn.CreateCommand())
                {
                    string w = BuildWhere(fromDate, toDate, keyword, kind, status, operatorName, level, customer, cmd);
                    // 合计按结账后算（明细照常列，见 HideHeld 注释）：笔数与金额、支付拆分同口径，
                    // 否则会出现「12 笔 / 少一笔的钱」的错位。
                    cmd.CommandText = "SELECT COUNT(*)" + FromSql + HideHeld(w, status);
                    res.Total.Count = Convert.ToInt32(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    string w = BuildWhere(fromDate, toDate, keyword, kind, status, operatorName, level, customer, cmd);
                    string whereMoney = w.Length == 0 ? " WHERE o.status='正常'" : w + " AND o.status='正常'";
                    cmd.CommandText = "SELECT IFNULL(SUM(o.receivable),0),IFNULL(SUM(o.card_covered),0)"
                        + FromSql + whereMoney;
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            res.Total.Receivable = Convert.ToDecimal(r.GetValue(0));
                            res.Total.CardCovered = Convert.ToDecimal(r.GetValue(1));
                        }
                    }
                }

                // ---- 支付方式拆分 ----
                using (var cmd = conn.CreateCommand())
                {
                    string where2 = BuildWhere(fromDate, toDate, keyword, kind, status, operatorName, level, customer, cmd);
                    cmd.CommandText =
                        "SELECT p.method,IFNULL(SUM(p.amount),0) FROM payments p" +
                        " JOIN orders o ON o.id=p.order_id" +
                        " LEFT JOIN members m ON m.id=o.member_id" +
                        " LEFT JOIN staff s ON s.id=o.staff_id" +
                        (where2.Length == 0 ? " WHERE o.status='正常'" : where2 + " AND o.status='正常'") +
                        " GROUP BY p.method";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string method = r.GetString(0);
                            decimal amt = Convert.ToDecimal(r.GetValue(1));
                            if (method == "会员余额") res.Total.Balance += amt;
                            else if (method == "现金") res.Total.Cash += amt;
                            else res.Total.Other += amt;
                        }
                    }
                }
            }
            return res;
        }

        /// <summary>单据详情：项目明细（含谁做的）+ 支付方式。</summary>
        public OrderDetail Detail(int orderId)
        {
            var d = new OrderDetail();
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT o.id,o.order_no,o.created_at,IFNULL(m.name,'散客'),o.kind," +
                        " o.subtotal,o.card_covered,o.receivable,o.status" +
                        FromSql + " WHERE o.id=@id";
                    AddP(cmd, "@id", orderId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return d;
                        d.Id = r.GetInt32(0);
                        d.OrderNo = r.GetString(1);
                        d.Time = r.GetString(2);
                        d.MemberName = r.GetString(3);
                        d.Kind = r.GetString(4);
                        d.Subtotal = Convert.ToDecimal(r.GetValue(5));
                        d.CardCovered = Convert.ToDecimal(r.GetValue(6));
                        d.Receivable = Convert.ToDecimal(r.GetValue(7));
                        d.Status = r.GetString(8);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT oi.item_name,oi.price,oi.qty,oi.by_card,IFNULL(s.name,'—')" +
                        " FROM order_items oi LEFT JOIN staff s ON s.id=oi.staff_id" +
                        " WHERE oi.order_id=@id ORDER BY oi.id";
                    AddP(cmd, "@id", orderId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            d.Items.Add(new ItemRow
                            {
                                ItemName = r.GetString(0),
                                Price = Convert.ToDecimal(r.GetValue(1)),
                                Qty = r.GetInt32(2),
                                ByCard = Convert.ToInt32(r.GetValue(3)) == 1,
                                Staff = r.GetString(4),
                            });
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT method,amount FROM payments WHERE order_id=@id ORDER BY id";
                    AddP(cmd, "@id", orderId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            d.Pays.Add(new PayRow
                            {
                                Method = r.GetString(0),
                                Amount = Convert.ToDecimal(r.GetValue(1)),
                            });
                    }
                }
            }
            return d;
        }

        /// <summary>会员等级去重列表（用于「会员等级」下拉；按常见优先级排）。</summary>
        public List<string> Levels()
        {
            var list = new List<string>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT DISTINCT level FROM members WHERE IFNULL(deleted_at,'')='' ORDER BY level";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(r.GetString(0));
                }
            }
            return list;
        }

        /// <summary>操作员名单（用于「按谁查」下拉）。只返回开过单的人 + 全部在册员工。</summary>
        public List<string> Operators()
        {
            var list = new List<string>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT DISTINCT s.name FROM staff s" +
                    " WHERE EXISTS(SELECT 1 FROM orders o WHERE o.staff_id=s.id)" +
                    " ORDER BY s.name";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(r.GetString(0));
                }
            }
            return list;
        }
    }
}
