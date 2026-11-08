using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 会员管理（模块 02）：建档/搜索/编辑/状态流转（冻结/解冻/挂失/解挂）/余额对账。
    /// 资金变动一律写 member_flows 流水（触发器保护，只增不改），多步写全部事务包裹。
    /// </summary>
    public class MemberService
    {
        /// <summary>
        /// 会员列表查询的公共列清单（SearchMembers 与 GetMember 共用）。
        /// 含 last_visit / card_sum / total_recharge 三个子查询，抽出来避免两处不同步。
        /// 额外带 deleted_at / deleted_reason（S2 注销）：**默认视图不含已注销会员**，
        /// 由调用方按筛选值放行（列表筛选「已注销」时只看他们；搜索/详情/导出仍可查）。
        /// </summary>
        private const string MemberCols =
            "SELECT m.id,m.card_no,m.name,m.phone,m.level,m.tags,m.balance,m.points,m.status,m.birthday,m.remark,m.created_at," +
            " (SELECT MAX(o.created_at) FROM orders o WHERE o.member_id=m.id) AS last_visit," +
            " (SELECT GROUP_CONCAT(ct.name||'·剩'||mc.remain||'次','；') FROM member_cards mc" +
            "   JOIN card_templates ct ON ct.id=mc.template_id" +
            "   WHERE mc.member_id=m.id AND mc.status='正常' AND mc.remain>0) AS card_sum," +
            " m.created_by, m.created_by_name," +
            " IFNULL(m.deleted_at,'') AS deleted_at, IFNULL(m.deleted_reason,'') AS deleted_reason," +
            " COALESCE((SELECT SUM(f.amount) FROM member_flows f WHERE f.member_id=m.id AND f.type='充值'),0)" +
            " - COALESCE((SELECT SUM(o.receivable) FROM orders o WHERE o.member_id=m.id AND o.kind='充值' AND o.status='已退款'),0) AS total_recharge" +
            " FROM members m";

        public class MemberRow
        {
            public int Id;
            public string CardNo;
            public string Name;
            public string Phone;
            public string Level;
            public string Tags;
            public decimal Balance;
            public int Points;
            public decimal TotalRecharge;  // 累计实收充值（净=充值−已退本金，赠送不算），等级按它判定（可随退款降）
            public string Status;
            public string Birthday;
            public string Remark;
            public string CreatedBy;
            public string CreatedByName;
            public string CreatedAt;
            public string LastVisit;    // 最近到店（无订单为空）
            public string CardSummary;  // 正常次卡汇总，如「洗剪吹10次卡·剩6次；…」
            public string DeletedAt;    // 注销时间（空=未注销）
            public string DeletedReason;// 注销原因
        }

        public class FlowRow
        {
            public string Time;
            public string Type;
            public decimal Amount;         // 带符号：充值/赠送/正向调整为正，消费/退款/红冲为负
            public decimal BalanceAfter;
            public string Operator;
            public string Note;
            /// <summary>B2（10-21）：关联单号——消费/退款/红冲流水指向对应订单，充值/调整类为空。</summary>
            public string OrderNo;
        }

        public class OrderRow
        {
            public string OrderNo;
            public string BizDate;
            public string Kind;
            public decimal Receivable;
            public string Status;
            public string PaySummary;      // 组合支付拆分，如「会员余额¥128.00 + 现金¥70.00」
        }

        public class BalanceCheck
        {
            public decimal Recharge;   // 充值（正）
            public decimal Gift;       // 赠送（正）
            public decimal Consume;    // 消费（正数展示）
            public decimal Refund;     // 退款（正数展示）
            public decimal Adjust;     // 调整+红冲（带符号）
            public decimal Computed;   // 流水推导余额
            public decimal Actual;     // 表内余额
            public bool Ok;
        }

        // ---------- 查询 ----------

        /// <summary>列表检索：kw 模糊匹配 姓名/手机号/卡号，status='全部状态' 不筛，tag 模糊匹配标签，与搜索取交集。
        /// S2（店主 10-15）：**默认不含已注销会员**；筛选选「已注销」时只看他们（此时不再按 status 列筛）。</summary>
        public List<MemberRow> SearchMembers(string keyword, string status, string tag, int limit)
        {
            var list = new List<MemberRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = MemberCols +
                    " WHERE (@k='' OR m.name LIKE '%'||@k||'%' OR m.phone LIKE '%'||@k||'%' OR m.card_no LIKE '%'||@k||'%')" +
                    " AND (@s='全部状态' OR m.status=@s OR @s='已注销')" +
                    " AND (@t='' OR m.tags LIKE '%'||@t||'%')" +
                    " AND (IFNULL(m.deleted_at,'')='' OR @s='已注销')" +
                    " ORDER BY m.id DESC LIMIT @lim";
                AddParams(cmd, keyword, status, tag);
                var p = cmd.CreateParameter(); p.ParameterName = "@lim"; p.Value = limit; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(ReadMember(r));
                }
            }
            return list;
        }

        /// <summary>
        /// L2 会员列表分页（店主 10-18 前端批）：返回对象版，一次拿齐「当前页明细 + 全量统计」。
        /// 与现有 <see cref="SearchMembers"/>/Summary 完全同源（同一套 WHERE 参数口径，含已注销放行规则），
        /// 只是把 out 参数包进返回对象（Bridge 反射取不出 out 参数，故新增返回对象版），并支持 offset。
        /// 汇总三项（Total/BalanceSum/Abnormal）**按筛选全量算，不受 offset/limit 影响**（铁律），
        /// Deleted 仅当筛选=已注销时为 Total（正常筛选已排除，故 0）。
        /// </summary>
        public class MemberPageResult
        {
            public List<MemberRow> Rows;
            /// <summary>当前筛选命中的全量会员数（分页只截 Rows，总数全量算）。</summary>
            public int Total;
            /// <summary>正常会员余额合计（当前筛选，全量）。</summary>
            public decimal BalanceSum;
            /// <summary>异常状态人数（全表：冻结/挂失，非当前筛选）。</summary>
            public int Abnormal;
            /// <summary>已归档（注销）人数：筛选=已注销时 = Total，否则 0。</summary>
            public int Deleted;
        }

        public MemberPageResult SearchMembersPage(string keyword, string status, string tag, int limit, int offset)
        {
            var result = new MemberPageResult { Rows = new List<MemberRow>() };
            if (limit <= 0) limit = 50;
            if (offset < 0) offset = 0;
            using (var conn = Db.OpenConnection())
            {
                // 1) 全量统计（口径与 Summary 逐字一致）——一条 SQL 拿 count/余额合计；异常另算（全表）。
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COUNT(*)," +
                        " COALESCE(SUM(CASE WHEN status='正常' THEN balance ELSE 0 END),0)" +
                        " FROM members" +
                        " WHERE (@k='' OR name LIKE '%'||@k||'%' OR phone LIKE '%'||@k||'%' OR card_no LIKE '%'||@k||'%')" +
                        " AND (@s='全部状态' OR status=@s OR @s='已注销')" +
                        " AND (@t='' OR tags LIKE '%'||@t||'%')" +
                        " AND (IFNULL(deleted_at,'')='' OR @s='已注销')";
                    AddParams(cmd, keyword, status, tag);
                    using (var r = cmd.ExecuteReader())
                    {
                        r.Read();
                        result.Total = r.GetInt32(0);
                        result.BalanceSum = r.GetDecimal(1);
                    }
                }
                using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = "SELECT COUNT(*) FROM members WHERE status<>'正常' AND IFNULL(deleted_at,'')=''";
                    result.Abnormal = Convert.ToInt32(cmd2.ExecuteScalar());
                }
                result.Deleted = status == "已注销" ? result.Total : 0;

                // 2) 当前页明细（明细 WHERE 用 m. 前缀，与聚合的无前缀逐字同口径）
                using (var cmd3 = conn.CreateCommand())
                {
                    cmd3.CommandText = MemberCols +
                        " WHERE (@k='' OR m.name LIKE '%'||@k||'%' OR m.phone LIKE '%'||@k||'%' OR m.card_no LIKE '%'||@k||'%')" +
                        " AND (@s='全部状态' OR m.status=@s OR @s='已注销')" +
                        " AND (@t='' OR m.tags LIKE '%'||@t||'%')" +
                        " AND (IFNULL(m.deleted_at,'')='' OR @s='已注销')" +
                        " ORDER BY m.id DESC LIMIT @lim OFFSET @off";
                    AddParams(cmd3, keyword, status, tag);
                    var pl = cmd3.CreateParameter(); pl.ParameterName = "@lim"; pl.Value = limit; cmd3.Parameters.Add(pl);
                    var po = cmd3.CreateParameter(); po.ParameterName = "@off"; po.Value = offset; cmd3.Parameters.Add(po);
                    using (var r = cmd3.ExecuteReader())
                    {
                        while (r.Read()) result.Rows.Add(ReadMember(r));
                    }
                }
            }
            return result;
        }

        /// <summary>汇总条：共 N 人（当前筛选）/ 正常会员余额合计 / 异常状态人数（全表）。
        /// 口径与 <see cref="SearchMembers"/> 完全一致（含已注销的放行规则），否则条数和列表对不上。</summary>
        public void Summary(string keyword, string status, string tag, out int count, out decimal balanceSum, out int abnormal)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*)," +
                    " COALESCE(SUM(CASE WHEN status='正常' THEN balance ELSE 0 END),0)" +
                    " FROM members" +
                    " WHERE (@k='' OR name LIKE '%'||@k||'%' OR phone LIKE '%'||@k||'%' OR card_no LIKE '%'||@k||'%')" +
                    " AND (@s='全部状态' OR status=@s OR @s='已注销')" +
                    " AND (@t='' OR tags LIKE '%'||@t||'%')" +
                    " AND (IFNULL(deleted_at,'')='' OR @s='已注销')";
                AddParams(cmd, keyword, status, tag);
                using (var r = cmd.ExecuteReader())
                {
                    r.Read();
                    count = r.GetInt32(0);
                    balanceSum = r.GetDecimal(1);
                }
                using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = "SELECT COUNT(*) FROM members WHERE status<>'正常' AND IFNULL(deleted_at,'')=''";
                    abnormal = Convert.ToInt32(cmd2.ExecuteScalar());
                }
            }
        }

        /// <summary>全部标签（去重，供筛选下拉）。</summary>
        public List<string> ListTags()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT tags FROM members WHERE tags IS NOT NULL AND tags<>''";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        foreach (string t in r.GetString(0).Split('、'))
                        {
                            string s = t.Trim();
                            if (s.Length > 0) set.Add(s);
                        }
                    }
                }
            }
            return new List<string>(set);
        }

        public MemberRow GetMember(int id)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = MemberCols + " WHERE m.id=@id";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read()) return ReadMember(r);
                }
            }
            return null;
        }

        public List<FlowRow> LoadMemberFlows(int memberId, int limit, int offset)
        {
            if (offset < 0) offset = 0;
            var list = new List<FlowRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT f.created_at,f.type,f.amount,f.balance_after,f.operator,f.note,IFNULL(o.order_no,'')" +
                                  " FROM member_flows f LEFT JOIN orders o ON o.id=f.order_id" +
                                  " WHERE f.member_id=@id ORDER BY f.id DESC LIMIT @lim OFFSET @off";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                var pl = cmd.CreateParameter(); pl.ParameterName = "@lim"; pl.Value = limit; cmd.Parameters.Add(pl);
                var po = cmd.CreateParameter(); po.ParameterName = "@off"; po.Value = offset; cmd.Parameters.Add(po);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new FlowRow
                        {
                            Time = r.GetString(0),
                            Type = r.GetString(1),
                            Amount = r.GetDecimal(2),
                            BalanceAfter = r.GetDecimal(3),
                            Operator = r.GetString(4),
                            Note = r.IsDBNull(5) ? "" : r.GetString(5),
                            OrderNo = r.IsDBNull(6) ? "" : r.GetString(6)
                        });
                    }
                }
            }
            return list;
        }

        public List<OrderRow> LoadMemberOrders(int memberId, int limit, int offset)
        {
            if (offset < 0) offset = 0;
            var list = new List<OrderRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT o.order_no,o.biz_date,o.kind,o.receivable,o.status," +
                    " (SELECT GROUP_CONCAT(p.method||'¥'||printf('%.2f',p.amount),' + ') FROM payments p WHERE p.order_id=o.id) AS pays" +
                    " FROM orders o WHERE o.member_id=@id ORDER BY o.id DESC LIMIT @lim OFFSET @off";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                var pl = cmd.CreateParameter(); pl.ParameterName = "@lim"; pl.Value = limit; cmd.Parameters.Add(pl);
                var po = cmd.CreateParameter(); po.ParameterName = "@off"; po.Value = offset; cmd.Parameters.Add(po);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new OrderRow
                        {
                            OrderNo = r.GetString(0),
                            BizDate = r.GetString(1),
                            Kind = r.GetString(2),
                            Receivable = r.GetDecimal(3),
                            Status = r.GetString(4),
                            PaySummary = r.IsDBNull(5) ? "" : r.GetString(5)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>余额恒等式：余额 = 充值+赠送−消费−退款±调整（流水带符号存储，SUM 即推导余额）。</summary>
        public BalanceCheck GetBalanceCheck(int memberId)
        {
            var c = new BalanceCheck();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT" +
                    " COALESCE(SUM(CASE WHEN type='充值' THEN amount ELSE 0 END),0)," +
                    " COALESCE(SUM(CASE WHEN type='赠送' THEN amount ELSE 0 END),0)," +
                    " COALESCE(SUM(CASE WHEN type='消费' THEN -amount ELSE 0 END),0)," +
                    " COALESCE(SUM(CASE WHEN type='退款' THEN -amount ELSE 0 END),0)," +
                    " COALESCE(SUM(CASE WHEN type IN('调整','红冲') THEN amount ELSE 0 END),0)," +
                    " COALESCE(SUM(amount),0), (SELECT balance FROM members WHERE id=@id)" +
                    " FROM member_flows WHERE member_id=@id";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    r.Read();
                    c.Recharge = r.GetDecimal(0);
                    c.Gift = r.GetDecimal(1);
                    c.Consume = r.GetDecimal(2);
                    c.Refund = r.GetDecimal(3);
                    c.Adjust = r.GetDecimal(4);
                    c.Computed = r.GetDecimal(5);
                    c.Actual = r.GetDecimal(6);
                }
            }
            c.Ok = Math.Abs(c.Computed - c.Actual) < 0.005m;
            return c;
        }

        // ---------- 写操作 ----------

        /// <summary>
        /// S2（店主 10-15）：**余额核销** —— 把会员剩余余额作坏账抹平，不退现金。
        /// 这是上一任老板的旧账：钱是前任收的、我们只接了余额，所以清理「一直不回来的老客户」要用核销而不是退款。
        /// 做法：同事务写一条 `type='调整'` 的**负数**流水 + 置 `members.balance=0` + 操作日志。
        ///   · 走已有的「调整」流水类型（member_flows CHECK 本就允许），**DDL 零改动**；
        ///   · 恒等式不变：`SUM(amount)` 仍等于 `members.balance`，`GetBalanceCheck` 照旧显示 OK；
        ///   · 流水只增不改（触发器保护），这笔核销随时可查、可对账，不像物理删记录那样查无对证。
        /// 店长专属；原因必填。返回核销掉的金额。
        /// </summary>
        public decimal WriteOffBalance(int id, string reason, string operatorName)
        {
            if (string.IsNullOrEmpty((reason ?? "").Trim()))
                throw new BizException("核销余额必须填写原因（写入流水与操作日志，不可省略）");
            if (!Session.IsManager) throw new BizException("余额核销仅限店长操作，请通知店长处理");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal balance;
                    string name, status, deletedAt;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,balance,status,IFNULL(deleted_at,'') FROM members WHERE id=@id";
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("会员不存在");
                            name = r.GetString(0);
                            balance = r.GetDecimal(1);
                            status = r.GetString(2);
                            deletedAt = r.GetString(3);
                        }
                    }
                    if (deletedAt.Length > 0) throw new BizException("该会员已注销，不能再次核销");
                    if (status == "挂失") throw new BizException("挂失会员请先解挂再核销（挂失是临时状态，核销是资金动作）");
                    if (balance == 0m) throw new BizException("该会员余额已经是 0，无需核销");

                    decimal after = 0m;
                    WriteAdjustFlow(conn, txn, id, -balance, after, operatorName,
                        "余额核销：" + reason.Trim() + "（核销前余额 " + Fmt.MoneyYuan(balance) + "，作坏账处理，不退现金）");
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET balance=0 WHERE id=@id";
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                        var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = operatorName; cmd.Parameters.Add(po);
                        var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavMembers; cmd.Parameters.Add(pm);
                        var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = "核销会员余额"; cmd.Parameters.Add(pa);
                        var pd = cmd.CreateParameter(); pd.ParameterName = "@d";
                        pd.Value = "会员#" + id + " " + name + " 核销 " + Fmt.MoneyYuan(balance) + "，原因：" + reason.Trim();
                        cmd.Parameters.Add(pd);
                        cmd.ExecuteNonQuery();
                    }
                    txn.Commit();
                    Logger.Info("余额核销：memberId=" + id + " " + name + " amount=" + balance + " by=" + operatorName);
                    return balance;
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

        /// <summary>
        /// S2（店主 10-15）：**注销会员** = 余额核销 + 软删除，一步完成。
        /// 店主 10-15 定的边界（写死，别改）：
        ///   · 记录**全部保留可查** —— 列表默认排除，搜索/详情/导出仍能查到，流水一行不删；
        ///   · **手机号、卡号不释放**，新会员不能占用（members.phone/card_no 是 UNIQUE，注销行仍占位）；
        ///   · **绝不做物理删除** —— member_flows 有 flows_no_delete 触发器，orders/member_cards 有外键。
        /// **店主 10-15 要求「两个按钮合为一个」**：归档时若还有余额，本方法**自己把余额核销掉**
        /// （一条负数「调整」流水 + balance=0，与单独核销完全同一条路径），不必也不允许分两步操作。
        /// 店长专属、原因必填（这一个原因同时写进流水 note 与操作日志）。
        /// </summary>
        public void DeleteMember(int id, string reason)
        {
            if (string.IsNullOrEmpty((reason ?? "").Trim()))
                throw new BizException("归档会员必须填写原因（写入操作日志，不可省略）");
            if (!Session.IsManager) throw new BizException("归档会员仅限店长操作，请通知店长处理");
            string why = reason.Trim();
            if (why.Length > 200) why = why.Substring(0, 200);

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal balance; string name, status, deletedAt;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,balance,status,IFNULL(deleted_at,'') FROM members WHERE id=@id";
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("会员不存在");
                            name = r.GetString(0);
                            balance = r.GetDecimal(1);
                            status = r.GetString(2);
                            deletedAt = r.GetString(3);
                        }
                    }
                    if (deletedAt.Length > 0) throw new BizException("该会员已归档，不能重复归档");
                    if (status == "挂失") throw new BizException("挂失会员请先解挂再归档");
                    // 有余额就地核销：同一个事务、同一条「调整」流水路径，与单独核销口径一致
                    if (balance != 0m)
                    {
                        WriteAdjustFlow(conn, txn, id, -balance, 0m, Session.Name,
                            "归档核销：" + why + "（核销前余额 " + Fmt.MoneyYuan(balance) + "，作坏账处理，不退现金）");
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE members SET balance=0 WHERE id=@id";
                            var pb = cmd.CreateParameter(); pb.ParameterName = "@id"; pb.Value = id; cmd.Parameters.Add(pb);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET deleted_at=@d,deleted_reason=@r,deleted_by=@b WHERE id=@id";
                        var p1 = cmd.CreateParameter(); p1.ParameterName = "@d";
                        p1.Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); cmd.Parameters.Add(p1);
                        var p2 = cmd.CreateParameter(); p2.ParameterName = "@r"; p2.Value = why; cmd.Parameters.Add(p2);
                        var p3 = cmd.CreateParameter(); p3.ParameterName = "@b"; p3.Value = Session.Name; cmd.Parameters.Add(p3);
                        var p4 = cmd.CreateParameter(); p4.ParameterName = "@id"; p4.Value = id; cmd.Parameters.Add(p4);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                        var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = Session.Name; cmd.Parameters.Add(po);
                        var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavMembers; cmd.Parameters.Add(pm);
                        var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = "归档会员"; cmd.Parameters.Add(pa);
                        var pd = cmd.CreateParameter(); pd.ParameterName = "@d";
                        pd.Value = "会员#" + id + " " + name + " 归档，原因：" + why +
                            (balance != 0m ? "；同时核销余额 " + Fmt.MoneyYuan(balance) + "（作坏账、不退现金）" : "") +
                            "（记录全留可查，手机号与卡号不再释放）";
                        cmd.Parameters.Add(pd);
                        cmd.ExecuteNonQuery();
                    }
                    txn.Commit();
                    Logger.Info("归档会员：memberId=" + id + " " + name + " reason=" + why +
                        (balance != 0m ? " writtenOff=" + balance : ""));
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

        /// <summary>
        /// 写一条「调整」流水（余额核销专用）。amount 带符号；biz_date 按营业日切分（与结账同口径）。
        /// 触发器禁止 UPDATE/DELETE member_flows —— 只增不改，这正是流水可信的原因。
        /// </summary>
        private static void WriteAdjustFlow(SQLiteConnection conn, SQLiteTransaction txn,
            int memberId, decimal amount, decimal balanceAfter, string operatorName, string note)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText =
                    "INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,operator,note)" +
                    " VALUES(@id,@d,'调整',@a,@b,@op,@n)";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = memberId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@d"; p2.Value = Fmt.BizDateOf(DateTime.Now, CutHour()); cmd.Parameters.Add(p2);
                var p3 = cmd.CreateParameter(); p3.ParameterName = "@a"; p3.Value = amount; cmd.Parameters.Add(p3);
                var p4 = cmd.CreateParameter(); p4.ParameterName = "@b"; p4.Value = balanceAfter; cmd.Parameters.Add(p4);
                var p5 = cmd.CreateParameter(); p5.ParameterName = "@op"; p5.Value = operatorName; cmd.Parameters.Add(p5);
                var p6 = cmd.CreateParameter(); p6.ParameterName = "@n"; p6.Value = note; cmd.Parameters.Add(p6);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 新建会员（事务）：校验 → 自动分配卡号 → 建档；可选初始充值写流水。
        /// 等级不收参（店主 2026-09-26 定：不手工选择，按累计实收充值自动判定），
        /// 初始充值写流水之后由 LevelRules.Apply 落定。
        /// </summary>
        public MemberRow CreateMember(string name, string phone, string birthday, string tags, string remark, decimal initAmount, decimal initGift, int createdByStaffId, string createdByNameHint)
        {
            birthday = NormalizeBirthday(birthday);
            ValidateBasic(name, phone, birthday, 0);
            if (initAmount < 0m) throw new BizException(UiText.MemberErrAmount);
            if (initGift < 0m) throw new BizException("赠送金额不能为负数");
            // 金额上限（10-15 店主反馈「不能新建会员了」后补）：积分按「金额 ÷ 每元积分」算，
            // 超出 int 范围时会抛一句谁也看不懂的「值对于 Int32 太大或太小」。
            // 这里先把话说清楚——超过上限九成是手滑输错了（比如把手机号填进了金额框）。
            if (initAmount > 99999999m) throw new BizException("首次充值金额过大（上限 99,999,999 元），请确认是否输错");
            if (initGift > 99999999m) throw new BizException("赠送金额过大（上限 99,999,999 元），请确认是否输错");

            // 建档经办人：前端点名 > 当前登录账号。姓名一律取库里员工名快照落库（见 EffectiveCreator），
            // 前端传来的 createdByNameHint 仅作桥参数占位，不参与写库 —— 姓名被改过也不能伪造留痕。
            int creatorStaffId = EffectiveCreator(createdByStaffId, out string creatorName);

            // D2 积分：建档初始充值也是「充值」单，按规则给分（与储值页充值同口径）。
            // 配置必须在事务外读好 —— GetPointConfig 走 GetSetting 另开连接，事务内会锁死 WAL。
            PointService.PointConfig pointCfg = null;
            if (initAmount > 0m)
            {
                try { pointCfg = new PointService().GetPointConfig(); }
                catch { pointCfg = null; }   // 读规则失败不能挡建档
            }

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    int id = InsertMember(conn, txn, name, phone, birthday, tags, remark, creatorStaffId, creatorName);
                    if (initAmount > 0m || initGift > 0m)
                    {
                        string bizDate = Fmt.BizDateOf(DateTime.Now, CutHour());
                        string opName = creatorName;
                        int opStaffId = creatorStaffId;
                        // 建档初始充值落「充值」单 + 现金支付（与储值 Recharge 同口径）：
                        // 既能在「消费记录」里查到这笔，也会计入日结营收（现金）；只写会员流水则两边都看不到。
                        // 金额与赠送同订单落两条流水，订单 subtotal=receivable=payments=本金（赠送不重复计收）。
                        int orderId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            string orderNo = W.NextNo(conn, txn, "C", bizDate);
                            cmd.CommandText = "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                                " VALUES(@no,@d,@m,@s,@sub,0,@rc,'充值','正常'); SELECT last_insert_rowid();";
                            W.P(cmd, "@no", orderNo); W.P(cmd, "@d", bizDate); W.P(cmd, "@m", id);
                            W.P(cmd, "@s", opStaffId); W.P(cmd, "@sub", initAmount); W.P(cmd, "@rc", initAmount);
                            orderId = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                        if (initAmount > 0m)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = txn;
                                cmd.CommandText = "INSERT INTO payments(order_id,method,amount) VALUES(@o,'现金',@a)";
                                W.P(cmd, "@o", orderId); W.P(cmd, "@a", initAmount);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        W.InsertFlow(conn, txn, id, "充值", initAmount, initAmount + initGift, orderId, opName,
                            "新建会员初始充值" + initAmount.ToString("0.##") + "元");
                        if (initGift > 0m)
                            W.InsertFlow(conn, txn, id, "赠送", initGift, initAmount + initGift, orderId, opName,
                                "新建会员初始充值赠送" + initGift.ToString("0.##") + "元");
                        // 充值提成：按本金计提（赠送不计），归建档经办人；比例 个人(充值项) > 全局充值比例。
                        if (initAmount > 0m)
                            CommissionService.InsertByRate(conn, txn, orderId, opStaffId, "充值", initAmount,
                                CommissionService.ResolveRechargeRate(conn, txn, opStaffId));
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE members SET balance=@a WHERE id=@id";
                            var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = initAmount + initGift; cmd.Parameters.Add(pa);
                            var pid = cmd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = id; cmd.Parameters.Add(pid);
                            cmd.ExecuteNonQuery();
                        }
                        W.Log(conn, txn, "会员管理", "新建会员",
                            name + " 初始充值" + initAmount.ToString("0.##") + "元" +
                            (initGift > 0m ? "送" + initGift.ToString("0.##") + "元" : "") +
                            "（经办人：" + creatorName + "）");
                        // D2：给分（充值口径，只按本金算；赠送不计积分，与储值 Recharge 一致）。
                        if (pointCfg != null && pointCfg.Enabled && pointCfg.EarnRecharge && initAmount > 0m)
                        {
                            int pts0 = new PointService().AddPoints(id, initAmount, "充值", pointCfg, conn, txn);
                            if (pts0 > 0) Logger.Info("积分：会员 " + id + " 建档初始充值获得 " + pts0 + " 分");
                        }
                    }
                    // 等级自动判定（店主 2026-09-26 定：不手工选择，按累计实收充值推导）。
                    // 新会员没有「旧等级」可留痕，上面「新建会员」已写 op_logs，这里只记 app 日志。
                    // 店主 10-15：「没有档了那就无」——一档都没启用时 Apply 返 null，
                    // 新会员等级落空白（DDL 默认 '普卡' 在此场景不成立，显式置空）；
                    // 老会员由 ApplyAll 统一不碰（"老的没必要动"）。
                    LevelRules.Change lv = LevelRules.Apply(conn, txn, id);
                    if (lv != null)
                    {
                        if (lv.Changed)
                            Logger.Info("等级自动判定：会员 " + name + " " + lv.Old + "→" + lv.New + "（按累计实收充值）");
                    }
                    else
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE members SET level='' WHERE id=@id";
                            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                            cmd.ExecuteNonQuery();
                        }
                        Logger.Info("等级自动判定：无可用等级档位，会员 " + name + " 等级留空");
                    }

                    txn.Commit();
                    Logger.Info("新建会员：" + name + "（" + phone + "）初始余额=" + initAmount + "，经办人=" + creatorName);
                    return GetMember(id);
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

        /// <summary>
        /// 编辑基本资料（不涉资金）。手机号重复拦截（排除本人）。
        /// 事务内先读旧值做字段级差异比对，再 UPDATE，只把真正变化的字段写 op_logs ——
        /// 会员页承诺「标签、敏感操作全程留痕」，谁在什么时候把「沉睡」改成了什么，日志里必须查得到；
        /// 无任何变化的提交不落日志，避免「开弹窗存一下」刷脏日志。
        /// 余额/积分/次卡一概不碰，所以不需要流水，也不用动 member_flows 触发器。
        /// 等级不收参（店主 2026-09-26 定：完全自动，不允许手工改），由 LevelRules 按累计实收充值判定。
        /// </summary>
        public void UpdateMember(int id, string name, string phone, string birthday, string tags, string remark)
        {
            birthday = NormalizeBirthday(birthday);
            ValidateBasic(name, phone, birthday, id);
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string oldName = "", oldPhone = "", oldBirthday = "", oldTags = "", oldRemark = "";
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,phone,birthday,tags,remark FROM members WHERE id=@id";
                        var pid0 = cmd.CreateParameter(); pid0.ParameterName = "@id"; pid0.Value = id; cmd.Parameters.Add(pid0);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("会员不存在");
                            oldName = r.IsDBNull(0) ? "" : r.GetString(0);
                            oldPhone = r.IsDBNull(1) ? "" : r.GetString(1);
                            oldBirthday = r.IsDBNull(2) ? "" : r.GetString(2);
                            oldTags = r.IsDBNull(3) ? "" : r.GetString(3);
                            oldRemark = r.IsDBNull(4) ? "" : r.GetString(4);
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET name=@n,phone=@p,birthday=@b,tags=@t,remark=@r WHERE id=@id";
                        var pn = cmd.CreateParameter(); pn.ParameterName = "@n"; pn.Value = name; cmd.Parameters.Add(pn);
                        var pp = cmd.CreateParameter(); pp.ParameterName = "@p"; pp.Value = string.IsNullOrEmpty(phone) ? (object)DBNull.Value : phone; cmd.Parameters.Add(pp);
                        var pb = cmd.CreateParameter(); pb.ParameterName = "@b"; pb.Value = string.IsNullOrEmpty(birthday) ? (object)DBNull.Value : birthday; cmd.Parameters.Add(pb);
                        var pt = cmd.CreateParameter(); pt.ParameterName = "@t"; pt.Value = tags ?? ""; cmd.Parameters.Add(pt);
                        var pr = cmd.CreateParameter(); pr.ParameterName = "@r"; pr.Value = string.IsNullOrEmpty(remark) ? (object)DBNull.Value : remark; cmd.Parameters.Add(pr);
                        var pid = cmd.CreateParameter(); pid.ParameterName = "@id"; pid.Value = id; cmd.Parameters.Add(pid);
                        cmd.ExecuteNonQuery();
                    }

                    var diffs = new List<string>();
                    AddDiff(diffs, "姓名", oldName, name ?? "");
                    AddDiff(diffs, "手机号", oldPhone, phone ?? "");
                    AddDiff(diffs, "生日", oldBirthday, birthday ?? "");
                    AddDiff(diffs, "标签", oldTags, tags ?? "");
                    AddDiff(diffs, "备注", oldRemark, remark ?? "");

                    if (diffs.Count > 0)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                            var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = Session.Name; cmd.Parameters.Add(po);
                            var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavMembers; cmd.Parameters.Add(pm);
                            var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = UiText.MemberActionEdit; cmd.Parameters.Add(pa);
                            var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = "会员#" + id + " " + name + "：" + string.Join("；", diffs); cmd.Parameters.Add(pd);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    txn.Commit();
                    Logger.Info("编辑会员资料：memberId=" + id + "，变化字段=" + diffs.Count +
                        (diffs.Count > 0 ? "（" + string.Join("；", diffs) + "）" : "，无实际变更未写日志"));
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

        /// <summary>字段差异比对：值相同不记；空值显示为「空」，日志里一眼能看出是「删掉了」还是「没填」。</summary>
        private static void AddDiff(List<string> list, string field, string oldVal, string newVal)
        {
            if (oldVal == newVal) return;
            string a = string.IsNullOrEmpty(oldVal) ? "空" : oldVal;
            string b = string.IsNullOrEmpty(newVal) ? "空" : newVal;
            list.Add(field + "：" + a + "→" + b);
        }

        /// <summary>状态流转：冻结/解冻/挂失/解挂（事务：状态 + 操作日志）。状态机：正常⇄冻结 → 挂失 → 解挂回正常。</summary>
        public void ChangeStatus(int id, string action)
        {
            string target;
            string logAction;
            if (action == UiText.MemberActionFreeze) { target = "冻结"; logAction = UiText.MemberActionFreeze; }
            else if (action == UiText.MemberActionUnfreeze) { target = "正常"; logAction = UiText.MemberActionUnfreeze; }
            else if (action == UiText.MemberActionReportLoss) { target = "挂失"; logAction = UiText.MemberActionReportLoss; }
            else if (action == UiText.MemberActionUnloss) { target = "正常"; logAction = UiText.MemberActionUnloss; }
            else throw new BizException("不支持的状态操作");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string current;
                    string name;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT status,name FROM members WHERE id=@id";
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("会员不存在");
                            current = r.GetString(0);
                            name = r.GetString(1);
                        }
                    }
                    EnsureTransition(current, target);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET status=@s WHERE id=@id";
                        var ps = cmd.CreateParameter(); ps.ParameterName = "@s"; ps.Value = target; cmd.Parameters.Add(ps);
                        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                        var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = Session.Name; cmd.Parameters.Add(po);
                        var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavMembers; cmd.Parameters.Add(pm);
                        var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = logAction; cmd.Parameters.Add(pa);
                        var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = "会员#" + id + " " + name + "：" + current + "→" + target; cmd.Parameters.Add(pd);
                        cmd.ExecuteNonQuery();
                    }
                    txn.Commit();
                    Logger.Info("会员状态变更：memberId=" + id + " " + current + "→" + target);
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

        private static void AddParams(SQLiteCommand cmd, string keyword, string status, string tag)
        {
            var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; pk.Value = (keyword ?? "").Trim(); cmd.Parameters.Add(pk);
            var ps = cmd.CreateParameter(); ps.ParameterName = "@s"; ps.Value = string.IsNullOrEmpty(status) ? UiText.MemberFilterAllStatus : status; cmd.Parameters.Add(ps);
            var pt = cmd.CreateParameter(); pt.ParameterName = "@t"; pt.Value = (tag ?? "").Trim(); cmd.Parameters.Add(pt);
        }

        private static MemberRow ReadMember(SQLiteDataReader r)
        {
            return new MemberRow
            {
                Id = r.GetInt32(0),
                CardNo = r.GetString(1),
                Name = r.GetString(2),
                Phone = r.IsDBNull(3) ? "" : r.GetString(3),
                Level = r.GetString(4),
                Tags = r.IsDBNull(5) ? "" : r.GetString(5),
                Balance = r.GetDecimal(6),
                Points = r.GetInt32(7),
                Status = r.GetString(8),
                Birthday = r.IsDBNull(9) ? "" : r.GetString(9),
                Remark = r.IsDBNull(10) ? "" : r.GetString(10),
                CreatedAt = r.GetString(11),
                LastVisit = r.IsDBNull(12) ? "" : r.GetString(12),
                CardSummary = r.IsDBNull(13) ? "" : r.GetString(13),
                CreatedBy = r.IsDBNull(14) ? "" : r.GetInt32(14).ToString(),
                CreatedByName = r.IsDBNull(15) ? "" : r.GetString(15),
                // MemberCols 列序：…14 created_by / 15 created_by_name / 16 deleted_at /
                // 17 deleted_reason / 18 total_recharge（后两列是 10-15 加注销时插进去的）
                DeletedAt = r.IsDBNull(16) ? "" : r.GetString(16),
                DeletedReason = r.IsDBNull(17) ? "" : r.GetString(17),
                TotalRecharge = r.GetDecimal(18)
            };
        }

        private static int CutHour()
        {
            return Fmt.ParseCutHour(DatabaseInitializer.GetSetting("day_cut", "02:00"));
        }

        private static void ValidateBasic(string name, string phone, string birthday, int excludeId)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BizException(UiText.MemberErrName);
            if (!string.IsNullOrEmpty(phone))
            {
                if (phone.Length != 11 || phone[0] != '1')
                    throw new BizException(UiText.MemberErrPhoneFormat);
                for (int i = 0; i < phone.Length; i++)
                {
                    if (phone[i] < '0' || phone[i] > '9') throw new BizException(UiText.MemberErrPhoneFormat);
                }
                using (var conn = Db.OpenConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM members WHERE phone=@p AND id<>@id";
                    var pp = cmd.CreateParameter(); pp.ParameterName = "@p"; pp.Value = phone; cmd.Parameters.Add(pp);
                    var pi = cmd.CreateParameter(); pi.ParameterName = "@id"; pi.Value = excludeId; cmd.Parameters.Add(pi);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
                        throw new BizException(UiText.MemberErrPhoneDup);
                }
            }
            if (!string.IsNullOrEmpty(birthday)) NormalizeBirthday(birthday);
        }

        /// <summary>
        /// 生日归一化为 MM-dd。同时接受「MM-dd」与日期控件输出的「YYYY-MM-DD」「YYYY/MM/DD」，
        /// 并拒绝未来日期（建档/编辑时不允许填明天之后的生日）。
        /// </summary>
        public static string NormalizeBirthday(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim().Replace('/', '-');
            string mmdd = s;

            if (s.Length == 10 && s[4] == '-' && s[7] == '-')
            {
                // YYYY-MM-DD → 取月日
                System.DateTime dt;
                if (!System.DateTime.TryParseExact(s, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out dt))
                    throw new BizException(UiText.MemberErrBirthday);
                if (dt.Date > System.DateTime.Today)
                    throw new BizException("生日不能晚于今天");
                mmdd = s.Substring(5, 2) + "-" + s.Substring(8, 2);
            }

            // L3（店主 10-14）：真实月日校验——02-30 这种假日期必须拦下（原来只查到 d<=31）。
            // 2 月按 29 天放行：闰年 2/29 是真生日，库里只存 mm-dd 不存年份。
            // NumberStyles.None 拒空白/符号，"+1-01" 这类花活进不来。
            int m = 0, d = 0;
            bool ok = mmdd.Length == 5 && mmdd[2] == '-'
                && int.TryParse(mmdd.Substring(0, 2), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out m)
                && int.TryParse(mmdd.Substring(3, 2), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out d);
            int[] daysInMonth = { 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
            if (!ok || m < 1 || m > 12 || d < 1 || d > daysInMonth[m - 1])
                throw new BizException(UiText.MemberErrBirthday);
            return mmdd;
        }

        private static int InsertMember(SQLiteConnection conn, SQLiteTransaction txn, string name, string phone, string birthday, string tags, string remark, int createdBy, string createdByName)
        {
            // 卡号自动分配 VIP0001 起，撞唯一约束（极端并发）则顺延重试
            for (int attempt = 0; attempt < 5; attempt++)
            {
                string cardNo = NextCardNo(conn, txn);
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO members(card_no,name,phone,tags,birthday,remark,created_by,created_by_name)" +
                                          " VALUES(@c,@n,@p,@t,@b,@r,@cb,@cbn); SELECT last_insert_rowid();";
                        var pc = cmd.CreateParameter(); pc.ParameterName = "@c"; pc.Value = cardNo; cmd.Parameters.Add(pc);
                        var pn = cmd.CreateParameter(); pn.ParameterName = "@n"; pn.Value = name.Trim(); cmd.Parameters.Add(pn);
                        var pp = cmd.CreateParameter(); pp.ParameterName = "@p"; pp.Value = string.IsNullOrEmpty(phone) ? (object)DBNull.Value : phone.Trim(); cmd.Parameters.Add(pp);
                        var pt = cmd.CreateParameter(); pt.ParameterName = "@t"; pt.Value = tags ?? ""; cmd.Parameters.Add(pt);
                        var pb = cmd.CreateParameter(); pb.ParameterName = "@b"; pb.Value = string.IsNullOrEmpty(birthday) ? (object)DBNull.Value : birthday.Trim(); cmd.Parameters.Add(pb);
                        var pr = cmd.CreateParameter(); pr.ParameterName = "@r"; pr.Value = string.IsNullOrEmpty(remark) ? (object)DBNull.Value : remark.Trim(); cmd.Parameters.Add(pr);
                        var pcb = cmd.CreateParameter(); pcb.ParameterName = "@cb"; pcb.Value = createdBy > 0 ? (object)createdBy : DBNull.Value; cmd.Parameters.Add(pcb);
                        var pcbn = cmd.CreateParameter(); pcbn.ParameterName = "@cbn"; pcbn.Value = string.IsNullOrEmpty(createdByName) ? (object)DBNull.Value : createdByName.Trim(); cmd.Parameters.Add(pcbn);
                        return Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                catch (SQLiteException)
                {
                    if (attempt == 4) throw new BizException(UiText.MemberErrCardNo);
                }
            }
            throw new BizException(UiText.MemberErrCardNo);
        }

        /// <summary>
        /// 建档经办人：前端点名的员工优先（收银台可指定「谁办的卡」），
        /// 该员工不存在或已停用则退回当前登录账号 —— 与收银台 EffectiveStaff 同一口径。
        /// </summary>
        private static int EffectiveCreator(int staffId, out string creatorName)
        {
            if (staffId > 0 && StaffNameExists(staffId, out creatorName)) return staffId;
            int sid = Session.IsLoggedIn ? Session.StaffId : 1;
            creatorName = Session.IsLoggedIn ? Session.Name : "系统";
            return sid;
        }

        /// <summary>员工是否存在且在职（软删除视同不存在，不能挂到离职员工名下）。</summary>
        private static bool StaffNameExists(int staffId, out string name)
        {
            name = "";
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM staff WHERE id=@id AND enabled=1";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = staffId; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value)
                {
                    name = Convert.ToString(v);
                    return true;
                }
            }
            return false;
        }

        private static string NextCardNo(SQLiteConnection conn, SQLiteTransaction txn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT MAX(CAST(substr(card_no,4) AS INTEGER)) FROM members WHERE card_no LIKE 'VIP%'";
                object v = cmd.ExecuteScalar();
                int next = (v == null || v == DBNull.Value) ? 1 : Convert.ToInt32(v) + 1;
                return "VIP" + next.ToString("D4");
            }
        }

        private static void EnsureTransition(string current, string target)
        {
            bool ok =
                (target == "冻结" && current == "正常") ||
                (target == "正常" && (current == "冻结" || current == "挂失")) ||
                (target == "挂失" && (current == "正常" || current == "冻结"));
            if (!ok) throw new BizException("当前状态「" + current + "」不允许流转到「" + target + "」");
        }
    }
}
