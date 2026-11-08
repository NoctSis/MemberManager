using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>桥接服务集（二）：储值管理（方案/充值/退款红冲）与次卡（模板/售卡/冻结/核销记录）。</summary>
    public class StoredValueService
    {
        public class PkgRow
        {
            public int Id;
            public decimal Amount;
            public decimal Gift;
            public bool Enabled;
        }

        public class RechargeRec
        {
            public int Id;
            public int MemberId;
            public string Time;
            public string MemberName;
            public string CardNo;
            public decimal Amount;
            public decimal Gift;
            public string Method;
            public string Operator;
            public string Status;      // 正常 / 已退款
            public string RefundNote;
        }

        public class ValueMetrics
        {
            public decimal TodayRecharge;
            public decimal TodayGift;
            public decimal MonthRecharge;
            public decimal Liability;
        }

        public List<PkgRow> Packages()
        {
            var list = new List<PkgRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 只回未软删的行（店主 09-27 加 deleted_at 列，界面不展示已删档位）
                cmd.CommandText = "SELECT id,amount,gift,enabled FROM recharge_packages" +
                    " WHERE deleted_at IS NULL ORDER BY amount";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new PkgRow
                        {
                            Id = r.GetInt32(0),
                            Amount = Convert.ToDecimal(r.GetValue(1)),
                            Gift = Convert.ToDecimal(r.GetValue(2)),
                            Enabled = Convert.ToInt32(r.GetValue(3)) == 1
                        });
                }
            }
            return list;
        }

        public void SavePackage(int id, decimal amount, decimal gift, bool enabled)
        {
            if (amount <= 0) throw new BizException("充值金额必须大于 0");
            if (gift < 0) throw new BizException("赠送金额不能为负数");
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 查重只看未软删的行；编辑时排除自身（改金额改到其他已有档位会撞，改回原值不算撞）
                    using (var cq = conn.CreateCommand())
                    {
                        cq.Transaction = txn;
                        cq.CommandText = "SELECT id,enabled,gift FROM recharge_packages" +
                            " WHERE amount=@a AND deleted_at IS NULL AND (id<>@self)";
                        W.P(cq, "@a", amount); W.P(cq, "@self", id <= 0 ? -1 : id);
                        using (var r = cq.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                bool on = Convert.ToInt32(r.GetValue(1)) == 1;
                                decimal g = Convert.ToDecimal(r.GetValue(2));
                                throw new BizException("金额档位 ¥" + amount.ToString("0.##") +
                                    " 已存在（当前" + (on ? "启用中" : "已停用") + "，赠送 ¥" + g.ToString("0.##") +
                                    "），请直接编辑该行，或换一个金额");
                            }
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        if (id <= 0)
                            cmd.CommandText = "INSERT INTO recharge_packages(amount,gift,enabled) VALUES(@a,@g,@e)";
                        else
                            cmd.CommandText = "UPDATE recharge_packages SET amount=@a,gift=@g,enabled=@e WHERE id=@id";
                        W.P(cmd, "@a", amount); W.P(cmd, "@g", gift); W.P(cmd, "@e", enabled ? 1 : 0);
                        if (id > 0) W.P(cmd, "@id", id);
                        try { cmd.ExecuteNonQuery(); }
                        catch (SQLiteException) { throw new BizException("该金额档位已存在，请直接修改或更换金额"); }
                    }
                    W.Log(conn, txn, "储值管理", id <= 0 ? "新增充值方案" : "修改充值方案",
                        "充" + amount.ToString("0.##") + "送" + gift.ToString("0.##") + (enabled ? "（启用）" : "（停用）"));
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
        /// 软删除充值方案（店主 09-27 定）：写 deleted_at + enabled=0 保留行；
        /// 历史充值流水不受影响（流水不引用方案 id，本来就与档位配置解耦）。
        /// UNIQUE(amount) 约束仍在，金额被删后短期内无法重新创建同档位——
        /// 想恢复请让店长在库表里清 deleted_at，或改一个新金额（同口径：项目/员工软删也一样）。
        /// </summary>
        public void DeletePackage(int id)
        {
            if (id <= 0) throw new BizException("方案 id 无效");
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal amount = 0m; decimal gift = 0m; bool found = false;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT amount,gift FROM recharge_packages WHERE id=@id AND deleted_at IS NULL";
                        W.P(cmd, "@id", id);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read()) { amount = Convert.ToDecimal(r.GetValue(0)); gift = Convert.ToDecimal(r.GetValue(1)); found = true; }
                        }
                    }
                    if (!found) throw new BizException("方案不存在或已删除");
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE recharge_packages SET deleted_at=datetime('now','localtime'), enabled=0 WHERE id=@id";
                        W.P(cmd, "@id", id);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "储值管理", "删除充值方案",
                        "充" + amount.ToString("0.##") + "送" + gift.ToString("0.##") + "（已删除，历史记录保留）");
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

        public ValueMetrics Metrics()
        {
            var m = new ValueMetrics();
            string bizDate = W.BizDate();
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    // 10-14 M6：今日/本月充值改从 orders(kind='充值',status='正常') 取，与报表页 RangeSummary 同口径——
                    // 已退款的充值单（status='已退款'）自动不计，修掉「退款后储值页仍虚高、与报表页不一致」。
                    // 赠送仍按 member_flows type='赠送'（发放口径，不随退冲减，店主 10-14 定）。
                    cmd.CommandText = "SELECT" +
                        " (SELECT IFNULL(SUM(receivable),0) FROM orders WHERE kind='充值' AND status='正常' AND biz_date=@d)," +
                        " (SELECT IFNULL(SUM(amount),0) FROM member_flows WHERE type='赠送' AND substr(created_at,1,10)=@d)";
                    W.P(cmd, "@d", bizDate);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            m.TodayRecharge = Convert.ToDecimal(r.GetValue(0));
                            m.TodayGift = Convert.ToDecimal(r.GetValue(1));
                        }
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(SUM(receivable),0) FROM orders WHERE kind='充值' AND status='正常' AND substr(biz_date,1,7)=@mo";
                    W.P(cmd, "@mo", bizDate.Substring(0, 7));
                    m.MonthRecharge = Convert.ToDecimal(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(SUM(balance),0) FROM members WHERE status<>'挂失' AND IFNULL(deleted_at,'')=''";
                    m.Liability = Convert.ToDecimal(cmd.ExecuteScalar());
                }
            }
            return m;
        }

        /// <summary>充值记录（含红冲退款单），按 id DESC 分页。limit/offset 供前端「加载更多」增量拉取。</summary>
        public List<RechargeRec> Records(int limit, int offset)
        {
            var list = new List<RechargeRec>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT f.id, f.member_id, f.created_at, m.name, m.card_no, f.amount, f.operator, f.type, f.note," +
                    " IFNULL((SELECT g.amount FROM member_flows g WHERE g.order_id=f.order_id AND g.type='赠送' AND g.member_id=f.member_id),0)," +
                    " IFNULL((SELECT p.method FROM payments p WHERE p.order_id=f.order_id LIMIT 1),'现金')," +
                    " COALESCE((SELECT o.status FROM orders o WHERE o.id=f.order_id),'')" +
                    " FROM member_flows f JOIN members m ON m.id=f.member_id" +
                    " WHERE f.type IN('充值','红冲') ORDER BY f.id DESC LIMIT @l OFFSET @o";
                W.P(cmd, "@l", limit);
                W.P(cmd, "@o", offset);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        bool isRefund = r.GetString(7) == "红冲";
                        // 10-13 H4：原充值行退款后其订单翻「已退款」，列表须同步显示（红冲行本身也视为退款）
                        string orderStatus = r.IsDBNull(11) ? "" : r.GetString(11);
                        bool refunded = isRefund || orderStatus == "已退款";
                        list.Add(new RechargeRec
                        {
                            Id = r.GetInt32(0),
                            MemberId = r.GetInt32(1),
                            Time = r.GetString(2),
                            MemberName = r.GetString(3),
                            CardNo = r.GetString(4),
                            Amount = Convert.ToDecimal(r.GetValue(5)),
                            Gift = Convert.ToDecimal(r.GetValue(9)),
                            Method = r.GetString(10),
                            Operator = r.GetString(6),
                            Status = refunded ? "已退款" : "正常",
                            RefundNote = isRefund ? r.GetString(8) : null
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>充值记录总条数（不受 offset/limit 影响）。前端「已显示 X / 共 Y 条」的 Y 取这里。</summary>
        public int RecordsCount()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM member_flows f WHERE f.type IN('充值','红冲')";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public class ExportResult
        {
            public string Path;
            public string Name;
            public long Size;
            public string Period;
            /// <summary>本次导出行数（两 sheet 合计）。</summary>
            public int Count;
            /// <summary>是否触到单次上限（分段导出时界面据此提示缩小时间段）。</summary>
            public bool Truncated;
        }

        /// <summary>
        /// 导出会员核销记录（真 .xlsx，零第三方依赖，走 ExcelWriter，与营业报表同构）。
        /// 双 sheet：「充值记录」（储值+赠送）+「次卡核销记录」；口径与前端「会员核销记录」tab 一致。
        /// 期间取两表并集。落点由 W.ResolveExportDir 统一解析：本次临时 saveDir（非空）＞全局 export_dir 设置＞兜底 data\exports；
        /// 导出动作写入操作日志（模块「储值管理」）。
        /// </summary>
        public ExportResult ExportRecords(string operatorName, string saveDir = null)
        {
            var list = Records(100000, 0);
            var uses = new CardService().UseRecords(100000, 0);
            return ExportCore(operatorName, saveDir, list, uses, 0);
        }

        /// <summary>
        /// M9（店主 10-15）：按时间段导出——「单次导出有上限」是设计而非缺陷，
        /// 全量就分几段导。每表上限 5000 行，触限时 Truncated=true（界面提示缩小时间段）。
        /// C2（10-21 店主定）：区间过滤改**营业日口径**——与报表/日结一致：
        /// 充值流水按 member_flows.biz_date；核销按 created_at 偏移日切点换算。
        /// 「2026-10-01 ~ 2026-10-31」= 营业日 10-01 至 10-31（含 10-31 当天营业到次日日切点）。
        /// </summary>
        public ExportResult ExportRecordsRange(string operatorName, string saveDir, string fromDate, string toDate)
        {
            var list = RecordsRange(fromDate, toDate, 5000);
            var uses = new CardService().UseRecordsRange(fromDate, toDate, 5000);
            return ExportCore(operatorName, saveDir, list, uses, 5000);
        }

        /// <summary>充值/红冲流水按时间段拉取（M9 分段导出用；C2 起按 biz_date 营业日过滤）。</summary>
        public List<RechargeRec> RecordsRange(string fromDate, string toDate, int limit)
        {
            var list = new List<RechargeRec>();
            string f = (fromDate ?? "").Trim(), t = (toDate ?? "").Trim();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // C2（10-21）：营业日口径——biz_date 列原生存的就是营业日，直接比。
                // 老库若 biz_date 为空（极端早期数据），回落 created_at 日期（IFNULL 双保险）。
                cmd.CommandText =
                    "SELECT f.id, f.member_id, f.created_at, m.name, m.card_no, f.amount, f.operator, f.type, f.note," +
                    " IFNULL((SELECT g.amount FROM member_flows g WHERE g.order_id=f.order_id AND g.type='赠送' AND g.member_id=f.member_id),0)," +
                    " IFNULL((SELECT p.method FROM payments p WHERE p.order_id=f.order_id LIMIT 1),'现金')," +
                    " COALESCE((SELECT o.status FROM orders o WHERE o.id=f.order_id),'')" +
                    " FROM member_flows f JOIN members m ON m.id=f.member_id" +
                    " WHERE f.type IN('充值','红冲')" +
                    (f.Length == 10 ? " AND IFNULL(f.biz_date, substr(f.created_at,1,10))>=@f" : "") +
                    (t.Length == 10 ? " AND IFNULL(f.biz_date, substr(f.created_at,1,10))<=@t" : "") +
                    " ORDER BY f.id DESC LIMIT @l";
                if (f.Length == 10) W.P(cmd, "@f", f);
                if (t.Length == 10) W.P(cmd, "@t", t);
                W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        bool isRefund = r.GetString(7) == "红冲";
                        string orderStatus = r.IsDBNull(11) ? "" : r.GetString(11);
                        bool refunded = isRefund || orderStatus == "已退款";
                        list.Add(new RechargeRec
                        {
                            Id = r.GetInt32(0),
                            MemberId = r.GetInt32(1),
                            Time = r.GetString(2),
                            MemberName = r.GetString(3),
                            CardNo = r.GetString(4),
                            Amount = Convert.ToDecimal(r.GetValue(5)),
                            Gift = Convert.ToDecimal(r.GetValue(9)),
                            Method = r.GetString(10),
                            Operator = r.GetString(6),
                            Status = refunded ? "已退款" : "正常",
                            RefundNote = isRefund ? r.GetString(8) : null
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>导出主体（全量版与分段版共用）。capHint=0 表示不限（不置 Truncated）。</summary>
        private ExportResult ExportCore(string operatorName, string saveDir, List<RechargeRec> list, List<CardService.UseRec> uses, int capHint)
        {
            string earliest = null, latest = null;
            if (list.Count > 0)
            {
                earliest = list[list.Count - 1].Time.Substring(0, 10);
                latest = list[0].Time.Substring(0, 10);
            }
            if (uses.Count > 0)
            {
                string useEarliest = uses[uses.Count - 1].Time.Substring(0, 10);
                string useLatest = uses[0].Time.Substring(0, 10);
                if (earliest == null || String.Compare(useEarliest, earliest) < 0) earliest = useEarliest;
                if (latest == null || String.Compare(useLatest, latest) > 0) latest = useLatest;
            }
            string period = (earliest != null && latest != null) ? (earliest + " ~ " + latest) : "（无记录）";

            var x = new ExcelWriter();
            x.Sheet("充值记录")
                .Widths(20, 14, 20, 14, 12, 14, 14, 12, 22)
                .FreezeHeaders(true)
                .Text(1, 1, "时间", ExcelWriter.S.Header)
                .Text(1, 2, "会员", ExcelWriter.S.Header)
                .Text(1, 3, "卡号", ExcelWriter.S.Header)
                .Text(1, 4, "充值金额", ExcelWriter.S.Header)
                .Text(1, 5, "赠送金额", ExcelWriter.S.Header)
                .Text(1, 6, "收款方式", ExcelWriter.S.Header)
                .Text(1, 7, "操作员", ExcelWriter.S.Header)
                .Text(1, 8, "状态", ExcelWriter.S.Header)
                .Text(1, 9, "退款备注", ExcelWriter.S.Header);

            int r = 2;
            // L6（店主 10-14）：充值/退款分区累计、净额单列。一单两行（原充值+红冲）直接相加
            // 会把本金和「本金+赠送」混在一起，合计看不出真实净收；红冲行的 Gift 是原单
            // 赠送额（正数），净赠送必须减掉才对的上。
            decimal sumNormal = 0m, sumRefund = 0m, sumGiftNormal = 0m, sumGiftRefund = 0m;
            int normalCount = 0, refundCount = 0;
            foreach (var rec in list)
            {
                x.Text(r, 1, rec.Time ?? "", ExcelWriter.S.Plain)
                    .Text(r, 2, rec.MemberName ?? "", ExcelWriter.S.Plain)
                    .Text(r, 3, rec.CardNo ?? "", ExcelWriter.S.Plain)
                    .Num(r, 4, rec.Amount, ExcelWriter.S.Money)
                    .Num(r, 5, rec.Gift, ExcelWriter.S.Money)
                    .Text(r, 6, rec.Method ?? "", ExcelWriter.S.Plain)
                    .Text(r, 7, rec.Operator ?? "", ExcelWriter.S.Plain)
                    .Text(r, 8, rec.Status ?? "", ExcelWriter.S.Plain)
                    .Text(r, 9, rec.RefundNote ?? "", ExcelWriter.S.Plain);
                if (rec.Status == "已退款") { refundCount++; sumRefund += rec.Amount; sumGiftRefund += rec.Gift; }
                else { normalCount++; sumNormal += rec.Amount; sumGiftNormal += rec.Gift; }
                r++;
            }
            if (list.Count == 0)
                x.Text(2, 1, "暂无充值记录", ExcelWriter.S.Plain);
            else
            {
                x.Text(r, 1, "合计（净额）", ExcelWriter.S.MoneyHead)
                    .Text(r, 2, "共 " + list.Count + " 条（正常 " + normalCount + " / 已退款 " + refundCount + "）", ExcelWriter.S.Plain)
                    .Num(r, 4, sumNormal + sumRefund, ExcelWriter.S.MoneyHead)
                    .Num(r, 5, sumGiftNormal - sumGiftRefund, ExcelWriter.S.MoneyHead);
                x.Text(r + 1, 1, "其中：正常充值", ExcelWriter.S.Plain)
                    .Num(r + 1, 4, sumNormal, ExcelWriter.S.MoneyHead)
                    .Num(r + 1, 5, sumGiftNormal, ExcelWriter.S.MoneyHead);
                x.Text(r + 2, 1, "其中：退款（红冲）", ExcelWriter.S.Plain)
                    .Num(r + 2, 4, sumRefund, ExcelWriter.S.MoneyHead)
                    .Num(r + 2, 5, -sumGiftRefund, ExcelWriter.S.MoneyHead);
            }

            // Sheet 2：次卡核销记录 —— 一行 = 一次核销（remain_after 是核销后的剩余次数）
            x.Sheet("次卡核销记录")
                .Widths(20, 14, 18, 22, 14, 14)
                .FreezeHeaders(true)
                .Text(1, 1, "时间", ExcelWriter.S.Header)
                .Text(1, 2, "会员", ExcelWriter.S.Header)
                .Text(1, 3, "卡名称", ExcelWriter.S.Header)
                .Text(1, 4, "服务项目", ExcelWriter.S.Header)
                .Text(1, 5, "核销后剩余", ExcelWriter.S.Header)
                .Text(1, 6, "操作员", ExcelWriter.S.Header);

            int ur = 2;
            foreach (var u in uses)
            {
                x.Text(ur, 1, u.Time ?? "", ExcelWriter.S.Plain)
                    .Text(ur, 2, u.MemberName ?? "", ExcelWriter.S.Plain)
                    .Text(ur, 3, u.CardName ?? "", ExcelWriter.S.Plain)
                    .Text(ur, 4, u.ItemName ?? "", ExcelWriter.S.Plain)
                    .Num(ur, 5, u.RemainAfter, ExcelWriter.S.Plain)
                    .Text(ur, 6, u.Operator ?? "", ExcelWriter.S.Plain);
                ur++;
            }
            if (uses.Count == 0)
                x.Text(2, 1, "暂无次卡核销记录", ExcelWriter.S.Plain);
            else
                x.Text(ur, 1, "合计", ExcelWriter.S.Plain)
                    .Text(ur, 2, "共 " + uses.Count + " 次核销", ExcelWriter.S.Plain);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = W.ResolveExportDir(saveDir);   // 统一解析：本次临时 ＞ 全局 export_dir ＞ data\exports
            string name = "会员核销记录-" + stamp + ".xlsx";
            string path = System.IO.Path.Combine(dir, name);
            long size = x.Save(path);
            Logger.Info("导出会员核销记录：" + path + "（" + size + " 字节）储值 " + list.Count + " 条 + 次卡核销 " + uses.Count + " 次，操作人 " + operatorName);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "储值管理", "导出会员核销记录",
                    "区间 " + period + "（营业日口径，日切点 " + DatabaseInitializer.GetSetting("day_cut", "02:00") +
                    "），储值 " + list.Count + " 条 + 次卡核销 " + uses.Count + " 次，文件 " + name);
            return new ExportResult
            {
                Path = path,
                Name = name,
                Size = size,
                Period = period,
                Count = list.Count + uses.Count,
                Truncated = capHint > 0 && (list.Count >= capHint || uses.Count >= capHint)
            };
        }

        /// <summary>会员充值：充值+赠送两条流水、余额累加、订单+收款（现金/余额）。同一事务。</summary>
        public string Recharge(int memberId, decimal amount, decimal gift, string method, string operatorName)
        {
            // 薄壳：保持 5 参签名（8 个冒烟脚本与老调用方依赖它），经手人回落登录账号（operatorStaffId=0）。
            return RechargeByStaff(memberId, amount, gift, method, operatorName, 0);
        }

        /// <summary>
        /// 会员充值（可指定经手人）。operatorStaffId&gt;0 时提成/流水归点名的员工
        /// （店里常共用一个账号登录，登录人≠经单人），姓名一律从 staff 表取快照、前端只传 id 防伪造；
        /// operatorStaffId&lt;=0 回落登录账号（与旧 Recharge 行为一致）。
        /// </summary>
        public string RechargeByStaff(int memberId, decimal amount, decimal gift, string method, string operatorName, int operatorStaffId)
        {
            if (amount <= 0) throw new BizException("充值金额必须大于 0");
            if (gift < 0) gift = 0;
            string orderNo;
            // 积分配置在事务外读好，避免 WAL 自死锁
            var pointCfg = new PointService().GetPointConfig();
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 经手人：点名员工优先，否则登录账号（与 orders.staff_id 同源）；流水 operator 名从 staff 表取快照
                    int opStaffId = operatorStaffId > 0 ? operatorStaffId : (Session.IsLoggedIn ? Session.StaffId : 1);
                    string opName = operatorName;
                    if (operatorStaffId > 0)
                    {
                        using (var cmdWho = conn.CreateCommand())
                        {
                            cmdWho.Transaction = txn;
                            cmdWho.CommandText = "SELECT name FROM staff WHERE id=@id";
                            W.P(cmdWho, "@id", operatorStaffId);
                            object who = cmdWho.ExecuteScalar();
                            if (who != null && who != DBNull.Value) opName = Convert.ToString(who);
                        }
                    }
                    string status = W.GetBalanceStatus(conn, txn, memberId);
                    if (status == "挂失") throw new BizException("会员已挂失，不能充值");
                    if (W.IsMemberDeleted(conn, txn, memberId)) throw new BizException("该会员已注销，不能充值");
                    // L8（店主 10-14）：支付方式白名单——原来非微信/支付宝一律静默记「现金」，
                    // 会员余额一旦从界面放出就会凭空增收（钱没扣、账记现金）。显式拒收。
                    if (method != "现金" && method != "微信" && method != "支付宝")
                        throw new BizException("充值仅支持现金、微信、支付宝" +
                            (method == "会员余额" ? "（会员余额不能用于充值，请到收银台消费）" : ""));
                    decimal balance = W.GetBalance(conn, txn, memberId);
                    int orderId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        orderNo = W.NextNo(conn, txn, "C", W.BizDate());
                        cmd.CommandText = "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                            " VALUES(@no,@d,@m,@s,@sub,0,@rc,'充值','正常'); SELECT last_insert_rowid();";
                        W.P(cmd, "@no", orderNo); W.P(cmd, "@d", W.BizDate()); W.P(cmd, "@m", memberId);
                        W.P(cmd, "@s", opStaffId);
                        W.P(cmd, "@sub", amount); W.P(cmd, "@rc", amount);
                        orderId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO payments(order_id,method,amount) VALUES(@o,@m,@a)";
                        W.P(cmd, "@o", orderId); W.P(cmd, "@m", method);
                        W.P(cmd, "@a", amount);
                        cmd.ExecuteNonQuery();
                    }
                    decimal after = balance + amount + gift;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET balance=@b WHERE id=@id";
                        W.P(cmd, "@b", after); W.P(cmd, "@id", memberId);
                        cmd.ExecuteNonQuery();
                    }
                    W.InsertFlow(conn, txn, memberId, "充值", amount, balance + amount, orderId, opName,
                        "充值" + amount.ToString("0.##") + "元（" + method + "）");
                    if (gift > 0)
                        W.InsertFlow(conn, txn, memberId, "赠送", gift, after, orderId, opName, "充值活动赠送");
                    // 充值提成：按本金计提（赠送不计），归收钱员工；比例 个人(充值项) > 全局充值比例。
                    CommissionService.InsertByRate(conn, txn, orderId, opStaffId, "充值", amount,
                        CommissionService.ResolveRechargeRate(conn, txn, opStaffId));
                    // 等级自动判定（店主 2026-09-26 定：等级不手工改，按累计实收充值达标升档）。
                    // 只在「真的升了」时补一条日志——每次充值都刷一条没人看。
                    LevelRules.Change lv = LevelRules.Apply(conn, txn, memberId);
                    if (lv != null && lv.Changed)
                    {
                        W.Log(conn, txn, UiText.NavMembers, "等级变更",
                            lv.MemberName + " 累计充值达标：" + lv.Old + "→" + lv.New);
                        Logger.Info("等级自动升档：会员 " + lv.MemberId + " " + lv.Old + "→" + lv.New);
                    }
                    W.Log(conn, txn, "储值管理", "会员充值",
                        "充值" + amount.ToString("0.##") + "送" + gift.ToString("0.##") + "，单号" + orderNo + "，经手人 " + opName);
                    // 充值也按规则给积分（本金+赠送都计）；场景开关由店主自由配置
                    if (pointCfg != null && pointCfg.Enabled && pointCfg.EarnRecharge)
                    {
                        int pts = new PointService().AddPoints(memberId, amount + gift, "充值", pointCfg, conn, txn);
                        if (pts > 0) Logger.Info("积分：会员 " + memberId + " 充值获得 " + pts + " 分");
                    }
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
            return orderNo;
        }

        /// <summary>
        /// 充值退款（红冲）：本金与赠送同笔回滚，余额扣回（扣为负则拒绝），原记录保留可追溯。
        /// 店主 09-27 拍板：退款只有店长可操作，收银员/技师一律拒绝（与前端「退款/改价」门控口径一致）。
        /// 10-13 冲账闭环（H1/H2/H4，店主批准「全做」+「积分也扣回」）：
        ///   ① 定位原充值订单（本会员最近一笔 正常 充值单、本金匹配）并翻成「已退款」——
        ///      账面查询（今日营收/日结/业绩/交接班角标）全部带 status='正常' 过滤，翻掉即自动核减；
        ///      流水页「已退款」筛选、收银台退款笔数从此有真实数据。
        ///   ② 给该单提成插一条负值冲账行（金额/提成取负、未结算）——提成榜/结算/明细与正行自动抵消，
        ///      兑现提成页「红冲同笔扣回」的文案承诺；正行保留可追溯。
        ///   ③ 按发放同式扣回本单充值所得积分（取 min(应扣, 现有)，不为负）。
        ///   ④ 红冲流水挂上原订单 order_id —— 储值页「今日充值/赠送」卡片按此核减，防重复退。
        /// 等级/累计充值维持「只升不降」（店主拍板，level-smoke B8 锁死），退款不冲。</summary>
        public void Refund(int memberId, decimal amount, decimal gift, string reason, string operatorName)
        {
            if (!Session.IsManager) throw new BizException("退款仅限店长操作，请通知店长处理");
            if (reason == null || reason.Trim().Length == 0) throw new BizException("退款必须填写原因");
            // 积分配置在事务外读好（WAL 自死锁规矩，与 RechargeByStaff 同款）
            var pointCfg = new PointService().GetPointConfig();
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    decimal balance = W.GetBalance(conn, txn, memberId);
                    decimal rollback = amount + gift;
                    if (balance < rollback) throw new BizException("会员当前余额不足扣回本金与赠送，请先处理账户");
                    decimal after = balance - rollback;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET balance=@b WHERE id=@id";
                        W.P(cmd, "@b", after); W.P(cmd, "@id", memberId);
                        cmd.ExecuteNonQuery();
                    }

                    // ① 定位原充值订单：本会员最近一笔 正常 充值单、本金精确匹配。
                    // 定位不到（历史无单流水等）时不冲订单/提成/积分，余额红冲照常，避免误伤。
                    int? orderId = null;
                    int orderStaff = 0;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT id, staff_id FROM orders" +
                            " WHERE member_id=@m AND kind='充值' AND status='正常' AND receivable=@a" +
                            " ORDER BY id DESC LIMIT 1";
                        W.P(cmd, "@m", memberId); W.P(cmd, "@a", amount);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read()) { orderId = r.GetInt32(0); orderStaff = r.GetInt32(1); }
                        }
                    }
                    if (orderId.HasValue)
                    {
                        // 防重复退：同一订单已存在红冲流水则拒绝（UI 原充值行翻「已退款」后按钮消失，
                        // 这里是接口层兜底——直调 API 也能拦住二次扣款）
                        int redCount;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "SELECT COUNT(*) FROM member_flows WHERE type='红冲' AND order_id=@o";
                            W.P(cmd, "@o", orderId.Value);
                            redCount = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                        if (redCount > 0)
                            throw new BizException("该充值单已退款过（存在红冲记录），不能重复退款");

                        // ① 原单翻「已退款」
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE orders SET status='已退款' WHERE id=@id";
                            W.P(cmd, "@id", orderId.Value);
                            cmd.ExecuteNonQuery();
                        }
                        // ② 提成红冲：镜像负值行（金额/提成取负、settled=0），榜单/结算/明细自动抵消。
                        // ★ SQLite 单连接铁律：不能在 reader 打开时于同连接另发写（会报
                        //   "No transaction is active" 或挂起）——先全读进内存、关 reader，再逐条写，
                        //   与 LevelRules.ApplyAll 同范式。
                        var revRows = new List<decimal[]>();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "SELECT amount, rate, commission FROM commissions WHERE order_id=@o AND type='充值'";
                            W.P(cmd, "@o", orderId.Value);
                            using (var r = cmd.ExecuteReader())
                            {
                                while (r.Read())
                                    revRows.Add(new decimal[]
                                    {
                                        Convert.ToDecimal(r.GetValue(0)),
                                        Convert.ToDecimal(r.GetValue(1)),
                                        Convert.ToDecimal(r.GetValue(2))
                                    });
                            }
                        }
                        foreach (var row in revRows)
                        {
                            decimal a = row[0], c = row[2];
                            if (a == 0m && c == 0m) continue;
                            using (var ins = conn.CreateCommand())
                            {
                                ins.Transaction = txn;
                                ins.CommandText = "INSERT INTO commissions(order_id,staff_id,type,amount,rate,commission,share,settled)" +
                                    " VALUES(@o,@s,'充值',@a,@r,@c,'100%',0)";
                                W.P(ins, "@o", orderId.Value);
                                W.P(ins, "@s", orderStaff);
                                W.P(ins, "@a", -a);
                                W.P(ins, "@r", row[1]);
                                W.P(ins, "@c", -c);
                                ins.ExecuteNonQuery();
                            }
                        }
                        // ③ 积分扣回：发放口径 floor((本金+赠送)/每元积分)，只扣到 0 为止
                        if (pointCfg != null && pointCfg.Enabled && pointCfg.EarnRecharge)
                        {
                            int took = new PointService().ClawPoints(memberId, rollback, "退款红冲：充值积分扣回", pointCfg, conn, txn);
                            if (took > 0) Logger.Info("积分：会员 " + memberId + " 退款扣回 " + took + " 分");
                        }
                    }

                    W.InsertFlow(conn, txn, memberId, "红冲", -rollback, after, orderId, operatorName, "充值退款红冲，原因：" + reason.Trim());
                    // 等级/累计充值核减（店主 2026-10-14 定「退款要减」）：原充值单已在上方翻「已退款」，
                    // CumulativeRecharge 现按订单状态净减 → 重算并写回 members.level，等级可能降档。
                    // 定位不到原订单（历史无单）时累计不变、Apply 为 no-op；下次启动 InitMemberLevels 也会兜底对齐。
                    LevelRules.Change lv = LevelRules.Apply(conn, txn, memberId);
                    if (lv != null && lv.Changed)
                        W.Log(conn, txn, UiText.NavMembers, "等级变更",
                            "退款核减累计充值：" + lv.Old + "→" + lv.New + "（会员 " + memberId + "）");
                    W.Log(conn, txn, "储值管理", "充值退款",
                        "红冲 " + rollback.ToString("0.##") +
                        (orderId.HasValue ? "，原订单已标「已退款」，提成/积分同笔扣回" : "（未定位到原订单，未冲提成/积分）") +
                        "，原因：" + reason.Trim());
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
    }

    /// <summary>次卡 / 期限卡。</summary>
    public class CardService
    {
        public class TplRow
        {
            public int Id;
            public string Name;
            public string ItemName;
            public int Times;
            public decimal Price;
            public int ValidMonths;
            public bool Enabled;
        }

        public class MemberCardRow
        {
            public int Id;
            public string MemberName;
            public string CardNo;
            public string TemplateName;
            public int Total;
            public int Remain;
            public string ExpireDate;
            public string Status;
            public int PurchaseTimes;  // 付费次数（老卡无记录时为 total）
            public int GiftTimes;      // 赠送次数（老卡为 0）
        }

        public class UseRec
        {
            public string Time;
            public string MemberName;
            public string CardName;
            public string ItemName;
            public int RemainAfter;
            public string Operator;
        }

        public List<TplRow> Templates()
        {
            var list = new List<TplRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,name,item_name,times,price,valid_months,enabled FROM card_templates ORDER BY id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new TplRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            ItemName = r.GetString(2),
                            Times = Convert.ToInt32(r.GetValue(3)),
                            Price = Convert.ToDecimal(r.GetValue(4)),
                            ValidMonths = Convert.ToInt32(r.GetValue(5)),
                            Enabled = Convert.ToInt32(r.GetValue(6)) == 1
                        });
                }
            }
            return list;
        }

        public void SaveTemplate(int id, string name, string itemName, int times, decimal price, int validMonths, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BizException("模板名称不能为空");
            if (times <= 0) throw new BizException("次数必须是正整数");
            if (price <= 0) throw new BizException("价格必须大于 0");
            if (validMonths <= 0) validMonths = 12;
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        if (id <= 0)
                            cmd.CommandText = "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES(@n,@i,@t,@p,@v,@e)";
                        else
                            cmd.CommandText = "UPDATE card_templates SET name=@n,item_name=@i,times=@t,price=@p,valid_months=@v,enabled=@e WHERE id=@id";
                        W.P(cmd, "@n", name.Trim()); W.P(cmd, "@i", itemName.Trim()); W.P(cmd, "@t", times);
                        W.P(cmd, "@p", price); W.P(cmd, "@v", validMonths); W.P(cmd, "@e", enabled ? 1 : 0);
                        if (id > 0) W.P(cmd, "@id", id);
                        try { cmd.ExecuteNonQuery(); }
                        catch (SQLiteException) { throw new BizException("同名模板已存在"); }
                    }
                    W.Log(conn, txn, "储值与次卡", id <= 0 ? "新建卡模板" : "修改卡模板", name.Trim());
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

        public List<MemberCardRow> MemberCards()
        {
            var list = new List<MemberCardRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT mc.id, m.name, m.card_no, t.name, mc.total, mc.remain, mc.expire_date, mc.status," +
                    " IFNULL(mc.purchase_times,mc.total), IFNULL(mc.gift_times,0)" +
                    " FROM member_cards mc JOIN members m ON m.id=mc.member_id JOIN card_templates t ON t.id=mc.template_id" +
                    " ORDER BY mc.id DESC";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new MemberCardRow
                        {
                            Id = r.GetInt32(0),
                            MemberName = r.GetString(1),
                            CardNo = r.GetString(2),
                            TemplateName = r.GetString(3),
                            Total = Convert.ToInt32(r.GetValue(4)),
                            Remain = Convert.ToInt32(r.GetValue(5)),
                            ExpireDate = r.GetString(6),
                            Status = r.GetString(7),
                            PurchaseTimes = Convert.ToInt32(r.GetValue(8)),
                            GiftTimes = Convert.ToInt32(r.GetValue(9)),
                        });
                }
            }
            return list;
        }

        /// <summary>次卡核销流水，按 id DESC 分页。limit/offset 供前端「加载更多」增量拉取。</summary>
        public List<UseRec> UseRecords(int limit, int offset)
        {
            var list = new List<UseRec>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT u.created_at, m.name, t.name, t.item_name, u.remain_after, u.operator" +
                    " FROM card_uses u JOIN member_cards mc ON mc.id=u.member_card_id" +
                    " JOIN members m ON m.id=mc.member_id JOIN card_templates t ON t.id=mc.template_id" +
                    " ORDER BY u.id DESC LIMIT @l OFFSET @o";
                W.P(cmd, "@l", limit);
                W.P(cmd, "@o", offset);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new UseRec
                        {
                            Time = r.GetString(0),
                            MemberName = r.GetString(1),
                            CardName = r.GetString(2),
                            ItemName = r.GetString(3),
                            RemainAfter = Convert.ToInt32(r.GetValue(4)),
                            Operator = r.GetString(5)
                        });
                }
            }
            return list;
        }

        /// <summary>次卡核销按时间段拉取（M9 分段导出用；created_at 日期部分比较）。</summary>
        public List<UseRec> UseRecordsRange(string fromDate, string toDate, int limit)
        {
            var list = new List<UseRec>();
            string f = (fromDate ?? "").Trim(), t = (toDate ?? "").Trim();
            // C2（10-21）：营业日口径——card_uses 没有 biz_date 列，用 created_at 减去日切点
            // 小时数换算营业日（day_cut=02:00 时，凌晨 1:30 的核销算前一天的营业日）。
            // 与 Fmt.BizDateOf 同义；导出（只读、独立连接）直接读 settings 取 day_cut，无 WAL 死锁风险。
            string cut = DatabaseInitializer.GetSetting("day_cut", "02:00");
            string cutMod = "-" + Fmt.ParseCutHour(cut) + " hours";
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT u.created_at, m.name, t.name, t.item_name, u.remain_after, u.operator" +
                    " FROM card_uses u JOIN member_cards mc ON mc.id=u.member_card_id" +
                    " JOIN members m ON m.id=mc.member_id JOIN card_templates t ON t.id=mc.template_id" +
                    (f.Length == 10 ? " WHERE substr(datetime(u.created_at,'" + cutMod + "'),1,10)>=@f" : "") +
                    (f.Length == 10 && t.Length == 10 ? " AND substr(datetime(u.created_at,'" + cutMod + "'),1,10)<=@t" : (t.Length == 10 ? " WHERE substr(datetime(u.created_at,'" + cutMod + "'),1,10)<=@t" : "")) +
                    " ORDER BY u.id DESC LIMIT @l";
                if (f.Length == 10) W.P(cmd, "@f", f);
                if (t.Length == 10) W.P(cmd, "@t", t);
                W.P(cmd, "@l", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new UseRec
                        {
                            Time = r.GetString(0),
                            MemberName = r.GetString(1),
                            CardName = r.GetString(2),
                            ItemName = r.GetString(3),
                            RemainAfter = Convert.ToInt32(r.GetValue(4)),
                            Operator = r.GetString(5)
                        });
                }
            }
            return list;
        }

        /// <summary>次卡核销总次数（不受 offset/limit 影响）。前端「已显示 X / 共 Y 次」的 Y 取这里。</summary>
        public int UseRecordsCount()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM card_uses u" +
                    " JOIN member_cards mc ON mc.id=u.member_card_id" +
                    " JOIN members m ON m.id=mc.member_id JOIN card_templates t ON t.id=mc.template_id";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 指定月份（yyyy-MM，按核销时间 card_uses.created_at）的次卡核销次数。
        /// 前端「本月消耗次数」用（店主 10-08：要能看见一个月消耗的卡次数）。
        /// </summary>
        public int UseRecordsCountByMonth(string month)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM card_uses WHERE substr(created_at,1,7)=@m";
                W.P(cmd, "@m", month ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 售卡：建卡 + 售卡订单 + 收款（不计会员余额），同一事务。
        /// 经手人：operatorStaffId>0 时记为点名的员工（店里常共用一个账号登录，登录人≠经单人）；
        /// 姓名一律从 staff 表取快照，前端只传 id，防伪造留痕。operatorStaffId<=0 回落登录账号。
        /// </summary>
        public string SellCard(int memberId, int templateId, string method, string operatorName,
                               int purchaseTimes, decimal unitPrice, int giftTimes, decimal discount,
                               int operatorStaffId)
        {
            string tplName;
            int months;
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string status = W.GetBalanceStatus(conn, txn, memberId);
                    if (status != "正常") throw new BizException("仅正常状态会员可购卡");
                    if (W.IsMemberDeleted(conn, txn, memberId)) throw new BizException("该会员已注销，不能购卡");
                    // 经手人：点名员工优先，否则登录人；姓名从库里取（防前端伪造）
                    int effStaffId = operatorStaffId > 0 ? operatorStaffId : (Session.IsLoggedIn ? Session.StaffId : 1);
                    string effName = string.IsNullOrEmpty(operatorName) ? "系统" : operatorName;
                    if (operatorStaffId > 0)
                    {
                        using (var cmdWho = conn.CreateCommand())
                        {
                            cmdWho.Transaction = txn;
                            cmdWho.CommandText = "SELECT name FROM staff WHERE id=@id";
                            W.P(cmdWho, "@id", operatorStaffId);
                            object who = cmdWho.ExecuteScalar();
                            if (who != null && who != DBNull.Value) effName = Convert.ToString(who);
                        }
                    }
                    decimal tplPrice; int tplTimes;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,times,price,valid_months FROM card_templates WHERE id=@id AND enabled=1";
                        W.P(cmd, "@id", templateId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("卡模板不存在或已停售");
                            tplName = r.GetString(0); tplTimes = Convert.ToInt32(r.GetValue(1));
                            tplPrice = Convert.ToDecimal(r.GetValue(2)); months = Convert.ToInt32(r.GetValue(3));
                        }
                    }
                    // 次数/单价允许现场改；<=0 表示沿用模板口径（模板价 ÷ 模板次数 = 单次折算价）。
                    // 单价必须严格 > 0：否则乘以 0 会把卡 0 元送出（服务端兜底，不能只靠前端校验）。
                    int useTimes = purchaseTimes > 0 ? purchaseTimes : tplTimes;
                    decimal useUnit = unitPrice > 0 ? unitPrice : (tplTimes > 0 ? Math.Round(tplPrice / tplTimes, 2) : tplPrice);
                    if (useTimes <= 0) throw new BizException("购买次数必须大于 0");
                    if (useUnit <= 0) throw new BizException("单次价必须大于 0");
                    if (giftTimes < 0) throw new BizException("赠送次数不能为负");
                    if (discount < 0) throw new BizException("优惠金额不能为负");
                    decimal gross = Math.Round(useTimes * useUnit, 2);
                    if (discount > gross) throw new BizException("优惠金额不能超过合计金额");
                    decimal payable = Math.Round(gross - discount, 2);
                    // L8（店主 10-14）：同充值——售卡只收现金/微信/支付宝；「会员余额」曾被
                    // 静默记成「现金」，界面一旦放出即凭空增收（钱没扣、账记现金），显式拒收。
                    if (method != "现金" && method != "微信" && method != "支付宝")
                        throw new BizException("售卡仅支持现金、微信、支付宝" +
                            (method == "会员余额" ? "（会员余额不能用于购卡，请到收银台消费）" : ""));
                    int cardTotal = useTimes + giftTimes;
                    string expire = DateTime.Now.AddMonths(months).ToString("yyyy-MM-dd");
                    int orderId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        string no = W.NextNo(conn, txn, "C", W.BizDate());
                        cmd.CommandText = "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                            " VALUES(@no,@d,@m,@s,@sub,0,@rc,'售卡','正常'); SELECT last_insert_rowid();";
                        W.P(cmd, "@no", no); W.P(cmd, "@d", W.BizDate()); W.P(cmd, "@m", memberId);
                        W.P(cmd, "@s", effStaffId);
                        W.P(cmd, "@sub", gross); W.P(cmd, "@rc", payable);
                        orderId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO payments(order_id,method,amount) VALUES(@o,@m,@a)";
                        W.P(cmd, "@o", orderId); W.P(cmd, "@m", method);
                        W.P(cmd, "@a", payable);
                        cmd.ExecuteNonQuery();
                    }
                    int cardId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status," +
                            " purchase_times,gift_times,discount_amount,paid_amount)" +
                            " VALUES(@m,@t,@tt,@r,@e,'正常',@pt,@gt,@dc,@pd); SELECT last_insert_rowid();";
                        W.P(cmd, "@m", memberId); W.P(cmd, "@t", templateId); W.P(cmd, "@tt", cardTotal);
                        W.P(cmd, "@r", cardTotal); W.P(cmd, "@e", expire);
                        W.P(cmd, "@pt", useTimes); W.P(cmd, "@gt", giftTimes);
                        W.P(cmd, "@dc", discount); W.P(cmd, "@pd", payable);
                        cardId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    // 次卡提成（M11，店主 10-14）：售卡时按 settings.card_commission_mode 计——
                    // sale 全额 / split 一半 / use 0（全额留到每次核销按折算价计）。
                    // 率走充值口径（个人充值项 > 个人通配 > 全局充值率），与充值单同规则；
                    // 提成归售卡经手人。
                    // ★ type 只能写 '消费'：commissions 表 CHECK 冻结在 ('充值','消费')（DDL 冻结，
                    //   不能加 '售卡'）——单子的真话由 orders.kind='售卡' 带，Ranks/StaffSummary
                    //   的业绩口径已按 kind 排除售卡行（售卡不计业绩，既定规则），明细徽标按 kind 显示。
                    string ccMode = CommissionService.CardCommissionMode(conn, txn);
                    decimal ccBase = 0m;
                    if (ccMode != "use")
                    {
                        ccBase = Math.Round(payable * (ccMode == "sale" ? 1m : 0.5m), 2);
                        CommissionService.InsertByRate(conn, txn, orderId, effStaffId, "消费", ccBase,
                            CommissionService.ResolveRechargeRate(conn, txn, effStaffId));
                    }
                    string ccNote = ccMode == "use" ? "，提成留到核销时计"
                        : (ccBase > 0 ? "，售卡提成 " + ccBase.ToString("0.##") + " 元（" +
                            CommissionService.CardCommissionModeName(ccMode) + "）" : "");
                    W.Log(conn, txn, "储值与次卡", "售卡", "售出" + tplName + "，" + useTimes + "次" +
                        (giftTimes > 0 ? "+赠" + giftTimes + "次" : "") + "，合计" + gross.ToString("0.##") +
                        (discount > 0 ? "，优惠" + discount.ToString("0.##") : "") + "，实收" + payable.ToString("0.##") + "元" +
                        ccNote,
                        effName);
                    txn.Commit();
                    return tplName;
                }
                catch (Exception ex)
                {
                    // rollback failure must never replace the original exception with an unreadable one
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        public void SetCardStatus(int cardId, string action)
        {
            string target = action == "冻结" ? "冻结" : (action == "解冻" ? "正常" : null);
            if (target == null) throw new BizException("不支持的操作");
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE member_cards SET status=@s WHERE id=@id";
                        W.P(cmd, "@s", target); W.P(cmd, "@id", cardId);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "储值与次卡", action == "冻结" ? "次卡冻结" : "次卡解冻", "卡ID " + cardId);
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
    }
}
