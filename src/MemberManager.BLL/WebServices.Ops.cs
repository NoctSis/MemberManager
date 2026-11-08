using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>桥接服务集（三）：交接班/日结、员工提成、权限与日志。</summary>
    public class ShiftService
    {
        public class HandoverRow
        {
            public int Id;
            public string Time;
            public string From;
            public string To;
            public decimal CashDiff;
            public string Status;
        }

        public class PayRow
        {
            public string Method;
            public decimal Amount;
        }

        public class DaySnapshot
        {
            public string BizDate;
            public decimal TotalRevenue;
            public List<PayRow> Pay = new List<PayRow>();
            public decimal Cash;
            public int OrderCount;
            public bool Settled;
        }

        public List<HandoverRow> Logs(int limit)
        {
            var list = new List<HandoverRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,created_at,from_staff,to_staff,cash_diff,reason FROM handover_logs ORDER BY id DESC LIMIT @l";
                W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        decimal diff = Convert.ToDecimal(r.GetValue(4));
                        string reason = r.IsDBNull(5) ? null : r.GetString(5);
                        list.Add(new HandoverRow
                        {
                            Id = r.GetInt32(0),
                            Time = r.GetString(1),
                            From = r.GetString(2),
                            To = r.GetString(3),
                            CashDiff = diff,
                            Status = Math.Abs(diff) < 0.005m ? "账实相符"
                                : (diff < 0 ? "短款 ¥" + Math.Abs(diff).ToString("0.00") : "长款 ¥" + diff.ToString("0.00"))
                                    + (string.IsNullOrEmpty(reason) ? "" : "（原因：" + reason + "）")
                        });
                    }
                }
            }
            return list;
        }

        public DaySnapshot Snapshot()
        {
            return SnapshotOn(W.BizDate());
        }

        /// <summary>
        /// 按指定营业日取快照（报表页日期切换用）。命名不与 Snapshot 重叠：BridgeDispatcher 参数个数严格匹配且禁同名重载。
        /// date 为空回落到当前营业日。
        /// </summary>
        public DaySnapshot SnapshotOn(string date)
        {
            string bizDate = string.IsNullOrEmpty(date) ? W.BizDate() : date;
            var s = new DaySnapshot { BizDate = bizDate };
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT method,IFNULL(SUM(amount),0) FROM payments p JOIN orders o ON o.id=p.order_id" +
                        " WHERE o.biz_date=@d AND o.status='正常' GROUP BY method";
                    W.P(cmd, "@d", bizDate);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            decimal amt = Convert.ToDecimal(r.GetValue(1));
                            s.Pay.Add(new PayRow { Method = r.GetString(0), Amount = amt });
                            s.TotalRevenue += amt;
                            if (r.GetString(0) == "现金") s.Cash += amt;
                        }
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE biz_date=@d AND status='正常'";
                    W.P(cmd, "@d", bizDate);
                    s.OrderCount = Convert.ToInt32(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM daily_settlements WHERE biz_date=@d";
                    W.P(cmd, "@d", bizDate);
                    s.Settled = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
            return s;
        }

        public void Handover(string toStaff, decimal cashSystem, decimal cashActual, string reason, string operatorName)
        {
            if (string.IsNullOrWhiteSpace(toStaff)) throw new BizException("请选择接班人");
            decimal diff = Math.Round(cashActual - cashSystem, 2);
            if (Math.Abs(diff) > 0.005m && string.IsNullOrWhiteSpace(reason))
                throw new BizException("现金有差异必须登记原因，不允许抹平");
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO handover_logs(from_staff,to_staff,cash_diff,reason) VALUES(@f,@t,@d,@r)";
                        W.P(cmd, "@f", operatorName); W.P(cmd, "@t", toStaff.Trim());
                        W.P(cmd, "@d", diff); W.P(cmd, "@r", Math.Abs(diff) < 0.005m ? null : reason.Trim());
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "交接班日结", "交接班",
                        "现金账面" + cashSystem.ToString("0.##") + "，实点" + cashActual.ToString("0.##") + "，移交" + toStaff.Trim());
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        public string DailySettle(string note, string operatorName)
        {
            string bizDate = W.BizDate();
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal total = 0, cash = 0;
                    var pays = new List<string>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT method,IFNULL(SUM(amount),0) FROM payments p JOIN orders o ON o.id=p.order_id" +
                            " WHERE o.biz_date=@d AND o.status='正常' GROUP BY method";
                        W.P(cmd, "@d", bizDate);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                decimal amt = Convert.ToDecimal(r.GetValue(1));
                                pays.Add(r.GetString(0) + "¥" + amt.ToString("0.00"));
                                total += amt;
                                if (r.GetString(0) == "现金") cash = amt;
                            }
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO daily_settlements(biz_date,total_revenue,pay_snapshot,cash_system,cash_actual,cash_diff,diff_reason,operator,note)" +
                            " VALUES(@d,@t,@p,@cs,@ca,0,NULL,@o,@n)";
                        W.P(cmd, "@d", bizDate); W.P(cmd, "@t", total); W.P(cmd, "@p", string.Join(" + ", pays.ToArray()));
                        W.P(cmd, "@cs", cash); W.P(cmd, "@ca", cash); W.P(cmd, "@o", operatorName); W.P(cmd, "@n", note);
                        try { cmd.ExecuteNonQuery(); }
                        catch (SQLiteException) { throw new BizException("该营业日已日结，不能重复执行"); }
                    }
                    W.Log(conn, txn, "交接班日结", "营业日结", "营业日" + bizDate + " 日结，营收" + total.ToString("0.##") + "元");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
            return bizDate;
        }
    }

    public class CommissionService
    {
        public class CommRow
        {
            public int Id;
            public string Date;
            public string OrderNo;
            public string Staff;
            public string Type;
            public decimal Amount;
            public decimal Rate;
            public decimal Commission;
            public string Share;
            public bool Settled;
            /// <summary>员工 id（T6 新增）—— 对账面板按它把逐笔挂到员工行上，避免同名员工串行。</summary>
            public int StaffId;
            /// <summary>订单类型（M11 新增）：'消费'/'充值'/'售卡'——commissions.type 被 CHECK 冻结在
            /// ('充值','消费')，售卡提成行的 type 是 '消费'，真话看这里；界面徽标按 kind 显示。</summary>
            public string Kind;
        }

        public class RankRow
        {
            public string Name;
            public decimal RechargeAmount;
            public decimal ConsumeAmount;
            public decimal Commission;
            public int Orders;
            // B1（10-21）：已结/未结拆列——发工资对账用「结了多少、还欠多少」一目了然。
            public decimal Settled;
            public decimal Unsettled;
        }

        public List<CommRow> Details(string month, int limit)
        {
            var list = new List<CommRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // M11（10-14）：多带一列 o.kind——售卡提成行的 type 只能是 '消费'
                // （CHECK 冻结），界面徽标按 kind 显示才不失真（售卡单显示「售卡」）。
                cmd.CommandText = "SELECT c.id, substr(c.created_at,1,10), o.order_no, s.name, c.type, c.amount, c.rate, c.commission, c.share, c.settled, c.staff_id, o.kind" +
                    " FROM commissions c JOIN staff s ON s.id=c.staff_id JOIN orders o ON o.id=c.order_id" +
                    " WHERE substr(c.created_at,1,7)=@mo ORDER BY c.id DESC LIMIT @l";
                W.P(cmd, "@mo", month); W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new CommRow
                        {
                            Id = r.GetInt32(0),
                            Date = r.GetString(1),
                            OrderNo = r.GetString(2),
                            Staff = r.GetString(3),
                            Type = r.GetString(4),
                            Amount = Convert.ToDecimal(r.GetValue(5)),
                            Rate = Convert.ToDecimal(r.GetValue(6)),
                            Commission = Convert.ToDecimal(r.GetValue(7)),
                            Share = r.GetString(8),
                            Settled = Convert.ToInt32(r.GetValue(9)) == 1,
                            StaffId = Convert.ToInt32(r.GetValue(10)),
                            Kind = r.IsDBNull(11) ? "" : r.GetString(11)
                        });
                }
            }
            return list;
        }

        public List<RankRow> Ranks(string month)
        {
            var list = new List<RankRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 第 6 列 = 业绩合计（充值业绩 + 消费业绩），仅用于排序，不进 RankRow。
                // 10-08 修正：原先 ORDER BY 4（提成合计）DESC —— 标题写的是「员工业绩排名」，
                // 名次却由提成本决定，两者不一致（提成率因人而异，提成高不代表业绩高）。
                // 现改为按业绩倒序，业绩并列时再用提成倒序破平。
                // M11（10-14）：业绩口径排除售卡单的提成行——售卡不计业绩（既定规则），
                // 且 commissions.type 被 CHECK 冻结在 ('充值','消费')，售卡提成只能落 '消费'，
                // 故按 orders.kind 排除，否则卡销售额会混进消费业绩。
                cmd.CommandText = "SELECT s.name," +
                    " IFNULL(SUM(CASE WHEN c.type='充值' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.type='消费' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0)," +
                    " IFNULL(SUM(c.commission),0), COUNT(*)," +
                    " IFNULL(SUM(CASE WHEN c.type='充值' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0) + IFNULL(SUM(CASE WHEN c.type='消费' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.settled=1 THEN c.commission ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.settled=0 THEN c.commission ELSE 0 END),0)" +
                    " FROM commissions c JOIN staff s ON s.id=c.staff_id LEFT JOIN orders o ON o.id=c.order_id WHERE substr(c.created_at,1,7)=@mo GROUP BY c.staff_id ORDER BY 6 DESC, 4 DESC";
                W.P(cmd, "@mo", month);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new RankRow
                        {
                            Name = r.GetString(0),
                            RechargeAmount = Convert.ToDecimal(r.GetValue(1)),
                            ConsumeAmount = Convert.ToDecimal(r.GetValue(2)),
                            Commission = Convert.ToDecimal(r.GetValue(3)),
                            Orders = Convert.ToInt32(r.GetValue(4)),
                            Settled = Convert.ToDecimal(r.GetValue(6)),
                            Unsettled = Convert.ToDecimal(r.GetValue(7))
                        });
                }
            }
            return list;
        }

        /// <summary>
        /// 按项目看业绩 / 提成（店主 10-10 定：拆到「哪个项目赚得多」，类似营收构成但落到提成/业绩口径）。
        /// 业绩 = order_items.price*qty（该项目当月做了多少，**精确**）；
        /// 提成 = 业绩 × 项目提成率（**估算**——commissions 表只存整单加权率、未存逐行，
        /// 且开单时若员工有个人比例会覆盖项目率，故此处只能按项目自身率估，实际以结算为准）。
        /// 匹配不到在售项目（改名/删除）的行 OnSale=false、提成率回落全局消费率。
        /// 口径：kind='消费' 且 status='正常'（已退款/挂单不计业绩），按月。
        /// </summary>
        public class ProjectBreakRow
        {
            public string Item;
            public string Unit;
            public int Qty;
            public decimal Perf;
            public decimal Commission;
            public decimal Rate;
            /// <summary>提成方式：'percent'=按价格百分比；'count'=按次卡核销次数。</summary>
            public string Mode;
            /// <summary>按次金额（mode='count' 时有效）。</summary>
            public decimal PerCount;
            public int Orders;
            public bool OnSale;
        }

        public List<ProjectBreakRow> ProjectBreakdown(string month)
        {
            var list = new List<ProjectBreakRow>();
            decimal gcon = Convert.ToDecimal(DatabaseInitializer.GetSetting("consume_commission_rate", "0.05"));
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT oi.item_name, si.unit, si.commission_rate, si.is_card_item, si.enabled," +
                    " IFNULL(SUM(oi.qty),0)," +
                    " IFNULL(SUM(oi.price*oi.qty),0)," +
                    " COUNT(DISTINCT oi.order_id), si.commission_mode, IFNULL(si.commission_per_count,0)" +
                    " FROM order_items oi" +
                    " JOIN orders o ON o.id=oi.order_id" +
                    " LEFT JOIN service_items si ON si.name=oi.item_name" +
                    " WHERE o.kind='消费' AND o.status='正常' AND substr(o.created_at,1,7)=@mo" +
                    " GROUP BY oi.item_name, si.unit, si.commission_rate, si.is_card_item, si.enabled, si.commission_mode, si.commission_per_count" +
                    " ORDER BY 7 DESC";
                W.P(cmd, "@mo", month);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string itemName = r.IsDBNull(0) ? "" : r.GetString(0);
                        bool onSale = !r.IsDBNull(4) && Convert.ToInt32(r.GetValue(4)) == 1;
                        bool isCount = !r.IsDBNull(8) && Convert.ToString(r.GetValue(8)) == "count";
                        decimal rate = r.IsDBNull(2) ? gcon : Convert.ToDecimal(r.GetValue(2));
                        decimal perCount = r.IsDBNull(9) ? 0m : Convert.ToDecimal(r.GetValue(9));
                        decimal perf = Convert.ToDecimal(r.GetValue(6));
                        decimal qty = Convert.ToDecimal(r.GetValue(5));
                        // 估算：百分比=业绩×率；按次=次数×单次金额（次卡核销次数=order_items 行量汇总）
                        decimal est = isCount ? Round2(qty * perCount) : Round2(perf * rate);
                        list.Add(new ProjectBreakRow
                        {
                            Item = itemName,
                            Unit = r.IsDBNull(1) ? "次" : r.GetString(1),
                            Qty = Convert.ToInt32(r.GetValue(5)),
                            Perf = perf,
                            Commission = est,
                            Rate = rate,
                            Mode = isCount ? "count" : "percent",
                            PerCount = perCount,
                            Orders = Convert.ToInt32(r.GetValue(7)),
                            OnSale = onSale,
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>员工提成对账汇总（T6）：一行一人，业绩按充值/消费分开、金额按未结/已结分开。</summary>
        public class StaffSumRow
        {
            public int StaffId;
            public string Name;
            public decimal RechargeAmount;
            public decimal ConsumeAmount;
            public int UnsettledCount;
            public decimal UnsettledAmount;
            public decimal SettledAmount;
            public int TotalOrders;
        }

        /// <summary>一次结算动作的结果（T6）。</summary>
        public class SettleResult
        {
            public int Count;
            public decimal Amount;
            public decimal Actual;
            public decimal Diff;
            public string Period;
        }

        /// <summary>结算批次（T6）：从 op_logs 解析出来的一条历史。</summary>
        public class SettleBatch
        {
            public int Id;
            public string Time;
            public string Operator;
            public string Period;
            public int StaffCount;
            public int Count;
            public decimal Amount;
            public decimal Actual;
            public decimal Diff;
            public string Reason;
            public string StaffNames;
        }

        /// <summary>
        /// 按员工汇总某月提成（T6「员工提成对账」面板的数据源）。
        /// 排序按未结金额倒序 —— 谁最该结谁在最上面。
        /// </summary>
        public List<StaffSumRow> StaffSummary(string month)
        {
            var list = new List<StaffSumRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // M11（10-14）：业绩两列排除售卡单提成行（售卡不计业绩；commissions.type 被
                // CHECK 冻结在 ('充值','消费')，售卡提成落 '消费'，只能按 orders.kind 排除）。
                // 未结/已结/笔数不分类型——售卡提成照样要结算。
                cmd.CommandText = "SELECT s.id, s.name," +
                    " IFNULL(SUM(CASE WHEN c.type='充值' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.type='消费' AND IFNULL(o.kind,'')<>'售卡' THEN c.amount ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.settled=0 THEN 1 ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.settled=0 THEN c.commission ELSE 0 END),0)," +
                    " IFNULL(SUM(CASE WHEN c.settled=1 THEN c.commission ELSE 0 END),0)," +
                    " COUNT(*)" +
                    " FROM commissions c JOIN staff s ON s.id=c.staff_id LEFT JOIN orders o ON o.id=c.order_id" +
                    " WHERE substr(c.created_at,1,7)=@mo GROUP BY c.staff_id ORDER BY 6 DESC, 3 DESC";
                W.P(cmd, "@mo", month);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new StaffSumRow
                        {
                            StaffId = r.GetInt32(0),
                            Name = r.GetString(1),
                            RechargeAmount = Convert.ToDecimal(r.GetValue(2)),
                            ConsumeAmount = Convert.ToDecimal(r.GetValue(3)),
                            UnsettledCount = Convert.ToInt32(r.GetValue(4)),
                            UnsettledAmount = Convert.ToDecimal(r.GetValue(5)),
                            SettledAmount = Convert.ToDecimal(r.GetValue(6)),
                            TotalOrders = Convert.ToInt32(r.GetValue(7))
                        });
                }
            }
            return list;
        }

        /// <summary>
        /// 按员工结算某月提成（T6）。**只结指定员工，绝不误伤他人** ——
        /// 这是与旧入口 <see cref="SettleAll"/>（全月一刀切）的根本区别。
        /// actualTotal &lt; 0 表示不登记实付差异（实付按应结）；≥ 0 且与应结不等时必须给 reason。
        /// settled 的更新与 op_logs 批次写入在**同一事务**；批次 detail 里存了被结的 commission id，
        /// 日后可反查「哪几笔被结了、什么时候、谁操作的」（commissions 表本身没有结算时间字段）。
        /// 幂等：没有未结的笔数时直接返回 count=0，不更新、不写日志。
        /// </summary>
        public SettleResult SettleByStaff(string month, string staffIdsCsv, decimal actualTotal, string reason, string operatorName)
        {
            var ids = new List<int>();
            if (!string.IsNullOrEmpty(staffIdsCsv))
            {
                foreach (var s in staffIdsCsv.Split(','))
                {
                    int v;
                    if (int.TryParse(s.Trim(), out v) && !ids.Contains(v)) ids.Add(v);
                }
            }
            if (ids.Count == 0) throw new BizException("请先选择要结算的员工");

            var result = new SettleResult { Period = month };

            var ph = new List<string>();
            for (int i = 0; i < ids.Count; i++) ph.Add("@s" + i);
            string inList = string.Join(",", ph.ToArray());
            string whereC = " WHERE c.settled=0 AND substr(c.created_at,1,7)=@mo AND c.staff_id IN(" + inList + ")";
            string whereT = " WHERE settled=0 AND substr(created_at,1,7)=@mo AND staff_id IN(" + inList + ")";

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    var commIds = new List<int>();
                    var perStaff = new List<string>();
                    decimal sum = 0m;

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT c.id, s.name, c.commission FROM commissions c JOIN staff s ON s.id=c.staff_id" + whereC;
                        W.P(cmd, "@mo", month);
                        for (int i = 0; i < ids.Count; i++) W.P(cmd, "@s" + i, ids[i]);
                        using (var r = cmd.ExecuteReader())
                        {
                            var cnt = new Dictionary<string, int>();
                            var amt = new Dictionary<string, decimal>();
                            var order = new List<string>();
                            while (r.Read())
                            {
                                commIds.Add(r.GetInt32(0));
                                string nm = r.GetString(1);
                                decimal cm = Convert.ToDecimal(r.GetValue(2));
                                sum += cm;
                                if (!cnt.ContainsKey(nm)) { cnt[nm] = 0; amt[nm] = 0m; order.Add(nm); }
                                cnt[nm] = cnt[nm] + 1;
                                amt[nm] = amt[nm] + cm;
                            }
                            foreach (var nm in order)
                                perStaff.Add(nm + ":" + cnt[nm] + "笔/" + amt[nm].ToString("0.00"));
                        }
                    }

                    result.Count = commIds.Count;
                    result.Amount = sum;
                    result.Actual = actualTotal < 0m ? sum : actualTotal;
                    result.Diff = result.Actual - sum;

                    if (commIds.Count == 0)
                    {
                        // 幂等：没有未结的就不动库、不写日志，重复点不会灌水
                        result.Actual = 0m;
                        result.Diff = 0m;
                        txn.Rollback();
                        return result;
                    }
                    if (result.Diff != 0m && string.IsNullOrEmpty(reason))
                        throw new BizException("实付与应结相差 " + result.Diff.ToString("0.00") + " 元，请填写差异原因");

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        // B1（10-21）：settled_at 与 settled=1 同一事务写入——哪笔提成何时被结，落库可查。
                        cmd.CommandText = "UPDATE commissions SET settled=1, settled_at=datetime('now','localtime')" + whereT;
                        W.P(cmd, "@mo", month);
                        for (int i = 0; i < ids.Count; i++) W.P(cmd, "@s" + i, ids[i]);
                        cmd.ExecuteNonQuery();
                    }

                    W.Log(conn, txn, "员工提成", "结算提成",
                        "月份=" + month +
                        "|人数=" + perStaff.Count +
                        "|笔数=" + commIds.Count +
                        "|应结=" + sum.ToString("0.00") +
                        "|实付=" + result.Actual.ToString("0.00") +
                        "|差异=" + result.Diff.ToString("0.00") +
                        "|原因=" + (reason ?? "") +
                        "|员工=" + string.Join(",", perStaff.ToArray()) +
                        "|ids=" + IdsBrief(commIds),
                        operatorName);
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
            return result;
        }

        /// <summary>被结算的 commission id 串（超 200 个截断，避免日志行过长）。</summary>
        private static string IdsBrief(List<int> ids)
        {
            if (ids.Count <= 200) return string.Join(",", ids.ConvertAll<string>(delegate (int x) { return x.ToString(); }).ToArray());
            var head = ids.GetRange(0, 200);
            return string.Join(",", head.ConvertAll<string>(delegate (int x) { return x.ToString(); }).ToArray())
                + "...(共" + ids.Count + "个)";
        }

        /// <summary>
        /// 结算批次历史（T6）：从 op_logs 读「结算提成」记录并解析 detail。
        /// 不新建表（DDL 冻结）；op_logs 带禁删触发器，正好当审计账用。
        /// </summary>
        public List<SettleBatch> SettleHistory(int limit)
        {
            if (limit <= 0 || limit > 500) limit = 50;
            var list = new List<SettleBatch>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, created_at, operator, detail FROM op_logs" +
                    " WHERE module='员工提成' AND action='结算提成' ORDER BY id DESC LIMIT @l";
                W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string d = r.IsDBNull(3) ? "" : r.GetString(3);
                        int n;
                        decimal m;
                        list.Add(new SettleBatch
                        {
                            Id = r.GetInt32(0),
                            Time = r.GetString(1),
                            Operator = r.GetString(2),
                            Period = Kv(d, "月份"),
                            StaffNames = Kv(d, "员工"),
                            Reason = Kv(d, "原因"),
                            StaffCount = int.TryParse(Kv(d, "人数"), out n) ? n : 0,
                            Count = int.TryParse(Kv(d, "笔数"), out n) ? n : 0,
                            Amount = decimal.TryParse(Kv(d, "应结"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out m) ? m : 0m,
                            Actual = decimal.TryParse(Kv(d, "实付"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out m) ? m : 0m,
                            Diff = decimal.TryParse(Kv(d, "差异"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out m) ? m : 0m
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>从 `k1=v1|k2=v2` 里取 key 的值（取第一个 = 之后的全部，容忍值里含 =）。</summary>
        private static string Kv(string detail, string key)
        {
            if (string.IsNullOrEmpty(detail)) return "";
            foreach (var seg in detail.Split('|'))
            {
                int i = seg.IndexOf('=');
                if (i > 0 && seg.Substring(0, i) == key) return seg.Substring(i + 1);
            }
            return "";
        }

        /// <summary>
        /// 导出某月提成 Excel（T5）：两个工作表「提成明细」（逐笔全量）+「提成排名」（按员工）。
        /// 与页面数据同源（走 Details / Ranks 同一批方法，不另写 SQL），保证「导出的数和屏幕上看的一致」。
        /// 落点由 W.ResolveExportDir 统一解析：本次临时 saveDir（非空）＞全局默认 export_dir 设置＞兜底 data\exports。
        /// 前端拿到路径后调 system.OpenPath 用系统默认程序打开。
        /// </summary>
        public ReportService.ExportResult ExportCommissionExcel(string month, string operatorName, string saveDir = null)
        {
            string bizMonth = W.BizDate().Substring(0, 7);
            string mm = string.IsNullOrEmpty(month) ? bizMonth : month;

            var x = new ExcelWriter();

            // 提成明细（逐笔全量，按 id 倒序）
            // 店主 10-08 定：删「占比」列 —— commissions.share 恒 '100%'（InsertOne 写死），
            // 摆在报表里只会误导，无信息量。剩 8 列：日期/订单号/员工/类型/金额/比例/提成/结算。
            x.Sheet("提成明细")
                .Widths(12, 16, 14, 8, 14, 10, 14, 10)
                .FreezeHeaders(true);
            x.Text(1, 1, "日期", ExcelWriter.S.Header).Text(1, 2, "订单号", ExcelWriter.S.Header)
                .Text(1, 3, "员工", ExcelWriter.S.Header).Text(1, 4, "类型", ExcelWriter.S.Header)
                .Text(1, 5, "金额", ExcelWriter.S.Header).Text(1, 6, "比例", ExcelWriter.S.Header)
                .Text(1, 7, "提成", ExcelWriter.S.Header)
                .Text(1, 8, "结算", ExcelWriter.S.Header);
            var rows = Details(mm, 99999);
            int r = 2;
            decimal sumAmount = 0m, sumCommission = 0m;
            // B1（10-21）：明细合计拆「已结 / 未结」小计——发工资前一眼看清该发多少、还欠多少。
            decimal sumSettled = 0m, sumUnsettled = 0m;
            foreach (var c in rows)
            {
                x.Text(r, 1, c.Date, ExcelWriter.S.Plain).Text(r, 2, c.OrderNo, ExcelWriter.S.Plain)
                    .Text(r, 3, c.Staff, ExcelWriter.S.Plain).Text(r, 4, c.Type, ExcelWriter.S.Plain)
                    .Num(r, 5, c.Amount, ExcelWriter.S.Money).Num(r, 6, c.Rate, ExcelWriter.S.Percent)
                    .Num(r, 7, c.Commission, ExcelWriter.S.Money)
                    .Text(r, 8, c.Settled ? "已结算" : "未结算", ExcelWriter.S.Plain);
                sumAmount += c.Amount;
                sumCommission += c.Commission;
                if (c.Settled) sumSettled += c.Commission; else sumUnsettled += c.Commission;
                r++;
            }
            if (rows.Count > 0)
            {
                x.Text(r, 1, "合计", ExcelWriter.S.MoneyHead).Num(r, 5, sumAmount, ExcelWriter.S.MoneyHead)
                    .Num(r, 7, sumCommission, ExcelWriter.S.MoneyHead);
                r++;
                x.Text(r, 1, "其中：已结算", ExcelWriter.S.Plain)
                    .Num(r, 7, sumSettled, ExcelWriter.S.MoneyHead);
                r++;
                x.Text(r, 1, "其中：未结算", ExcelWriter.S.Plain)
                    .Num(r, 7, sumUnsettled, ExcelWriter.S.MoneyHead);
            }
            else
            {
                x.Text(2, 1, "本月暂无提成数据", ExcelWriter.S.Plain);
            }

            // 提成排名（按员工汇总）——B1（10-21）：加「已结 / 未结」两列，此表可直接当工资单用
            x.Sheet("提成排名").Widths(14, 16, 16, 14, 10, 14, 14).FreezeHeaders(true);
            x.Text(1, 1, "姓名", ExcelWriter.S.Header).Text(1, 2, "充值业绩", ExcelWriter.S.Header)
                .Text(1, 3, "消费业绩", ExcelWriter.S.Header).Text(1, 4, "提成合计", ExcelWriter.S.Header)
                .Text(1, 5, "单数", ExcelWriter.S.Header)
                .Text(1, 6, "已结算", ExcelWriter.S.Header).Text(1, 7, "未结算", ExcelWriter.S.Header);
            var ranks = Ranks(mm);
            r = 2;
            foreach (var k in ranks)
            {
                x.Text(r, 1, k.Name, ExcelWriter.S.Plain).Num(r, 2, k.RechargeAmount, ExcelWriter.S.Money)
                    .Num(r, 3, k.ConsumeAmount, ExcelWriter.S.Money).Num(r, 4, k.Commission, ExcelWriter.S.Money)
                    .Num(r, 5, k.Orders, ExcelWriter.S.Count)
                    .Num(r, 6, k.Settled, ExcelWriter.S.Money)
                    .Num(r, 7, k.Unsettled, ExcelWriter.S.Money);
                r++;
            }
            if (ranks.Count == 0)
                x.Text(2, 1, "本区间暂无提成数据", ExcelWriter.S.Plain);

            string dir = W.ResolveExportDir(saveDir);   // 统一解析：本次临时 ＞ 全局 export_dir ＞ data\exports
            string name = "提成表-" + mm + ".xlsx";
            string path = System.IO.Path.Combine(dir, name);
            long size = x.Save(path);
            Logger.Info("导出提成表 Excel：" + path + "（" + size + " 字节）操作人 " + operatorName);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "报表导出", "导出提成表", "期间 " + mm + "，文件 " + name);
            return new ReportService.ExportResult { Path = path, Name = name, Size = size, Period = mm };
        }

        public void SettleAll(string month, string operatorName)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    int n;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE commissions SET settled=1 WHERE settled=0 AND substr(created_at,1,7)=@mo";
                        W.P(cmd, "@mo", month);
                        n = cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "员工提成", "批量结算", month + " 共结算 " + n + " 笔");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        public void SaveRates(decimal rechargeRate, decimal consumeRate, string operatorName)
        {
            if (rechargeRate < 0 || rechargeRate > 1 || consumeRate < 0 || consumeRate > 1)
                throw new BizException("提成比例须在 0 - 100% 之间");
            // 两个比例 + 留痕必须同一事务：原先两次 SetSetting 各开一条连接、各自提交，
            // 第二次失败会留下「充值比例已改、消费比例未改」的半状态。
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    DatabaseInitializer.SetSetting(conn, txn, "recharge_commission_rate", rechargeRate.ToString("0.####"));
                    DatabaseInitializer.SetSetting(conn, txn, "consume_commission_rate", consumeRate.ToString("0.####"));
                    W.Log(conn, txn, "员工提成", "修改提成规则", "充值 " + (rechargeRate * 100).ToString("0.#") + "% · 消费 " + (consumeRate * 100).ToString("0.#") + "%");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 次卡提成结算方式（店主 10-14：三套都做、可切换；10-15 定默认核销时全算）。
        /// settings.card_commission_mode：
        ///   sale  = 售卡时全算——售卡人按「实收×充值比例」拿，之后每次核销不再计；
        ///   use   = 核销时全算——售卡不计，每次核销服务人按「单次折算价×项目比例」拿（默认）；
        ///   split = 两段各半——售卡时计一半，每次核销计一半。
        /// 基数对半、率各走各的（售卡侧=充值率解析，核销侧=项目率解析）；总盘不超「实收×率」。
        /// 切换对**未结算**提成立即重计（冲正+新行，见 ApplyModeToUnsettled），已结算的不动。
        /// 脏值一律回落 use。
        /// </summary>
        public static string CardCommissionMode(SQLiteConnection conn, SQLiteTransaction txn)
        {
            string v = DatabaseInitializer.GetSetting(conn, txn, "card_commission_mode", "use");
            return (v == "sale" || v == "split") ? v : "use";   // 脏值回落 use
        }

        /// <summary>无 conn/txn 重载（接口层读取用）。</summary>
        public static string CardCommissionMode()
        {
            string v = DatabaseInitializer.GetSetting("card_commission_mode", "use");
            return (v == "sale" || v == "split") ? v : "use";
        }

        /// <summary>模式的中文名（日志/界面共用）。</summary>
        public static string CardCommissionModeName(string mode)
        {
            return mode == "sale" ? "售卡时全算" : mode == "use" ? "核销时全算" : "两段各半";
        }

        /// <summary>
        /// 该卡的「单次项目价」= 这张卡对应服务项目的单个项目价（一次到店做一次服务的钱）。
        /// 取自 card_templates.item_name → service_items.price；查不到返回 0（上限无从折算 → 不设限）。
        /// </summary>
        public static decimal CardItemPrice(SQLiteConnection conn, SQLiteTransaction txn, int memberCardId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "SELECT IFNULL((SELECT si.price FROM service_items si" +
                    " JOIN card_templates ct ON ct.item_name=si.name" +
                    " JOIN member_cards mc ON mc.template_id=ct.id" +
                    " WHERE mc.id=@k LIMIT 1),0)";
                var p = cmd.CreateParameter(); p.ParameterName = "@k"; p.Value = memberCardId; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
            }
        }

        /// <summary>
        /// 该卡实收。老卡（10-11 前建的，paid_amount 为空）没有留下实收 —— 按面值兜底
        /// （单次项目价 × 总次数），即「假定当初没打折」，提成照全额单次价发。
        /// 真的没收钱（实收 0，如赠送卡）就返回 0 → 没有提成额度，一分不发。
        /// </summary>
        public static decimal CardPaid(SQLiteConnection conn, SQLiteTransaction txn, int memberCardId)
        {
            decimal paid = 0m; int total = 0; bool hasPaid = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT paid_amount, total FROM member_cards WHERE id=@k";
                var p = cmd.CreateParameter(); p.ParameterName = "@k"; p.Value = memberCardId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return 0m;
                    hasPaid = !r.IsDBNull(0);
                    paid = hasPaid ? Convert.ToDecimal(r.GetValue(0)) : 0m;
                    total = Convert.ToInt32(r.GetValue(1));
                }
            }
            if (hasPaid) return paid > 0m ? paid : 0m;                  // 有实收就用实收；0 元卡没有额度
            decimal unit = CardItemPrice(conn, txn, memberCardId);
            return Math.Round(unit * total, 2);                          // 老卡：按面值兜底
        }

        /// <summary>
        /// 这张卡**在本单之外**已经发出的提成合计（含已结算；冲正行是负数，会自动净掉）。
        /// 用它和额度比，才能做到「每次都按一次项目的钱记，但整张卡的额度用完之后不再记」。
        /// </summary>
        public static decimal CardEarned(SQLiteConnection conn, SQLiteTransaction txn, int memberCardId, int excludeOrderId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "SELECT IFNULL(SUM(c.commission),0) FROM commissions c" +
                    " WHERE c.order_id<>@o" +
                    " AND c.order_id IN (SELECT DISTINCT cu.order_id FROM card_uses cu WHERE cu.member_card_id=@k)";
                var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; pk.Value = memberCardId; cmd.Parameters.Add(pk);
                var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = excludeOrderId; cmd.Parameters.Add(po);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
            }
        }

        /// <summary>
        /// 按指定模式重算「整单提成」。**订单数据必须已落库**（order_items / card_uses）——
        /// 结账落库后与模式切换重算共用这一条路径，两处口径永远一致（项目不冲突）。
        ///   · 售卡单：base = receivable × 份额（sale 1 / split 0.5 / use 0），率走充值口径；
        ///   · 消费单：非卡抵行照旧（价格×率 或 按次）；
        ///     **卡抵行（店主 10-15 定）= 一次核销就记「一次的钱」**：
        ///       基数 = linePrice（该服务项目的单次价格快照），先到先得、次次全记，
        ///       不按「卡实收÷次数」摊薄；但整张卡有额度上限，用完即止（超出部分当「找零」不计，见 <see cref="cardExcess"/>）。
        ///       按次项目（服务计量）不参与对半；百分比行 sale→0、use→全额、split→一半。
        ///   · 其他单种（充值等）不随模式变，原样返回旧值由调用方跳过。
        /// 返回 (提成额, 计提基数, 是否随模式变)；cardExcess = 本单因超出卡片额度而没记的提成。
        /// </summary>
        public static decimal OrderCommission(SQLiteConnection conn, SQLiteTransaction txn, int orderId, int staffId, string ccMode, out decimal baseAmount, out bool modeDriven, out decimal cardExcess)
        {
            cardExcess = 0m;
            string kind = "";
            decimal receivable = 0m;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT kind, receivable FROM orders WHERE id=@o";
                var p = cmd.CreateParameter(); p.ParameterName = "@o"; p.Value = orderId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read()) { kind = r.GetString(0); receivable = Convert.ToDecimal(r.GetValue(1)); }
                }
            }
            if (kind == "售卡")
            {
                modeDriven = true;
                decimal share = ccMode == "sale" ? 1m : ccMode == "split" ? 0.5m : 0m;
                baseAmount = Math.Round(receivable * share, 2);
                return baseAmount <= 0m ? 0m : Math.Round(baseAmount * ResolveRechargeRate(conn, txn, staffId), 2);
            }
            if (kind != "消费") { modeDriven = false; baseAmount = 0m; return -1m; }   // 充值等：不随模式变

            modeDriven = true;
            decimal share2 = ccMode == "sale" ? 0m : ccMode == "split" ? 0.5m : 1m;
            decimal subtotal = 0m, commission = 0m;
            // 本单每张卡核销了几次（先到先得地扣额度用，按卡 id 升序）
            var usedCards = new List<KeyValuePair<int, int>>();   // memberCardId, uses
            int usedTotal = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "SELECT member_card_id, COUNT(*) FROM card_uses WHERE order_id=@o" +
                    " GROUP BY member_card_id ORDER BY member_card_id";
                var p = cmd.CreateParameter(); p.ParameterName = "@o"; p.Value = orderId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int n = Convert.ToInt32(r.GetValue(1));
                        usedCards.Add(new KeyValuePair<int, int>(r.GetInt32(0), n));
                        usedTotal += n;
                    }
                }
            }
            var lines = new List<KeyValuePair<int, KeyValuePair<int, int>>>();  // itemId, (byCard, qty)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT item_id, by_card, qty FROM order_items WHERE order_id=@o ORDER BY id";
                var p = cmd.CreateParameter(); p.ParameterName = "@o"; p.Value = orderId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        lines.Add(new KeyValuePair<int, KeyValuePair<int, int>>(
                            r.GetInt32(0), new KeyValuePair<int, int>(Convert.ToInt32(r.GetValue(1)), r.GetInt32(2))));
            }
            // 卡抵行先攒起来（一次的钱, 次数），最后按卡套额度
            var cardLines = new List<decimal[]>();
            foreach (var ln in lines)
            {
                int itemId = ln.Key; bool byCard = ln.Value.Key != 0; int qty = ln.Value.Value;
                // 项目现制（mode/rate/perCount 按库内现值解析；item_id=0 的自定义行回落全局消费率）
                bool isCount = false; decimal rate = -1m, perCount = 0m;
                if (itemId > 0)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT commission_mode, commission_rate, commission_per_count FROM service_items WHERE id=@i";
                        var p = cmd.CreateParameter(); p.ParameterName = "@i"; p.Value = itemId; cmd.Parameters.Add(p);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                isCount = r.GetString(0) == "count";
                                rate = Convert.ToDecimal(r.GetValue(1));
                                perCount = Convert.ToDecimal(r.GetValue(2));
                            }
                        }
                    }
                }
                ItemCommission ic = ResolveItemCommission(conn, txn, staffId, itemId, isCount, rate, perCount);
                decimal linePrice = ItemPriceOf(conn, txn, orderId, itemId);
                if (ic.IsCount)
                {
                    if (byCard)
                    {
                        // 按次卡抵行：一次 = 一个固定钱数，同样走额度，但不参与对半（服务计量）
                        cardLines.Add(new decimal[] { Round2(ic.PerCount), qty });
                    }
                    else commission += Round2(ic.PerCount * qty);
                }
                else if (byCard)
                {
                    // ★ 卡抵行 = 单次项目的钱（这是这一次服务的价钱），不摊薄成「实收÷次数」
                    cardLines.Add(new decimal[] { Round2(linePrice * ic.Rate * share2), qty });
                }
                else
                    commission += Round2(linePrice * qty * ic.Rate);
            }

            // —— 卡抵行提成落地：按「每张卡本次核销几次」摊到卡上，逐卡套额度 ——
            decimal cardDesired = 0m, cardQty = 0m;
            foreach (decimal[] cl in cardLines) { cardDesired += Round2(cl[0] * cl[1]); cardQty += cl[1]; }
            if (cardDesired > 0m && usedTotal > 0 && cardQty > 0m)
            {
                decimal perSession = Round2(cardDesired / cardQty);   // 本单平均「一次的钱」
                for (int i = 0; i < usedCards.Count; i++)
                {
                    int cid = usedCards[i].Key, uses = usedCards[i].Value;
                    decimal want = Round2(perSession * uses);
                    if (want <= 0m) continue;
                    // 额度 = 这张卡实收能折算出的提成总额 = 实收 × (一次的钱 ÷ 单次项目价)
                    decimal unitPrice = CardItemPrice(conn, txn, cid);
                    decimal pool;
                    if (unitPrice > 0m)
                    {
                        decimal paid = CardPaid(conn, txn, cid);
                        pool = Math.Round(paid * perSession / unitPrice, 2);
                    }
                    else
                    {
                        pool = want;   // 查不到单次项目价（极端情况）：不设限，照发
                    }
                    decimal left = Round2(pool - CardEarned(conn, txn, cid, orderId));
                    decimal pay = left <= 0m ? 0m : (want > left ? left : want);
                    commission += pay;
                    cardExcess += Round2(want - pay);
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(SUM(price*qty),0) FROM order_items WHERE order_id=@o";
                var p = cmd.CreateParameter(); p.ParameterName = "@o"; p.Value = orderId; cmd.Parameters.Add(p);
                subtotal = Convert.ToDecimal(cmd.ExecuteScalar());
            }
            baseAmount = Round2(subtotal);
            return Round2(commission);
        }

        /// <summary>order_items 里该项目的成交价快照（取第一行；改名/删项不影响历史价）。</summary>
        private static decimal ItemPriceOf(SQLiteConnection conn, SQLiteTransaction txn, int orderId, int itemId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT price FROM order_items WHERE order_id=@o AND item_id=@i ORDER BY id LIMIT 1";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@o"; p1.Value = orderId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@i"; p2.Value = itemId; cmd.Parameters.Add(p2);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
            }
        }

        /// <summary>
        /// 切换次卡提成模式后，把受影响的「未结算订单」整单按新规矩重计
        /// （店主 10-15：改完即时生效要落在未结算订单上）。
        /// 按**订单**聚合，不按行：
        ///   · 候选 = 状态正常 且（售卡单 或 有卡抵的消费单）；
        ///   · 该单只要存在已结算提成行 → 整单跳过（钱已发，绝不回改）；
        ///   · 旧值 = 该单未结算提成合计（一行都没有时为 0 —— 换模式后本该有却还没有的，这次补上）；
        ///   · 新值 = 与结账完全同一条口径 OrderCommission（项目不冲突、两边永远一致）。
        /// **只增不删**：原行一律不动，写镜像冲正行（提成与基数一起取负）+ 新行。
        /// 业绩口径因此天然守恒：原行(+基数) + 冲正(−基数) + 新行(+基数) = 该单项目原价，只算一次。
        /// 返回 [冲正行数, 新行数]。
        /// </summary>
        public static int[] ApplyModeToUnsettled(SQLiteConnection conn, SQLiteTransaction txn, string newMode)
        {
            int reversed = 0, added = 0;
            var cand = new List<object[]>();   // { orderId, staffId, settledCnt, type }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                // 一行提成都没有的订单也要进候选：staff_id 回落 orders.staff_id（经手人）。
                cmd.CommandText =
                    "SELECT o.id," +
                    " COALESCE((SELECT MAX(c.staff_id) FROM commissions c WHERE c.order_id=o.id AND c.settled=0), o.staff_id)," +
                    " (SELECT COUNT(*) FROM commissions c WHERE c.order_id=o.id AND c.settled=1)," +
                    " COALESCE((SELECT MAX(c.type) FROM commissions c WHERE c.order_id=o.id AND c.settled=0),'')" +
                    " FROM orders o" +
                    " WHERE o.status='正常' AND (o.kind='售卡' OR (o.kind='消费' AND o.card_covered>0))" +
                    " ORDER BY o.id";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        cand.Add(new object[] { r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3) });
            }
            foreach (object[] c in cand)
            {
                int orderId = (int)c[0], staffId = (int)c[1], settledCnt = (int)c[2];
                string type = (string)c[3];
                if (settledCnt > 0) continue;                       // 已结算：钱已发，不碰
                decimal oldComm = SumUnsettled(conn, txn, orderId, "commission");
                decimal oldBase = SumUnsettled(conn, txn, orderId, "amount");
                decimal baseAmount; bool modeDriven; decimal excess;   // excess 只在结账时用到（重计不重复提示）
                decimal newComm = OrderCommission(conn, txn, orderId, staffId, newMode, out baseAmount, out modeDriven, out excess);
                if (!modeDriven) continue;                          // 充值等：不随模式变
                if (Math.Abs(newComm - oldComm) < 0.005m) continue; // 新规矩下一样，不动
                if (oldComm != 0m)
                {
                    // 冲正行：提成与基数一起取负（基数取负才能守住「业绩=项目原价只算一次」）
                    decimal oldRate = oldBase > 0m ? Math.Round(oldComm / oldBase, 4) : 0m;
                    InsertRaw(conn, txn, orderId, staffId, type, -oldBase, oldRate, -oldComm);
                    reversed++;
                }
                if (newComm > 0m)
                {
                    string newType = type.Length > 0 ? type : "消费";
                    decimal newRate = baseAmount > 0m ? Math.Round(newComm / baseAmount, 4) : 0m;
                    InsertRaw(conn, txn, orderId, staffId, newType, baseAmount, newRate, newComm);
                    added++;
                }
            }
            return new int[] { reversed, added };
        }

        /// <summary>该单未结算提成行的某列合计（col 由本文件内定，非外部输入）。</summary>
        private static decimal SumUnsettled(SQLiteConnection conn, SQLiteTransaction txn, int orderId, string col)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(SUM(" + col + "),0) FROM commissions WHERE order_id=@o AND settled=0";
                var p = cmd.CreateParameter(); p.ParameterName = "@o"; p.Value = orderId; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
            }
        }

        /// <summary>原始插入一行提成（冲正/重计用，允许 amount=0；不走 InsertOne 的 amount>0 门）。</summary>
        private static void InsertRaw(SQLiteConnection conn, SQLiteTransaction txn,
            int orderId, int staffId, string type, decimal amount, decimal rate, decimal commission)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled)" +
                    " VALUES(@o,@s,@t,@a,@r,@c,'100%',0)";
                W.P(cmd, "@o", orderId); W.P(cmd, "@s", staffId); W.P(cmd, "@t", type);
                W.P(cmd, "@a", amount); W.P(cmd, "@r", rate); W.P(cmd, "@c", commission);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>接口层：读当前次卡提成结算方式。</summary>
        public string GetCardCommissionMode()
        {
            return CardCommissionMode();
        }

        /// <summary>
        /// 接口层：切次卡提成结算方式。**对未结算提成立即重计**（店主 10-15）——
        /// 未结算的售卡/卡抵消费提成行按新规矩冲正+重计（只增不删，原行不动）；
        /// 已结算（钱已发）与已退款订单一律不碰。设置与重计同一事务，失败整体回滚。
        /// </summary>
        public void SaveCardCommissionMode(string mode, string operatorName)
        {
            if (mode != "sale" && mode != "use" && mode != "split")
                throw new BizException("不支持的次卡提成结算方式");
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    DatabaseInitializer.SetSetting(conn, txn, "card_commission_mode", mode);
                    int[] rec = ApplyModeToUnsettled(conn, txn, mode);
                    W.Log(conn, txn, "员工提成", "次卡提成设置",
                        "结算方式改为「" + CardCommissionModeName(mode) + "」；未结算提成重计：冲正 " +
                        rec[0] + " 行、重计 " + rec[1] + " 行（已结算/已退款不碰）");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        public decimal[] GetRates()
        {
            return new decimal[]
            {
                Convert.ToDecimal(DatabaseInitializer.GetSetting("recharge_commission_rate", "0.03")),
                Convert.ToDecimal(DatabaseInitializer.GetSetting("consume_commission_rate", "0.05"))
            };
        }

        /// <summary>
        /// 员工个人提成比例（0-1）。**无个人比例返回 -1** —— 调用方据此判断是否覆盖。
        /// 存在 settings 键 staff_rate_&lt;staffId&gt;（SaveStaff 写入、rate&lt;0 时删除）。
        /// </summary>
        public static decimal PersonalRate(SQLiteConnection conn, SQLiteTransaction txn, int staffId)
        {
            if (staffId <= 0) return -1m;
            string v = DatabaseInitializer.GetSetting(conn, txn, "staff_rate_" + staffId, null);
            if (v == null) return -1m;
            decimal n;
            if (decimal.TryParse(v, out n) && n >= 0m && n <= 1m) return n;
            return -1m;   // 脏值不当作个人比例，回落全局
        }

        /// <summary>
        /// 提成比例解析（09-30 店主定口径）：**个人比例优先，没有才回落全局**。返回小数 0~1。
        /// 全局键：充值 recharge_commission_rate（默认 0.03）、消费 consume_commission_rate（默认 0.05）。
        /// </summary>
        public static decimal ResolveRate(SQLiteConnection conn, SQLiteTransaction txn, int staffId, string type)
        {
            decimal personal = PersonalRate(conn, txn, staffId);
            if (personal >= 0m) return personal;
            bool isRecharge = type == "充值";
            return Convert.ToDecimal(DatabaseInitializer.GetSetting(conn, txn,
                isRecharge ? "recharge_commission_rate" : "consume_commission_rate",
                isRecharge ? "0.03" : "0.05"));
        }

        /// <summary>
        /// 写入一行提成（挂调用方事务，与单据同生共死）。rate 与 commission 由调用方按语义算好：
        /// 消费是多行加权平均率 + 提成总额独立累加，故两处都要传。0 元单不产生提成行。
        /// </summary>
        public static void InsertOne(SQLiteConnection conn, SQLiteTransaction txn,
            int orderId, int staffId, string type, decimal amount, decimal rate, decimal commission)
        {
            if (amount <= 0m) return;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled)" +
                    " VALUES(@o,@s,@t,@a,@r,@c,'100%',0)";
                W.P(cmd, "@o", orderId);
                W.P(cmd, "@s", staffId);
                W.P(cmd, "@t", type);
                W.P(cmd, "@a", amount);
                W.P(cmd, "@r", rate);
                W.P(cmd, "@c", commission);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 单比例直乘计提（充值用）：commission = amount × rate，四舍五入到分。
        /// 与 InsertOne 分开命名（不走同名重载）—— BLL 方法反射按名取参，重载会 AmbiguousMatch。
        /// </summary>
        public static void InsertByRate(SQLiteConnection conn, SQLiteTransaction txn,
            int orderId, int staffId, string type, decimal amount, decimal rate)
        {
            InsertOne(conn, txn, orderId, staffId, type, amount, rate, Round2(amount * rate));
        }

        /// <summary>某员工在某提成项上的最终口径（店主 10-11 改造）。</summary>
        public class ItemCommission
        {
            /// <summary>true=按次数结算（每做一次拿固定金额）；false=按价格百分比。</summary>
            public bool IsCount;
            /// <summary>百分比模式：0~1 比例。</summary>
            public decimal Rate;
            /// <summary>次数模式：单次固定金额。</summary>
            public decimal PerCount;
        }

        /// <summary>员工逐项提成配置行（员工管理矩阵持久化用）。</summary>
        public class ItemRateRow
        {
            public int StaffId;
            /// <summary>0=充值；&gt;0=service_items.id；-1=通配（迁移用，UI 不暴露）。</summary>
            public int ItemId;
            public decimal Value;
            public string UpdatedAt;
        }

        /// <summary>员工逐项提成保存输入。</summary>
        public class ItemRateInput
        {
            public int ItemId;
            public decimal Value;
        }

        /// <summary>
        /// 读某员工在某 item_id 上的个人配置值（staff_item_rates）。无记录返回 null。
        /// item_id：0=充值，-1=通配，&gt;0=具体项目。
        /// </summary>
        private static decimal? StaffItemRate(SQLiteConnection conn, SQLiteTransaction txn, int staffId, int itemId)
        {
            if (staffId <= 0) return null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT value FROM staff_item_rates WHERE staff_id=@s AND item_id=@i";
                W.P(cmd, "@s", staffId); W.P(cmd, "@i", itemId);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return null;
                decimal n;
                if (decimal.TryParse(Convert.ToString(v), out n)) return n;
                return null;
            }
        }

        /// <summary>
        /// 解析某员工在某项目上的提成口径（店主 10-11 改造：支持逐项 + 按次数）。
        /// 优先级：个人(具体项) &gt; 个人(通配 -1, 仅百分比项) &gt; 项目值 &gt; 全局兜底。
        ///   · isCountItem=true（项目 mode='count'）：只用 个人具体项 / 项目 per_count，通配百分比不适用；
        ///     无配置则 per_count=0（需店主在矩阵或项目里填）。
        ///   · isCountItem=false：先个人具体项，再通配(-1)百分比，再项目 commission_rate，最后全局消费比例。
        /// projectRate / projectPerCount 由调用方从 service_items 行读好传入（避免本方法再开连接）。
        /// </summary>
        public static ItemCommission ResolveItemCommission(SQLiteConnection conn, SQLiteTransaction txn, int staffId, int itemId, bool isCountItem, decimal projectRate, decimal projectPerCount)
        {
            decimal? specific = StaffItemRate(conn, txn, staffId, itemId);
            if (specific.HasValue)
            {
                if (isCountItem) return new ItemCommission { IsCount = true, PerCount = specific.Value };
                return new ItemCommission { IsCount = false, Rate = specific.Value };
            }
            if (!isCountItem)
            {
                decimal? wild = StaffItemRate(conn, txn, staffId, -1);
                if (wild.HasValue) return new ItemCommission { IsCount = false, Rate = wild.Value };
            }
            if (isCountItem) return new ItemCommission { IsCount = true, PerCount = projectPerCount };
            decimal gcon = Convert.ToDecimal(DatabaseInitializer.GetSetting(conn, txn, "consume_commission_rate", "0.05"));
            return new ItemCommission { IsCount = false, Rate = projectRate >= 0m ? projectRate : gcon };
        }

        /// <summary>
        /// 解析某员工的充值提成比例（0~1）：个人(充值项 item_id=0) &gt; 全局 recharge_commission_rate。
        /// 旧 staff_rate_&lt;id&gt; 已迁移成 (staff_id,0)，故个人充值比例优先于全局。
        /// </summary>
        public static decimal ResolveRechargeRate(SQLiteConnection conn, SQLiteTransaction txn, int staffId)
        {
            decimal? specific = StaffItemRate(conn, txn, staffId, 0);
            if (specific.HasValue) return specific.Value;
            return Convert.ToDecimal(DatabaseInitializer.GetSetting(conn, txn, "recharge_commission_rate", "0.03"));
        }

        /// <summary>
        /// 保存某员工的逐项提成配置（员工管理矩阵）。逐行 upsert；
        /// value&lt;=0 视为「清除该项个人配置」（回落项目值/全局）。通配(-1)不在此写入（迁移专用）。
        /// </summary>
        public void SaveStaffItemRates(int staffId, List<ItemRateInput> rates, string operatorName)
        {
            if (staffId <= 0) throw new BizException("员工不存在");
            if (rates == null) rates = new List<ItemRateInput>();
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    foreach (var r in rates)
                    {
                        if (r.ItemId == -1) continue; // 通配不暴露，UI 不传
                        if (r.Value <= 0m)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = txn;
                                cmd.CommandText = "DELETE FROM staff_item_rates WHERE staff_id=@s AND item_id=@i";
                                W.P(cmd, "@s", staffId); W.P(cmd, "@i", r.ItemId);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = txn;
                                cmd.CommandText =
                                    "INSERT INTO staff_item_rates(staff_id,item_id,value,updated_at) VALUES(@s,@i,@v,datetime('now','localtime'))" +
                                    " ON CONFLICT(staff_id,item_id) DO UPDATE SET value=@v, updated_at=datetime('now','localtime')";
                                W.P(cmd, "@s", staffId); W.P(cmd, "@i", r.ItemId); W.P(cmd, "@v", r.Value);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                    W.Log(conn, txn, "员工提成", "修改员工提成配置", "员工#" + staffId + " 共 " + rates.Count + " 项");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>读取某员工的逐项提成配置（员工管理矩阵回填用）。</summary>
        public List<ItemRateRow> GetStaffItemRates(int staffId)
        {
            var list = new List<ItemRateRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT staff_id,item_id,value,updated_at FROM staff_item_rates WHERE staff_id=@s";
                W.P(cmd, "@s", staffId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new ItemRateRow
                        {
                            StaffId = r.GetInt32(0),
                            ItemId = r.GetInt32(1),
                            Value = Convert.ToDecimal(r.GetValue(2)),
                            UpdatedAt = r.IsDBNull(3) ? "" : r.GetString(3)
                        });
                }
            }
            return list;
        }

        private static decimal Round2(decimal v)
        {
            return Math.Round(v, 2, MidpointRounding.AwayFromZero);
        }
    }

    public class PermissionService
    {
        /// <summary>
        /// 权限模块清单（店主 09-27 定：10 项固定，与前端勾选清单共用同一份）。
        /// 「退款/改价」锁死店长专属 —— 后端 SetPerm / SetAccountPerms 双处拦截。
        /// </summary>
        public static readonly string[] ModuleNames =
        {
            "会员管理", "收银开单", "退款/改价", "储值与次卡", "交接班日结",
            "报表导出", "员工提成", "权限设置", "数据备份", "系统设置",
            // 店主 10-24：积分商城独立成模块——收银员/技师能进「积分商城」兑换礼品，
            // 但不给「系统设置」（后者含积分规则修改、礼品增删改等管理功能）。
            "积分商城"
        };

        /// <summary>
        /// 新非店长账号的默认权限（店主 11-05：收银员必须能充值、卖次卡）。
        /// 与 DAL 的 SeedIfEmpty.cashierOn、前端 PERM_CASHIER_DEFAULT 三处同源，改一处必须同步三处。
        /// 管理类模块（系统设置 / 退款改价 / 数据备份 / 权限设置 等）一律不给。
        /// </summary>
        private static readonly string[] CashierDefault =
            { "会员管理", "收银开单", "交接班日结", "积分商城", "储值与次卡" };

        private const string ModuleRefund = "退款/改价";

        /// <summary>
        /// M10 服务端权限校验（店主 10-18）：确认当前登录账号对「某模块」放行，供 Bridge 入口统一拦。
        /// 判定与前端 perm.canModule 同源（店长恒放行；按人权限 staff_perms 优先、回落 role_perms）。
        /// 边界：本方法拦的是「已登录的非店长账号」绕过前端 UI、直接构造脚本调敏感写接口；
        /// 未登录 / 店长一律放行（冒烟脚本反射直调 BLL 从不登录、后台自动调度 autoTick 亦不登录）。
        /// 破坏性写（Refund/DeleteMember/WriteOffBalance）另有 <see cref="Session.IsManager"/> 硬兜底，本方法不替代、只做第二道按人把关。
        /// </summary>
        public void RequireModule(string module)
        {
            if (string.IsNullOrEmpty(module)) return;          // 无模块要求的调用不拦
            if (Session.IsManager) return;                      // 店长恒放行（防把自己锁在系统外）
            if (!Session.IsLoggedIn) return;                    // 未登录放行（冒烟/自动调度/后台路径）
            var perms = ResolvePerms(null, null, Session.StaffId, Session.Role);
            if (perms[module] == false)
                throw new BizException("当前账号没有「" + module + "」的操作权限，请通知店长在「权限设置」里开通");
        }

        public class StaffRow
        {
            public int Id;
            public string Name;
            /// <summary>登录账号（staff.login_name，全库唯一）。姓名可以重复，登录靠它 ——
            /// 「登录员工」与「操作员工」是两件事：登录认账号，开单认经手人下拉（店主 11-05）。</summary>
            public string LoginName;
            public string Role;
            public string Phone;
            public bool Enabled;
            /// <summary>个人提成比例（0-1）；取自 settings 键 staff_rate_&lt;id&gt;，缺省用全局比例。</summary>
            public decimal Rate;
            /// <summary>经手单据数（历史留痕，删除员工后依然保留）。</summary>
            public int Orders;
            /// <summary>经手业绩合计（receivable 求和，已红冲的会自动扣回）。</summary>
            public decimal Amount;
            /// <summary>是否已设登录密码（true=登录账号，false=纯登记员工/技师）。</summary>
            public bool HasPwd;
            /// <summary>A2 底薪（10-21）：默认 0 = 不启用；非 0 时工资单应发 = 底薪 + 已结提成。</summary>
            public decimal BaseSalary;
            /// <summary>该账号是否已单独配置过权限（staff_perms 里有记录）。</summary>
            public bool Custom;
        }

        public class Matrix
        {
            public List<string> Modules;
            public List<string> Roles;
            public Dictionary<string, List<bool>> Perms;
        }

        /// <summary>
        /// 按账号权限视图（设置页权限弹窗用）：
        /// Modules = 全部 10 个模块名；Allowed[module] = 该账号生效权限。
        /// 生效口径：staff_perms 有记录 → 以 staff_perms 为准；一条都没有 → 回落到该账号角色的 role_perms。
        /// </summary>
        public class AccountPermData
        {
            public int StaffId;
            public string Name;
            public string Role;
            public bool Custom;
            public List<string> Modules;
            public Dictionary<string, bool> Allowed;
        }

        public class LogRow
        {
            public string Time;
            public string Operator;
            public string Module;
            public string Action;
            public string Detail;
            public string Result;
        }

        public List<StaffRow> StaffList()
        {
            var list = new List<StaffRow>();
            // 个人提成比例存在 settings 里：先一次性取回，避免在打开游标的同时再开连接（N+1）
            var rates = new Dictionary<int, decimal>();
            // 已单独配过权限的账号（staff_perms 里有任意一行）—— 同一个连接里顺手取回
            var custom = new HashSet<int>();
            using (var rc = Db.OpenConnection())
            using (var rcmd = rc.CreateCommand())
            {
                rcmd.CommandText = "SELECT key,value FROM settings WHERE key LIKE 'staff_rate_%'";
                using (var rr = rcmd.ExecuteReader())
                {
                    while (rr.Read())
                    {
                        string k = rr.GetString(0);
                        int sid;
                        if (!int.TryParse(k.Substring("staff_rate_".Length), out sid)) continue;
                        decimal d;
                        if (decimal.TryParse(rr.GetString(1), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out d))
                            rates[sid] = d;
                    }
                }
            }
            try
            {
                using (var rc2 = Db.OpenConnection())
                using (var rcmd2 = rc2.CreateCommand())
                {
                    rcmd2.CommandText = "SELECT DISTINCT staff_id FROM staff_perms";
                    using (var rr2 = rcmd2.ExecuteReader())
                    {
                        while (rr2.Read()) custom.Add(rr2.GetInt32(0));
                    }
                }
            }
            catch
            {
                // staff_perms 表在老库上可能还不存在（EnsureDatabase 下次启动会补建），此时按「未单独配置」处理
            }
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 交易记录列：单据数 / 业绩合计。
                // 单据数＝历史留痕（含已退款），业绩合计＝排除已退款（10-13 H1/H2/H4：
                // 退款红冲后原订单翻「已退款」，须从业绩里核减，否则账面虚高）。
                cmd.CommandText =
                    "SELECT s.id,s.name,s.role,IFNULL(s.phone,''),s.enabled," +
                    " (IFNULL(s.pwd_hash,'')<>'')," +
                    " (SELECT COUNT(*) FROM orders o WHERE o.staff_id=s.id)," +
                    " IFNULL((SELECT SUM(o.receivable) FROM orders o WHERE o.staff_id=s.id AND o.status<>'已退款'),0)," +
                    " IFNULL(s.base_salary,0)," +
                    " IFNULL(s.login_name,'')" +
                    " FROM staff s WHERE s.deleted_at IS NULL" +
                    " ORDER BY CASE s.role WHEN '店长' THEN 0 WHEN '收银员' THEN 1 ELSE 2 END, s.id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new StaffRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            LoginName = r.GetString(9),
                            Role = r.GetString(2),
                            Phone = r.GetString(3),
                            Enabled = Convert.ToInt32(r.GetValue(4)) == 1,
                            HasPwd = Convert.ToInt32(r.GetValue(5)) == 1,
                            Custom = custom.Contains(r.GetInt32(0)),
                            Rate = rates.ContainsKey(r.GetInt32(0)) ? rates[r.GetInt32(0)] : -1m,
                            Orders = Convert.ToInt32(r.GetValue(6)),
                            Amount = Convert.ToDecimal(r.GetValue(7)),
                            BaseSalary = Convert.ToDecimal(r.GetValue(8))
                        });
                }
            }
            return list;
        }

        public Matrix MatrixData()
        {
            string[] modules = ModuleNames;
            // L9（店主 10-14）：技师进矩阵——老库没有技师角色模板时账号回落全 false、
            // 连基础页都进不去；模板由 DatabaseInitializer.BackfillTechRolePerms 补齐，
            // 这里暴露出来，店主就能在「权限设置」里直接勾选技师能进哪些模块。
            var m = new Matrix { Modules = new List<string>(modules), Roles = new List<string> { "店长", "收银员", "技师" }, Perms = new Dictionary<string, List<bool>>() };
            var map = new Dictionary<string, Dictionary<string, bool>>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT role,module,allowed FROM role_perms";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string role = r.GetString(0), mod = r.GetString(1);
                        bool ok = Convert.ToInt32(r.GetValue(2)) == 1;
                        if (!map.ContainsKey(role)) map[role] = new Dictionary<string, bool>();
                        map[role][mod] = ok;
                    }
                }
            }
            foreach (string role in m.Roles)
            {
                var flags = new List<bool>();
                foreach (string mod in modules)
                    flags.Add(role == "店长" || (map.ContainsKey(role) && map[role].ContainsKey(mod) && map[role][mod]));
                m.Perms[role] = flags;
            }
            return m;
        }

        public void SetPerm(string role, string module, bool allowed)
        {
            if (role == "店长") throw new BizException("店长角色拥有全部权限且不可修改");
            // 退款/改价 锁死为店长专属（店主 09-27 定：只有店长可退款，店员/收银员无法抉择）。
            // 非店长角色一律拒绝开通，后端 Refund 亦有 Session.IsManager 硬校验兜底。
            if (module == ModuleRefund && allowed)
                throw new BizException("「退款/改价」仅限店长操作，无法授权给" + role);
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT OR REPLACE INTO role_perms(role,module,allowed) VALUES(@r,@m,@a)";
                        W.P(cmd, "@r", role); W.P(cmd, "@m", module); W.P(cmd, "@a", allowed ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "权限设置", "修改角色权限", role + " 「" + module + "」 " + (allowed ? "开通" : "关闭"));
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        // ---------- 按账号权限（店主 09-27：权限按人勾选，不再只按角色）----------
        // 解析口径（唯一实现，界面与门控都走它）：
        //   1) 店长 → 全部模块恒 true（防把自己锁在系统外）；
        //   2) 其他账号 → staff_perms 里有任意一行 = 已单独配置，以 staff_perms 为准（缺的模块 = 未开通）；
        //      staff_perms 里一行都没有 = 还没配过，回落到该角色的 role_perms（老账号零改造升级）。

        /// <summary>解析某账号的生效权限（全部模块，缺省 false）。conn/txn 可空 = 用独立连接自管理。</summary>
        private Dictionary<string, bool> ResolvePerms(SQLiteConnection conn, SQLiteTransaction txn, int staffId, string role)
        {
            var res = new Dictionary<string, bool>();
            foreach (string m in ModuleNames) res[m] = false;
            if (staffId <= 0 || role == null) return res;
            if (role == "店长")
            {
                foreach (string m in ModuleNames) res[m] = true;
                return res;
            }

            var own = new Dictionary<string, bool>();
            bool hasOwn = false;
            SQLiteConnection owned = null;
            try
            {
                var c = conn ?? (owned = Db.OpenConnection());
                using (var cmd = c.CreateCommand())
                {
                    if (txn != null) cmd.Transaction = txn;
                    cmd.CommandText = "SELECT module,allowed FROM staff_perms WHERE staff_id=@id";
                    W.P(cmd, "@id", staffId);
                    try
                    {
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string m = r.GetString(0);
                                own[m] = Convert.ToInt32(r.GetValue(1)) == 1;
                                hasOwn = true;
                            }
                        }
                    }
                    catch
                    {
                        hasOwn = false; // 老库上 staff_perms 表尚未建，回落角色默认
                    }
                }
            }
            finally
            {
                if (owned != null) owned.Dispose();
            }

            if (hasOwn)
            {
                foreach (string m in ModuleNames)
                    res[m] = own.ContainsKey(m) && own[m];
                return res;
            }

            // 回落：按角色的 role_perms 默认值
            using (var c3 = Db.OpenConnection())
            using (var cmd = c3.CreateCommand())
            {
                cmd.CommandText = "SELECT module,allowed FROM role_perms WHERE role=@r";
                W.P(cmd, "@r", role);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string m = r.GetString(0);
                        res[m] = Convert.ToInt32(r.GetValue(1)) == 1;
                    }
                }
            }
            return res;
        }

        /// <summary>取某账号的按人权限视图（设置页「权限」弹窗用）。</summary>
        public AccountPermData AccountPerms(int staffId)
        {
            string name, role;
            bool custom = false;
            using (var conn = Db.OpenConnection())
            {
                LoadStaffNameRole(conn, null, staffId, out name, out role);
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM staff_perms WHERE staff_id=@id";
                        W.P(cmd, "@id", staffId);
                        custom = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                    }
                }
                catch { custom = false; }
            }
            var perms = ResolvePerms(null, null, staffId, role);
            var data = new AccountPermData
            {
                StaffId = staffId,
                Name = name,
                Role = role,
                Custom = custom,
                Modules = new List<string>(ModuleNames),
                Allowed = new Dictionary<string, bool>(perms)
            };
            return data;
        }

        /// <summary>
        /// 覆盖式设置某账号的按人权限：把 staff_perms 里该账号的记录全删了重插。
        /// 参数是「已开通的模块名数组」，其余模块一律落 allowed=0，界面勾选即所见即所得。
        /// </summary>
        public void SetAccountPerms(int staffId, string[] modules, string operatorName)
        {
            var allowed = new HashSet<string>(modules == null ? new string[0] : modules);
            string name, role;
            using (var conn = Db.OpenConnection())
            {
                LoadStaffNameRole(conn, null, staffId, out name, out role);
            }

            // 退款锁死店长专属：非店长账号无论怎么勾都不给（与 SetPerm / Refund 硬校验三处一致）
            if (role != "店长" && allowed.Contains(ModuleRefund))
                throw new BizException("「退款/改价」仅限店长操作，无法授权给" + name);

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "DELETE FROM staff_perms WHERE staff_id=@id";
                        W.P(cmd, "@id", staffId);
                        cmd.ExecuteNonQuery();
                    }
                    int n = 0;
                    foreach (string m in ModuleNames)
                    {
                        bool ok = role == "店长" ? true : allowed.Contains(m);
                        TxnSetPerm(conn, txn, staffId, m, ok);
                        if (ok) n++;
                    }
                    W.Log(conn, txn, "权限设置", "修改账号权限",
                        name + "（" + role + "）开通 " + n + " 个模块（按人单独配置）");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 当前登录账号的生效权限模块名（前端登录一次拉一次，用于页签与按钮门控）。
        /// 店长返回全部模块；未登录返回空数组（前端按无权限处理）。
        /// </summary>
        public string[] MyPerms()
        {
            var list = new List<string>();
            if (!Session.IsLoggedIn) return list.ToArray();
            var perms = ResolvePerms(null, null, Session.StaffId, Session.Role);
            foreach (string m in ModuleNames)
                if (perms[m]) list.Add(m);
            return list.ToArray();
        }

        public void ToggleStaff(int id, string operatorName)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string name = null, role = null; bool enabled = false; int managerOn = 0;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        // 列序必须显式带上 id：0=id 1=name 2=role 3=enabled
                        cmd.CommandText = "SELECT id,name,role,enabled FROM staff WHERE deleted_at IS NULL";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                int rid = r.GetInt32(0);
                                string rname = r.GetString(1);
                                string rrole = r.GetString(2);
                                bool ren = Convert.ToInt32(r.GetValue(3)) == 1;
                                if (rrole == "店长" && ren) managerOn++;
                                if (rid == id) { name = rname; role = rrole; enabled = ren; }
                            }
                        }
                    }
                    if (name == null) throw new BizException("账号不存在");
                    if (role == "店长" && enabled) throw new BizException("至少保留一名店长账号，不可停用");
                    if (role == "店长" && !enabled && managerOn >= 1) { /* 重新启用店长允许 */ }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE staff SET enabled=@e WHERE id=@id";
                        W.P(cmd, "@e", enabled ? 0 : 1); W.P(cmd, "@id", id);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "权限设置", enabled ? "停用账号" : "启用账号", name + "（" + role + "）");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        // ---------- 事务内 settings 读写 ----------
        // 关键：绝不能在已开启的事务里再 Db.OpenConnection() 读同一张表——
        // WAL 下写事务持有锁，第二条连接会死等 → SQLiteException: database is locked。
        // 所有事务内的 settings 操作都必须挂到同一个 conn/txn 上。
        // upsert 语句本身只有一份真源（DatabaseInitializer.SettingUpsert），此处不再另写一遍。
        private static void TxnSetSetting(SQLiteConnection conn, SQLiteTransaction txn, string key, string value)
        {
            DatabaseInitializer.SetSetting(conn, txn, key, value);
        }

        private static void TxnDelSetting(SQLiteConnection conn, SQLiteTransaction txn, string key)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "DELETE FROM settings WHERE key=@k";
                W.P(cmd, "@k", key);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 读员工的姓名与角色；不存在则抛错。AccountPerms / SetAccountPerms / DeleteStaff 三处共用，
        /// 保证「不存在」文案与查询列序只有一份。txn 可空（不在事务里时传 null）。
        /// </summary>
        private static void LoadStaffNameRole(SQLiteConnection conn, SQLiteTransaction txn, int staffId, out string name, out string role)
        {
            name = null; role = null;
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = "SELECT name,role FROM staff WHERE id=@id";
                W.P(cmd, "@id", staffId);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read()) { name = r.GetString(0); role = r.GetString(1); }
                }
            }
            if (name == null) throw new BizException("账号不存在");
        }

        public void ResetPwd(int staffId, string operatorName)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE staff SET pwd_hash=@h WHERE id=@id";
                        W.P(cmd, "@h", DatabaseInitializer.HashPassword("123456"));
                        W.P(cmd, "@id", staffId);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "权限设置", "重置密码", "员工ID " + staffId + " 密码重置为初始密码");
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>员工/账号表单的公共校验（姓名、角色、手机号）。SaveStaff 与 SaveAccount 共用，保证规则与文案一致。</summary>
        private static void CheckStaffBasics(string name, string role, ref string phone)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BizException("姓名不能为空");
            if (name.Length > 20) throw new BizException("姓名不能超过 20 个字符");
            if (role != "店长" && role != "收银员" && role != "技师")
                throw new BizException("角色只能是 店长 / 收银员 / 技师");
            if (string.IsNullOrWhiteSpace(phone)) phone = "";
        }

        /// <summary>
        /// 写 staff 行：id&lt;=0 新建、&gt;0 更新，返回员工 id（新建取 last_insert_rowid）。
        /// pwdHash 语义 = 本次要写入的密码哈希：新建时必须给；更新时传 null 表示「不改密码列」。
        /// SaveStaff / SaveAccount 共用，SQL 只有一份。
        ///
        /// 11-04 店主：姓名可重复（店里两个「李」），但登录名必须唯一。
        ///   · 新建：登录名默认=姓名，被占了就自动加 -2/-3…
        ///   · 更新：**登录名保持不变**——改显示名不该把人家登录弄断。
        /// </summary>
        private static int UpsertStaffRow(SQLiteConnection conn, SQLiteTransaction txn, int id, string name, string role, string phone, bool enabled, string pwdHash, decimal baseSalary)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                if (id <= 0)
                {
                    string login = PickUniqueLoginName(conn, txn, 0, name);
                    cmd.CommandText = "INSERT INTO staff(name,login_name,role,phone,pwd_hash,enabled,base_salary) VALUES(@n,@l,@r,@p,@h,@e,@b)";
                    W.P(cmd, "@n", name); W.P(cmd, "@l", login); W.P(cmd, "@r", role); W.P(cmd, "@p", phone);
                    W.P(cmd, "@h", pwdHash); W.P(cmd, "@e", enabled ? 1 : 0);
                    W.P(cmd, "@b", baseSalary < 0m ? 0m : baseSalary);
                    cmd.ExecuteNonQuery();
                    using (var c2 = conn.CreateCommand())
                    {
                        c2.Transaction = txn;
                        c2.CommandText = "SELECT last_insert_rowid()";
                        return Convert.ToInt32(c2.ExecuteScalar());
                    }
                }
                if (pwdHash == null)
                {
                    // base_salary<0 = 调用方没传（老调用路径），不动该列
                    cmd.CommandText = baseSalary < 0m
                        ? "UPDATE staff SET name=@n,role=@r,phone=@p,enabled=@e WHERE id=@id"
                        : "UPDATE staff SET name=@n,role=@r,phone=@p,enabled=@e,base_salary=@b WHERE id=@id";
                }
                else
                {
                    cmd.CommandText = baseSalary < 0m
                        ? "UPDATE staff SET name=@n,role=@r,phone=@p,pwd_hash=@h,enabled=@e WHERE id=@id"
                        : "UPDATE staff SET name=@n,role=@r,phone=@p,pwd_hash=@h,enabled=@e,base_salary=@b WHERE id=@id";
                    W.P(cmd, "@h", pwdHash);
                }
                W.P(cmd, "@n", name); W.P(cmd, "@r", role); W.P(cmd, "@p", phone);
                W.P(cmd, "@e", enabled ? 1 : 0); W.P(cmd, "@id", id);
                if (baseSalary >= 0m) W.P(cmd, "@b", baseSalary);
                cmd.ExecuteNonQuery();
            }
            return id;
        }

        /// <summary>
        /// 取一个没人用的登录名：先要 preferred，被占了依次试 -2/-3/…（不含自己 id）。
        /// </summary>
        private static string PickUniqueLoginName(SQLiteConnection conn, SQLiteTransaction txn, int selfId, string preferred)
        {
            string baseName = string.IsNullOrWhiteSpace(preferred) ? "user" : preferred.Trim();
            for (int n = 0; n < 200; n++)
            {
                string cand = n == 0 ? baseName : baseName + "-" + (n + 1);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = txn;
                    cmd.CommandText = "SELECT COUNT(*) FROM staff WHERE IFNULL(login_name,'')=@l AND id<>@self";
                    W.P(cmd, "@l", cand); W.P(cmd, "@self", selfId);
                    int hit = Convert.ToInt32(cmd.ExecuteScalar());
                    if (hit == 0) return cand;
                }
            }
            return baseName + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>
        /// 新增 / 编辑员工。员工是「实际操作的人」（技师、收银员），不登录也可登记，
        /// 收银时可选其姓名，业绩与提成记在其名下。登录凭证由「系统设置 · 账号密码」单独维护。
        /// </summary>
        public void SaveStaff(int id, string name, string role, string phone, decimal rate, bool enabled, string operatorName, decimal baseSalary = -1m)
        {
            name = (name ?? "").Trim();
            CheckStaffBasics(name, role, ref phone);
            // rate < 0 是合法特殊值（删除该键、沿用全局比例），故只校验上界
            if (rate > 1m) throw new BizException("提成比例须在 0 - 100% 之间");
            // A2 底薪：负数除了 -1（未传）外不合法；上界同会员充值上限量级
            if (baseSalary < -1m || baseSalary > 99999999m) throw new BizException("底薪金额不合法");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 纯「做项目/拿提成」的员工不登录：pwd_hash 写空串（NOT NULL 但要能判 hasPwd）。
                    // 占位符必须是空串而非 '!nologin!'——否则 ListEnabledStaff/StaffList.hasPwd
                    // （都判 pwd_hash<>''）会把没开登录的人误标成「可登录」，还会漏进登录页下拉清单。
                    // 要给这人开通登录，走「系统设置·账号密码」SaveAccount 设真密码，本行不变。
                    int staffId = UpsertStaffRow(conn, txn, id, name, role, phone, enabled, id <= 0 ? "" : null, baseSalary);

                    // 个人提成比例：<0 表示删除该键、沿用全局比例
                    if (rate < 0) TxnDelSetting(conn, txn, "staff_rate_" + staffId);
                    else TxnSetSetting(conn, txn, "staff_rate_" + staffId,
                        rate.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    // §三十一：个人默认比例同步进 staff_item_rates 通配(-1)槽，
                    // 让新的逐项计提（ResolveItemCommission 通配回落）也能取到；
                    // 与上面 settings 键保持同值，编辑即两边同更，互不漂移。
                    if (rate < 0)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "DELETE FROM staff_item_rates WHERE staff_id=@s AND item_id=-1";
                            W.P(cmd, "@s", staffId);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText =
                                "INSERT INTO staff_item_rates(staff_id,item_id,value,updated_at) VALUES(@s,-1,@v,datetime('now','localtime'))" +
                                " ON CONFLICT(staff_id,item_id) DO UPDATE SET value=@v, updated_at=datetime('now','localtime')";
                            W.P(cmd, "@s", staffId); W.P(cmd, "@v", rate);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    W.Log(conn, txn, "员工管理", id <= 0 ? "新增员工" : "编辑员工",
                        name + "（" + role + "）" + (rate < 0 ? " 提成沿用全局" : " 提成比例 " + (rate * 100m).ToString("0.##") + "%"));
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 新增 / 编辑「登录账号」（系统设置 · 账号密码）。与 SaveStaff 的区别：这里会设置登录密码。
        /// 员工（技师）只登记姓名即可做项目，要不要给登录权限由店长在此单独开通。
        /// 返回账号 id（供前端紧接着调 SetAccountPerms 落按人权限）。
        /// 新建非店长账号时自动写入默认权限（会员管理 + 收银开单）——
        /// 店主 09-27 定「默认只给用得上的，修改类默认不给」，店长要放开就在权限弹窗里打勾。
        /// </summary>
        public int SaveAccount(int id, string name, string role, string phone, string pwd, bool enabled, string operatorName)
        {
            name = (name ?? "").Trim();
            CheckStaffBasics(name, role, ref phone);
            // 店主 10-24 定：技师不提供登录——技师只在「员工管理」登记做项目/算提成，
            // 不进系统。这里从账号管理侧阻断，配合 AuthService.Login 的硬拦形成两道防线。
            if (role == "技师")
                throw new BizException("技师不提供登录账号：请改选「收银员」，或只在员工管理里登记技师");
            if (id <= 0 && string.IsNullOrWhiteSpace(pwd)) throw new BizException("新建账号必须设置初始密码");
            if (!string.IsNullOrWhiteSpace(pwd) && pwd.Length < 6) throw new BizException("密码至少 6 位");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 密码留空 = 不改密码（UpsertStaffRow 传 null 即不更新 pwd_hash 列）；
                    // 新建时必非空（上面已校验），故不会写出 NULL。
                    string hash = string.IsNullOrWhiteSpace(pwd) ? null : DatabaseInitializer.HashPassword(pwd);
                    int staffId = UpsertStaffRow(conn, txn, id, name, role, phone, enabled, hash, -1m);

                    // 新建非店长账号：写入默认权限（幂等：INSERT OR REPLACE），
                    // 让「还没打开过权限弹窗的新账号」也有明确的最小权限，而不是靠角色默认兜底。
                    if (id <= 0 && role != "店长")
                    {
                        foreach (string m in ModuleNames)
                            TxnSetPerm(conn, txn, staffId, m, Array.IndexOf(CashierDefault, m) >= 0);
                    }

                    W.Log(conn, txn, "权限设置", id <= 0 ? "新增登录账号" : "编辑登录账号",
                        name + "（" + role + "）" + (string.IsNullOrWhiteSpace(pwd) ? "" : " 并重置密码")
                        + (id <= 0 && role != "店长" ? "，按默认权限开通" : ""));
                    txn.Commit();
                    return staffId;
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>事务内写 staff_perms 一行（SaveAccount / SetAccountPerms 共用）。</summary>
        private static void TxnSetPerm(SQLiteConnection conn, SQLiteTransaction txn, int staffId, string module, bool allowed)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT OR REPLACE INTO staff_perms(staff_id,module,allowed) VALUES(@id,@m,@a)";
                W.P(cmd, "@id", staffId); W.P(cmd, "@m", module); W.P(cmd, "@a", allowed ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 删除员工（软删除）。店主口径：员工离职是正常现象，允许删除；
        /// 该员工名下的历史单据/流水/提成「不同步删除」，账单上仍按姓名留痕，单独管理。
        /// 实现：写 staff.deleted_at 并置 enabled=0，行保留 —— 历史 join staff 仍能取到姓名。
        /// 店长账号仍不可删（至少保留一名店长）。
        /// </summary>
        public void DeleteStaff(int id, string operatorName)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string name, role;
                    LoadStaffNameRole(conn, txn, id, out name, out role);
                    if (role == "店长") throw new BizException("店长账号不可删除（至少保留一名店长）");

                    // 有多少业务记录只用于「留痕说明」，不再作为拒绝删除的理由
                    int used = 0;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM orders WHERE staff_id=@id)" +
                            " + (SELECT COUNT(*) FROM order_items WHERE staff_id=@id)" +
                            " + (SELECT COUNT(*) FROM commissions WHERE staff_id=@id)";
                        W.P(cmd, "@id", id);
                        used = Convert.ToInt32(cmd.ExecuteScalar());
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        // 11-04 店主：这里**只**从「做项目的人」名单移除（收银台不再能选他、不再计提成），
                        // 不碰 enabled、不碰 pwd_hash —— 登录账号是另一回事，在「设置·登录账号」单独管。
                        // 原实现顺手 enabled=0，等于把登录一起废掉，店主删个人就登不进系统了。
                        cmd.CommandText = "UPDATE staff SET deleted_at=datetime('now','localtime') WHERE id=@id";
                        W.P(cmd, "@id", id);
                        cmd.ExecuteNonQuery();
                    }
                    TxnDelSetting(conn, txn, "staff_rate_" + id);
                    W.Log(conn, txn, "员工管理", "移出员工名单",
                        name + "（" + role + "）不再出现在收银台选人、不再计提成；" +
                        "其登录账号不受影响（如需禁止登录请到「系统设置·登录账号」停用）" +
                        (used > 0 ? "；其名下 " + used + " 条历史单据/提成全部保留" : ""));
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        public List<LogRow> Logs(string keyword, int limit)
        {
            var list = new List<LogRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                string where = "";
                if (!string.IsNullOrEmpty(keyword) && keyword.Trim().Length > 0)
                    where = " WHERE operator LIKE @k OR module LIKE @k OR action LIKE @k OR detail LIKE @k";
                cmd.CommandText = "SELECT created_at,operator,module,action,detail,result FROM op_logs" + where + " ORDER BY id DESC LIMIT @l";
                if (where.Length > 0) W.P(cmd, "@k", "%" + keyword.Trim() + "%");
                W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new LogRow
                        {
                            Time = r.GetString(0),
                            Operator = r.GetString(1),
                            Module = r.GetString(2),
                            Action = r.GetString(3),
                            Detail = r.IsDBNull(4) ? "" : r.GetString(4),
                            Result = r.GetString(5)
                        });
                }
            }
            return list;
        }
    }
}
