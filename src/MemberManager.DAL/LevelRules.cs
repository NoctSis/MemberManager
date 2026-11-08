using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace MemberManager.DAL
{
    /// <summary>
    /// 会员等级制度（店主 2026-09-26 定）：等级不再手工选择，完全由「累计实收充值」推导。
    ///   累计实收（净）= type='充值' 之和 − 已退款充值单 receivable（店主 2026-10-14 定「退款要减」；赠送仍不算）；
    ///   判定     = 启用等级中「门槛 ≤ 累计实收」的几档里取门槛最高的一档；
    ///   兜底     = 所有门槛都没够到时，落到门槛最低的一档（入门级）；
    ///   制度一变（改名/改门槛/启停/删除）就重算全体会员，改掉的等级不会再挂在任何会员身上。
    /// 所有方法都复用调用方传入的 conn/tx，绝不自行开第二条连接 —— WAL 下会 database is locked。
    /// </summary>
    public static class LevelRules
    {
        public class LevelDef
        {
            public int Id;
            public string Name;
            public decimal MinRecharge;
            public bool Enabled;
        }

        /// <summary>单个会员的重算结果。Changed=false 表示等级没变、未写库。</summary>
        public class Change
        {
            public int MemberId;
            public string MemberName;
            public string Old;
            public string New;
            public bool Changed
            {
                get { return !string.Equals(Old, New, StringComparison.Ordinal); }
            }
        }

        /// <summary>全体重算结果。Changed = 等级真正发生变化的会员数。</summary>
        public class Result
        {
            public int Changed;
            public List<Change> Items = new List<Change>();
        }

        /// <summary>
        /// 累计实收充值（净）= 本金充值之和 − 已退款本金之和（店主 2026-10-14 定「退款要减」）。
        ///   · 只算 type='充值'（赠送不计入门槛，店主 2026-09-26 定）；
        ///   · 已退款的充值单（kind='充值' AND status='已退款'）按其 receivable（=本金）从累计里扣回
        ///     → 等级随净累计重算、可降（推翻 10-13 之前的「只升不降」临时定调）；
        ///   · 红冲流水（type='红冲'，金额含本金+赠送）不在此扣——它管余额退回；
        ///     累计口径统一走订单状态，DDL 冻结零改动。定位不到原订单的历史退款无法净减（无单可翻）。
        /// </summary>
        public static decimal CumulativeRecharge(SQLiteConnection conn, SQLiteTransaction tx, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT" +
                    " (SELECT COALESCE(SUM(amount),0) FROM member_flows WHERE member_id=@id AND type='充值')" +
                    " - (SELECT COALESCE(SUM(receivable),0) FROM orders WHERE member_id=@id AND kind='充值' AND status='已退款')";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                return Convert.ToDecimal(cmd.ExecuteScalar());
            }
        }

        /// <summary>载入全部等级定义（含停用的，兜底要用）。按门槛升序、同门槛按 id 升序。</summary>
        public static List<LevelDef> Load(SQLiteConnection conn, SQLiteTransaction tx)
        {
            var list = new List<LevelDef>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id,name,min_recharge,enabled FROM member_levels ORDER BY min_recharge, id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new LevelDef
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            MinRecharge = r.GetDecimal(2),
                            Enabled = Convert.ToInt32(r.GetValue(3)) != 0
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 按累计实收解析应达等级。列表已按门槛升序：
        /// 兜底取第一个**启用**的档——最低档被停用时不能拿它当兜底，
        /// 否则会员等级会显示成「已停用」档名（M7，店主 10-14）；
        /// 最后一次命中「达标」的就是门槛最高（目标）。
        /// **一档都没启用（或制度为空）时返回 null**（店主 10-15：「没有档了那就无」）——
        /// 调用方一律把会员等级落空（界面显示「无」），绝不塞幻影档名。
        /// </summary>
        public static LevelDef Resolve(List<LevelDef> all, decimal cumulative)
        {
            LevelDef floor = null, best = null;
            foreach (var d in all)
            {
                if (!d.Enabled) continue;
                if (floor == null) floor = d;
                if (cumulative >= d.MinRecharge) best = d;
            }
            return best ?? floor;
        }

        /// <summary>单人重算：值变了才 UPDATE。全停用/制度为空时把等级落空（显示「无」）；会员不存在返回 null。</summary>
        public static Change Apply(SQLiteConnection conn, SQLiteTransaction tx, int memberId)
        {
            List<LevelDef> all = Load(conn, tx);
            return all.Count == 0 ? null : ApplyOne(conn, tx, memberId, all);
        }

        /// <summary>制度变更后重算全体会员。制度为空时不动数据。</summary>
        public static Result ApplyAll(SQLiteConnection conn, SQLiteTransaction tx)
        {
            var res = new Result();
            List<LevelDef> all = Load(conn, tx);
            if (all.Count == 0) return res;

            // 先把会员快照读进内存再改：SQLite 单连接下，不能在 reader 打开时另发 UPDATE。
            var ids = new List<int>();
            var names = new Dictionary<int, string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id, IFNULL(name,'') FROM members ORDER BY id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int id = r.GetInt32(0);
                        ids.Add(id);
                        names.Add(id, r.GetString(1));
                    }
                }
            }

            foreach (int id in ids)
            {
                Change ch = ApplyOne(conn, tx, id, all);
                if (ch == null) continue;
                ch.MemberName = names[id];
                res.Items.Add(ch);
                if (ch.Changed) res.Changed++;
            }
            return res;
        }

        private static Change ApplyOne(SQLiteConnection conn, SQLiteTransaction tx, int memberId, List<LevelDef> all)
        {
            string old;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT IFNULL(level,'') FROM members WHERE id=@id";
                var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = memberId; cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return null;   // 会员不存在
                old = Convert.ToString(v);
            }

            // M7（店主 10-14/10-15）：制度里只剩停用档 → Resolve 返回 null（停用档不当兜底）→
            // 与「制度为空」同口径，会员等级落空（界面显示「无」）。
            // 等级是累计充值算出来的派生值、不是手工选的，重新启用档位后会自动算回，故落空无损；
            // 反倒是留着已删除/已停用的旧档名会挂出幻影等级，所以这里必须写空。
            LevelDef targetDef = Resolve(all, CumulativeRecharge(conn, tx, memberId));
            string target = targetDef == null ? "" : targetDef.Name;
            var ch = new Change { MemberId = memberId, Old = old, New = target };
            if (!ch.Changed) return ch;

            using (var u = conn.CreateCommand())
            {
                u.Transaction = tx;
                u.CommandText = "UPDATE members SET level=@l WHERE id=@id";
                var pl = u.CreateParameter(); pl.ParameterName = "@l"; pl.Value = target; u.Parameters.Add(pl);
                var pi = u.CreateParameter(); pi.ParameterName = "@id"; pi.Value = memberId; u.Parameters.Add(pi);
                u.ExecuteNonQuery();
            }
            return ch;
        }
    }
}
