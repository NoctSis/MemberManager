using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 收银开单（模块 03，04 交接包 §2.3）：项目/分类查询、开单会员搜索（仅正常状态）、
    /// 结账事务（订单+明细+支付+流水+次卡核销+提成 同一事务，失败整体回滚）、挂单/取单、当班速览。
    /// 恒等式：日结总营收 = 各支付方式之和（payments 合计恒等于 receivable）。
    /// </summary>
    public class PosService
    {
        /// <summary>
        /// 开单会员查询的公共列清单（ListBillingMembers 与 GetBillingMember 共用）。
        /// 抽出来避免两处列清单不同步 —— 原先各抄一份，改一处漏一处就会让某个入口缺字段。
        /// </summary>
        private const string BillingMemberCols =
            "SELECT m.id,m.card_no,m.name,IFNULL(m.phone,''),m.balance,m.points," +
            " (SELECT COALESCE(SUM(mc.remain),0) FROM member_cards mc" +
            "   WHERE mc.member_id=m.id AND mc.status='正常' AND mc.remain>0" +
            "   AND date(mc.expire_date)>=date('now','localtime'))" +
            " FROM members m";

        public class ItemRow
        {
            public int Id;
            public string Name;
            public string Category;
            public decimal Price;
            public string Unit;
            public bool IsCardItem;
        }

        /// <summary>开单会员信息（仅正常状态会员；CardRemain=全部正常未过期次卡余次合计）。</summary>
        public class BillingMember
        {
            public int Id;
            public string CardNo;
            public string Name;
            public string Phone;
            public decimal Balance;
            public int Points;
            public int CardRemain;
        }

        /// <summary>开单会员的一张可用次卡（供收银台展开看「到底是哪几张卡、各剩几次」）。</summary>
        public class CardRow
        {
            public int Id;
            public string CardName;
            public int Remain;
            public int Total;
            public string ExpireDate;
            public string ItemName;    // 卡对应的项目（售卡模板绑定）
            /// <summary>单次折算价：优先用实收 ÷ 总次数（含赠送），老卡无实收时回退「模板售价 ÷ 模板次数」。</summary>
            public decimal UnitPrice;
            public int PurchaseTimes;  // 付费购买次数（不含赠送）
            public int GiftTimes;      // 赠送次数（计入 total，但不收款）
        }

        /// <summary>账单行（UI 传入；价格以库内为准重读，防界面数据过期）。</summary>
        public class CartLine
        {
            public int ItemId;
            public int Qty;
            public bool ByCard;
            // ---- 以下仅取单还原展示用（结账时价格仍按库内现价重读）----
            public string Name;
            public decimal Price;
            public string Unit;
            public bool IsCardItem;
        }

        public class HeldOrderRow
        {
            public int Id;
            public string OrderNo;
            public string MemberName;   // 散客显示「散客」
            public decimal Amount;
            public int ItemCount;
            public string CreatedAt;
            public int? MemberId;
            public string StaffName;    // 挂单人（谁挂的单）
        }

        public class HeldOrder
        {
            public int OrderId;
            public string OrderNo;
            public int? MemberId;
            /// <summary>挂单时的经手人（0=未指定）。**取单后前端要把它恢复成当前经办人**，
            /// 否则结账会用「当前选中/登录账号」，与挂单时选的人不一致（店主 10-04 反馈）。</summary>
            public int StaffId;
            public string StaffName;    // 挂单人姓名（提示用）
            public List<CartLine> Lines = new List<CartLine>();
        }

        /// <summary>当班速览（按营业日；交接班模块上线前以营业日代班次）。</summary>
        public class ShiftInfo
        {
            public decimal Revenue;         // 本班收银（消费单实收合计）
            public int Count;               // 收银笔数
            public decimal BalanceRatio;    // 余额支付占比（0-1）
            public int RefundPending;       // 今日已退款笔数（无审批流程，统计真实发生的退款）
        }

        public class CheckoutResult
        {
            public string OrderNo;
            public decimal Subtotal;
            public decimal CardCovered;
            public decimal Receivable;
            public decimal BalanceUsed;     // 余额实扣（不超过应收）
            public decimal BalanceBack;     // 余额多填部分——原路退回未扣（M3 找零，店主 10-15）
            public decimal CashIn;          // 现金实收入账（已扣找零）
            public decimal Change;          // 现金找零
            public int CardUsedTimes;       // 本次核销次卡次数
        }

        // ---------- 查询 ----------

        /// <summary>项目列表（启用中；category 空串=全部）。</summary>
        public List<ItemRow> ListItems(string category)
        {
            var list = new List<ItemRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                string c = (category ?? "").Trim();
                if (c == "全部") c = "";
                cmd.CommandText =
                    "SELECT id,name,category,price,unit,is_card_item FROM service_items" +
                    " WHERE enabled=1 AND (@c='' OR category=@c) ORDER BY id";
                var p = cmd.CreateParameter(); p.ParameterName = "@c"; p.Value = c; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ItemRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            Category = r.GetString(2),
                            Price = r.GetDecimal(3),
                            Unit = r.GetString(4),
                            IsCardItem = r.GetInt32(5) != 0
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>开单会员搜索：仅状态=正常（冻结/挂失不可开单），模糊匹配 手机号/姓名/卡号。
        /// S2（店主 10-15）：**已注销会员一律不可开单**（默认视图看不到，这里再加一道闸）。</summary>
        public List<BillingMember> SearchBillingMembers(string keyword, int limit)
        {
            var list = new List<BillingMember>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = BillingMemberCols +
                    " WHERE m.status='正常' AND IFNULL(m.deleted_at,'')=''" +
                    " AND (@k='' OR m.phone LIKE '%'||@k||'%' OR m.name LIKE '%'||@k||'%' OR m.card_no LIKE '%'||@k||'%')" +
                    " ORDER BY m.id LIMIT @lim";
                var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; pk.Value = (keyword ?? "").Trim(); cmd.Parameters.Add(pk);
                var pl = cmd.CreateParameter(); pl.ParameterName = "@lim"; pl.Value = limit; cmd.Parameters.Add(pl);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(ReadBillingMember(r));
                }
            }
            return list;
        }

        /// <summary>单个开单会员快照（换会员/结账后刷新余额与次卡余次）。</summary>
        public BillingMember GetBillingMember(int memberId)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = BillingMemberCols + " WHERE m.id=@id AND IFNULL(m.deleted_at,'')=''";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read()) return ReadBillingMember(r);
                }
            }
            return null;
        }

        /// <summary>
        /// 会员可用次卡明细（正常 + 未过期 + 有余次），先到期先用同序。
        /// 用于收银台展开：不必再翻会员档案就知道是哪几张卡、各剩几次、何时到期。
        /// 卡名来自 card_templates.name（member_cards 本身不冗余卡名）。
        /// </summary>
        public List<CardRow> ListMemberCards(int memberId)
        {
            var list = new List<CardRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT mc.id,IFNULL(ct.name,'次卡'),mc.remain,mc.total,IFNULL(mc.expire_date,'')," +
                    " IFNULL(ct.item_name,''),IFNULL(ct.price,0)," +
                    " mc.purchase_times,mc.gift_times,mc.paid_amount,IFNULL(ct.times,0)" +
                    " FROM member_cards mc LEFT JOIN card_templates ct ON ct.id=mc.template_id" +
                    " WHERE mc.member_id=@id AND mc.status='正常' AND mc.remain>0" +
                    " AND date(mc.expire_date)>=date('now','localtime')" +
                    " ORDER BY mc.expire_date ASC, mc.id ASC";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int total = r.GetInt32(3);
                        decimal price = Convert.ToDecimal(r.GetValue(6));
                        int purchase = r.IsDBNull(7) ? total : Convert.ToInt32(r.GetValue(7));
                        int gift = r.IsDBNull(8) ? 0 : Convert.ToInt32(r.GetValue(8));
                        decimal paid = r.IsDBNull(9) ? 0m : Convert.ToDecimal(r.GetValue(9));
                        int tplTimes = r.IsDBNull(10) ? 0 : Convert.ToInt32(r.GetValue(10));
                        // 单次折算价：这张卡实收多少钱 ÷ 卡上总次数（含赠送）。
                        // 老卡没记实收时回退模板口径 —— 注意必须除以「模板次数」而非
                        // 卡总次数，否则赠送的部分会把折算价摊低。
                        decimal unit;
                        if (paid > 0 && total > 0) unit = Math.Round(paid / total, 2);
                        else if (tplTimes > 0) unit = Math.Round(price / tplTimes, 2);
                        else unit = total > 0 ? Math.Round(price / total, 2) : 0m;
                        list.Add(new CardRow
                        {
                            Id = r.GetInt32(0),
                            CardName = r.GetString(1),
                            Remain = r.GetInt32(2),
                            Total = total,
                            ExpireDate = r.GetString(4),
                            ItemName = r.GetString(5),
                            UnitPrice = unit,
                            PurchaseTimes = purchase,
                            GiftTimes = gift,
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>挂单列表（含会员名/散客、金额、项数、时间、挂单人）。</summary>
        public List<HeldOrderRow> ListHeldOrders()
        {
            var list = new List<HeldOrderRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT o.id,o.order_no,o.receivable,o.created_at,o.member_id," +
                    " IFNULL(m.name,'" + UiText.PosWalkIn + "')," +
                    " (SELECT COALESCE(SUM(oi.qty),0) FROM order_items oi WHERE oi.order_id=o.id)," +
                    " IFNULL(s.name,'')" +
                    " FROM orders o LEFT JOIN members m ON m.id=o.member_id" +
                    " LEFT JOIN staff s ON s.id=o.staff_id" +
                    " WHERE o.status='挂单' AND IFNULL(o.voided_at,'')='' ORDER BY o.created_at DESC";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new HeldOrderRow
                        {
                            Id = r.GetInt32(0),
                            OrderNo = r.GetString(1),
                            Amount = r.GetDecimal(2),
                            CreatedAt = r.GetString(3),
                            MemberId = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                            MemberName = r.GetString(5),
                            ItemCount = Convert.ToInt32(r.GetValue(6)),
                            StaffName = r.GetString(7)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>当班速览：本班收银/笔数/余额支付占比/退款待审批（按营业日）。</summary>
        public ShiftInfo GetShiftSnapshot(string bizDate)
        {
            var s = new ShiftInfo();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COALESCE(SUM(receivable),0), COUNT(*) FROM orders" +
                    " WHERE biz_date=@d AND kind='消费' AND status='正常'";
                var p = cmd.CreateParameter(); p.ParameterName = "@d"; p.Value = bizDate; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        s.Revenue = r.GetDecimal(0);
                        s.Count = r.GetInt32(1);
                    }
                }
                using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText =
                        "SELECT COALESCE(SUM(p.amount),0) FROM payments p" +
                        " JOIN orders o ON o.id=p.order_id" +
                        " WHERE o.biz_date=@d AND o.kind='消费' AND o.status='正常' AND p.method='会员余额'";
                    var p2 = cmd2.CreateParameter(); p2.ParameterName = "@d"; p2.Value = bizDate; cmd2.Parameters.Add(p2);
                    decimal balancePaid = Convert.ToDecimal(cmd2.ExecuteScalar());
                    s.BalanceRatio = s.Revenue > 0m ? Math.Round(balancePaid / s.Revenue, 4) : 0m;
                }
            }
            // 今日已退款笔数：店主 09-25 拍板不引入「审批中」状态，这里统计真实发生的退款
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE biz_date=@d AND status='已退款'";
                var p = cmd.CreateParameter(); p.ParameterName = "@d"; p.Value = bizDate; cmd.Parameters.Add(p);
                s.RefundPending = Convert.ToInt32(cmd.ExecuteScalar());
            }
            return s;
        }

        // ---------- 写操作 ----------

        /// <summary>
        /// 挂单：账单行落库为 status='挂单' 订单（单号 H 前缀），不写支付/流水/提成。
        /// 会员可空（散客挂单）。staffId 为空时用当前登录账号（店主可在收银台指定是谁开的单）。
        /// 账单为空抛 PosErrHoldEmpty。
        /// </summary>
        public string HoldOrder(IList<CartLine> lines, int? memberId, int? staffId)
        {
            if (lines == null || lines.Count == 0) throw new BizException(UiText.PosErrHoldEmpty);
            decimal subtotal, cardCovered, receivable;
            var priced = PriceLines(lines, out subtotal, out cardCovered, out receivable);
            string bizDate = Fmt.BizDateOf(DateTime.Now, CutHour());

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string orderNo = NextOrderNo(conn, txn, "H", bizDate);
                    int orderId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText =
                            "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                            " VALUES(@no,@d,@m,@s,@sub,@cc,@rc,'消费','挂单'); SELECT last_insert_rowid();";
                        var pno = cmd.CreateParameter(); pno.ParameterName = "@no"; pno.Value = orderNo; cmd.Parameters.Add(pno);
                        var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = bizDate; cmd.Parameters.Add(pd);
                        var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = memberId.HasValue ? (object)memberId.Value : DBNull.Value; cmd.Parameters.Add(pm);
                        var ps = cmd.CreateParameter(); ps.ParameterName = "@s"; ps.Value = EffectiveStaff(staffId); cmd.Parameters.Add(ps);
                        var p1 = cmd.CreateParameter(); p1.ParameterName = "@sub"; p1.Value = subtotal; cmd.Parameters.Add(p1);
                        var p2 = cmd.CreateParameter(); p2.ParameterName = "@cc"; p2.Value = cardCovered; cmd.Parameters.Add(p2);
                        var p3 = cmd.CreateParameter(); p3.ParameterName = "@rc"; p3.Value = receivable; cmd.Parameters.Add(p3);
                        orderId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    InsertOrderItems(conn, txn, orderId, priced, staffId);
                    txn.Commit();
                    Logger.Info("挂单：" + orderNo + " 会员=" + (memberId.HasValue ? memberId.Value.ToString() : "散客") + " 应收=" + receivable);
                    return orderNo;
                }
                catch (Exception ex)
                {
#if DEBUG
                    // 回滚失败绝不能把原始异常替换成看不懂的提示；原始原因必须原样抛出
#endif
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>取单：读挂单头+明细还原账单（结账时传 heldOrderId 完成原单）。</summary>
        public HeldOrder TakeOrder(int orderId)
        {
            var ho = new HeldOrder { OrderId = orderId };
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT o.order_no,o.member_id,IFNULL(o.staff_id,0),IFNULL(s.name,'')" +
                        " FROM orders o LEFT JOIN staff s ON s.id=o.staff_id" +
                        " WHERE o.id=@id AND o.status='挂单' AND IFNULL(o.voided_at,'')=''";
                    var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = orderId; cmd.Parameters.Add(p);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) throw new BizException(UiText.PosErrHeldNotFound);
                        ho.OrderNo = r.GetString(0);
                        ho.MemberId = r.IsDBNull(1) ? (int?)null : r.GetInt32(1);
                        ho.StaffId = r.GetInt32(2);
                        ho.StaffName = r.GetString(3);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COALESCE(si.id,0),oi.qty,oi.by_card," +
                        " COALESCE(si.name,oi.item_name),oi.price,IFNULL(si.unit,'次'),IFNULL(si.is_card_item,0)" +
                        " FROM order_items oi LEFT JOIN service_items si ON si.id=oi.item_id" +
                        " WHERE oi.order_id=@id ORDER BY oi.id";
                    var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = orderId; cmd.Parameters.Add(p);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            ho.Lines.Add(new CartLine
                            {
                                ItemId = r.GetInt32(0), // M1：按 item_id 匹配；项目被删/停用（含老库未回填行）为 0，结账重读库价时拦截
                                Qty = r.GetInt32(1),
                                ByCard = r.GetInt32(2) != 0,
                                Name = r.GetString(3),
                                Price = r.GetDecimal(4),
                                Unit = r.GetString(5),
                                IsCardItem = r.GetInt32(6) != 0
                            });
                        }
                    }
                }
            }
            return ho;
        }

        /// <summary>
        /// 作废挂单（店主 11-05 定：收银台「清空」用在取回来的挂单上 = 彻底作废，
        /// 不再放回挂单列表，也就是「又挂单上了」那个毛病）。
        /// 做法与会员注销同口径：orders.status 的 CHECK 冻结在 ('正常','挂单','已退款')，
        /// 塞不进「作废」，所以只加软标记 voided_at，行永久保留、查询层过滤。
        /// 挂单从来不写 payments / member_flows / commissions，作废它钱一分不动。
        /// 已结账、已作废、不存在的 id 一律拒绝，避免误伤正常单。
        /// </summary>
        public string VoidHeldOrder(int orderId)
        {
            if (orderId <= 0) throw new BizException(UiText.PosErrHeldVoided);
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string orderNo;
                    decimal receivable;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText =
                            "SELECT order_no,receivable FROM orders" +
                            " WHERE id=@id AND status='挂单' AND IFNULL(voided_at,'')=''";
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = orderId; cmd.Parameters.Add(p);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException(UiText.PosErrHeldVoided);
                            orderNo = r.GetString(0);
                            receivable = r.GetDecimal(1);
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText =
                            "UPDATE orders SET voided_at=@v" +
                            " WHERE id=@id AND status='挂单' AND IFNULL(voided_at,'')=''";
                        var pv = cmd.CreateParameter(); pv.ParameterName = "@v";
                        pv.Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); cmd.Parameters.Add(pv);
                        var pi = cmd.CreateParameter(); pi.ParameterName = "@id"; pi.Value = orderId; cmd.Parameters.Add(pi);
                        if (cmd.ExecuteNonQuery() != 1) throw new BizException(UiText.PosErrHeldVoided);
                    }
                    W.Log(conn, txn, UiText.NavPos, "挂单作废",
                        orderNo + " 作废（原单金额 " + Fmt.MoneyYuan(receivable) + "，未收款，不计营收）");
                    txn.Commit();
                    Logger.Info("挂单作废：" + orderNo);
                    return orderNo;
                }
                catch (Exception)
                {
                    // 原异常原样抛，回滚失败只记日志，绝不把真因吞掉
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 结账（核心事务）：校验 → 订单+明细+支付 → 扣余额写流水 → 次卡核销 → 写提成源单 → 日志。
        /// heldOrderId 非空时完成原挂单（保持 H 单号，状态挂单→正常），否则生成 D 单号新单。
        /// staffId 为该单的操作员（谁开的单/谁做的项目），为空时用当前登录账号。
        /// 失败整体回滚：扣余额、扣次卡、写流水、写提成源单同一事务。
        /// </summary>
        public CheckoutResult Checkout(IList<CartLine> lines, int? memberId, decimal payBalance, decimal payCash, int? heldOrderId, int? staffId)
        {
            if (lines == null || lines.Count == 0) throw new BizException(UiText.PosErrCartEmpty);
            if (payBalance < 0m || payCash < 0m) throw new BizException(UiText.PosErrCashShort);

            decimal subtotal, cardCovered, receivable;
            var priced = PriceLines(lines, out subtotal, out cardCovered, out receivable);
            receivable = Round2(receivable);

            // 散客约束：无余额支付、无次卡
            if (!memberId.HasValue)
            {
                if (payBalance > 0m) throw new BizException(UiText.PosErrWalkInBalance);
                if (cardCovered > 0m) throw new BizException(UiText.PosErrWalkInCard);
            }

            int cardTimesNeeded = 0;
            for (int i = 0; i < priced.Count; i++) if (priced[i].ByCard) cardTimesNeeded += priced[i].Qty;

            // 本次操作员：店主在收银台选定的员工优先，未选则退回当前登录账号
            int opStaffId = EffectiveStaff(staffId);
            string opName = ResolveStaffName(opStaffId);

            string bizDate = Fmt.BizDateOf(DateTime.Now, CutHour());
            var result = new CheckoutResult
            {
                Subtotal = Round2(subtotal),
                CardCovered = Round2(cardCovered),
                Receivable = receivable
            };

            // D2 积分规则：必须在事务开始之前读好（GetPointConfig 走 GetSetting 会另开连接，
            // 放到事务里会把 WAL 锁死）。读失败就跳过积分，绝不能影响结账本身。
            PointService.PointConfig pointCfg = null;
            if (memberId.HasValue)
            {
                try { pointCfg = new PointService().GetPointConfig(); }
                catch { pointCfg = null; }
            }

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal memberBalance = 0m;
                    string memberName = "";
                    // 会员校验（事务内读库为准，防界面数据过期）
                    if (memberId.HasValue)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "SELECT name,balance,status,IFNULL(deleted_at,'') FROM members WHERE id=@id";
                            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId.Value; cmd.Parameters.Add(p);
                            using (var r = cmd.ExecuteReader())
                            {
                                if (!r.Read()) throw new BizException("会员不存在");
                                memberName = r.GetString(0);
                                memberBalance = r.GetDecimal(1);
                                string st = r.GetString(2);
                                if (r.GetString(3).Length > 0) throw new BizException("该会员已注销，不能开单");
                                if (st != "正常") throw new BizException(string.Format(UiText.PosErrMemberStatus, st));
                            }
                        }
                        if (payBalance > memberBalance)
                            throw new BizException(string.Format(UiText.PosErrBalanceShort, memberName,
                                Fmt.MoneyYuan(Round2(memberBalance)), Fmt.MoneyYuan(Round2(memberBalance))));
                        if (cardTimesNeeded > 0)
                        {
                            int have = SumCardRemain(conn, txn, memberId.Value);
                            if (cardTimesNeeded > have)
                                throw new BizException(string.Format(UiText.PosErrCardShort, cardTimesNeeded, have));
                        }
                    }

                    // 实收校验：余额 + 现金 ≥ 应收
                    decimal received = payBalance + payCash;
                    if (received + 0.005m < receivable)
                        throw new BizException(string.Format(UiText.PosErrCashShort, Fmt.MoneyYuan(Round2(receivable - received))));

                    // M3（店主 10-15：拒收改「找零」）：余额没有找零抽屉——超额部分原路退回
                    // （不扣），就像现金多收了要找回去；现金超额照旧走 result.Change。
                    // 旧行为是「多扣不找不退也不入账」，会员平白吃亏，已废。
                    result.BalanceUsed = Round2(Math.Min(payBalance, receivable));
                    result.BalanceBack = Round2(payBalance - result.BalanceUsed);
                    decimal cashDue = Round2(receivable - result.BalanceUsed);
                    result.Change = Round2(Math.Max(0m, payCash - cashDue));
                    result.CashIn = Round2(Math.Max(0m, payCash - result.Change));

                    // 订单（新单生成 D 单号；取单完成原挂单保持 H 单号）
                    int orderId;
                    if (heldOrderId.HasValue)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText =
                                "SELECT order_no FROM orders WHERE id=@id AND status='挂单' AND IFNULL(voided_at,'')=''";
                            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = heldOrderId.Value; cmd.Parameters.Add(p);
                            object v = cmd.ExecuteScalar();
                            if (v == null || v == DBNull.Value) throw new BizException(UiText.PosErrHeldNotFound);
                            result.OrderNo = Convert.ToString(v);
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            // M2（店主 10-14）：跨营业日取单，biz_date 一并更新为结账日——
                            // 钱是今天收的，日结/营收/业绩都该算今天；挂单那天的数不动。
                            cmd.CommandText =
                                "UPDATE orders SET member_id=@m,subtotal=@sub,card_covered=@cc,receivable=@rc,biz_date=@d,status='正常' WHERE id=@id";
                            var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = memberId.HasValue ? (object)memberId.Value : DBNull.Value; cmd.Parameters.Add(pm);
                            var p1 = cmd.CreateParameter(); p1.ParameterName = "@sub"; p1.Value = result.Subtotal; cmd.Parameters.Add(p1);
                            var p2 = cmd.CreateParameter(); p2.ParameterName = "@cc"; p2.Value = result.CardCovered; cmd.Parameters.Add(p2);
                            var p3 = cmd.CreateParameter(); p3.ParameterName = "@rc"; p3.Value = result.Receivable; cmd.Parameters.Add(p3);
                            var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = bizDate; cmd.Parameters.Add(pd);
                            var pid = cmd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = heldOrderId.Value; cmd.Parameters.Add(pid);
                            cmd.ExecuteNonQuery();
                        }
                        orderId = heldOrderId.Value;
                    }
                    else
                    {
                        result.OrderNo = NextOrderNo(conn, txn, "D", bizDate);
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText =
                                "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                                " VALUES(@no,@d,@m,@s,@sub,@cc,@rc,'消费','正常'); SELECT last_insert_rowid();";
                            var pno = cmd.CreateParameter(); pno.ParameterName = "@no"; pno.Value = result.OrderNo; cmd.Parameters.Add(pno);
                            var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = bizDate; cmd.Parameters.Add(pd);
                            var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = memberId.HasValue ? (object)memberId.Value : DBNull.Value; cmd.Parameters.Add(pm);
                            var ps = cmd.CreateParameter(); ps.ParameterName = "@s"; ps.Value = opStaffId; cmd.Parameters.Add(ps);
                            var p1 = cmd.CreateParameter(); p1.ParameterName = "@sub"; p1.Value = result.Subtotal; cmd.Parameters.Add(p1);
                            var p2 = cmd.CreateParameter(); p2.ParameterName = "@cc"; p2.Value = result.CardCovered; cmd.Parameters.Add(p2);
                            var p3 = cmd.CreateParameter(); p3.ParameterName = "@rc"; p3.Value = result.Receivable; cmd.Parameters.Add(p3);
                            orderId = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                    }

                    // 明细（先删旧挂单明细再写，保证与当前账单一致）
                    if (heldOrderId.HasValue)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "DELETE FROM order_items WHERE order_id=@id";
                            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = orderId; cmd.Parameters.Add(p);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    InsertOrderItems(conn, txn, orderId, priced, opStaffId);

                    // 支付方式（只记资金类支付：余额 + 现金，合计恒等于应收）。
                    // 次卡核销不计收（售卡时已计收），不落 payments 行——对账恒等式「应收合计=支付合计」。
                    InsertPayment(conn, txn, orderId, "会员余额", result.BalanceUsed);
                    InsertPayment(conn, txn, orderId, "现金", result.CashIn);

                    // 扣余额 + 写流水（资金变动必产生流水）
                    if (memberId.HasValue && result.BalanceUsed > 0m)
                    {
                        decimal after = Round2(memberBalance - result.BalanceUsed);
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE members SET balance=@b WHERE id=@id";
                            var pb = cmd.CreateParameter(); pb.ParameterName = "@b"; pb.Value = after; cmd.Parameters.Add(pb);
                            var pid = cmd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = memberId.Value; cmd.Parameters.Add(pid);
                            cmd.ExecuteNonQuery();
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText =
                                "INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,order_id,operator,note)" +
                                " VALUES(@id,@d,'消费',@a,@b,@o,@op,@n)";
                            var pid = cmd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = memberId.Value; cmd.Parameters.Add(pid);
                            var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = bizDate; cmd.Parameters.Add(pd);
                            var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = -result.BalanceUsed; cmd.Parameters.Add(pa);
                            var pb = cmd.CreateParameter(); pb.ParameterName = "@b"; pb.Value = after; cmd.Parameters.Add(pb);
                            var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = orderId; cmd.Parameters.Add(po);
                            var pop = cmd.CreateParameter(); pop.ParameterName = "@op"; pop.Value = opName; cmd.Parameters.Add(pop);
                            var pn = cmd.CreateParameter(); pn.ParameterName = "@n"; pn.Value = result.OrderNo; cmd.Parameters.Add(pn);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // 次卡核销：先到期先用（expire_date 升序），每卡一行 card_uses
                    if (cardTimesNeeded > 0)
                    {
                        result.CardUsedTimes = RedeemCards(conn, txn, memberId.Value, orderId, cardTimesNeeded, opName);
                    }

                    // 提成源单：与「模式切换重算未结算提成」共用 OrderCommission 同一条路径
                    // （都从落库的 order_items/card_uses 重算）——两处口径永远一致，项目不冲突。
                    decimal commBase;
                    bool modeDriven;
                    decimal commExcess;
                    decimal commission = Round2(CommissionService.OrderCommission(
                        conn, txn, orderId, opStaffId,
                        CommissionService.CardCommissionMode(conn, txn),
                        out commBase, out modeDriven, out commExcess));
                    if (commission != 0m)
                    {
                        decimal avgRate = commBase > 0m ? Math.Round(commission / commBase, 4) : 0m;
                        CommissionService.InsertOne(conn, txn, orderId, opStaffId, "消费", commBase, avgRate, commission);
                    }

                    // 操作日志（留痕）
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                        var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = opName; cmd.Parameters.Add(po);
                        var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavPos; cmd.Parameters.Add(pm);
                        var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = "收银结账"; cmd.Parameters.Add(pa);
                        var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = result.OrderNo + " 应收 " + Fmt.MoneyYuan(result.Receivable) + "（余额 " + Fmt.MoneyYuan(result.BalanceUsed) + " + 现金 " + Fmt.MoneyYuan(result.CashIn) + " + 次卡 " + Fmt.MoneyYuan(result.CardCovered) + "）" + (result.BalanceBack > 0m ? "，余额多填 " + Fmt.MoneyYuan(result.BalanceBack) + " 未扣" : "")
                            + (commExcess > 0m ? "，提成超卡额度 " + Fmt.MoneyYuan(commExcess) + " 未计（找零）" : ""); cmd.Parameters.Add(pd);
                        cmd.ExecuteNonQuery();
                    }

                    // D2：按积分规则给会员累加。用「实收」而不是小计 —— 次卡抵扣那部分
                    // 在售卡时已经收过钱了，不重复给分。场景开关由店主在积分规则里自由配置。
                    if (memberId.HasValue && pointCfg != null && pointCfg.Enabled && pointCfg.EarnConsume)
                    {
                        int earned = new PointService().AddPoints(memberId.Value, result.Receivable, "消费积分", pointCfg, conn, txn);
                        if (earned > 0) Logger.Info("积分：会员 " + memberId.Value + " 本单获得 " + earned + " 分");
                    }

                    txn.Commit();
                    Logger.Info("结账：" + result.OrderNo + " 应收=" + result.Receivable + " 余额=" + result.BalanceUsed + " 现金=" + result.CashIn + " 次卡=" + result.CardCovered);
                    return result;
                }
                catch (Exception ex)
                {
#if DEBUG
                    // 回滚失败绝不能把原始异常替换成看不懂的提示；原始原因必须原样抛出
#endif
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        // ---------- 内部 ----------

        private class PricedLine
        {
            public int ItemId;
            public string Name;
            public decimal Price;
            public decimal Rate;   // 提成率（百分比模式）
            public string Mode;    // 'percent' | 'count'
            public decimal PerCount; // 按次金额（mode='count'）
            public int Qty;
            public bool ByCard;
        }

        private static int CutHour()
        {
            return Fmt.ParseCutHour(DatabaseInitializer.GetSetting("day_cut", "02:00"));
        }

        /// <summary>本次操作员：店主指定的员工优先；未指定（或该员工已不存在）退回当前登录账号。</summary>
        private static int EffectiveStaff(int? staffId)
        {
            if (staffId.HasValue && staffId.Value > 0 && StaffExists(staffId.Value)) return staffId.Value;
            return Session.StaffId;
        }

        private static bool StaffExists(int staffId)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM staff WHERE id=@id AND enabled=1";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = staffId; cmd.Parameters.Add(p);
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>操作员显示名（写流水/日志/核销留痕用）。</summary>
        private static string ResolveStaffName(int staffId)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM staff WHERE id=@id";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = staffId; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value) return Convert.ToString(v);
            }
            return Session.Name;
        }

        /// <summary>可选操作员列表（启用中的员工，含店长/收银员/技师）——收银台「本次操作」下拉用。</summary>
        public class StaffOption
        {
            public int Id;
            public string Name;
            public string Role;
            /// <summary>登录账号（staff.login_name）。**姓名允许重复，登录账号唯一** ——
            /// 店里有两个「李娜」时，经手人下拉靠这个区分「哪个李娜」（店主 11-05 反复强调的分离点）。</summary>
            public string LoginName;
        }

        public List<StaffOption> ListStaffOptions()
        {
            var list = new List<StaffOption>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 已归档（deleted_at 非空）的员工绝不出现在经手人下拉里 —— 它只是历史留痕。
                cmd.CommandText = "SELECT id,name,role,IFNULL(login_name,'') FROM staff" +
                    " WHERE enabled=1 AND IFNULL(deleted_at,'')=''" +
                    " ORDER BY CASE role WHEN '店长' THEN 1 WHEN '收银员' THEN 2 ELSE 3 END, id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StaffOption
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            Role = r.GetString(2),
                            LoginName = r.GetString(3)
                        });
                    }
                }
            }
            return list;
        }

        private static decimal Round2(decimal v)
        {
            return Math.Round(v, 2, MidpointRounding.AwayFromZero);
        }

        private static BillingMember ReadBillingMember(SQLiteDataReader r)
        {
            return new BillingMember
            {
                Id = r.GetInt32(0),
                CardNo = r.GetString(1),
                Name = r.GetString(2),
                Phone = r.GetString(3),
                Balance = r.GetDecimal(4),
                Points = r.GetInt32(5),
                CardRemain = Convert.ToInt32(r.GetValue(6))
            };
        }

        /// <summary>按库内现价/启用态重读账单行并汇总（价格以库为准）。</summary>
        private static List<PricedLine> PriceLines(IList<CartLine> lines, out decimal subtotal, out decimal cardCovered, out decimal receivable)
        {
            var priced = new List<PricedLine>();
            subtotal = 0m; cardCovered = 0m;
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name,price,commission_rate,commission_mode,commission_per_count,enabled FROM service_items WHERE id=@id";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = 0; cmd.Parameters.Add(p);
                for (int i = 0; i < lines.Count; i++)
                {
                    CartLine l = lines[i];
                    if (l.Qty <= 0) throw new BizException(UiText.PosErrQty);
                    p.Value = l.ItemId;
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) throw new BizException(UiText.PosErrItemDisabled.Replace("{0}", "#" + l.ItemId));
                        string name = r.GetString(0);
                        decimal price = r.GetDecimal(1);
                        decimal rate = r.GetDecimal(2);
                        string mode = r.IsDBNull(3) ? "percent" : Convert.ToString(r.GetValue(3));
                        decimal perCount = r.IsDBNull(4) ? 0m : Convert.ToDecimal(r.GetValue(4));
                        bool enabled = r.GetInt32(5) != 0;
                        if (!enabled) throw new BizException(string.Format(UiText.PosErrItemDisabled, name));
                        priced.Add(new PricedLine { ItemId = l.ItemId, Name = name, Price = price, Rate = rate, Mode = mode, PerCount = perCount, Qty = l.Qty, ByCard = l.ByCard });
                        subtotal += price * l.Qty;
                        if (l.ByCard) cardCovered += price * l.Qty;
                    }
                }
            }
            receivable = subtotal - cardCovered;
            return priced;
        }

        private static void InsertOrderItems(SQLiteConnection conn, SQLiteTransaction txn, int orderId, List<PricedLine> lines, int? staffId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO order_items(order_id,item_id,item_name,price,qty,by_card,staff_id) VALUES(@o,@i,@n,@p,@q,@c,@s)";
                var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = orderId; cmd.Parameters.Add(po);
                var pi = cmd.CreateParameter(); pi.ParameterName = "@i"; pi.Value = 0; cmd.Parameters.Add(pi);
                var pn = cmd.CreateParameter(); pn.ParameterName = "@n"; pn.Value = ""; cmd.Parameters.Add(pn);
                var pp = cmd.CreateParameter(); pp.ParameterName = "@p"; pp.Value = 0m; cmd.Parameters.Add(pp);
                var pq = cmd.CreateParameter(); pq.ParameterName = "@q"; pq.Value = 0; cmd.Parameters.Add(pq);
                var pc = cmd.CreateParameter(); pc.ParameterName = "@c"; pc.Value = 0; cmd.Parameters.Add(pc);
                var ps = cmd.CreateParameter(); ps.ParameterName = "@s"; ps.Value = EffectiveStaff(staffId); cmd.Parameters.Add(ps);
                for (int i = 0; i < lines.Count; i++)
                {
                    // M1（店主 10-14）：落 item_id 快照——项目改名/撞名不再让历史挂单变死单。
                    // 自定义行（无对应服务项目）为 0，取单时按 0 拦截并提示，与旧行为一致。
                    pi.Value = lines[i].ItemId;
                    pn.Value = lines[i].Name;
                    pp.Value = lines[i].Price;
                    pq.Value = lines[i].Qty;
                    pc.Value = lines[i].ByCard ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void InsertPayment(SQLiteConnection conn, SQLiteTransaction txn, int orderId, string method, decimal amount)
        {
            if (amount <= 0m) return; // 0 元方式不落行，payments 合计仍等于 receivable
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO payments(order_id,method,amount) VALUES(@o,@m,@a)";
                var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = orderId; cmd.Parameters.Add(po);
                var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = method; cmd.Parameters.Add(pm);
                var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = Round2(amount); cmd.Parameters.Add(pa);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>事务内统计会员可用次卡余次（正常+未过期）。</summary>
        private static int SumCardRemain(SQLiteConnection conn, SQLiteTransaction txn, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "SELECT COALESCE(SUM(remain),0) FROM member_cards" +
                    " WHERE member_id=@id AND status='正常' AND remain>0" +
                    " AND date(expire_date)>=date('now','localtime')";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>次卡核销：先到期先用；扣完置「已用完」；每核销 1 次写 1 行 card_uses（对账按行数计次）。</summary>
        private static int RedeemCards(SQLiteConnection conn, SQLiteTransaction txn, int memberId, int orderId, int timesNeeded, string operatorName)
        {
            int used = 0;
            var cards = new List<KeyValuePair<int, int>>(); // member_card_id, remain
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "SELECT id,remain FROM member_cards" +
                    " WHERE member_id=@id AND status='正常' AND remain>0" +
                    " AND date(expire_date)>=date('now','localtime')" +
                    " ORDER BY expire_date ASC, id ASC";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) cards.Add(new KeyValuePair<int, int>(r.GetInt32(0), r.GetInt32(1)));
                }
            }
            using (var upd = conn.CreateCommand())
            using (var ins = conn.CreateCommand())
            {
                upd.Transaction = txn;
                upd.CommandText = "UPDATE member_cards SET remain=@r,status=@st WHERE id=@id";
                var pr = upd.CreateParameter(); pr.ParameterName = "@r"; pr.Value = 0; upd.Parameters.Add(pr);
                var pst = upd.CreateParameter(); pst.ParameterName = "@st"; pst.Value = "正常"; upd.Parameters.Add(pst);
                var pid = upd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = 0; upd.Parameters.Add(pid);
                ins.Transaction = txn;
                ins.CommandText = "INSERT INTO card_uses(member_card_id,order_id,remain_after,operator) VALUES(@c,@o,@r,@op)";
                var pc = ins.CreateParameter(); pc.ParameterName = "@c"; pc.Value = 0; ins.Parameters.Add(pc);
                var po = ins.CreateParameter(); po.ParameterName = "@o"; po.Value = orderId; ins.Parameters.Add(po);
                var prr = ins.CreateParameter(); prr.ParameterName = "@r"; prr.Value = 0; ins.Parameters.Add(prr);
                var pop = ins.CreateParameter(); pop.ParameterName = "@op"; pop.Value = operatorName; ins.Parameters.Add(pop);

                for (int i = 0; i < cards.Count && used < timesNeeded; i++)
                {
                    int cardId = cards[i].Key;
                    int remain = cards[i].Value;
                    int take = Math.Min(remain, timesNeeded - used);
                    int after = remain - take;
                    pr.Value = after;
                    pst.Value = after == 0 ? "已用完" : "正常";
                    pid.Value = cardId;
                    upd.ExecuteNonQuery();
                    pc.Value = cardId;
                    // 每次核销单独一行，且 remain_after **逐次递减** —— 同一批核销多次时各行也互不相同，
                    // 避免出现「完全一致的多行」（店主 10-04 定：实际使用禁止完全重复的业务数据）。
                    // 行数仍 = 核销次数（「对账按行数计次」的口径不变）；末行值 = after = 卡的最终余次。
                    for (int j = 0; j < take; j++)
                    {
                        prr.Value = remain - j - 1;
                        ins.ExecuteNonQuery();
                    }
                    used += take;
                }
            }
            return used;
        }

        /// <summary>单号生成：前缀(1字符)+营业日 yyyyMMdd+"-"+两位序号；按前缀+日取最大序号+1。</summary>
        private static string NextOrderNo(SQLiteConnection conn, SQLiteTransaction txn, string prefix, string bizDate)
        {
            string compact = bizDate.Replace("-", "");
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT MAX(CAST(substr(order_no,11) AS INTEGER)) FROM orders WHERE order_no LIKE @p";
                var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = prefix + compact + "-%"; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                int next = (v == null || v == DBNull.Value) ? 1 : Convert.ToInt32(v) + 1;
                return prefix + compact + "-" + next.ToString("D2");
            }
        }
    }
}
