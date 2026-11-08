using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 会员等级制度管理（店主 2026-09-26 定）：等级不手工选择，按「累计实收充值」自动判定。
    /// 这里只管制度本身——新增 / 改名 / 改门槛 / 启停 / 删除。
    /// 每次制度变更都在同一事务里重算全体会员，保证：
    ///   · 改名后会员身上立刻换成新名字；
    ///   · 改门槛后按新门槛重算；
    ///   · 删除后该等级不再挂在任何人身上（至少留一个等级，否则所有会员无处落脚）。
    /// 变更与重算人数全部写 op_logs，日志里能看到「改了什么、动了几个人」。
    /// </summary>
    public class LevelService
    {
        public class LevelRow
        {
            public int Id;
            public string Name;
            public decimal MinRecharge;
            public bool Enabled;
            public int MemberCount;   // 当前挂在这个等级名下的会员数
        }

        /// <summary>制度列表（含停用），按门槛升序；带各级现有会员数。</summary>
        public List<LevelRow> List()
        {
            var list = new List<LevelRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT l.id, l.name, l.min_recharge, l.enabled," +
                    " (SELECT COUNT(*) FROM members m WHERE m.level=l.name AND IFNULL(m.deleted_at,'')='') AS cnt" +
                    " FROM member_levels l ORDER BY l.min_recharge, l.id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new LevelRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            MinRecharge = r.GetDecimal(2),
                            Enabled = Convert.ToInt32(r.GetValue(3)) != 0,
                            MemberCount = Convert.ToInt32(r.GetValue(4))
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>新增 / 改名 / 改门槛 / 启停。id=0 表示新增。返回等级实际发生变化的会员数。</summary>
        public int Save(int id, string name, decimal minRecharge, bool enabled)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new BizException("等级名称不能为空");
            if (name.Length > 20) throw new BizException("等级名称过长（最多 20 字）");
            if (minRecharge < 0m) throw new BizException("累计充值门槛不能为负数");
            if (minRecharge > 99999999m) throw new BizException("累计充值门槛金额过大");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    bool exists = false;
                    string oldName = ""; decimal oldMin = 0m; bool oldEnabled = true;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,min_recharge,enabled FROM member_levels WHERE id=@id";
                        W.P(cmd, "@id", id);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                exists = true;
                                oldName = r.GetString(0);
                                oldMin = r.GetDecimal(1);
                                oldEnabled = Convert.ToInt32(r.GetValue(2)) != 0;
                            }
                        }
                    }

                    // 名称与门槛各自唯一：门槛撞车就没法判定该落哪一档
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT COUNT(*) FROM member_levels WHERE name=@n AND id<>@id";
                        W.P(cmd, "@n", name); W.P(cmd, "@id", id);
                        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) throw new BizException("已有同名等级「" + name + "」，请换一个名字");
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT COUNT(*) FROM member_levels WHERE min_recharge=@m AND id<>@id";
                        W.P(cmd, "@m", (double)minRecharge); W.P(cmd, "@id", id);
                        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
                            throw new BizException("已有等级使用同样的门槛 " + Money(minRecharge) + " 元，门槛必须互不相同");
                    }

                    if (exists)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE member_levels SET name=@n,min_recharge=@m,enabled=@e WHERE id=@id";
                            W.P(cmd, "@n", name); W.P(cmd, "@m", (double)minRecharge);
                            W.P(cmd, "@e", enabled ? 1 : 0); W.P(cmd, "@id", id);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "INSERT INTO member_levels(name,min_recharge,enabled) VALUES(@n,@m,@e)";
                            W.P(cmd, "@n", name); W.P(cmd, "@m", (double)minRecharge);
                            W.P(cmd, "@e", enabled ? 1 : 0);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    LevelRules.Result res = LevelRules.ApplyAll(conn, txn);
                    W.Log(conn, txn, UiText.NavSettings, exists ? "等级制度调整" : "新增等级",
                        BuildDetail(exists, oldName, name, oldMin, minRecharge, oldEnabled, enabled, res.Changed));
                    Logger.Info("等级制度" + (exists ? "调整" : "新增") + "：" + name +
                        " 门槛=" + minRecharge + " 启用=" + enabled + "，同步会员=" + res.Changed);
                    txn.Commit();
                    return res.Changed;
                }
                catch (Exception ex)
                {
                    // 回滚失败不能让原始异常被替换成一句看不懂的「值不能为 null」——原始原因必须原样抛出
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("回滚失败（原异常继续抛出）：" + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>删除等级（至少保留一个）。返回等级实际发生变化的会员数。</summary>
        public int Delete(int id)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string oldName = "";
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT COUNT(*) FROM member_levels";
                        if (Convert.ToInt32(cmd.ExecuteScalar()) <= 1)
                            throw new BizException("至少要保留一个等级（删空后所有会员没有等级可落）");
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name FROM member_levels WHERE id=@id";
                        W.P(cmd, "@id", id);
                        object v = cmd.ExecuteScalar();
                        if (v == null || v == DBNull.Value) throw new BizException("等级不存在，请刷新后重试");
                        oldName = Convert.ToString(v);
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "DELETE FROM member_levels WHERE id=@id";
                        W.P(cmd, "@id", id);
                        cmd.ExecuteNonQuery();
                    }

                    LevelRules.Result res = LevelRules.ApplyAll(conn, txn);
                    W.Log(conn, txn, UiText.NavSettings, "删除等级",
                        "删除等级「" + oldName + "」，已按剩余制度同步 " + res.Changed + " 位会员");
                    Logger.Info("删除等级：" + oldName + "，同步会员=" + res.Changed);
                    txn.Commit();
                    return res.Changed;
                }
                catch (Exception ex)
                {
                    // 回滚失败不能让原始异常被替换成一句看不懂的「值不能为 null」——原始原因必须原样抛出
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("回滚失败（原异常继续抛出）：" + rb.Message); }
                    throw;
                }
            }
        }

        private static string BuildDetail(bool exists, string oldName, string name,
            decimal oldMin, decimal minRecharge, bool oldEnabled, bool enabled, int changed)
        {
            var parts = new List<string>();
            if (!exists) parts.Add("新增等级「" + name + "」");
            else if (!string.Equals(oldName, name, StringComparison.Ordinal))
                parts.Add("等级「" + oldName + "」改名为「" + name + "」");
            if (!exists || oldMin != minRecharge)
                parts.Add("累计充值门槛" + (exists ? " " + Money(oldMin) : "") + "元 → " + Money(minRecharge) + "元");
            if (exists && oldEnabled != enabled)
                parts.Add(enabled ? "已启用" : "已停用");
            return string.Join("，", parts) + "；已按新制度同步 " + changed + " 位会员";
        }

        private static string Money(decimal v)
        {
            return v.ToString("0.##");
        }
    }
}
