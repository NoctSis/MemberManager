using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MemberManager.Common;
using MemberManager.DAL;

namespace MemberManager.BLL
{
    /// <summary>桥接服务集（四）：数据备份、系统设置、积分营销、报表聚合。</summary>
    public class BackupService
    {
        public class BackupRow
        {
            public int Id;
            public string Time;
            public string Type;   // 自动 / 手动 / 恢复前快照
            public string Size;
            public string Note;
            public string FileName;
        }

        /// <summary>
        /// 备份目录走 AppPaths：绿色便携模式下落在 exe 旁的 backups\，
        /// 整包拷走时备份跟着一起走；非便携时落在当前用户的 %LocalAppData%。
        /// （早期这里硬编码了 CommonApplicationData，便携模式下备份会跑到系统公共目录。）
        /// </summary>
        public static string BackupDir
        {
            get { return AppPaths.BackupDir; }
        }

        public List<BackupRow> List()
        {
            var list = new List<BackupRow>();
            Directory.CreateDirectory(BackupDir);
            var files = new List<string>(Directory.GetFiles(BackupDir, "member-*.db"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            files.Reverse();
            int id = 1;
            foreach (string f in files)
            {
                var fi = new FileInfo(f);
                string name = Path.GetFileNameWithoutExtension(f);
                string type = "自动";
                if (name.Contains("-M")) type = "手动";
                if (name.Contains("-S")) type = "恢复前快照";
                string timePart = name.Replace("member-", "").Replace("-M", "").Replace("-S", "");
                // 文件名时间戳是 yyyyMMdd-HHmm（13 位，见 BackupNow 的 stamp 拼接）。
                // 早先这里判的是 15 位（历史格式 yyyyMMdd-HHmmss），于是 13 位的名字**永远不被格式化**，
                // 列表「时间」列直接显示成 20261105-1948。两种长度前 8 位日期、后 4 位时分位置一致，统一按 ≥13 处理。
                if (timePart.Length >= 13)
                    timePart = timePart.Substring(0, 4) + "-" + timePart.Substring(4, 2) + "-" + timePart.Substring(6, 2) + " " +
                        timePart.Substring(9, 2) + ":" + timePart.Substring(11, 2);
                list.Add(new BackupRow
                {
                    Id = id++,
                    Time = timePart,
                    Type = type,
                    Size = (fi.Length / 1024.0 / 1024.0).ToString("0.0") + " MB",
                    Note = type == "自动" ? "每日自动备份" : (type == "手动" ? "店长手动备份" : "恢复前自动快照"),
                    FileName = Path.GetFileName(f)
                });
                if (list.Count >= 30) break;
            }
            return list;
        }

        /// <summary>
        /// 数据库体检（店主 10-15）：坏库不备份、坏备份不许恢复。
        /// 两步：① `PRAGMA quick_check` 看结构是否完整；② 关键表 `members` 必须在
        /// （防住「文件能打开但其实是个空库/别的库」）。返回错误原因；通过返回 null。
        /// </summary>
        public static string HealthCheck(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return "文件不存在";
            try
            {
                // 用普通方式打开（不用 Read Only）：WAL 库体检时需要能创建 -shm，只读会直接失败
                using (var conn = new SQLiteConnection("Data Source=" + dbPath))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA quick_check;";
                        object v = cmd.ExecuteScalar();
                        string s = v == null ? "" : Convert.ToString(v);
                        if (!string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)) return "结构体检未通过：" + s;
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='members'";
                        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return "文件里没有会员表，不是本系统的数据库";
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "打不开：" + ex.Message;
            }
        }

        /// <summary>体检当前库（给界面/冒烟用）。通过返回 null，否则返回中文原因。</summary>
        public string CheckLive()
        {
            return HealthCheck(Db.DbPath);
        }

        // ==================== 导出 / 导入（店主 11-05） ====================
        // 导出：按表勾选，默认勾「业务数据 + 系统底座」，可全选。
        // 导入：店主 11-05 改为「按表恢复」——文件里有哪张表就替换哪张，没有的表保持当前数据，
        //      所以缺系统底座的「数据导出」也能安全导入（账号/项目/设置都还在，照样能登录开单）。
        //      完整备份自然等于整库替换，走的是同一条路。
        // 「表齐不齐」仍照固定清单比对（不依赖元数据表），只用于界面提示"这是完整备份还是数据导出"。

        public class TableInfo
        {
            public string Name;      // 物理表名
            public string Label;     // 中文名
            public string Group;     // 业务数据 / 系统底座 / 其他
            public int Count;        // 当前行数
            public bool DefaultOn;   // 是否默认勾选（业务数据 + 系统底座）
        }

        /// <summary>
        /// 表 → 中文名 / 分组。第 4 列仅用于说明，不参与逻辑。
        /// 未登记的表一律归「其他」并照常列出——绝不因为漏登记就把表藏起来。
        /// </summary>
        private static readonly string[,] TableMeta = new string[,]
        {
            // 业务数据（店主说的「重要的」）
            { "members",           "会员档案",     "业务数据" },
            // 店主 11-05：等级按累计充值自动判定，属于会员数据本身，跟着会员一起走
            { "member_levels",     "会员等级",     "业务数据" },
            { "member_cards",      "会员次卡",     "业务数据" },
            { "member_flows",      "会员流水",     "业务数据" },
            { "orders",            "订单主表",     "业务数据" },
            { "order_items",       "消费明细",     "业务数据" },
            { "payments",          "支付流水",     "业务数据" },
            { "commissions",       "提成",         "业务数据" },
            { "card_uses",         "次卡核销",     "业务数据" },
            // 系统底座（缺了恢复出来登不进去 / 项目名空白）
            { "staff",             "员工账号",     "系统底座" },
            { "staff_perms",       "员工按人权限", "系统底座" },
            { "role_perms",        "角色权限",     "系统底座" },
            { "settings",          "系统设置",     "系统底座" },
            { "service_items",     "服务项目",     "系统底座" },
            { "recharge_packages", "储值方案",     "系统底座" },
            { "card_templates",    "次卡模板",     "系统底座" },
            { "staff_item_rates",  "员工项目提成", "系统底座" },
            // 其他（有需要再勾，默认不勾）
            { "op_logs",           "操作日志",     "其他" },
            { "point_flows",       "积分流水",     "其他" },
            { "point_gifts",       "积分礼品",     "其他" },
            { "gift_exchanges",    "礼品兑换",     "其他" },
            { "daily_settlements", "日结记录",     "其他" },
            { "handover_logs",     "交接班记录",   "其他" },
        };

        private static int CountIn(SQLiteConnection conn, string table)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM \"" + table + "\"";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static List<string> ActualTables(SQLiteConnection conn)
        {
            var actual = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) actual.Add(r.GetString(0));
            }
            return actual;
        }

        /// <summary>当前库的表清单（界面上可勾选的那些），按元数据顺序保证分组有序。</summary>
        public List<TableInfo> Tables()
        {
            var list = new List<TableInfo>();
            using (var conn = Db.OpenConnection())
            {
                var actual = ActualTables(conn);
                int metaRows = TableMeta.GetLength(0);
                for (int i = 0; i < metaRows; i++)
                {
                    string n = TableMeta[i, 0];
                    if (!actual.Contains(n)) continue;   // 老库还没建这张表，不列
                    list.Add(new TableInfo
                    {
                        Name = n,
                        Label = TableMeta[i, 1],
                        Group = TableMeta[i, 2],
                        Count = CountIn(conn, n),
                        DefaultOn = TableMeta[i, 2] != "其他",
                    });
                }
                foreach (string n in actual)
                {
                    bool known = false;
                    for (int i = 0; i < metaRows; i++) if (TableMeta[i, 0] == n) { known = true; break; }
                    if (known) continue;
                    list.Add(new TableInfo { Name = n, Label = n, Group = "其他", Count = CountIn(conn, n), DefaultOn = false });
                }
            }
            return list;
        }

        public class ExportSelectedResult
        {
            public string FileName;
            public long SizeKb;
            public int TableCount;
            public int TotalTables;
            public List<string> Added;   // 为满足依赖自动补上的表（中文名）
        }

        /// <summary>
        /// 按勾选导出：先整库在线复制（结构/索引/触发器一次到位），再把没勾的表 DROP 掉。
        /// 为什么不「逐表 CREATE + INSERT」：那要手工重写建表语句，触发器还会建错库；
        /// 复制后 DROP 得到的表结构和原库 100% 一致，且 member_flows 的禁删触发器也不必绕过。
        /// </summary>
        public ExportSelectedResult ExportSelected(string[] tables, string targetPath, string operatorName)
        {
            if (tables == null || tables.Length == 0) throw new BizException("请至少勾选一张表");
            if (string.IsNullOrEmpty(targetPath)) throw new BizException("请选择保存位置");

            List<string> actual;
            using (var conn = Db.OpenConnection()) actual = ActualTables(conn);

            // 白名单：只认真实存在的表名（表名要拼进 SQL，不能信前端）
            var keep = new List<string>();
            foreach (string t in tables)
                if (!string.IsNullOrEmpty(t) && actual.Contains(t) && !keep.Contains(t)) keep.Add(t);
            if (keep.Count == 0) throw new BizException("没有可导出的表");

            // 依赖补全：明细表没有主表就是孤儿数据，带出去也看不懂
            var added = new List<string>();
            Action<string> need = n =>
            {
                if (actual.Contains(n) && !keep.Contains(n)) { keep.Add(n); added.Add(n); }
            };
            if (keep.Contains("order_items") || keep.Contains("payments") || keep.Contains("card_uses") || keep.Contains("commissions")) need("orders");
            if (keep.Contains("card_uses")) need("member_cards");
            if (keep.Contains("member_cards") || keep.Contains("member_flows") || keep.Contains("orders") || keep.Contains("card_uses")) need("members");
            if (keep.Contains("gift_exchanges")) need("point_gifts");
            if (keep.Contains("staff_perms")) need("staff");

            string bad = HealthCheck(Db.DbPath);
            if (bad != null) throw new BizException("数据库体检未通过，已取消导出：" + bad);

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(targetPath)) File.Delete(targetPath);

            using (var src = Db.OpenConnection())
            using (var dst = new SQLiteConnection("Data Source=" + targetPath))
            {
                dst.Open();
                src.BackupDatabase(dst, "main", "main", -1, null, 0);
                foreach (string t in actual)
                {
                    if (keep.Contains(t)) continue;
                    using (var cmd = dst.CreateCommand())
                    {
                        cmd.CommandText = "DROP TABLE IF EXISTS \"" + t + "\"";
                        cmd.ExecuteNonQuery();
                    }
                }
                using (var cmd = dst.CreateCommand())
                {
                    cmd.CommandText = "VACUUM;";
                    cmd.ExecuteNonQuery();
                }
            }
            SealBackupFile(targetPath);

            string badNew = HealthCheck(targetPath);
            if (badNew != null)
            {
                try { File.Delete(targetPath); } catch { }
                throw new BizException("导出文件体检未通过，已作废：" + badNew);
            }

            // 补上的表翻成中文名，日志和界面提示都能看懂
            var addedLabels = new List<string>();
            foreach (string a in added) addedLabels.Add(LabelOf(a));

            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据导出", "导出数据文件",
                    Path.GetFileName(targetPath) + "（" + keep.Count + "/" + actual.Count + " 张表" +
                    (addedLabels.Count > 0 ? "；自动补依赖：" + string.Join("、", addedLabels.ToArray()) : "") + "）");

            return new ExportSelectedResult
            {
                FileName = Path.GetFileName(targetPath),
                SizeKb = new FileInfo(targetPath).Length / 1024,
                TableCount = keep.Count,
                TotalTables = actual.Count,
                Added = addedLabels,
            };
        }

        private static string LabelOf(string table)
        {
            for (int i = 0; i < TableMeta.GetLength(0); i++)
                if (TableMeta[i, 0] == table) return TableMeta[i, 1];
            return table;
        }

        public class InspectResult
        {
            public bool IsThisSystem;    // 有 members 表 = 本系统的库
            public bool IsFull;          // 23 张表齐全 = 可以完整恢复
            public string FileName;
            public long SizeKb;
            public int TableCount;
            public int MemberCount;
            public int OrderCount;
            public string FileTime;      // 文件修改时间
            public List<string> Missing; // 缺哪几张（中文名）
            public string Error;         // 非空 = 不能导入
        }

        /// <summary>自动识别一个 .db：能不能导入、是不是完整备份、里面有多少数据。</summary>
        public InspectResult Inspect(string path)
        {
            var r = new InspectResult { FileName = Path.GetFileName(path), Missing = new List<string>() };
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { r.Error = "文件不存在"; return r; }
            r.SizeKb = new FileInfo(path).Length / 1024;
            r.FileTime = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            try
            {
                using (var conn = new SQLiteConnection("Data Source=" + path))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA quick_check;";
                        object qv = cmd.ExecuteScalar();
                        string s = qv == null ? "" : Convert.ToString(qv);
                        if (!string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase))
                        { r.Error = "文件已损坏：" + s; return r; }
                    }
                    var have = ActualTables(conn);
                    r.IsThisSystem = have.Contains("members");
                    if (!r.IsThisSystem) { r.Error = "这不是本系统的数据库（找不到会员表）"; return r; }
                    r.TableCount = have.Count;
                    if (have.Contains("members")) r.MemberCount = CountIn(conn, "members");
                    if (have.Contains("orders")) r.OrderCount = CountIn(conn, "orders");
                    for (int i = 0; i < TableMeta.GetLength(0); i++)
                        if (!have.Contains(TableMeta[i, 0])) r.Missing.Add(TableMeta[i, 1]);
                    r.IsFull = r.Missing.Count == 0;
                }
            }
            catch (Exception ex) { r.Error = "打不开：" + ex.Message; }
            return r;
        }

        public class ImportTableInfo
        {
            public string Label;   // 中文表名
            public int Count;      // 恢复进来的行数
        }

        /// <summary>按表恢复的结果：恢复了哪些（含行数）、哪些文件里没有（当前数据原样保留）。</summary>
        public class ImportResult
        {
            public string Snapshot;                                  // 恢复前快照文件名
            public List<ImportTableInfo> Restored = new List<ImportTableInfo>();
            public List<string> Kept = new List<string>();           // 文件里没有 → 没动
            public int TotalTables;
        }

        /// <summary>
        /// 店主 11-05：导入不再要求「表齐全」，改成**按表恢复**。
        ///
        /// 以前是整库文件覆盖：缺任何一张表就拒绝，因为覆盖后缺表会让系统缺功能（甚至登录不了）。
        /// 现在改成把文件里**有**的表整表搬进当前库，文件里**没有**的表一个字都不动——
        /// 于是「只导出会员和流水」的包也能安全恢复：账号、设置、项目这些还留在当前库，
        /// 恢复完照样能登录开单，只是那几类数据没被替换。
        ///
        /// 安全兜底三件：
        ///  ① 恢复前先把当前库拍成「恢复前快照」（走 SQLite 在线备份，营业中也能拍）；
        ///  ② 全程一个事务，任一张表行数对不上就整体回滚，当前数据零改动；
        ///  ③ member_flows 的禁删触发器先 DROP、写完原样重建，不绕过业务约束。
        /// </summary>
        public ImportResult Import(string path, string operatorName)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new BizException("文件不存在");
            InspectResult info = Inspect(path);
            if (!string.IsNullOrEmpty(info.Error)) throw new BizException("不能导入：" + info.Error);

            Directory.CreateDirectory(BackupDir);
            string snap = Path.Combine(BackupDir, "member-" + DateTime.Now.ToString("yyyy-MM-dd HH:mm").Replace("-", "").Replace(":", "").Replace(" ", "-") + "-S.db");
            using (var c1 = Db.OpenConnection())
            using (var c2 = SQLiteOpenHelperBackup(snap))
            {
                c1.BackupDatabase(c2, "main", "main", -1, null, 0);
            }
            SealBackupFile(snap);

            var result = new ImportResult { Snapshot = Path.GetFileName(snap) };

            using (var conn = Db.OpenConnection())
            {
                // 外键必须在事务外关：整表替换的中间态必然违反引用，数据整体来自同一系统，提交后自洽
                Exec(conn, null, "PRAGMA foreign_keys=OFF");
                bool attached = false;
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "ATTACH DATABASE @p AS src";
                        cmd.Parameters.AddWithValue("@p", path);
                        cmd.ExecuteNonQuery();
                    }
                    attached = true;

                    List<string> dst = TablesOf(conn, null, "main");
                    List<string> src = TablesOf(conn, null, "src");
                    result.TotalTables = dst.Count;

                    using (var txn = conn.BeginTransaction())
                    {
                        // 禁删触发器会挡住 DELETE：先记下建它的语句，DROP，整表搬完再原样重建
                        var triggers = TriggersOf(conn, txn, "main");
                        foreach (var kv in triggers) Exec(conn, txn, "DROP TRIGGER IF EXISTS main." + Quote(kv.Key));

                        foreach (string t in OrderedTables(dst, src))
                        {
                            if (!src.Contains(t)) { result.Kept.Add(LabelOf(t)); continue; }
                            List<string> sc = ColumnsOf(conn, txn, "src", t);
                            List<string> dc = ColumnsOf(conn, txn, "main", t);
                            var common = new List<string>();
                            foreach (string c in dc) if (sc.Contains(c)) common.Add(c);
                            if (common.Count == 0) { result.Kept.Add(LabelOf(t)); continue; }

                            string cols = string.Join(",", common.ConvertAll(Quote).ToArray());
                            Exec(conn, txn, "DELETE FROM main." + Quote(t));
                            Exec(conn, txn, "INSERT INTO main." + Quote(t) + "(" + cols + ") SELECT " + cols + " FROM src." + Quote(t));

                            int want = CountInSchema(conn, txn, "src", t);
                            int got = CountInSchema(conn, txn, "main", t);
                            if (want != got)
                                throw new BizException("「" + LabelOf(t) + "」行数对不上（文件 " + want + " 行，实际写入 " + got +
                                    " 行），已整体回滚，当前数据没有任何改动");
                            result.Restored.Add(new ImportTableInfo { Label = LabelOf(t), Count = got });
                        }

                        foreach (var kv in triggers) Exec(conn, txn, kv.Value);
                        txn.Commit();
                    }

                    Exec(conn, null, "DETACH DATABASE src");
                    attached = false;
                }
                catch
                {
                    if (attached) { try { Exec(conn, null, "DETACH DATABASE src"); } catch { } }
                    throw;
                }
                finally
                {
                    try { Exec(conn, null, "PRAGMA foreign_keys=ON"); } catch { }
                }

                var restoredLabels = new List<string>();
                foreach (var it in result.Restored) restoredLabels.Add(it.Label);
                W.Log(conn, null, "数据备份", "导入备份文件",
                    "从外部文件恢复 " + Path.GetFileName(path) + "：恢复 " + result.Restored.Count + " 张表（" +
                    string.Join("、", restoredLabels.ToArray()) + "）；文件未含 " + result.Kept.Count + " 张表，保持原样；恢复前快照 " + Path.GetFileName(snap));
            }

            return result;
        }

        /* ---------- 按表恢复用到的 SQLite 小工具（都带 schema，main=当前库 src=导入文件） ---------- */

        private static void Exec(SQLiteConnection conn, SQLiteTransaction txn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static string Quote(string ident) { return "\"" + ident.Replace("\"", "\"\"") + "\""; }

        private static List<string> TablesOf(SQLiteConnection conn, SQLiteTransaction txn, string schema)
        {
            var list = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = "SELECT name FROM " + schema + ".sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
            }
            return list;
        }

        private static List<string> ColumnsOf(SQLiteConnection conn, SQLiteTransaction txn, string schema, string table)
        {
            var list = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = "PRAGMA " + schema + ".table_info(" + Quote(table) + ")";
                using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(1));
            }
            return list;
        }

        private static int CountInSchema(SQLiteConnection conn, SQLiteTransaction txn, string schema, string table)
        {
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = "SELECT COUNT(*) FROM " + schema + "." + Quote(table);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>当前库所有触发器（名字 → 原始 CREATE 语句），禁删触发器靠它原样重建。</summary>
        private static Dictionary<string, string> TriggersOf(SQLiteConnection conn, SQLiteTransaction txn, string schema)
        {
            var d = new Dictionary<string, string>();
            using (var cmd = conn.CreateCommand())
            {
                if (txn != null) cmd.Transaction = txn;
                cmd.CommandText = "SELECT name, sql FROM " + schema + ".sqlite_master WHERE type='trigger' AND sql IS NOT NULL";
                using (var r = cmd.ExecuteReader()) while (r.Read()) d[r.GetString(0)] = r.GetString(1);
            }
            return d;
        }

        /// <summary>恢复顺序：先按元数据登记顺序（父表在前，外键不容易出问题），没登记的排最后。</summary>
        private static List<string> OrderedTables(List<string> dst, List<string> src)
        {
            var order = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < TableMeta.GetLength(0); i++)
            {
                string n = TableMeta[i, 0];
                if (seen.Contains(n)) continue;
                if (dst.Contains(n) || src.Contains(n)) { order.Add(n); seen.Add(n); }
            }
            foreach (string n in dst) if (seen.Add(n)) order.Add(n);
            foreach (string n in src) if (seen.Add(n)) order.Add(n);
            return order;
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public class ExportVersionResult
        {
            public int Count;
            public string Dir;
            public List<string> Files = new List<string>();
        }

        /// <summary>
        /// 店主 11-05：把备份列表里勾中的版本原样另存出去（换机、存档、给会计）。
        /// 与上方「导出数据」分工不同：那个是按表挑内容、导出**当前库**；这个是把**已经有**的备份文件复制走，
        /// 可以一次勾多个版本，落到同一个文件夹。只认 backups 目录里真实存在的文件名
        /// （取 Path.GetFileName 比对，挡住 ..\ 之类的路径穿越）。
        /// </summary>
        public ExportVersionResult ExportVersions(string[] fileNames, string targetDir, string operatorName)
        {
            if (fileNames == null || fileNames.Length == 0) throw new BizException("请先勾选要导出的备份版本");
            if (string.IsNullOrEmpty(targetDir)) throw new BizException("请选择导出位置");
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(BackupDir);

            var result = new ExportVersionResult { Dir = targetDir };
            foreach (string raw in fileNames)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string name = Path.GetFileName(raw);          // 只允许纯文件名
                if (!string.Equals(name, raw, StringComparison.Ordinal)) continue;
                string src = Path.Combine(BackupDir, name);
                if (!File.Exists(src)) continue;

                string dst = Path.Combine(targetDir, name);
                int n = 2;
                while (File.Exists(dst))
                {
                    dst = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(name) + "_" + n + ".db");
                    n++;
                }
                File.Copy(src, dst, false);
                result.Files.Add(Path.GetFileName(dst));
            }
            if (result.Files.Count == 0)
                throw new BizException("勾选的版本在备份目录里已经找不到了，请刷新列表后重新勾选");
            result.Count = result.Files.Count;

            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据备份", "导出备份版本",
                    "导出 " + result.Count + " 个备份版本到 " + targetDir + "：" + string.Join("、", result.Files.ToArray()));
            return result;
        }

        /// <summary>
        /// 备份轮转（店主 11-05）：备份可以留，但不能无限堆。
        /// 自动备份留最近 30 个 + 月初份最多再留 2 个；手动备份留最近 30 个；恢复前快照留最近 10 个。
        /// 只删 backups\ 下的文件，绝不碰活动库；删除动作写日志，可追溯。
        /// </summary>
        public int Rotate(string operatorName)
        {
            Directory.CreateDirectory(BackupDir);
            var files = new List<FileInfo>();
            foreach (string f in Directory.GetFiles(BackupDir, "member-*.db")) files.Add(new FileInfo(f));
            files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));   // 新的排前面

            var auto = new List<FileInfo>();
            var manual = new List<FileInfo>();
            var snap = new List<FileInfo>();
            foreach (var f in files)
            {
                string n = Path.GetFileNameWithoutExtension(f.Name);
                if (n.Contains("-S")) snap.Add(f);
                else if (n.Contains("-M")) manual.Add(f);
                else auto.Add(f);
            }

            var drop = new List<FileInfo>();
            // 月初锚点：形如 20261001-2132，第 7~8 位是日，等于 01 就是月初那份。
            // 店主 11-05 定：**月初份只留最近 2 个**——备份是整库快照，后面的都包含前面的
            // 全部数据，再往前留只是成倍占盘，没有恢复价值（原来「每月 1 号永久留」一年能堆到十几份）。
            int monthKept = 0;
            for (int i = 0; i < auto.Count; i++)
            {
                string n = Path.GetFileNameWithoutExtension(auto[i].Name).Replace("member-", "");
                bool firstOfMonth = n.Length >= 8 && n.Substring(6, 2) == "01";
                if (i < 30)
                {
                    if (firstOfMonth) monthKept++;   // 落在最近 30 个里的月初份，本来就要留，计入名额
                    continue;
                }
                if (firstOfMonth && monthKept < 2)
                {
                    monthKept++;
                    continue;
                }
                drop.Add(auto[i]);
            }
            for (int i = 30; i < manual.Count; i++) drop.Add(manual[i]);
            for (int i = 10; i < snap.Count; i++) drop.Add(snap[i]);

            int removed = 0;
            foreach (var f in drop)
            {
                try { f.Delete(); removed++; }
                catch (Exception ex) { Logger.Warn("备份轮转删除失败 " + f.Name + "：" + ex.Message); }
            }
            if (removed > 0)
            {
                try
                {
                    using (var conn = Db.OpenConnection())
                        W.Log(conn, null, "数据备份", "备份轮转",
                            "自动清理 " + removed + " 个过期备份（自动留30+月初2份，手动留30，快照留10）");
                }
                catch { /* 日志写不进去不影响清理结果 */ }
            }
            return removed;
        }

        /// <summary>
        /// 把备份文件「封口」成单个文件：checkpoint 把 WAL 内容并入主库，再删掉 -wal / -shm。
        /// 只用于刚生成的备份副本，**绝不能对当前库调用**。任何一步失败都不影响备份本身。
        /// </summary>
        private static void SealBackupFile(string target)
        {
            try
            {
                using (var conn = new SQLiteConnection("Data Source=" + target))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        cmd.ExecuteNonQuery();
                    }
                }
                foreach (string ext in new string[] { "-wal", "-shm" })
                {
                    string side = target + ext;
                    if (File.Exists(side)) File.Delete(side);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("备份封口失败（备份仍可用，只是多了伴生文件）：" + ex.Message);
            }
        }

        public string BackupNow(bool manual, string operatorName)
        {
            Directory.CreateDirectory(BackupDir);
            // 备份前：源库体检不过就别备份——坏备份只会污染备份列表，还会被误当成救命稻草恢复。
            string bad = HealthCheck(Db.DbPath);
            if (bad != null)
            {
                Logger.Warn("备份前体检未通过，已取消备份：" + bad);
                try
                {
                    using (var conn = Db.OpenConnection())
                        W.Log(conn, null, "数据备份", "备份失败", "备份前体检未通过：" + bad + "（请立即停止使用，改用上一次备份恢复）");
                }
                catch { /* 日志写不进去也不能吞掉下面的异常 */ }
                throw new BizException("数据库体检未通过，已取消备份：" + bad + "。请停止操作并改用上一次备份恢复。");
            }

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm").Replace("-", "").Replace(":", "").Replace(" ", "-");
            string target = Path.Combine(BackupDir, "member-" + stamp + (manual ? "-M" : "") + ".db");
            using (var src = Db.OpenConnection())
            using (var dst = SQLiteOpenHelperBackup(target))
            {
                src.BackupDatabase(dst, "main", "main", -1, null, 0);
            }
            // 备份后：当场验证产物可用，不然「备份成功」只是自我安慰
            string badNew = HealthCheck(target);
            if (badNew != null)
            {
                try { File.Delete(target); } catch { }
                Logger.Warn("备份产物体检未通过，已作废：" + badNew);
                throw new BizException("备份产物体检未通过，已作废：" + badNew);
            }
            // 备份必须是**单个自包含文件**：源库是 WAL 模式，打开备份时会带出 -wal/-shm 伴生文件。
            // 先 checkpoint 把 WAL 里的内容落进主库，再删掉两个伴生文件 —— 以后拷走这一个 .db 就是完整备份。
            SealBackupFile(target);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据备份", manual ? "手动备份" : "自动备份", Path.GetFileName(target) + "（体检通过）");
            // 备份成功后顺手轮转：备份可以留，但不能无限堆（失败不影响本次备份结果）
            try { Rotate(operatorName); }
            catch (Exception ex) { Logger.Warn("备份轮转失败（不影响本次备份）：" + ex.Message); }
            return Path.GetFileName(target);
        }

        private static SQLiteConnection SQLiteOpenHelperBackup(string path)
        {
            var conn = new SQLiteConnection("Data Source=" + path);
            conn.Open();
            return conn;
        }

        public string Restore(string fileName, string operatorName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith("member-") || fileName.Contains(".."))
                throw new BizException("非法的备份文件名");
            string src = Path.Combine(BackupDir, fileName);
            if (!File.Exists(src)) throw new BizException("备份文件不存在");
            // 恢复前体检：损坏的备份一律拒绝，当前库一个字节都不动
            string bad = HealthCheck(src);
            if (bad != null)
            {
                Logger.Warn("拒绝恢复损坏的备份 " + fileName + "：" + bad);
                throw new BizException("这个备份已损坏，不能恢复：" + bad + "。请换一个日期更早的备份。");
            }
            // 恢复前快照
            string snap = Path.Combine(BackupDir, "member-" + DateTime.Now.ToString("yyyy-MM-dd HH:mm").Replace("-", "").Replace(":", "").Replace(" ", "-") + "-S.db");
            using (var c1 = Db.OpenConnection())
            using (var c2 = SQLiteOpenHelperBackup(snap))
            {
                c1.BackupDatabase(c2, "main", "main", -1, null, 0);
            }
            SQLiteConnection.ClearAllPools();
            // 旧 -wal/-shm 不清，会被当成新库的追加内容，恢复出来的数据可能夹带旧页
            TryDelete(Db.DbPath + "-wal");
            TryDelete(Db.DbPath + "-shm");
            File.Copy(src, Db.DbPath, true);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据备份", "恢复备份", "恢复自 " + fileName + "，恢复前快照 " + Path.GetFileName(snap));
            return Path.GetFileName(snap);
        }

        /// <summary>D4：删除一个备份版本（只删备份文件，绝不动当前数据库）。</summary>
        public void Delete(string fileName, string operatorName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith("member-") || fileName.Contains(".."))
                throw new BizException("非法的备份文件名");
            string path = Path.Combine(BackupDir, fileName);
            if (!File.Exists(path)) throw new BizException("备份文件不存在");
            File.Delete(path);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据备份", "删除备份", fileName);
        }

        public class AutoRow
        {
            public bool Enabled;
            public int Hour;
            public string LastDate;   // 上次自动备份的日期 yyyy-MM-dd，空=从未
            public string Today;
        }

        public AutoRow GetAuto()
        {
            string enabled = DatabaseInitializer.GetSetting("backup_auto_enabled", "0");
            string hour = DatabaseInitializer.GetSetting("backup_auto_hour", "20");
            int h;
            if (!int.TryParse(hour, out h) || h < 0 || h > 23) h = 20;
            return new AutoRow
            {
                Enabled = enabled == "1",
                Hour = h,
                LastDate = DatabaseInitializer.GetSetting("backup_last_date", ""),
                Today = DateTime.Now.ToString("yyyy-MM-dd")
            };
        }

        public void SetAuto(bool enabled, int hour, string operatorName)
        {
            if (hour < 0 || hour > 23) throw new BizException("自动备份时间必须在 0-23 点之间");
            DatabaseInitializer.SetSetting("backup_auto_enabled", enabled ? "1" : "0");
            DatabaseInitializer.SetSetting("backup_auto_hour", hour.ToString());
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "数据备份", "设置自动备份", enabled ? ("开启，每天 " + hour + " 点") : "关闭");
        }

        /// <summary>
        /// D3 每日自动备份：程序启动和定时各调一次。同一天只做一次（日期记在 settings），
        /// 到点（当前小时 >= 设定小时）才执行。返回生成的文件名；未开启 / 未到点 /
        /// 当天已备份都返回空串（调用方据此决定是否提示）。
        /// </summary>
        public string AutoTick(string operatorName)
        {
            AutoRow a = GetAuto();
            if (!a.Enabled) return "";
            if (a.LastDate == a.Today) return "";
            if (DateTime.Now.Hour < a.Hour) return "";
            // 体检失败时 BackupNow 会抛——自动备份绝不能因此把程序带崩：
            // 这里兜住、只写日志，且不推进日期（下次再试，问题不会被静默翻篇）。
            string name;
            try
            {
                name = BackupNow(false, operatorName);
            }
            catch (Exception ex)
            {
                Logger.Warn("每日自动备份失败：" + ex.Message);
                try
                {
                    using (var conn = Db.OpenConnection())
                        W.Log(conn, null, "数据备份", "自动备份失败", ex.Message + "（当天不会重复尝试，请手动处理）");
                }
                catch { }
                return "";
            }
            // 备份已由 BackupNow 记录日志，这里只推进日期，避免重复流水
            DatabaseInitializer.SetSetting("backup_last_date", a.Today);
            return name;
        }
    }

    public class SettingsService
    {
        public Dictionary<string, string> GetAll()
        {
            var d = new Dictionary<string, string>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT key,value FROM settings";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) d[r.GetString(0)] = r.GetString(1);
                }
            }
            return d;
        }

        public void Save(Dictionary<string, string> kv, string operatorName)
        {
            if (kv == null || kv.Count == 0) return;
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    foreach (var pair in kv)
                        DatabaseInitializer.SetSetting(conn, txn, pair.Key, pair.Value ?? "");
                    W.Log(conn, txn, "系统设置", "保存设置", "共 " + kv.Count + " 项");
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

    public class PointService
    {
        public class GiftRow
        {
            public int Id;
            public string Name;
            public int Points;
            public int Stock;
        }

        public class PointRec
        {
            public string Time;
            public string MemberName;
            public int Change;
            public string Reason;
            public int BalanceAfter;
        }

        public class Rules
        {
            public string Earn;
            public string Birthday;
            public string Redeem;
            public string Valid;
            // 结构化数值：真正参与计算。上面四条文本只是给人看的，由这些数值生成。
            public decimal PerYuan;        // 每消费 / 充值多少元得 1 分
            public decimal BirthdayMult;   // 生日倍率（1 = 不额外赠送）
            public int PointsPerYuan;      // 多少分抵 1 元
            public int ValidMonths;        // 积分有效期（月），0 = 永久
            public bool Enabled;           // 总开关
            public bool EarnConsume;       // 消费给分
            public bool EarnRecharge;      // 充值给分
        }

        public class PointConfig
        {
            public decimal PerYuan;
            public decimal BirthdayMult;
            public int PointsPerYuan;
            public int ValidMonths;
            // 场景开关（店主可自由决定在什么情况下能获得积分）
            public bool Enabled;       // 总开关：停用后不再累加新积分（已有积分仍可兑换）
            public bool EarnConsume;   // 消费（结账）时给分
            public bool EarnRecharge; // 充值（储值充值 + 建档初始充值）时给分
        }

        public List<GiftRow> Gifts()
        {
            var list = new List<GiftRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,name,points,stock FROM point_gifts ORDER BY id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new GiftRow
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            Points = Convert.ToInt32(r.GetValue(2)),
                            Stock = Convert.ToInt32(r.GetValue(3))
                        });
                }
            }
            return list;
        }

        /// <summary>
        /// 新增 / 编辑礼品（店主 10-24：积分礼品原先只有种子数据，店主无法自己加）。
        /// id=0 新增，id>0 编辑。库存可自由设置（>=0）；改名不影响历史兑换记录（gift_exchanges 存 gift_id + points 快照）。
        /// </summary>
        public void SaveGift(int id, string name, int points, int stock, string operatorName)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new BizException("礼品名称不能为空");
            if (name.Length > 20) throw new BizException("礼品名称不能超过 20 个字");
            if (points <= 0) throw new BizException("所需积分必须大于 0");
            if (points > 9999999) throw new BizException("所需积分过大");
            if (stock < 0) throw new BizException("库存不能为负数");
            if (stock > 9999999) throw new BizException("库存过大");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // 同名礼品只允许一个（避免兑换时选错）
                    using (var ck = conn.CreateCommand())
                    {
                        ck.Transaction = txn;
                        ck.CommandText = "SELECT COUNT(*) FROM point_gifts WHERE name=@n AND id<>@id";
                        W.P(ck, "@n", name); W.P(ck, "@id", id);
                        if (Convert.ToInt32(ck.ExecuteScalar()) > 0)
                            throw new BizException("已有同名礼品「" + name + "」");
                    }

                    if (id <= 0)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "INSERT INTO point_gifts(name,points,stock) VALUES(@n,@p,@s)";
                            W.P(cmd, "@n", name); W.P(cmd, "@p", points); W.P(cmd, "@s", stock);
                            cmd.ExecuteNonQuery();
                        }
                        W.Log(conn, txn, "积分与营销", "新增礼品",
                            name + "（" + points + " 分 / 库存 " + stock + "）", operatorName);
                    }
                    else
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE point_gifts SET name=@n,points=@p,stock=@s WHERE id=@id";
                            W.P(cmd, "@n", name); W.P(cmd, "@p", points); W.P(cmd, "@s", stock); W.P(cmd, "@id", id);
                            if (cmd.ExecuteNonQuery() == 0) throw new BizException("礼品不存在（可能已被删除）");
                        }
                        W.Log(conn, txn, "积分与营销", "编辑礼品",
                            name + "（" + points + " 分 / 库存 " + stock + "）", operatorName);
                    }
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 删除礼品（店主 10-24）。已发生的兑换记录保留（gift_exchanges 有外键指向本表，
        /// 故不能物理删除有兑换历史的礼品——改为把库存置 0 并改名标记「已下架」，
        /// 既不破坏历史，又不再出现在可兑换列表里）。无兑换历史的直接删。
        /// </summary>
        public void DeleteGift(int id, string operatorName)
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
                        cmd.CommandText = "SELECT name FROM point_gifts WHERE id=@id";
                        W.P(cmd, "@id", id);
                        object v = cmd.ExecuteScalar();
                        if (v == null) throw new BizException("礼品不存在（可能已被删除）");
                        name = Convert.ToString(v);
                    }

                    int used;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT COUNT(*) FROM gift_exchanges WHERE gift_id=@id";
                        W.P(cmd, "@id", id);
                        used = Convert.ToInt32(cmd.ExecuteScalar());
                    }

                    if (used > 0)
                    {
                        // 有兑换历史：下架（改名 + 库存 0），保外键、保历史可读
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "UPDATE point_gifts SET name=@n,stock=0 WHERE id=@id";
                            W.P(cmd, "@n", name + "（已下架）"); W.P(cmd, "@id", id);
                            cmd.ExecuteNonQuery();
                        }
                        W.Log(conn, txn, "积分与营销", "下架礼品",
                            name + "（有 " + used + " 次兑换历史，改为下架保留记录）", operatorName);
                    }
                    else
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = txn;
                            cmd.CommandText = "DELETE FROM point_gifts WHERE id=@id";
                            W.P(cmd, "@id", id);
                            cmd.ExecuteNonQuery();
                        }
                        W.Log(conn, txn, "积分与营销", "删除礼品", name, operatorName);
                    }
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>
        /// 手工调整会员积分（店主 10-24）：正数加分、负数扣分，用于活动奖励 / 补偿 / 纠错。
        /// 与自动给分（AddPoints）分开：**不套生日倍数、不按金额换算**，就是直接改这个数。
        /// 扣分不能把余额扣成负数（低于 0 直接拒，让店主自己算清楚）。
        /// </summary>
        public int AdjustPoints(int memberId, int change, string reason, string operatorName)
        {
            if (memberId <= 0) throw new BizException("请先选择会员");
            if (change == 0) throw new BizException("调整分值不能为 0");
            reason = (reason ?? "").Trim();
            if (reason.Length == 0) throw new BizException("请填写调整原因（会写入积分流水与操作日志）");
            if (reason.Length > 50) throw new BizException("原因不能超过 50 个字");

            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string memberName;
                    int before;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name, IFNULL(points,0) FROM members WHERE id=@id";
                        W.P(cmd, "@id", memberId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("会员不存在（可能已归档）");
                            memberName = r.GetString(0);
                            // points 是 INTEGER，按 long 接住再夹回 int，绝不让它抛溢出
                            long b = Convert.ToInt64(r.GetValue(1));
                            before = b > int.MaxValue ? int.MaxValue : (int)b;
                        }
                    }

                    long target = (long)before + change;
                    if (target < 0)
                        throw new BizException("扣分超出余额：当前 " + before + " 分，本次要扣 " + Math.Abs((long)change) + " 分");
                    if (target > int.MaxValue) target = int.MaxValue;
                    int after = (int)target;

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET points=@p WHERE id=@id;" +
                            " INSERT INTO point_flows(member_id,change,reason,balance_after) VALUES(@id,@ch,@rs,@ba)";
                        W.P(cmd, "@p", after); W.P(cmd, "@id", memberId);
                        W.P(cmd, "@ch", change);
                        W.P(cmd, "@rs", "手工调整：" + reason);
                        W.P(cmd, "@ba", after);
                        cmd.ExecuteNonQuery();
                    }

                    W.Log(conn, txn, "积分与营销", "手工调整积分",
                        memberName + " " + (change > 0 ? "+" : "") + change + " 分（" + before + " → " + after + "），原因：" + reason,
                        operatorName);
                    txn.Commit();
                    return after;
                }
                catch (Exception ex)
                {
                    try { txn.Rollback(); } catch (Exception rb) { Logger.Warn("rollback failed, original exception rethrown: " + rb.Message); }
                    throw;
                }
            }
        }

        /// <summary>兑换记录行（店主 10-24：兑换台账，看谁换了什么）。</summary>
        public class ExchangeRow
        {
            public int Id;
            public string Time;
            public string MemberName;
            public string GiftName;
            public int Points;
            public string Operator;
        }

        /// <summary>兑换记录台账，按 id DESC 分页。</summary>
        public List<ExchangeRow> Exchanges(int limit, int offset)
        {
            var list = new List<ExchangeRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT e.id, e.created_at, IFNULL(m.name,'（已删除）'), IFNULL(g.name,'（已下架）'), e.points, e.operator" +
                    " FROM gift_exchanges e" +
                    " LEFT JOIN members m ON m.id=e.member_id" +
                    " LEFT JOIN point_gifts g ON g.id=e.gift_id" +
                    " ORDER BY e.id DESC LIMIT @l OFFSET @o";
                W.P(cmd, "@l", limit); W.P(cmd, "@o", offset);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new ExchangeRow
                        {
                            Id = r.GetInt32(0),
                            Time = r.GetString(1),
                            MemberName = r.GetString(2),
                            GiftName = r.GetString(3),
                            Points = Convert.ToInt32(r.GetValue(4)),
                            Operator = r.GetString(5)
                        });
                }
            }
            return list;
        }

        /// <summary>兑换记录总条数（分页用，不受 limit/offset 影响）。</summary>
        public int ExchangesCount()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM gift_exchanges";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>积分流水，按 id DESC 分页。limit/offset 供前端「加载更多」增量拉取。</summary>
        public List<PointRec> Records(int limit, int offset)
        {
            var list = new List<PointRec>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT p.created_at, m.name, p.change, p.reason, p.balance_after FROM point_flows p" +
                    " JOIN members m ON m.id=p.member_id ORDER BY p.id DESC LIMIT @l OFFSET @o";
                W.P(cmd, "@l", limit);
                W.P(cmd, "@o", offset);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new PointRec
                        {
                            Time = r.GetString(0),
                            MemberName = r.GetString(1),
                            Change = Convert.ToInt32(r.GetValue(2)),
                            Reason = r.GetString(3),
                            BalanceAfter = Convert.ToInt32(r.GetValue(4))
                        });
                }
            }
            return list;
        }

        /// <summary>积分流水总条数（不受 offset/limit 影响）。前端「已显示 X / 共 Y 条」的 Y 取这里。</summary>
        public int RecordsCount()
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM point_flows p";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 积分规则。说明文字**由数值生成**，保证页面上看到的和实际执行的永远一致
        /// （旧实现里文本是手写的、不参与任何计算，改了积分也不会变）。
        /// </summary>
        public Rules GetRules()
        {
            PointConfig c = GetPointConfig();
            string earn;
            if (!c.Enabled) earn = "积分功能已停用：不再累加新积分（会员已有积分仍可正常兑换）";
            else if (c.EarnConsume && c.EarnRecharge) earn = "消费 / 充值 每 " + c.PerYuan.ToString("0.##") + " 元 = 1 积分";
            else if (c.EarnConsume) earn = "仅消费获得积分：每 " + c.PerYuan.ToString("0.##") + " 元 = 1 积分（充值不给分）";
            else if (c.EarnRecharge) earn = "仅充值获得积分：每 " + c.PerYuan.ToString("0.##") + " 元 = 1 积分（消费不给分）";
            else earn = "消费与充值均不累加积分（如需启用请打开对应开关）";
            return new Rules
            {
                PerYuan = c.PerYuan,
                BirthdayMult = c.BirthdayMult,
                PointsPerYuan = c.PointsPerYuan,
                ValidMonths = c.ValidMonths,
                Enabled = c.Enabled,
                EarnConsume = c.EarnConsume,
                EarnRecharge = c.EarnRecharge,
                Earn = earn,
                Birthday = c.BirthdayMult > 1m
                    ? ("生日当天消费 " + c.BirthdayMult.ToString("0.##") + " 倍积分")
                    : "生日当天不额外赠送积分",
                Redeem = c.PointsPerYuan + " 积分 = 1 元（仅限积分商城兑换，不可直接抵现）",
                Valid = c.ValidMonths > 0 ? ("积分有效期 " + c.ValidMonths + " 个月，到期自动失效") : "积分永久有效"
            };
        }

        public PointConfig GetPointConfig()
        {
            decimal perYuan = 1m, mult = 2m;
            int ppy = 100, months = 12;
            bool enabled = true, earnConsume = true, earnRecharge = true;
            decimal.TryParse(DatabaseInitializer.GetSetting("points_per_yuan", "1"), out perYuan);
            decimal.TryParse(DatabaseInitializer.GetSetting("points_birthday_mult", "2"), out mult);
            int.TryParse(DatabaseInitializer.GetSetting("points_points_per_yuan", "100"), out ppy);
            int.TryParse(DatabaseInitializer.GetSetting("points_valid_months", "12"), out months);
            // 注意：C# 的 bool.TryParse 只认 "True"/"true"，不认 "1"！
            // 存储口径是 "1"/"0"，必须显式比较；非 "0" 一律视为开（老库无此键 → 保持开启）。
            enabled = DatabaseInitializer.GetSetting("points_enabled", "1") != "0";
            earnConsume = DatabaseInitializer.GetSetting("points_earn_consume", "1") != "0";
            earnRecharge = DatabaseInitializer.GetSetting("points_earn_recharge", "1") != "0";
            if (perYuan <= 0m) perYuan = 1m;
            if (mult < 1m) mult = 1m;
            if (ppy <= 0) ppy = 100;
            if (months < 0) months = 0;
            return new PointConfig { PerYuan = perYuan, BirthdayMult = mult, PointsPerYuan = ppy, ValidMonths = months,
                Enabled = enabled, EarnConsume = earnConsume, EarnRecharge = earnRecharge };
        }

        public void SavePointConfig(decimal perYuan, decimal birthdayMult, int pointsPerYuan, int validMonths,
            bool enabled, bool earnConsume, bool earnRecharge, string operatorName)
        {
            if (perYuan <= 0m) throw new BizException("「多少元得 1 分」必须大于 0");
            if (birthdayMult < 1m) throw new BizException("生日倍率不能小于 1");
            if (pointsPerYuan <= 0) throw new BizException("「多少分抵 1 元」必须大于 0");
            if (validMonths < 0) throw new BizException("有效期不能为负（0 表示永久有效）");
            DatabaseInitializer.SetSetting("points_per_yuan", perYuan.ToString("0.##"));
            DatabaseInitializer.SetSetting("points_birthday_mult", birthdayMult.ToString("0.##"));
            DatabaseInitializer.SetSetting("points_points_per_yuan", pointsPerYuan.ToString());
            DatabaseInitializer.SetSetting("points_valid_months", validMonths.ToString());
            DatabaseInitializer.SetSetting("points_enabled", enabled ? "1" : "0");
            DatabaseInitializer.SetSetting("points_earn_consume", earnConsume ? "1" : "0");
            DatabaseInitializer.SetSetting("points_earn_recharge", earnRecharge ? "1" : "0");
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "积分与营销", "修改积分规则",
                    "每 " + perYuan.ToString("0.##") + " 元=1分；生日 " + birthdayMult.ToString("0.##") + " 倍；" +
                    pointsPerYuan + " 分=1 元；有效期 " + (validMonths > 0 ? validMonths + " 个月" : "永久") + "；" +
                    (enabled ? "启用" : "停用") + "；获得场景=" + (earnConsume ? "消费" : "") + (earnConsume && earnRecharge ? "、" : "") + (earnRecharge ? "充值" : "") + (earnConsume || earnRecharge ? "" : "无"));
        }

        /// <summary>
        /// D2：按规则给会员累加积分（收银结账 / 充值都走这里）。
        /// 旧实现里积分只在兑换礼品时减少、消费永远不加分，规则形同虚设。
        /// 必须在调用方的事务内执行（传 conn/txn），避免 WAL 自死锁。
        /// 注意 cfg 必须**在事务开始之前**由调用方读好再传进来 —— 本方法内部绝不能调
        /// GetSetting/SetSetting，那会另开连接把事务锁死。
        /// </summary>
        public int AddPoints(int memberId, decimal amount, string reason, PointConfig cfg, SQLiteConnection conn, SQLiteTransaction txn)
        {
            if (memberId <= 0 || amount <= 0m || cfg == null) return 0;
            // 全程用 decimal 算完再转 int，且**夹到 int 上限**：金额/每元积分一旦超出 int 范围，
            // 直接 (int) 会抛「值对于 Int32 太大或太小」——那句话对店主毫无意义（10-15 实测踩到）。
            decimal raw = Math.Floor(amount / cfg.PerYuan);
            if (raw > int.MaxValue) raw = int.MaxValue;
            if (raw <= 0m) return 0;
            int pts = (int)raw;
            if (cfg.BirthdayMult > 1m && IsBirthdayToday(conn, txn, memberId))
            {
                decimal boosted = Math.Floor(pts * cfg.BirthdayMult);
                pts = boosted > int.MaxValue ? int.MaxValue : (int)boosted;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "UPDATE members SET points=IFNULL(points,0)+@p WHERE id=@id";
                W.P(cmd, "@p", pts); W.P(cmd, "@id", memberId);
                cmd.ExecuteNonQuery();
            }
            int after = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id";
                W.P(cmd, "@id", memberId);
                // points 是 INTEGER 列，读出来按 long 接住再夹回 int，绝不让它抛溢出
                long afterL = Convert.ToInt64(cmd.ExecuteScalar());
                after = afterL > int.MaxValue ? int.MaxValue : (int)afterL;
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO point_flows(member_id,change,reason,balance_after) VALUES(@id,@ch,@rs,@ba)";
                W.P(cmd, "@id", memberId); W.P(cmd, "@ch", pts);
                W.P(cmd, "@rs", reason); W.P(cmd, "@ba", after);
                cmd.ExecuteNonQuery();
            }
            return pts;
        }

        /// <summary>
        /// 扣回积分（充值退款红冲用）：按发放同式 floor(金额/每元积分) 计算应扣，
        /// 但**不套生日倍率**（退款不是再赚，避免把生日加成也扣掉而多罚），且**只扣到 0 为止**
        /// （会员可能已用掉部分积分，不为负）。与 <see cref="AddPoints"/> 对称，
        /// 必须在调用方事务内执行（传 conn/txn）；cfg 须在事务外读好再传。
        /// 返回实际扣掉的分（≤应扣），供日志。
        /// </summary>
        public int ClawPoints(int memberId, decimal amount, string reason, PointConfig cfg, SQLiteConnection conn, SQLiteTransaction txn)
        {
            if (memberId <= 0 || amount <= 0m || cfg == null || cfg.PerYuan <= 0) return 0;
            int shouldClaw = (int)Math.Floor(amount / cfg.PerYuan);
            if (shouldClaw <= 0) return 0;

            int current;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id";
                W.P(cmd, "@id", memberId);
                current = Convert.ToInt32(cmd.ExecuteScalar());
            }
            int claw = Math.Min(shouldClaw, current);
            if (claw <= 0) return 0;
            int after = current - claw;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "UPDATE members SET points=@p WHERE id=@id";
                W.P(cmd, "@p", after); W.P(cmd, "@id", memberId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "INSERT INTO point_flows(member_id,change,reason,balance_after) VALUES(@id,@ch,@rs,@ba)";
                W.P(cmd, "@id", memberId); W.P(cmd, "@ch", -claw); W.P(cmd, "@rs", reason); W.P(cmd, "@ba", after);
                cmd.ExecuteNonQuery();
            }
            return claw;
        }

        private static bool IsBirthdayToday(SQLiteConnection conn, SQLiteTransaction txn, int memberId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT IFNULL(birthday,'') FROM members WHERE id=@id";
                W.P(cmd, "@id", memberId);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return false;
                string b = Convert.ToString(v);
                if (b.Length < 5) return false;
                string md = b.Length >= 10 ? b.Substring(5, 5) : b.Substring(0, 5);   // MM-DD
                return md == DateTime.Now.ToString("MM-dd");
            }
        }

        public int Exchange(int memberId, int giftId, string operatorName)
        {
            using (var conn = Db.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    string giftName; int giftPoints, stock;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT name,points,stock FROM point_gifts WHERE id=@id";
                        W.P(cmd, "@id", giftId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) throw new BizException("礼品不存在");
                            giftName = r.GetString(0); giftPoints = Convert.ToInt32(r.GetValue(1)); stock = Convert.ToInt32(r.GetValue(2));
                        }
                    }
                    if (stock <= 0) throw new BizException("礼品库存不足");
                    int points;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "SELECT points FROM members WHERE id=@id";
                        W.P(cmd, "@id", memberId);
                        object v = cmd.ExecuteScalar();
                        if (v == null) throw new BizException("会员不存在");
                        points = Convert.ToInt32(v);
                    }
                    if (points < giftPoints)
                        throw new BizException("积分不足：当前 " + points + " 分，本次兑换需 " + giftPoints + " 分");
                    int after = points - giftPoints;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = txn;
                        cmd.CommandText = "UPDATE members SET points=@p WHERE id=@id; UPDATE point_gifts SET stock=stock-1 WHERE id=@g;" +
                            " INSERT INTO gift_exchanges(member_id,gift_id,points,operator) VALUES(@id,@g,@pt,@op);" +
                            " INSERT INTO point_flows(member_id,change,reason,balance_after) VALUES(@id,@ch,@rs,@ba)";
                        W.P(cmd, "@p", after); W.P(cmd, "@id", memberId); W.P(cmd, "@g", giftId);
                        W.P(cmd, "@pt", giftPoints); W.P(cmd, "@op", operatorName);
                        W.P(cmd, "@ch", -giftPoints); W.P(cmd, "@rs", "兑换：" + giftName); W.P(cmd, "@ba", after);
                        cmd.ExecuteNonQuery();
                    }
                    W.Log(conn, txn, "积分与营销", "积分兑换", memberId + " 用 " + giftPoints + " 分兑换「" + giftName + "」");
                    txn.Commit();
                    return after;
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

    public class ReportService
    {
        public class Summary
        {
            /// <summary>营收 = 区间内全部正常单据实收合计（消费 + 售卡 + 充值）。
            /// 店主 10-13 定：营收要把会员充值的钱算进去。Recharge/Consume/SellCard
            /// 是它的三个组成部分（三者相加恒等于 Revenue），仍单列供构成图与分项卡使用。</summary>
            public decimal Revenue;
            public decimal Recharge;
            public decimal Consume;
            /// <summary>售卡（项目卡）金额。营收构成三块之一（散单=消费 / 项目=售卡 / 充值），
            /// 由本查询显式统计，前端不拿 Revenue−Consume 倒推（10-08 店主定）。</summary>
            public decimal SellCard;
            public decimal Refund;
            public decimal Avg;
        }

        public Summary RangeSummary(string range)
        {
            string bizDate = W.BizDate();
            string from = bizDate, to = bizDate;
            if (range == "本周")
            {
                DateTime d = DateTime.Parse(bizDate);
                int dow = (int)d.DayOfWeek; if (dow == 0) dow = 7;
                from = d.AddDays(1 - dow).ToString("yyyy-MM-dd");
            }
            else if (range == "本月" || range == "自定义")
            {
                // 店主 10-08 定：「本月」= 自然月整月（1 号 ~ 当月最后一天），不再截到今天。
                // 口径须与 ReportService.RangeSummary 完全一致，否则导出的「统计期间」与真实区间不符。
                from = bizDate.Substring(0, 7) + "-01";
                DateTime fd = DateTime.Parse(from);
                to = fd.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
            }
            var s = new Summary();
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT kind, IFNULL(SUM(receivable),0), COUNT(*) FROM orders" +
                        " WHERE status='正常' AND biz_date BETWEEN @f AND @t GROUP BY kind";
                    W.P(cmd, "@f", from); W.P(cmd, "@t", to);
                    int orders = 0;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string kind = r.GetString(0);
                            decimal amt = Convert.ToDecimal(r.GetValue(1));
                            int cnt = Convert.ToInt32(r.GetValue(2));
                            if (kind == "充值") s.Recharge += amt;
                            // 营收 = 全部 kind 实收（含充值，店主 10-13 定）。
                            // 前端不再自己相加 Revenue+Recharge —— 那样会翻倍。
                            s.Revenue += amt;
                            // 三块营收构成显式统计（散单=消费 / 项目=售卡 / 充值），前端不倒推
                            if (kind == "消费") { s.Consume += amt; orders += cnt; }
                            else if (kind == "售卡") s.SellCard += amt;
                        }
                    }
                    s.Avg = orders > 0 ? Math.Round(s.Consume / orders, 2) : 0;
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(SUM(amount),0) FROM member_flows WHERE type='红冲' AND substr(biz_date,1,10) BETWEEN @f AND @t";
                    W.P(cmd, "@f", from); W.P(cmd, "@t", to);
                    s.Refund = Math.Abs(Convert.ToDecimal(cmd.ExecuteScalar()));
                }
            }
            return s;
        }

        public class DailySummaryResult
        {
            /// <summary>当日营收 = 指定营业日全部正常单据应收合计（消费 + 售卡 + 充值，含充值）。
            /// 与主工作台「今日营收」、日结总营收同一口径。</summary>
            public decimal Revenue;  // 当日营收（指定营业日所有正常单据应收合计）
            public decimal Cash;     // 当日现金（payments.method='现金' 且对应正常单据的现金部分）
        }

        public DailySummaryResult DailySummary(string bizDate)
        {
            var d = new DailySummaryResult();
            using (var conn = Db.OpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(SUM(receivable),0) FROM orders WHERE status='正常' AND biz_date=@t";
                    W.P(cmd, "@t", bizDate);
                    d.Revenue = Convert.ToDecimal(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(SUM(p.amount),0) FROM payments p JOIN orders o ON p.order_id=o.id " +
                        "WHERE p.method='现金' AND o.status='正常' AND o.biz_date=@t";
                    W.P(cmd, "@t", bizDate);
                    d.Cash = Convert.ToDecimal(cmd.ExecuteScalar());
                }
            }
            return d;
        }

        public List<CommissionService.RankRow> Ranks(string month)
        {
            return new CommissionService().Ranks(month);
        }

        /// <summary>
        /// 按员工汇总指定日期的所有改动：服务项目 / 服务金额 / 充值金额 / 售卡金额。
        /// 数据源：orders + order_items + staff，UNION ALL 后按 staff 分组。
        /// 门店视角：谁在店里干了什么、给会员加了多少钱，一张表看清。
        /// </summary>
        public class StaffActivityRow
        {
            public int StaffId;
            public string Name;
            public string Role;
            public int OrdersCount;      // 单据笔数（消费+充值+售卡）
            public int ItemsCount;       // 服务项数（order_items 行数）
            public decimal ConsumeAmount; // 消费单据应收合计
            public int RechargeCount;   // 充值笔数
            public decimal RechargeAmount; // 充值金额合计（按会员余额增加口径，来自 member_flows 的 充值）
            public int SellCardCount;   // 售卡笔数
            public decimal SellCardAmount;
            public decimal TotalAmount;
        }

        public List<StaffActivityRow> StaffActivity(string date)
        {
            if (string.IsNullOrEmpty(date)) date = W.BizDate();
            var list = new List<StaffActivityRow>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 一次查完三类（消费/充值/售卡）的按员工聚合，用 CASE WHEN 分流，只绑一次 @d。
                // 口径说明：
                //   消费 → orders(kind='消费').receivable + order_items 计数
                //   充值 → orders(kind='充值').receivable
                //   售卡 → orders(kind='售卡').receivable
                // 三种都要求 status='正常'，红冲/退款不计入本日业绩。
                cmd.CommandText = @"
SELECT
  o.staff_id,
  s.name AS staff_name,
  s.role AS staff_role,
  COUNT(*)                                          AS order_count,
  SUM(COALESCE(oi.cnt, 0))                         AS items_count,
  SUM(CASE WHEN o.kind='消费' THEN o.receivable ELSE 0 END) AS consume_amount,
  SUM(CASE WHEN o.kind='充值' THEN 1 ELSE 0 END) AS recharge_count,
  SUM(CASE WHEN o.kind='充值' THEN o.receivable ELSE 0 END) AS recharge_amount,
  SUM(CASE WHEN o.kind='售卡' THEN 1 ELSE 0 END) AS sellcard_count,
  SUM(CASE WHEN o.kind='售卡' THEN o.receivable ELSE 0 END) AS sellcard_amount
FROM orders o
JOIN staff s ON s.id = o.staff_id
LEFT JOIN (SELECT order_id, SUM(qty) AS cnt FROM order_items GROUP BY order_id) oi ON oi.order_id = o.id
WHERE o.biz_date=@d AND o.status='正常'
GROUP BY o.staff_id, s.name, s.role
ORDER BY (SUM(CASE WHEN o.kind='消费' THEN o.receivable ELSE 0 END) +
          SUM(CASE WHEN o.kind='充值' THEN o.receivable ELSE 0 END) +
          SUM(CASE WHEN o.kind='售卡' THEN o.receivable ELSE 0 END)) DESC, s.name";
                W.P(cmd, "@d", date);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        decimal cons = Convert.ToDecimal(r.GetValue(5));
                        decimal rec = Convert.ToDecimal(r.GetValue(7));
                        decimal sc = Convert.ToDecimal(r.GetValue(9));
                        list.Add(new StaffActivityRow
                        {
                            StaffId = r.GetInt32(0),
                            Name = r.GetString(1),
                            Role = r.GetString(2),
                            OrdersCount = Convert.ToInt32(r.GetValue(3)),
                            ItemsCount = Convert.ToInt32(r.GetValue(4)),
                            ConsumeAmount = cons,
                            RechargeCount = Convert.ToInt32(r.GetValue(6)),
                            RechargeAmount = rec,
                            SellCardCount = Convert.ToInt32(r.GetValue(8)),
                            SellCardAmount = sc,
                            TotalAmount = Math.Round(cons + rec + sc, 2),
                        });
                    }
                }
            }
            return list;
        }

        public class StaffOrderRow
        {
            public int StaffId;
            public string StaffName;
            public string StaffRole;
            public string Date;       // 营业日 YYYY-MM-dd
            public string Time;       // HH:mm
            public string Category;   // 消费 / 充值 / 售卡
            public string MemberName; // 消费/售卡对应会员姓名；充值订单也带会员
            public string OrderNo;
            public decimal Amount;    // receivable（净营收口径，与报表一致）
        }

        /// <summary>
        /// 按营业日区间查询员工明细单据（店主 09-28 定：报表中心打印用）。
        /// 每笔单据一行；只计 status='正常'；kind 保持原始（消费/充值/售卡）不做合并。
        /// 按员工 → 日期 → 时间 排序，前端按员工分组渲染合并单元格区块。
        /// 区间为空或 from > to 时返回空列表，不报错。
        /// </summary>
        public List<StaffOrderRow> StaffOrdersBetween(string fromDate, string toDate)
        {
            var list = new List<StaffOrderRow>();
            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate)) return list;
            if (fromDate.CompareTo(toDate) > 0) return list;
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
  o.staff_id, s.name, s.role,
  o.biz_date, strftime('%H:%M', o.created_at),
  o.kind,
  COALESCE(m.name, ''),
  o.order_no,
  o.receivable
FROM orders o
JOIN staff s ON s.id = o.staff_id
LEFT JOIN members m ON m.id = o.member_id
WHERE o.biz_date BETWEEN @f AND @t AND o.status='正常'
ORDER BY s.name, o.biz_date, o.created_at";
                W.P(cmd, "@f", fromDate);
                W.P(cmd, "@t", toDate);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StaffOrderRow
                        {
                            StaffId = r.GetInt32(0),
                            StaffName = r.GetString(1),
                            StaffRole = r.GetString(2),
                            Date = r.GetString(3),
                            Time = r.GetString(4),
                            Category = r.GetString(5),
                            MemberName = r.GetString(6),
                            OrderNo = r.GetString(7),
                            Amount = Convert.ToDecimal(r.GetValue(8)),
                        });
                    }
                }
            }
            return list;
        }

        public class StaffDailyPivotRow
        {
            public int StaffId;
            public string StaffName;
            public string StaffRole;
            public string Date;       // 营业日 YYYY-MM-dd
            public string Category;   // 散单 / 会员抵扣 / 次卡 / 会员充值
            public decimal Amount;    // 金额（次卡列为 0）
            public int Count;         // 次数（只有次卡列有值）
        }

        /// <summary>
        /// 员工每日分类透视数据（店主 09-28 定：报表中心导出用透视表格式）。
        /// 分类口径（按收款渠道，与 orders.kind 不同）：
        ///   散单     = 消费订单 + payments.method∈{现金,微信,支付宝} → 金额
        ///   会员抵扣 = 消费订单 + payments.method=会员余额          → 金额
        ///   次卡     = 消费订单 + card_uses 记录数                  → 次数
        ///   会员充值 = 充值订单 + 售卡订单                          → 金额（预付费）
        /// 每个员工每天每个分类一行，已聚合。区间为空或 from > to 时返回空列表。
        /// </summary>
        public List<StaffDailyPivotRow> StaffDailyPivot(string fromDate, string toDate)
        {
            var list = new List<StaffDailyPivotRow>();
            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate)) return list;
            if (fromDate.CompareTo(toDate) > 0) return list;
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT o.staff_id, s.name, s.role, o.biz_date,
  CASE WHEN p.method IN ('现金','微信','支付宝') THEN '散单'
       WHEN p.method = '会员余额' THEN '会员抵扣'
       ELSE '其他' END AS cat,
  ROUND(SUM(p.amount), 2) AS amt,
  COUNT(*) AS cnt
FROM orders o
JOIN staff s ON s.id = o.staff_id
JOIN payments p ON p.order_id = o.id
WHERE o.biz_date BETWEEN @f AND @t AND o.status='正常' AND o.kind='消费'
GROUP BY o.staff_id, s.name, s.role, o.biz_date, cat

UNION ALL

SELECT o.staff_id, s.name, s.role, o.biz_date,
  '次卡' AS cat,
  0 AS amt,
  COUNT(*) AS cnt
FROM orders o
JOIN staff s ON s.id = o.staff_id
JOIN card_uses cu ON cu.order_id = o.id
WHERE o.biz_date BETWEEN @f AND @t AND o.status='正常' AND o.kind='消费'
GROUP BY o.staff_id, s.name, s.role, o.biz_date

UNION ALL

SELECT o.staff_id, s.name, s.role, o.biz_date,
  '会员充值' AS cat,
  ROUND(SUM(o.receivable), 2) AS amt,
  COUNT(*) AS cnt
FROM orders o
JOIN staff s ON s.id = o.staff_id
WHERE o.biz_date BETWEEN @f AND @t AND o.status='正常' AND o.kind IN ('充值','售卡')
GROUP BY o.staff_id, s.name, s.role, o.biz_date
ORDER BY 2, 4, 5";
                W.P(cmd, "@f", fromDate);
                W.P(cmd, "@t", toDate);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StaffDailyPivotRow
                        {
                            StaffId = r.GetInt32(0),
                            StaffName = r.GetString(1),
                            StaffRole = r.GetString(2),
                            Date = r.GetString(3),
                            Category = r.GetString(4),
                            Amount = Convert.ToDecimal(r.GetValue(5)),
                            Count = Convert.ToInt32(r.GetValue(6)),
                        });
                    }
                }
            }
            return list;
        }

        public class ExportResult
        {
            public string Path;
            public string Name;
            public long Size;
            public string Period;
        }

        /// <summary>区间天数（含首尾），异常返回 0。</summary>
        private static int DaysBetween(string f, string t)
        {
            try
            {
                DateTime a = DateTime.Parse(f);
                DateTime b = DateTime.Parse(t);
                int d = (int)(b - a).TotalDays + 1;
                return d < 1 ? 0 : d;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 导出员工业绩透视 Excel（店主 09-28 定：日期对齐透视，所有员工横向并排）。
        /// 每员工占 5 列：散单/会员抵扣/次卡/会员充值/当日业绩；按业绩倒序排员工。
        /// 每日期占 1 行，横向对齐同一天的所有员工数据；底部「全员总结」聚合每位员工，再列全员合计。
        /// 业绩口径（09-28 修订）：业绩 = 散单 + 会员充值；会员抵扣属余额消费（充值时已计业绩），
        /// 再计会与会员充值重复，故不计入业绩，但仍单列展示。员工仅取区间内有单据者（参与接单）。
        /// </summary>
        public ExportResult ExportStaffDetailExcel(string fromDate, string toDate, string operatorName, string saveDir)
        {
            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
                throw new BizException("请先选择导出的起止日期");
            if (fromDate.CompareTo(toDate) > 0)
                throw new BizException("起始日期不能晚于结束日期");

            var rows = StaffDailyPivot(fromDate, toDate);
            int days = DaysBetween(fromDate, toDate);
            const int SUB = 5; // 每个员工的子列数

            // 收集员工 + 按业绩倒序
            var staffMap = new Dictionary<int, StaffInfo>();
            var staffOrder = new List<int>();
            var staffTotals = new Dictionary<int, decimal>();
            foreach (var q in rows)
            {
                if (!staffMap.ContainsKey(q.StaffId))
                {
                    staffMap[q.StaffId] = new StaffInfo { Name = q.StaffName, Role = q.StaffRole };
                    staffOrder.Add(q.StaffId);
                    staffTotals[q.StaffId] = 0;
                }
                // 业绩口径（店主 09-28 定）：业绩 = 散单 + 会员充值。
                // 会员抵扣属余额消费（钱在充值时已计过业绩），再计一遍会与会员充值重复，故不计业绩。
                if (q.Category == "散单" || q.Category == "会员充值")
                    staffTotals[q.StaffId] += q.Amount;
            }
            staffOrder.Sort(delegate(int a, int b) { return staffTotals[b].CompareTo(staffTotals[a]); });

            int staffCount = staffOrder.Count;
            int COLS = 1 + staffCount * SUB; // 日期列 + 每员工 5 列

            // 收集所有日期（升序）
            var daySet = new HashSet<string>();
            foreach (var q in rows) daySet.Add(q.Date);
            var dayKeys = new List<string>(daySet);
            dayKeys.Sort();

            // 索引：(日期,员工ID,分类) → (金额,次数)
            var idx = new Dictionary<string, decimal>();
            var idxCnt = new Dictionary<string, int>();
            foreach (var q in rows)
            {
                string k = q.Date + "|" + q.StaffId + "|" + q.Category;
                decimal av;
                if (idx.TryGetValue(k, out av)) idx[k] = av + q.Amount;
                else idx[k] = q.Amount;
                int cv;
                if (idxCnt.TryGetValue(k, out cv)) idxCnt[k] = cv + q.Count;
                else idxCnt[k] = q.Count;
            }

            var x = new ExcelWriter();
            x.Sheet("员工业绩明细").FreezeHeaders(false);

            // 列宽：日期列稍宽，其余统一
            var widths = new double[COLS];
            widths[0] = 24;   // 日期列，兼作底部合计标签列（容纳「总业绩（散单+会员充值）」等长标签，不折叠）
            for (int i = 1; i < COLS; i++) widths[i] = 13;
            x.Widths(widths);

            // 抬头 3 行
            int r = 1;
            x.Text(r, 1, "收银系统 · 员工业绩明细表", ExcelWriter.S.Header);
            x.Merge(r, 1, r, COLS); r++;
            x.Text(r, 1, "统计范围：" + fromDate + " ~ " + toDate + "（共 " + days + " 天，" + staffCount + " 位员工）", ExcelWriter.S.Plain);
            x.Merge(r, 1, r, COLS); r++;
            x.Text(r, 1, "打印时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "　·　导出人：" + (operatorName ?? ""), ExcelWriter.S.Plain);
            x.Merge(r, 1, r, COLS); r++;

            if (rows.Count == 0 || staffCount == 0)
            {
                r++;
                x.Text(r, 1, "所选时间段暂无单据。", ExcelWriter.S.Plain);
                x.Merge(r, 1, r, COLS);
            }
            else
            {
                r++; // 空行

                // ====== 双层表头 ======
                // 第 1 行表头：日期 | 员工姓名（每员工合并 5 列）
                x.Text(r, 1, "日期", ExcelWriter.S.Header);
                x.Merge(r, 1, r + 1, 1); // 日期跨两行
                for (int i = 0; i < staffCount; i++)
                {
                    int c1 = 2 + i * SUB;
                    int c2 = c1 + SUB - 1;
                var info = staffMap[staffOrder[i]];
                int ci = i % ExcelWriter.FILL_PALETTE.Length;   // 每位员工一个浅色，整列块可区分
                x.Text(r, c1, info.Name + "（" + info.Role + "）", ExcelWriter.S.Header, ci);
                x.Merge(r, c1, r, c2);
                }
                r++;

                // 第 2 行子表头：散单 / 会员抵扣 / 次卡 / 会员充值 / 当日业绩
                string[] subNames = { "散单", "会员抵扣", "次卡", "会员充值", "当日业绩" };
                for (int i = 0; i < staffCount; i++)
                {
                    int c1 = 2 + i * SUB;
                    int ci = i % ExcelWriter.FILL_PALETTE.Length;
                    for (int j = 0; j < SUB; j++)
                        x.Text(r, c1 + j, subNames[j], ExcelWriter.S.Header, ci);
                }
                r++;

                // 每员工累计
                var stCash = new decimal[staffCount];
                var stBal = new decimal[staffCount];
                var stRec = new decimal[staffCount];
                var stCnt = new int[staffCount];

                // ====== 日期数据行 ======
                foreach (string dk in dayKeys)
                {
                    x.Text(r, 1, dk, ExcelWriter.S.Plain);
                    for (int i = 0; i < staffCount; i++)
                    {
                        int sid = staffOrder[i];
                        int c1 = 2 + i * SUB;
                        int ci = i % ExcelWriter.FILL_PALETTE.Length;

                        decimal cash = 0, bal = 0, rec = 0;
                        int cnt = 0;
                        string kCash = dk + "|" + sid + "|散单";
                        string kBal = dk + "|" + sid + "|会员抵扣";
                        string kRec = dk + "|" + sid + "|会员充值";
                        string kCnt = dk + "|" + sid + "|次卡";

                        decimal v;
                        if (idx.TryGetValue(kCash, out v)) cash = v;
                        if (idx.TryGetValue(kBal, out v)) bal = v;
                        if (idx.TryGetValue(kRec, out v)) rec = v;
                        int vv;
                        if (idxCnt.TryGetValue(kCnt, out vv)) cnt = vv;

                        x.Num(r, c1 + 0, cash, ExcelWriter.S.Money, ci);
                        x.Num(r, c1 + 1, bal, ExcelWriter.S.Money, ci);
                        x.Text(r, c1 + 2, cnt + " 次", ExcelWriter.S.Count, ci);
                        x.Num(r, c1 + 3, rec, ExcelWriter.S.Money, ci);
                        x.Num(r, c1 + 4, cash + rec, ExcelWriter.S.MoneyHead, ci);   // 业绩=散单+会员充值

                        stCash[i] += cash;
                        stBal[i] += bal;
                        stRec[i] += rec;
                        stCnt[i] += cnt;
                    }
                    r++;
                }

                // ====== 全员总结行（底部 1 行）======
                decimal gCash = 0, gBal = 0, gRec = 0;
                int gCnt = 0;
                x.Text(r, 1, "全员总结", ExcelWriter.S.MoneyHead);
                for (int i = 0; i < staffCount; i++)
                {
                    int c1 = 2 + i * SUB;
                    int ci = i % ExcelWriter.FILL_PALETTE.Length;
                    x.Num(r, c1 + 0, stCash[i], ExcelWriter.S.MoneyHead, ci);
                    x.Num(r, c1 + 1, stBal[i], ExcelWriter.S.MoneyHead, ci);
                    x.Text(r, c1 + 2, stCnt[i] + " 次", ExcelWriter.S.MoneyHead, ci);
                    x.Num(r, c1 + 3, stRec[i], ExcelWriter.S.MoneyHead, ci);
                    x.Num(r, c1 + 4, stCash[i] + stRec[i], ExcelWriter.S.MoneyHead, ci);   // 业绩=散单+会员充值
                    gCash += stCash[i];
                    gBal += stBal[i];
                    gRec += stRec[i];
                    gCnt += stCnt[i];
                }
                r++;

                // ====== 全员合计明细：每行 1 个指标，标签在 A 列（已加宽到 24，不折叠），数值紧邻 B 列 ======
                x.Text(r, 1, "总业绩（散单+会员充值）", ExcelWriter.S.MoneyHead);
                x.Num(r, 2, gCash + gRec, ExcelWriter.S.MoneyHead);
                r++;

                x.Text(r, 1, "总散单", ExcelWriter.S.Plain);
                x.Num(r, 2, gCash, ExcelWriter.S.MoneyHead);
                r++;

                x.Text(r, 1, "总会员抵扣", ExcelWriter.S.Plain);
                x.Num(r, 2, gBal, ExcelWriter.S.MoneyHead);
                r++;

                x.Text(r, 1, "总耗卡次数", ExcelWriter.S.Plain);
                x.Text(r, 2, gCnt + " 次", ExcelWriter.S.Count);
                r++;

                x.Text(r, 1, "总会员充值", ExcelWriter.S.Plain);
                x.Num(r, 2, gRec, ExcelWriter.S.MoneyHead);
                r++;
            }

            string dir = W.ResolveExportDir(saveDir);   // 统一解析：本次临时 ＞ 全局 export_dir ＞ data\exports
            string name = "业绩表-" + fromDate + "至" + toDate + ".xlsx";
            string path = System.IO.Path.Combine(dir, name);
            long size = x.Save(path);
            Logger.Info("导出业绩表 Excel：" + path + "（" + size + " 字节）操作人 " + operatorName);
            using (var conn = Db.OpenConnection())
                W.Log(conn, null, "报表导出", "导出业绩表", "期间 " + fromDate + " ~ " + toDate + "，文件 " + name);
            return new ExportResult { Path = path, Name = name, Size = size, Period = fromDate + " ~ " + toDate };
        }

        private class StaffInfo
        {
            public string Name;
            public string Role;
        }
    }
}
