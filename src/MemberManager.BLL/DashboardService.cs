using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 主工作台聚合查询（04 交接包 §2.1）。只读。
    /// 业务表暂无数据时全部返回 0/空列表，指标随 02-04 模块逐个「点亮」。
    /// </summary>
    public class DashboardService
    {
        // ---- 今日指标 ----
        public class TodayMetrics
        {
            /// <summary>今日营收 = 今日实际收款（消费 + 售卡 + 充值全部实收）。
            /// 店主 10-13 定：当日营收要把会员充值的钱算进去。
            /// 与日结总营收（payments 合计）、报表页「当日营收」口径一致 ——
            /// 原先这里把充值排除在外，是全系统唯一一处不一致，现已统一。</summary>
            public decimal Revenue;
            public int RevenueCount;
            public decimal RevenueYesterday;  // 昨日营收（同口径，含充值，用于涨跌对比）
            public decimal Recharge;            // 今日充值（含赠送）—— 单列一项，是营收的组成部分
            public int RechargeCount;
            public decimal Consume;             // 今日消费
            public int ConsumeCount;
            public int NewMembers;              // 今日新增会员（按营业日窗口）
            public int NewMembersMonth;         // 本月累计新增
        }

        // ---- 近7日趋势 ----
        public class TrendPoint
        {
            public string BizDate;
            /// <summary>当日营收 = 当日全部实收（消费 + 售卡 + 充值）。</summary>
            public decimal Revenue;
            /// <summary>当日充值 —— 营收的组成部分，单列成线便于看出「钱里有多少是预存」。</summary>
            public decimal Recharge;
        }

        // ---- 提醒行（生日/沉睡） ----
        public class ReminderRow
        {
            public int MemberId;
            public string Name;
            public string Sub;        // 等级 / 最近到店
            public decimal Balance;
        }

        /// <summary>今日四指标。bizDate 为当前营业日 yyyy-MM-dd，cutHour 为日切点小时。</summary>
        public TodayMetrics GetTodayMetrics(string bizDate, int cutHour)
        {
            var m = new TodayMetrics();
            // 营收 = 全部 kind 实收（含充值）。orders.kind 恒非空，用 IS NOT NULL 表示「不限 kind」，
            // 这样 QueryDay 不必为「全部」单开一条 SQL 分支。
            const string allKinds = "IS NOT NULL";
            using (var conn = Db.OpenConnection())
            {
                QueryDay(conn, bizDate, allKinds, out m.Revenue, out m.RevenueCount);
                QueryDay(conn, bizDate, "='充值'", out m.Recharge, out m.RechargeCount);
                QueryDay(conn, bizDate, "='消费'", out m.Consume, out m.ConsumeCount);

                string yesterday = DateTime.Parse(bizDate).AddDays(-1).ToString("yyyy-MM-dd");
                int dummy;
                QueryDay(conn, yesterday, allKinds, out m.RevenueYesterday, out dummy);

                DateTime winStart = DateTime.Parse(bizDate).AddDays(-1).AddHours(cutHour);
                m.NewMembers = QueryMemberCount(conn, winStart, winStart.AddDays(1));

                DateTime monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                m.NewMembersMonth = QueryMemberCount(conn, monthStart, monthStart.AddMonths(1));
            }
            return m;
        }

        /// <summary>近 N 个营业日的营收/充值序列（不足补 0，保证日期连续）。
        /// 营收 = 当日全部实收（含充值，与「今日营收」卡片同口径）；充值单列一条线作为其中一部分。</summary>
        public List<TrendPoint> GetTrend(int days, string todayBizDate)
        {
            var map = new Dictionary<string, TrendPoint>();
            DateTime t = DateTime.Parse(todayBizDate);
            string start = t.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT biz_date," +
                    " COALESCE(SUM(receivable),0)," +
                    " COALESCE(SUM(CASE WHEN kind='充值' THEN receivable ELSE 0 END),0)" +
                    " FROM orders WHERE biz_date>=@s AND status='正常' GROUP BY biz_date";
                var p = cmd.CreateParameter(); p.ParameterName = "@s"; p.Value = start; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        map[r.GetString(0)] = new TrendPoint
                        {
                            BizDate = r.GetString(0),
                            Revenue = r.GetDecimal(1),
                            Recharge = r.GetDecimal(2)
                        };
                    }
                }
            }
            var list = new List<TrendPoint>();
            for (int i = days - 1; i >= 0; i--)
            {
                string d = t.AddDays(-i).ToString("yyyy-MM-dd");
                TrendPoint pt;
                if (!map.TryGetValue(d, out pt)) pt = new TrendPoint { BizDate = d };
                list.Add(pt);
            }
            return list;
        }

        /// <summary>今日生日会员（birthday 存 MM-dd）。</summary>
        public List<ReminderRow> GetBirthdayToday()
        {
            var list = new List<ReminderRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, name, level, balance FROM members" +
                    " WHERE status='正常' AND IFNULL(deleted_at,'')=''" +
                    " AND birthday IS NOT NULL AND birthday<>''" +
                    " AND substr(birthday,1,5)=strftime('%m-%d','now','localtime')" +
                    " ORDER BY name";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ReminderRow
                        {
                            MemberId = r.GetInt32(0),
                            Name = r.GetString(1),
                            Sub = r.IsDBNull(2) ? "" : r.GetString(2),
                            Balance = r.GetDecimal(3)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>沉睡会员：正常状态且 60 天无到店（无订单），按最近到店升序，最多 5 人。</summary>
        public List<ReminderRow> GetSleepingMembers(int days)
        {
            var list = new List<ReminderRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT m.id, m.name, m.balance, MAX(o.created_at) AS last_at" +
                    " FROM members m LEFT JOIN orders o ON o.member_id=m.id" +
                    " WHERE m.status='正常' AND IFNULL(m.deleted_at,'')=''" +
                    " GROUP BY m.id" +
                    " HAVING (last_at IS NULL AND m.created_at < datetime('now','localtime','-" + days + " days'))" +
                    "     OR (last_at IS NOT NULL AND last_at < datetime('now','localtime','-" + days + " days'))" +
                    " ORDER BY last_at ASC LIMIT 5";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string sub;
                        if (r.IsDBNull(3)) sub = UiText.DashNeverVisit;
                        else
                        {
                            // created_at 是 TEXT（'yyyy-MM-dd HH:mm:ss'），SQLite 驱动通常给回 string，
                            // 直接 (DateTime) 强转会抛 InvalidCastException —— 6 个月数据里出现了
                            // 「沉睡会员」才走到这条分支（店主 11-04 造演示数据时暴露）。
                            // 两种类型都接住：DateTime 直接格式化，其余取字符串前 10 位（就是日期）。
                            object v = r.GetValue(3);
                            string day = v is DateTime
                                ? ((DateTime)v).ToString("yyyy-MM-dd")
                                : (Convert.ToString(v) ?? "").Trim();
                            if (day.Length > 10) day = day.Substring(0, 10);
                            sub = day + UiText.DashSleepSuffix;
                        }
                        list.Add(new ReminderRow
                        {
                            MemberId = r.GetInt32(0),
                            Name = r.GetString(1),
                            Sub = sub,
                            Balance = r.GetDecimal(2)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>挂单笔数（快捷操作待办角标）。</summary>
        public int CountPendingOrders()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE status='挂单' AND IFNULL(voided_at,'')=''";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 今日退款笔数（首页/当班角标）。
        /// 店主 09-25 拍板：不引入「审批中」状态 —— 定稿 DDL 的 orders.status 只有
        /// 正常/挂单/已退款，退款单不提供直接修改，需要更正时由店长操作。
        /// 所以这里统计的是**已经发生的退款**，不再是原来那个恒 0 的"待审批"占位。
        /// </summary>
        public int CountPendingRefunds()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE status='已退款' AND biz_date=@d";
                var p = cmd.CreateParameter(); p.ParameterName = "@d"; p.Value = W.BizDate(); cmd.Parameters.Add(p);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>送祝福/唤醒 → 写任务记录到 op_logs（DDL 定稿无任务表，借日志表落痕，只增不删）。</summary>
        public void RecordReminderTask(string action, int memberId, string memberName)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result)" +
                                  " VALUES(@o,@m,@a,@d,'成功')";
                var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = Session.Name; cmd.Parameters.Add(po);
                var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = UiText.NavDashboard; cmd.Parameters.Add(pm);
                var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = action; cmd.Parameters.Add(pa);
                var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = "会员#" + memberId + " " + memberName; cmd.Parameters.Add(pd);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 按营业日汇总某种 kind 的实收与笔数。
        /// kindOp 拼在 `kind` 与后续条件之间（**调用方不要带前导空格**，空格由本方法补）：
        /// 传 "='充值'" / "='消费'" 限定单类；传 "IS NOT NULL" 表示不限 kind
        /// （orders.kind 恒非空）——营收口径用这个。
        /// </summary>
        private static void QueryDay(SQLiteConnection conn, string bizDate, string kindOp, out decimal sum, out int count)
        {
            sum = 0m; count = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(receivable),0), COUNT(*) FROM orders" +
                                  " WHERE biz_date=@d AND kind " + kindOp + " AND status='正常'";
                var p = cmd.CreateParameter(); p.ParameterName = "@d"; p.Value = bizDate; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        sum = r.GetDecimal(0);
                        count = r.GetInt32(1);
                    }
                }
            }
        }

        private static int QueryMemberCount(SQLiteConnection conn, DateTime from, DateTime to)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM members WHERE created_at>=@f AND created_at<@t";
                var pf = cmd.CreateParameter(); pf.ParameterName = "@f"; pf.Value = from.ToString("yyyy-MM-dd HH:mm:ss"); cmd.Parameters.Add(pf);
                var pt = cmd.CreateParameter(); pt.ParameterName = "@t"; pt.Value = to.ToString("yyyy-MM-dd HH:mm:ss"); cmd.Parameters.Add(pt);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
