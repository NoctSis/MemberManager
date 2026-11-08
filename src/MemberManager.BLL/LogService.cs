using System;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 操作日志保留期清理（店主 11-05 定：到了 180 天——或设置里填的那个数——就开始删最老的记录）。
    ///
    /// 背景：设置页一直有「操作日志保留天数」，前端还认真校验「须为不小于 30 的整数」，
    /// 但后端从来没有任何代码读过它 —— 日志只增不减，一年能堆 6~9 MB，备份跟着一起变胖。
    ///
    /// **边界（必须守住）**：
    ///   · 只删 op_logs 一张表。业务流水 member_flows / orders / payments / commissions 一条都不动，
    ///     「流水只增不删、更正走红冲」的铁律照旧 —— 那是钱，日志只是操作痕迹。
    ///   · op_logs 上有 logs_no_delete 触发器（BEFORE DELETE RAISE ABORT），所以必须同一事务里
    ///     先 DROP TRIGGER、删完再 CREATE 回来。SQLite 的 DDL 是事务性的，中途任何一步失败整体回滚，
    ///     触发器会自动恢复，绝不会留下「日志随便删」的空窗。
    ///   · 清理动作本身写一条日志留痕（留多少天、删了多少条、最早/最晚时间），
    ///     这条是删完才写的，不会被自己删掉。
    /// </summary>
    public class LogService
    {
        /// <summary>与设置页前端校验一致的下限：小于 30 天一律按 30 天算，防止误填 1 天把日志清光。</summary>
        public const int MinKeepDays = 30;

        public class LogStats
        {
            public int Total;          // 当前日志条数
            public string Oldest;      // 最早一条的时间（空=没有日志）
            public string Newest;      // 最近一条的时间
            public int KeepDays;       // 当前生效的保留天数
            public int Overdue;        // 按保留期算，应被清掉的条数
            public string LastPurge;   // 上次清理时间（settings.log_purged_at，空=从未）
        }

        public class PurgeResult
        {
            public int Removed;        // 本次删掉条数
            public int KeepDays;       // 实际执行的保留天数
            public int Remain;         // 删完还剩条数
            public string Cutoff;      // 分界线（早于这个时间的被删）
        }

        /// <summary>读取当前生效的保留天数（settings.log_keep_days，默认 180，低于 30 按 30）。</summary>
        public static int KeepDays()
        {
            int d;
            if (!int.TryParse(DatabaseInitializer.GetSetting("log_keep_days", "180"), out d)) d = 180;
            if (d < MinKeepDays) d = MinKeepDays;
            return d;
        }

        /// <summary>设置页展示用：有多少条、最早一条是什么时候、按保留期该清多少。</summary>
        public LogStats Stats()
        {
            var s = new LogStats { KeepDays = KeepDays() };
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*),IFNULL(MIN(created_at),''),IFNULL(MAX(created_at),'') FROM op_logs";
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            s.Total = r.GetInt32(0);
                            s.Oldest = r.GetString(1);
                            s.Newest = r.GetString(2);
                        }
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM op_logs WHERE created_at < @c";
                    W.P(cmd, "@c", Cutoff(s.KeepDays));
                    s.Overdue = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            s.LastPurge = DatabaseInitializer.GetSetting("log_purged_at", "");
            return s;
        }

        /// <summary>
        /// 按保留天数清掉更老的日志。keepDays ≤ 0 时读设置里的值。
        /// 返回删了多少条（0 = 本来就没有过期记录，属正常结果，不报错）。
        /// </summary>
        public PurgeResult Purge(int keepDays, string operatorName)
        {
            int keep = keepDays > 0 ? keepDays : KeepDays();
            if (keep < MinKeepDays) keep = MinKeepDays;
            string cutoff = Cutoff(keep);
            var result = new PurgeResult { KeepDays = keep, Cutoff = cutoff };

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 1) 暂时摘掉「日志禁止删除」触发器（同一事务，失败回滚会自动恢复）
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "DROP TRIGGER IF EXISTS logs_no_delete";
                        cmd.ExecuteNonQuery();
                    }
                    // 2) 只删过期的那部分
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "DELETE FROM op_logs WHERE created_at < @c";
                        W.P(cmd, "@c", cutoff);
                        result.Removed = cmd.ExecuteNonQuery();
                    }
                    // 3) 触发器立刻装回去（哪怕一条都没删，也要保证它还在）
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText =
                            "CREATE TRIGGER IF NOT EXISTS logs_no_delete BEFORE DELETE ON op_logs " +
                            "BEGIN SELECT RAISE(ABORT,'" + TrgMsg() + "'); END";
                        cmd.ExecuteNonQuery();
                    }
                    // 4) 留痕 + 记下本次清理时间（事务内用挂 conn/txn 的重载，避免 WAL 自死锁）
                    if (result.Removed > 0)
                        W.Log(conn, txn, UiText.NavSettings, "日志清理",
                            "按保留 " + keep + " 天清理 " + result.Removed + " 条操作日志（早于 " + cutoff + "）", operatorName);
                    DatabaseInitializer.SetSetting(conn, txn, "log_purged_at",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT COUNT(*) FROM op_logs";
                        result.Remain = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
            if (result.Removed > 0)
                Logger.Info("操作日志清理：保留 " + keep + " 天，删除 " + result.Removed + " 条，剩余 " + result.Remain + " 条");
            return result;
        }

        /// <summary>
        /// 启动时调用：每天最多清一次（settings.log_purged_day 记今天日期）。
        /// 任何异常都只记日志、不打断启动 —— 日志清理失败不该拦着店主收银。
        /// </summary>
        public static int AutoTick()
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (DatabaseInitializer.GetSetting("log_purged_day", "") == today) return 0;
                var r = new LogService().Purge(0, null);
                DatabaseInitializer.SetSetting("log_purged_day", today);
                return r.Removed;
            }
            catch (Exception ex)
            {
                Logger.Warn("启动时清理操作日志失败（不影响使用）：" + ex.Message);
                return 0;
            }
        }

        /// <summary>分界线：早于「今天 - N 天」的记录算过期。created_at 是 'yyyy-MM-dd HH:mm:ss' 本地时间，字符串比较即可。</summary>
        private static string Cutoff(int keepDays)
        {
            return DateTime.Now.Date.AddDays(-keepDays).ToString("yyyy-MM-dd") + " 00:00:00";
        }

        /// <summary>触发器里的中文提示语。集中一处，重建触发器时不至于把文案写歪。</summary>
        private static string TrgMsg()
        {
            return UiText.LogNoDelete;
        }
    }
}
