using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>
    /// 前端桥接公共服务集（一）：项目列表（服务/商品管理）。
    /// 表：service_items；写操作一律 op_logs 留痕。
    /// </summary>
    public class ProjectService
    {
        public class ProjectRow
        {
            public int Id;
            public string Name;
            public string Category;
            public decimal Price;
            public string Unit;
            public bool IsCardItem;
            public bool Enabled;
            /// <summary>每做一次这个项目核销掉几次（0=不是次卡项/不消耗）。</summary>
            public int CardTimes;
            /// <summary>项目提成率 0~1（对应 0%~100%），消费结账时逐行按它计提；0 表示不提。</summary>
            public decimal CommissionRate;
            /// <summary>提成方式：'percent'=按价格百分比（默认）；'count'=按次卡核销次数算固定金额。</summary>
            public string CommissionMode;
            /// <summary>按次金额：仅 mode='count' 生效，每做一次（核销一次）提成的固定金额。</summary>
            public decimal CommissionPerCount;
        }

        public List<ProjectRow> ListItems(string category)
        {
            var list = new List<ProjectRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 软删除的项目（deleted_at 非空）不再列出 —— 历史单据靠 id 关联，不受影响
                cmd.CommandText = "SELECT id,name,category,price,unit,is_card_item,enabled,IFNULL(card_times,0),IFNULL(commission_rate,0.05),IFNULL(commission_mode,'percent'),IFNULL(commission_per_count,0) FROM service_items" +
                    " WHERE deleted_at IS NULL" +
                    (string.IsNullOrEmpty(category) || category == "全部" ? "" : " AND category=@c") +
                    " ORDER BY id";
                if (!string.IsNullOrEmpty(category) && category != "全部")
                {
                    var p = cmd.CreateParameter(); p.ParameterName = "@c"; p.Value = category; cmd.Parameters.Add(p);
                }
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var row = new ProjectRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            Category = r.GetString(2),
                            Price = Convert.ToDecimal(r.GetValue(3)),
                            Unit = r.GetString(4),
                            IsCardItem = Convert.ToInt32(r.GetValue(5)) == 1,
                            Enabled = Convert.ToInt32(r.GetValue(6)) == 1,
                            CardTimes = r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7)),
                            CommissionRate = r.IsDBNull(8) ? 0.05m : Convert.ToDecimal(r.GetValue(8)),
                            CommissionMode = r.IsDBNull(9) ? "percent" : Convert.ToString(r.GetValue(9)),
                            CommissionPerCount = r.IsDBNull(10) ? 0m : Convert.ToDecimal(r.GetValue(10))
                        };
                        list.Add(row);
                    }
                }
            }
            return list;
        }

        public void SaveItem(int id, string name, string category, decimal price, string unit, bool isCardItem, bool enabled, int cardTimes, decimal commissionRate, string commissionMode, decimal commissionPerCount)
        {
            string n = (name ?? "").Trim();
            string c = (category ?? "").Trim();
            if (n.Length == 0) throw new BizException("项目名称不能为空");
            if (n.Length > 50) throw new BizException("项目名称过长（最多 50 字）");
            // 分类可留空（店主 09-26：新增项目默认就是空、空就是「无」）；空归入「未分类」保持分类页签可用
            if (c.Length == 0) c = "未分类";
            if (price < 0) throw new BizException("价格不能为负数");
            if (price > 999999) throw new BizException("价格过大（最高 999999 元）");
            string u = (unit ?? "").Trim();
            if (u.Length == 0) u = "次";
            // A4：每做一次核销掉几次。非次卡项一律 0；次卡项没填/填 0 时兜底为 1 次。
            int ct = isCardItem ? (cardTimes > 0 ? cardTimes : 1) : 0;
            if (ct > 999) throw new BizException("单次核销次数过大（最高 999）");
            // 提成率 0~1（对应 0%~100%）；0 表示不提
            if (commissionRate < 0 || commissionRate > 1) throw new BizException("提成比例须在 0 - 100% 之间");
            // 提成方式：只接受 'percent' / 'count'，其余兜底 'percent'
            string cm = (commissionMode == "count") ? "count" : "percent";
            // 按次金额（仅 count 模式有意义）：0~正数；负数当 0
            decimal pc = commissionPerCount < 0m ? 0m : commissionPerCount;
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 重名校验：新建时全表查重，修改时排除自身；已软删除的项目不占名字
                    using (var dup = conn.CreateCommand())
                    {
                        dup.Transaction = txn;
                        dup.CommandText = "SELECT COUNT(*) FROM service_items WHERE name=@n AND id<>@id AND deleted_at IS NULL";
                        W.P(dup, "@n", n); W.P(dup, "@id", id);
                        if (Convert.ToInt32(dup.ExecuteScalar()) > 0)
                            throw new BizException("已有同名项目「" + n + "」，请换一个名称");
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        if (id <= 0)
                        {
                            cmd.CommandText = "INSERT INTO service_items(name,category,price,unit,is_card_item,enabled,commission_rate,card_times,commission_mode,commission_per_count) VALUES(@n,@c,@p,@u,@ic,@e,@cr,@ct,@cm,@pc)";
                            W.P(cmd, "@n", n); W.P(cmd, "@c", c); W.P(cmd, "@p", price);
                            W.P(cmd, "@u", u); W.P(cmd, "@ic", isCardItem ? 1 : 0); W.P(cmd, "@e", enabled ? 1 : 0);
                            W.P(cmd, "@cr", commissionRate);
                            W.P(cmd, "@ct", ct);
                            W.P(cmd, "@cm", cm); W.P(cmd, "@pc", pc);
                        }
                        else
                        {
                            cmd.CommandText = "UPDATE service_items SET name=@n,category=@c,price=@p,unit=@u,is_card_item=@ic,enabled=@e,commission_rate=@cr,card_times=@ct,commission_mode=@cm,commission_per_count=@pc WHERE id=@id";
                            W.P(cmd, "@n", n); W.P(cmd, "@c", c); W.P(cmd, "@p", price);
                            W.P(cmd, "@u", u); W.P(cmd, "@ic", isCardItem ? 1 : 0); W.P(cmd, "@e", enabled ? 1 : 0);
                            W.P(cmd, "@cr", commissionRate);
                            W.P(cmd, "@ct", ct);
                            W.P(cmd, "@cm", cm); W.P(cmd, "@pc", pc);
                            W.P(cmd, "@id", id);
                        }
                        if (cmd.ExecuteNonQuery() == 0) throw new BizException("保存失败：项目不存在或已被删除");
                    }
                    W.Log(conn, txn, "项目列表", id <= 0 ? "新增项目" : "修改项目", n + "（" + c + " ¥" + price.ToString("0.##") + " · 提成" + cm + "）");
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

        public void SetEnabled(int id, bool enabled)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string name;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE service_items SET enabled=@e WHERE id=@id; SELECT name FROM service_items WHERE id=@id";
                        W.P(cmd, "@e", enabled ? 1 : 0); W.P(cmd, "@id", id);
                        object v = cmd.ExecuteScalar();
                        if (v == null) throw new BizException("项目不存在");
                        name = v.ToString();
                    }
                    W.Log(conn, txn, "项目列表", enabled ? "启用项目" : "停用项目", name);
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
        /// 删除项目（软删除）。店主 09-26 口径：删除后只是「不再售卖」，
        /// 该项目名下的历史订单 / 流水 / 提成「不同步删除」——账单仍按原项目名留痕，各项目之间互相独立。
        /// 实现：写 service_items.deleted_at 并置 enabled=0，行保留（历史单据靠 item_id 关联）。
        /// 同时把该项目名下的次卡套餐一并停售：已售出的卡仍被 member_cards.template_id 引用、照常核销。
        /// </summary>
        public void DeleteItem(int id)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string name;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name FROM service_items WHERE id=@id AND deleted_at IS NULL";
                        W.P(cmd, "@id", id);
                        object v = cmd.ExecuteScalar();
                        if (v == null || v == DBNull.Value) throw new BizException("项目不存在或已被删除");
                        name = v.ToString();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE service_items SET enabled=0,deleted_at=datetime('now','localtime') WHERE id=@id";
                        W.P(cmd, "@id", id);
                        cmd.ExecuteNonQuery();
                    }
                    // 次卡套餐停售（不删）：member_cards.template_id 还引用着，已售出的卡要能继续核销
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE card_templates SET enabled=0 WHERE item_name=@n AND enabled=1";
                        W.P(cmd, "@n", name);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "项目列表", "删除项目", name + "（已下架，历史单据与流水保留）");
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

    /// <summary>桥接服务公用小工具（日志/取参）。</summary>
    internal static class W
    {
        public static void P(SQLiteCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        /// <summary>
        /// 统一的导出目录解析（店主 10-08 定：4 个导出落点不一致——「员工业绩」弹选择、其余固定 data\exports —— 收敛到一个真源）。
        /// 优先级：
        ///   ① 本次临时指定 saveDir（非空）→ 用它（一次性，不写回全局；前端「换个位置」走这里）；
        ///   ② 全局默认 settings.export_dir（店主在「系统设置」选的默认导出文件夹，选一次全店通用）；
        ///   ③ 兜底 <c>安装目录\exports</c>（店主 11-05：所有文件尽量都在安装的目录里面；便携=exe 旁，非便携=%LocalAppData%）。
        ///     早先兜底在 data\exports，跟库文件混在一层，店主翻不到；现在提到安装目录下的 exports\。
        /// 返回的目录若不存在会自动创建，保证后续 x.Save(path) 不炸。
        /// 4 个导出方法（ExportExcel / ExportCommissionExcel / ExportRecords / ExportStaffDetailExcel）全部改调它。
        /// </summary>
        public static string ResolveExportDir(string saveDir)
        {
            string dir = null;
            // ① 本次临时指定
            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                string full = SafeFullPath(saveDir);
                if (full != null && (System.IO.Directory.Exists(full) || CanCreate(full))) dir = full;
            }
            // ② 全局默认
            if (dir == null)
            {
                string g = DatabaseInitializer.GetSetting("export_dir", null);
                if (!string.IsNullOrWhiteSpace(g))
                {
                    string full = SafeFullPath(g);
                    if (full != null && (System.IO.Directory.Exists(full) || CanCreate(full))) dir = full;
                }
            }
            // ③ 兜底 安装目录\exports
            if (dir == null)
                dir = AppPaths.ExportsDir;
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>当前生效的默认导出目录（= 全局 export_dir，未设则兜底 安装目录\exports）。给设置页展示用、给两个文件夹对话框当起始位置。</summary>
        public static string DefaultExportDir()
        {
            string g = DatabaseInitializer.GetSetting("export_dir", null);
            if (!string.IsNullOrWhiteSpace(g))
            {
                string full = SafeFullPath(g);
                if (full != null) return full;
            }
            return AppPaths.ExportsDir;
        }

        private static string SafeFullPath(string p)
        {
            try { return System.IO.Path.GetFullPath(p.Trim()); } catch { return null; }
        }

        private static bool CanCreate(string dir)
        {
            // 目标目录本身可能还不存在，但父目录存在 → 可以创建它（文件夹选择框选中的必已存在，这条主要给全局设置兜底）
            try
            {
                string parent = System.IO.Path.GetDirectoryName(dir);
                return !string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent);
            }
            catch { return false; }
        }

        /// <summary>写操作日志。operatorOverride 用于「实际经手人」与登录账号不一致的场景（如共用账号时点名的售卡人）。</summary>
        public static void Log(SQLiteConnection conn, SQLiteTransaction txn, string module, string action, string detail, string operatorOverride = null)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                P(cmd, "@o", string.IsNullOrEmpty(operatorOverride) ? (Session.IsLoggedIn ? Session.Name : "系统") : operatorOverride);
                P(cmd, "@m", module);
                P(cmd, "@a", action);
                P(cmd, "@d", detail);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>单号生成（与 PosService.NextOrderNo 同规则：前缀+yyyymmdd-序号）。</summary>
        public static string NextNo(SQLiteConnection conn, SQLiteTransaction txn, string prefix, string bizDate)
        {
            string compact = bizDate.Replace("-", "");
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT MAX(CAST(substr(order_no,11) AS INTEGER)) FROM orders WHERE order_no LIKE @p";
                P(cmd, "@p", prefix + compact + "-%");
                object v = cmd.ExecuteScalar();
                int next = (v == null || v == DBNull.Value) ? 1 : Convert.ToInt32(v) + 1;
                return prefix + compact + "-" + next.ToString("D2");
            }
        }

        /// <summary>进程级日切点缓存（小时，默认 2 与旧硬编码行为一致）。
        /// 10-13 H3：BizDate() 必须在写事务内可调用（InsertFlow/NextNo 都在事务里走它），
        /// 而 WAL 自死锁铁律禁止事务内独立连接读 settings（GetSetting 会另开连接死锁）。
        /// 故改「缓存 + 安全点刷新」：RefreshDayCut() 只在安全点调用
        /// （SystemInfoService.GetStatus——独立连接、当前无写事务）；事务内一律读缓存，绝不碰 settings。
        /// 未刷新时默认 2（与旧行为完全一致，冒烟/桥接直调 BLL 不回归）；
        /// 前端改日切点保存后 initDayCut→system.status 会同步刷新本缓存，次一安全点即生效。</summary>
        private static int _dayCutHour = 2;

        /// <summary>安全点刷新日切点缓存（当前无写事务才能调）。</summary>
        public static void RefreshDayCut()
        {
            _dayCutHour = Fmt.ParseCutHour(DatabaseInitializer.GetSetting("day_cut", "02:00"));
        }

        /// <summary>当前营业日（日切点读 day_cut 设置的进程级缓存；未刷新前默认 2 点，与旧行为一致）。</summary>
        public static string BizDate()
        {
            return Fmt.BizDateOf(DateTime.Now, _dayCutHour);
        }

        public static decimal GetBalance(SQLiteConnection conn, SQLiteTransaction txn, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT balance FROM members WHERE id=@id";
                P(cmd, "@id", memberId);
                object v = cmd.ExecuteScalar();
                if (v == null) throw new BizException("会员不存在");
                return Convert.ToDecimal(v);
            }
        }

        public static string GetBalanceStatus(SQLiteConnection conn, SQLiteTransaction txn, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT status FROM members WHERE id=@id";
                P(cmd, "@id", memberId);
                object v = cmd.ExecuteScalar();
                if (v == null) throw new BizException("会员不存在");
                return v.ToString();
            }
        }

        /// <summary>S2（店主 10-15）：会员是否已注销（软删除）。任何资金动作前都要问一句。</summary>
        public static bool IsMemberDeleted(SQLiteConnection conn, SQLiteTransaction txn, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(deleted_at,'') FROM members WHERE id=@id";
                P(cmd, "@id", memberId);
                object v = cmd.ExecuteScalar();
                if (v == null) throw new BizException("会员不存在");
                return v.ToString().Length > 0;
            }
        }

        public static void InsertFlow(SQLiteConnection conn, SQLiteTransaction txn, int memberId, string type,
            decimal amount, decimal balanceAfter, int? orderId, string operatorName, string note)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,order_id,operator,note)" +
                    " VALUES(@m,@d,@t,@a,@b,@o,@op,@n)";
                P(cmd, "@m", memberId);
                P(cmd, "@d", BizDate());
                P(cmd, "@t", type);
                P(cmd, "@a", amount);
                P(cmd, "@b", balanceAfter);
                P(cmd, "@o", orderId.HasValue ? (object)orderId.Value : DBNull.Value);
                P(cmd, "@op", operatorName);
                P(cmd, "@n", note);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
