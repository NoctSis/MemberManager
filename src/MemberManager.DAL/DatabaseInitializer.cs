using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using MemberManager.Common;

namespace MemberManager.DAL
{
    /// <summary>
    /// 数据库初始化：建目录 → 建表（DDL 原样+变更单+索引）→ 种子数据（仅空库时写入）。
    /// 种子照开发文档 §5.6：账号/角色权限/服务项目/储值方案/卡模板/积分礼品/settings 默认值；
    /// 不预写任何会员、流水、单据（恒等式从零天然成立）。
    /// </summary>
    public static class DatabaseInitializer
    {
        /// <summary>密码哈希：SHA-256（固定盐 + 明文），十六进制小写。</summary>
        public static string HashPassword(string plain)
        {
            const string salt = "MemberManager#2026#Salt";
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + plain));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 全部权限模块（11 个）。与 BLL 的 PermissionService.ModuleNames 保持一致——
        /// DAL 不能引用 BLL（下层引用上层会成环），故在本文件维护副本。
        /// **加模块时两处必须同步**：10-24 曾因种子区硬编码旧 10 个模块，
        /// 导致第 11 个「积分商城」在新库里永远插不进 role_perms。
        /// </summary>
        private static readonly string[] ModuleNamesAll =
        {
            "会员管理", "收银开单", "退款/改价", "储值与次卡", "交接班日结",
            "报表导出", "员工提成", "权限设置", "数据备份", "系统设置", "积分商城"
        };

        /// <summary>幂等初始化。返回种子是否本次写入（用于自检展示）。</summary>
        public static bool EnsureDatabase()
        {
            Db.EnsureDirectories();
            bool seeded;
            using (var conn = Db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                ExecuteNonQuery(conn, tx, Sql.Ddl);
                ExecuteNonQuery(conn, tx, Sql.Indexes);
                PatchSchema(conn, tx);
                // L7（店主 10-18）：老库幂等补「手机号非空唯一」部分索引。
                // 新库 DDL 已有列级 phone UNIQUE（SQLite 允许多个 NULL），这里是给老库的
                // 防御性兜底；建前先查重，存在历史重号则跳过并告警（不卡首启）。
                EnsurePhoneUniqueIndex(conn, tx);
                EnsureLoginNameAndIndex(conn, tx);
                CreateLevelTables(conn, tx);
                CreateStaffPermsTable(conn, tx);
                CreateStaffItemRatesTable(conn, tx);
                MigrateStaffItemRates(conn, tx);
                seeded = SeedIfEmpty(conn, tx);
                // 历史补账必须放在种子之后：否则首启时空库还没写入演示充值流水，
                // 补账看不到任何 order_id IS NULL 的流水就空转，标志位也不落。
                BackfillNewMemberRechargeOrders(conn, tx);
                // 等级制度在补账之后：等级由累计实收充值推导，必须等历史充值流水齐了再算。
                InitMemberLevels(conn, tx);
                // 店主 11-05 定：收银员默认「必须有储值与次卡」——收银台的会员充值、售卡是日常收银动作。
                // 管理动作（充值方案/卡模板增删改、次卡冻结、退款红冲、项目价目维护）仍锁店长：
                //   前端 projects / stored-value 两页 MANAGER_ONLY，后端管理接口另绑「系统设置」模块。
                // 新库由 SeedIfEmpty 的 cashierOn 保证；老库 role_perms 里历史写的是 0，这里抬高。
                // UPDATE 幂等（下次跑无匹配行）；按人显式关掉的 staff_perms=0 不会被本行覆盖。
                ExecuteNonQuery(conn, tx,
                    "UPDATE role_perms SET allowed=1 WHERE role='收银员' AND module='储值与次卡' AND allowed=0");
                // 店主 10-24：老库补「积分商城」模块默认权限（收银员/技师 on、店长 on）。
                // INSERT OR IGNORE 幂等——已有该行（按人调过的角色默认）不覆盖。
                ExecuteNonQuery(conn, tx,
                    "INSERT OR IGNORE INTO role_perms(role,module,allowed) VALUES('店长','积分商城',1)");
                ExecuteNonQuery(conn, tx,
                    "INSERT OR IGNORE INTO role_perms(role,module,allowed) VALUES('收银员','积分商城',1)");
                ExecuteNonQuery(conn, tx,
                    "INSERT OR IGNORE INTO role_perms(role,module,allowed) VALUES('技师','积分商城',1)");
                // ★ 关键补丁（10-24 实测踩坑）：光补 role_perms 不够——
                // staff_perms 有行的账号「以按人为准、缺的模块算 false」，
                // 所以已单独配过权限的账号（如员工「李」）即使角色默认给了积分商城，
                // 侧边栏照样不显示。必须给这些账号补一行 staff_perms 才生效。
                // 取值：店长=1；其余按该角色的默认 allowed（不覆盖已有行）。
                ExecuteNonQuery(conn, tx,
                    "INSERT OR IGNORE INTO staff_perms(staff_id,module,allowed) " +
                    "SELECT s.id,'积分商城', " +
                    "  CASE WHEN s.role='店长' THEN 1 ELSE " +
                    "    (SELECT r.allowed FROM role_perms r WHERE r.role=s.role AND r.module='积分商城') " +
                    "  END " +
                    "FROM staff s " +
                    "WHERE EXISTS (SELECT 1 FROM staff_perms p WHERE p.staff_id=s.id)");
                // ★ 11-05 同款踩坑：staff_perms 有行的账号「按人为准、缺的模块算 false」，
                // 光改 role_perms 对已单独配过权限的老账号不生效。这里只补缺失行：
                // 店长恒 1，其余按角色默认（收银员 1 / 技师 0）；店主显式关掉的 0 原样保留。
                ExecuteNonQuery(conn, tx,
                    "INSERT OR IGNORE INTO staff_perms(staff_id,module,allowed) " +
                    "SELECT s.id,'储值与次卡', " +
                    "  CASE WHEN s.role='店长' THEN 1 ELSE " +
                    "    (SELECT r.allowed FROM role_perms r WHERE r.role=s.role AND r.module='储值与次卡') " +
                    "  END " +
                    "FROM staff s " +
                    "WHERE EXISTS (SELECT 1 FROM staff_perms p WHERE p.staff_id=s.id)");
                // 老库补「收银」账号（店主 10-08 定：收银员属「系统必需」，新装开箱即有）。
                // 种子只在空库跑，10-08 之前建的老库缺这个通用收银账号 → 这里幂等补齐。
                BackfillCashierAccount(conn, tx);
                // ★ 11-05 补：登录名回填必须在「所有插 staff 的地方」之后再跑一次。
                // 上面 SeedIfEmpty（演示数据）与 BackfillCashierAccount（通用收银账号）插行时都不带 login_name，
                // 而 EnsureLoginNameAndIndex 在种子**之前**就执行过了 —— 全新库首启后全员 login_name 为空，
                // 登录页只能靠「LoginName || Name」兜底，两个同名新员工就会撞成同一个登录值。
                // 本方法天然幂等（空才填、重名才加 -id、索引 IF NOT EXISTS），重复调用无副作用。
                EnsureLoginNameAndIndex(conn, tx);
                // M1 / L9 一次性回填（店主 10-14）：挂在种子之后，保证 service_items 已就绪。
                BackfillOrderItemIds(conn, tx);
                BackfillTechRolePerms(conn, tx);
                // 10-18：占位密码 '!nologin!' 归零。老库里用旧版建过的纯员工还留着占位符，
                // 会被 hasPwd / 登录清单（都判 pwd_hash<>''）误判成「可登录」。归零后这些行
                // 变回「无登录账号」，要给登录得走系统设置·账号密码设真密码。天然幂等。
                NormalizeNoLoginPlaceholders(conn, tx);
                tx.Commit();
            }
            return seeded;
        }

        /// <summary>
        /// 老库补「收银」登录账号（店主 10-08 定：收银员属「系统必需」，新装开箱即有两个可用账号）。
        /// SeedIfEmpty 仅在 staff 表为空时执行，所以 10-08 之前建的老库不会自动获得这个通用收银账号。
        /// 策略：若 staff 表里一条 role='收银员' 都没有，才补一个默认收银账号
        /// （收银 / 收银员 / phone=0 / 初始密码 123456 / enabled=1）；已有任何收银员
        /// （含演示库里的具名员工）则不动。纯存在性判断 → 天然幂等，无需标志位，
        /// 重复启动不会重复插入。演示库本身已有收银员 → 本方法对其为 no-op。
        /// </summary>
        private static void BackfillCashierAccount(SQLiteConnection conn, SQLiteTransaction tx)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM staff WHERE role='收银员'";
                if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
            }
            Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                "收银", "收银员", "0", HashPassword("123456"));
        }

        private static void ExecuteNonQuery(SQLiteConnection conn, SQLiteTransaction tx, string batch)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 增量补丁：只做「新增列」，不修改/删除任何既有列，不改写既有数据。
        /// 每条 ALTER 前先查 PRAGMA table_info，列已存在则跳过 —— 幂等，老库可直接升级。
        /// 变更单：
        ///   staff.deleted_at          员工软删除时间（删除后历史单据仍按姓名留痕，流水不跟着删）
        ///   service_items.card_times  服务项目自带次数（0=普通项目，>0=次卡项目）
        ///   service_items.deleted_at  项目软删除（有流水的项目也能下架，历史单据保留）
        ///   member_cards.purchase_times   付费购买次数（不含赠送）
        ///   member_cards.gift_times       赠送次数（不收款，但计入总次数）
        ///   member_cards.discount_amount  本次售卡优惠金额
        ///   member_cards.paid_amount      本次售卡实收金额（= 次数×单价 − 优惠）
        ///   members.created_by            建档经办人 staff.id（员工软删除后按姓名快照仍留痕）
        ///   members.created_by_name       建档经办人姓名快照（员工改名/停用后档案仍能看出是谁开的卡）
        ///   order_items.item_id           挂单行指向的服务项目 id（M1，店主 10-14）：
        ///                               项目改名/撞名不再让历史挂单变死单——取单按 id 匹配，不再按名字 JOIN
        /// 这几列刻意可空：老会员为 NULL，界面显示「历史档案（未记录经办人）」，不做回填。
        /// </summary>
        private static void PatchSchema(SQLiteConnection conn, SQLiteTransaction tx)
        {
            AddColumn(conn, tx, "staff", "deleted_at", "TEXT");
            // 11-04 店主：员工姓名可以重复（两个「李」），但登录必须能区分开——
            // 加独立登录名。回填 = 姓名；重名的追加 -id，见 EnsureLoginNameAndIndex。
            AddColumn(conn, tx, "staff", "login_name", "TEXT");
            AddColumn(conn, tx, "service_items", "card_times", "INTEGER NOT NULL DEFAULT 0");
            AddColumn(conn, tx, "service_items", "deleted_at", "TEXT");
            AddColumn(conn, tx, "member_cards", "purchase_times", "INTEGER");
            AddColumn(conn, tx, "member_cards", "gift_times", "INTEGER");
            AddColumn(conn, tx, "member_cards", "discount_amount", "REAL");
            AddColumn(conn, tx, "member_cards", "paid_amount", "REAL");
            AddColumn(conn, tx, "members", "created_by", "INTEGER");
            AddColumn(conn, tx, "members", "created_by_name", "TEXT");
            // 充值方案软删除（店主 09-27 定）：档位金额 UNIQUE 约束，软删行仍占金额——
            // 与项目/员工删除同口径，界面看不到但数据保留；金额被删后想用回，得先让店主手改库或换新金额。
            AddColumn(conn, tx, "recharge_packages", "deleted_at", "TEXT");
            // 提成改造（店主 10-11）：项目提成方式 + 按次金额。
            AddColumn(conn, tx, "service_items", "commission_mode", "TEXT NOT NULL DEFAULT 'percent'");
            AddColumn(conn, tx, "service_items", "commission_per_count", "REAL NOT NULL DEFAULT 0");
            // 挂单行按 item_id 匹配（M1，店主 10-14）：见 BackfillOrderItemIds 一次性回填。
            AddColumn(conn, tx, "order_items", "item_id", "INTEGER NOT NULL DEFAULT 0");
            // 会员「注销 + 余额核销」（S2，店主 10-15 定）：软删除，不是物理删。
            // members.status 的 CHECK 冻结在 ('正常','冻结','挂失')，加不了「已注销」，
            // 所以注销状态放这三列上：列表默认排除、搜索/导出仍可查、手机号与卡号永久占用。
            AddColumn(conn, tx, "members", "deleted_at", "TEXT");
            AddColumn(conn, tx, "members", "deleted_reason", "TEXT");
            AddColumn(conn, tx, "members", "deleted_by", "TEXT");
            // B1 提成结算可反查（店主 10-21）：哪笔提成何时被结算，落库可查，
            // 不再只靠 op_logs 批次 id 串反推。老库已结行该列为 NULL——历史不回填（口径红线）。
            AddColumn(conn, tx, "commissions", "settled_at", "TEXT");
            // A2 底薪（店主 10-21 定）：默认 0 = 不启用；店里按提成算钱的员工完全无感。
            AddColumn(conn, tx, "staff", "base_salary", "REAL NOT NULL DEFAULT 0");
            // 挂单作废（店主 11-05 定：「清空」= 彻底作废这张挂单，别又放回列表）。
            // orders.status 的 CHECK 冻结在 ('正常','挂单','已退款')，塞不进「作废」，
            // 所以与会员注销同口径：加一列软标记，行永久保留，所有挂单可见点过滤掉。
            // 挂单不写 payments/member_flows/commissions，作废它不碰任何钱。
            AddColumn(conn, tx, "orders", "voided_at", "TEXT");
        }

        /// <summary>
        /// L7（店主 10-18）：老库幂等补「手机号非空唯一」部分索引。
        /// 新库由 DDL 列级 phone UNIQUE 强制（SQLite 列级 UNIQUE 允许多个 NULL，
        /// 即"非空号唯一、空号不限"，与业务口径一致）。10-18 之前建的老库若缺该约束，
        /// 这里补一个部分唯一索引做兜底。
        /// 建前查重：若存在被 2 位以上会员共用的同一非空手机号（历史重号），
        /// 直接建唯一索引会卡住首启，故跳过并告警，留给店主人工去重后再触发。
        /// 幂等：CREATE UNIQUE INDEX IF NOT EXISTS，重号检测每次启动只告警不写。
        /// </summary>
        private static void EnsurePhoneUniqueIndex(SQLiteConnection conn, SQLiteTransaction tx)
        {
            int dup;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT COUNT(DISTINCT phone) FROM (" +
                    " SELECT phone FROM members WHERE phone IS NOT NULL AND phone<>''" +
                    " GROUP BY phone HAVING COUNT(*)>1 )";
                dup = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (dup > 0)
            {
                Logger.Info("L7：检测到 " + dup + " 个非空手机号被多个会员共用，跳过部分唯一索引（需先人工去重）");
                return;
            }
            // 口径与业务写入一致：空号存 NULL（或留空串），不占唯一位；只有非空非空串手机号强制唯一。
            Exec(conn, tx,
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_members_phone_unique ON members(phone)" +
                " WHERE phone IS NOT NULL AND phone<>''");
        }

        /// <summary>
        /// 11-04 店主：员工姓名允许重复（店里就出现过两个「李」），但登录必须唯一可区分。
        /// 原来 Login 按 name 查、只取第一条 —— 同名时永远命中停用的那条，新账号进不去系统。
        ///
        ///   · staff.login_name 独立于 name，登录按 login_name 查；
        ///   · 存量回填 = 姓名；重名的「后到者」追加 -id；
        ///   · 处理顺序 enabled DESC, id ASC —— **启用中的账号优先占住原名**，
        ///     这样店主正在用的登录名不会被历史停用行抢走（否则等于强制改密码登录）；
        ///   · 唯一索引只管非空登录名，空值不占位。
        ///
        /// 幂等：已回填的 login_name 不会被改写（只有空的才填 name）。
        /// </summary>
        private static void EnsureLoginNameAndIndex(SQLiteConnection conn, SQLiteTransaction tx)
        {
            Exec(conn, tx,
                "UPDATE staff SET login_name = name WHERE IFNULL(login_name,'')=''");

            // 逐行看谁撞名，后到者改名。SQL 窗口函数在老 SQLite 上不一定有，这里用 C# 判重更稳。
            var seen = new System.Collections.Generic.HashSet<string>();
            var clashes = new System.Collections.Generic.List<KeyValuePair<int, string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id, IFNULL(login_name,'') FROM staff ORDER BY enabled DESC, id ASC";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int id = r.GetInt32(0);
                        string ln = r.GetString(1);
                        if (string.IsNullOrEmpty(ln)) continue;
                        if (seen.Add(ln)) continue;   // 第一个占住原名
                        clashes.Add(new System.Collections.Generic.KeyValuePair<int, string>(id, ln));
                    }
                }
            }
            foreach (var kv in clashes)
            {
                string unique = kv.Value + "-" + kv.Key;
                Exec(conn, tx, "UPDATE staff SET login_name=@p0 WHERE id=@p1", unique, kv.Key);
                Logger.Info("login_name：「" + kv.Value + "」重名，id=" + kv.Key + " 的登录名改为「" + unique + "」");
            }

            Exec(conn, tx,
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_staff_login_name ON staff(login_name)" +
                " WHERE IFNULL(login_name,'')<>''");
        }

        /// <summary>
        /// M1 一次性回填（店主 10-14）：order_items.item_id 加列后，老行全是 0（未解析），
        /// 取单会一律当成「项目已删」拦住。这里按名字把能解析的老行补上 id：
        ///   · 名字唯一命中 → 绑定该 id，之后项目改名/撞名都不影响历史挂单结账；
        ///   · 名字无匹配（早先手输的临时行 / 项目已删）→ 保持 0，结账时照旧拦截并提示。
        /// 只跑一次（flag order_items_itemid_backfilled）——之后新行都由 InsertOrderItems
        /// 显式写 item_id（自定义行为 0），不会被反复重解析。撞名时子查询取任一匹配，尽力而为。
        /// </summary>
        private static void BackfillOrderItemIds(SQLiteConnection conn, SQLiteTransaction tx)
        {
            if (GetSettingInTx(conn, tx, "order_items_itemid_backfilled", "") == "1") return;
            ExecuteNonQuery(conn, tx,
                "UPDATE order_items SET item_id=COALESCE(" +
                "(SELECT si.id FROM service_items si WHERE si.name=order_items.item_name),0)" +
                " WHERE item_id=0");
            Exec(conn, tx, "INSERT OR REPLACE INTO settings(key,value) VALUES(@p0,@p1)",
                "order_items_itemid_backfilled", "1");
        }

        /// <summary>
        /// L9 一次性回填（店主 10-14）：技师角色权限模板。新库由 SeedIfEmpty 种
        /// （仅「员工提成」开通，其余关）；10-14 之前建的老库 role_perms 里没有技师行 →
        /// 技师账号回落角色默认时全 false，可能连基础页都进不去。这里按存在性判断补齐，
        /// 天然幂等（店主若手工清空技师行，下次启动会再种上——与等级默认档同口径）。
        /// 模块清单与 SeedIfEmpty 的 modules 数组保持一致（两处同步改）。
        /// </summary>
        private static void BackfillTechRolePerms(SQLiteConnection conn, SQLiteTransaction tx)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM role_perms WHERE role='技师'";
                if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
            }
            // 模块清单：DAL 不能引用 BLL（下层引用上层会成环），故在本文件内维护常量数组，
            // 与 BLL PermissionService.ModuleNames 保持一致。加模块时两处都要同步
            // （10-24 曾因只改 BLL 漏改这里，冒烟脚本硬编码 10 也一并打破）。
            string[] modules = ModuleNamesAll;
            foreach (string m in modules)
                Exec(conn, tx, "INSERT OR IGNORE INTO role_perms(role,module,allowed) VALUES('技师',@p0,@p1)",
                    m, m == "员工提成" || m == "积分商城" ? 1 : 0);
        }

        /// <summary>
        /// 10-18：把老库里遗留的占位登录密码 '!nologin!' 归零（纯员工不该带"可登录"标记）。
        /// 旧版 SaveStaff 给不登录的技师写的是 '!nologin!'（非空），而「是否有登录账号」与
        /// 登录页清单都按 pwd_hash&lt;&gt;'' 判定 → 这些纯员工会被误标成可登录、还会漏进下拉。
        /// 归零后它们变回「无登录账号」，要开通登录走「系统设置·账号密码」设真密码（本行不动）。
        /// 天然幂等：UPDATE 只命中占位符行，下次启动无匹配 → 0 行更新。
        /// </summary>
        private static void NormalizeNoLoginPlaceholders(SQLiteConnection conn, SQLiteTransaction tx)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE staff SET pwd_hash='' WHERE pwd_hash='!nologin!'";
                int n = cmd.ExecuteNonQuery();
                if (n > 0)
                    Logger.Info("占位登录密码归零：" + n + " 位纯员工（旧 '!nologin!' → 空）");
            }
        }

        /// <summary>
        /// 会员等级制度表（店主 2026-09-26 新增）。纯增量建表：
        /// 不动 Sql.Ddl 里任何既有表与列，也不改 members.level（仍是自由文本，语义不变）。
        ///   name          等级名，店主可随意改（改名后全体会员按新制度重算）
        ///   min_recharge  累计实收充值达到此金额即达到该等级（赠送不计）
        ///   enabled       停用后不再用于判定，但历史等级名仍保留在 members.level 上
        /// </summary>
        private static void CreateLevelTables(SQLiteConnection conn, SQLiteTransaction tx)
        {
            Exec(conn, tx,
                "CREATE TABLE IF NOT EXISTS member_levels(" +
                " id INTEGER PRIMARY KEY AUTOINCREMENT," +
                " name TEXT NOT NULL UNIQUE," +
                " min_recharge REAL NOT NULL DEFAULT 0 CHECK(min_recharge>=0)," +
                " enabled INTEGER NOT NULL DEFAULT 1," +
                " created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
            Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_member_levels_min ON member_levels(min_recharge, id)");
        }

        /// <summary>
        /// 按账号权限表（店主 2026-09-27 定：权限从「按角色」细化为「按人」）。纯增量建表，不动 Sql.Ddl。
        ///   · 一行 = 某个登录账号的某个模块授权。allowed=1 开通、0 关闭。
        ///   · 表里「有一条及以上记录」即表示该账号已单独配置过，此时以本表为准；
        ///     一条都没有 = 还没单独配过，回落到 role_perms 的「角色默认权限」——老账号零改造升级。
        ///   · 员工软删除后本表残留记录不影响任何东西（MyPerms 只对已登录账号取数）。
        /// </summary>
        private static void CreateStaffPermsTable(SQLiteConnection conn, SQLiteTransaction tx)
        {
            Exec(conn, tx,
                "CREATE TABLE IF NOT EXISTS staff_perms(" +
                " staff_id INTEGER NOT NULL," +
                " module TEXT NOT NULL," +
                " allowed INTEGER NOT NULL DEFAULT 1 CHECK(allowed IN (0,1))," +
                " updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                " PRIMARY KEY(staff_id,module))");
        }

        /// <summary>
        /// 员工逐项提成比例表（店主 10-11 提成改造）。
        ///   · 一行 = 某员工在某「提成项」上的比例/金额。纯增量建表，不动 Sql.Ddl。
        ///   · item_id 含义：0=会员充值；&gt;0=service_items.id（具体项目）；
        ///     -1=通配（仅作回落，迁移旧 staff_rate_&lt;id&gt; 用，UI 不暴露、不可编辑）。
        ///   · value 语义随该提成项的方式而定：充值与百分比项目 = 0~1 小数比例；
        ///     按次项目（service_items.commission_mode='count'）= 单次固定金额（≥0）。
        ///   · 优先级（解析时）：个人(具体项) &gt; 个人(通配 -1, 仅百分比项) &gt; 项目值 &gt; 全局兜底。
        /// </summary>
        private static void CreateStaffItemRatesTable(SQLiteConnection conn, SQLiteTransaction tx)
        {
            Exec(conn, tx,
                "CREATE TABLE IF NOT EXISTS staff_item_rates(" +
                " staff_id INTEGER NOT NULL," +
                " item_id INTEGER NOT NULL," +
                " value REAL NOT NULL DEFAULT 0," +
                " updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                " PRIMARY KEY(staff_id,item_id))");
        }

        /// <summary>
        /// 老库迁移：旧 settings 键 staff_rate_&lt;id&gt;（单一百分比，曾同时覆盖充值与消费）
        /// → 拆成 staff_item_rates 的 (staff_id,0) 充值 + (staff_id,-1) 通配消费，保证升级后
        /// 「个人比例」行为不变；-1 仅作回落值，UI 不暴露。幂等（flag staff_item_rates_migrated）。
        /// 迁移完删除旧键，避免旧读取逻辑（PersonalRate）再命中。
        /// </summary>
        private static void MigrateStaffItemRates(SQLiteConnection conn, SQLiteTransaction tx)
        {
            if (GetSettingInTx(conn, tx, "staff_item_rates_migrated", "") == "1") return;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT key,value FROM settings WHERE key LIKE 'staff_rate_%'";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string key = r.GetString(0);
                        string val = r.GetString(1);
                        string sidStr = key.Length > "staff_rate_".Length ? key.Substring("staff_rate_".Length) : "";
                        int sid;
                        decimal v;
                        if (!int.TryParse(sidStr, out sid) || sid <= 0) continue;
                        if (!decimal.TryParse(val, out v)) continue;
                        Exec(conn, tx, "INSERT OR IGNORE INTO staff_item_rates(staff_id,item_id,value) VALUES(@p0,@p1,@p2)", sid, 0, v);
                        Exec(conn, tx, "INSERT OR IGNORE INTO staff_item_rates(staff_id,item_id,value) VALUES(@p0,@p1,@p2)", sid, -1, v);
                    }
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM settings WHERE key LIKE 'staff_rate_%'";
                cmd.ExecuteNonQuery();
            }
            Exec(conn, tx, "INSERT OR REPLACE INTO settings(key,value) VALUES(@p0,@p1)", "staff_item_rates_migrated", "1");
        }

        /// <summary>
        /// 等级制度初始化 + 全体对齐（店主 2026-09-26 定：等级不手填，按累计实收充值自动判定）。
        ///   · 默认四档只灌一次：用 settings 记标记，店主把等级删光后重启不会又冒回来；
        ///   · 灌完后立即重算全体会员，让老会员（含演示数据）的等级跟制度对齐——
        ///     演示数据里按余额随手填的「金卡/钻石卡」本来就不符合门槛，必须纠正；
        ///   · 重算幂等，值没变就不写，启动时平时是零成本；只记 app 日志，不写 op_logs（无店主操作可追责）。
        /// </summary>
        private static void InitMemberLevels(SQLiteConnection conn, SQLiteTransaction tx)
        {
            if (GetSettingInTx(conn, tx, "member_levels_seeded", "") != "1")
            {
                object[,] lv = {
                    { "普卡", 0m }, { "银卡", 1000m }, { "金卡", 5000m }, { "钻石卡", 10000m }
                };
                for (int i = 0; i < lv.GetLength(0); i++)
                    Exec(conn, tx, "INSERT INTO member_levels(name,min_recharge,enabled) VALUES(@p0,@p1,1)",
                        lv[i, 0], lv[i, 1]);
                Exec(conn, tx, "INSERT OR REPLACE INTO settings(key,value) VALUES(@p0,@p1)",
                    "member_levels_seeded", "1");
            }

            LevelRules.Result r = LevelRules.ApplyAll(conn, tx);
            if (r.Changed > 0)
                Logger.Info("等级制度对齐：按累计实收充值重算了 " + r.Changed + " 位会员的等级");
        }

        /// <summary>
        /// 历史补账：早期「新建会员 + 初始充值」只写了 member_flows（order_id 为空），
        /// 没落 orders，导致这笔充值在「消费记录」（读 orders）里看不到、现金也没计入日结营收。
        /// 这里给这类流水补一张「充值」单 + 现金支付，让历史数据也能查到、账目对齐。
        ///
        /// 约束（务必遵守，踩过坑）：
        ///  · member_flows 有 BEFORE UPDATE 触发器禁止修改 → 不能回填 order_id，
        ///    改用 settings 键 backfilled_newmember_flows 记录已补的流水 id 保证幂等。
        ///  · 全程在 EnsureDatabase 的同一 conn/tx 里执行，直接读写 settings 表，
        ///    绝不走 GetSetting/SetSetting（那会另开连接 → WAL 下 database is locked）。
        /// </summary>
        private static void BackfillNewMemberRechargeOrders(SQLiteConnection conn, SQLiteTransaction tx)
        {
            const string flagKey = "backfilled_newmember_flows";
            // 已补过的流水 id（逗号分隔）
            var done = new System.Collections.Generic.HashSet<string>();
            string stored = GetSettingInTx(conn, tx, flagKey, "");
            if (!string.IsNullOrEmpty(stored))
                foreach (string t in stored.Split(','))
                    if (t.Trim().Length > 0) done.Add(t.Trim());

            // 找所有「充值」且尚无对应单据、且尚未补过的新会员初始充值流水
            var targets = new List<int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id FROM member_flows WHERE type='充值' AND amount>0 AND order_id IS NULL";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        int fid = r.GetInt32(0);
                        if (!done.Contains(fid.ToString())) targets.Add(fid);
                    }
                }
            }
            if (targets.Count == 0) return;

            foreach (int fid in targets)
            {
                int memberId; decimal amount; string bizDate; int? opStaff;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT member_id, amount, biz_date, " +
                        " (SELECT id FROM staff WHERE name=IFNULL(ef.operator,'') ORDER BY id LIMIT 1) " +
                        " FROM member_flows ef WHERE ef.id=@fid";
                    W2P(cmd, "@fid", fid);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) continue;
                        memberId = r.GetInt32(0);
                        amount = Convert.ToDecimal(r.GetValue(1));
                        bizDate = r.IsDBNull(2) ? "" : r.GetString(2);
                        opStaff = r.IsDBNull(3) ? (int?)null : r.GetInt32(3);
                    }
                }
                if (string.IsNullOrEmpty(bizDate)) bizDate = "1970-01-01";

                // 1) 补充值单
                int orderId;
                string orderNo;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    orderNo = NextOrderNoInTx(conn, tx, "C", bizDate);
                    cmd.CommandText = "INSERT INTO orders(order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status)" +
                        " VALUES(@no,@d,@m,@s,@sub,0,@rc,'充值','正常'); SELECT last_insert_rowid();";
                    W2P(cmd, "@no", orderNo); W2P(cmd, "@d", bizDate); W2P(cmd, "@m", memberId);
                    W2P(cmd, "@s", opStaff ?? 1); W2P(cmd, "@sub", amount); W2P(cmd, "@rc", amount);
                    orderId = Convert.ToInt32(cmd.ExecuteScalar());
                }
                // 2) 补现金支付（初始充值默认按现金收款；这是账面口径，便于日结营收对齐）
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO payments(order_id,method,amount) VALUES(@o,'现金',@a)";
                    W2P(cmd, "@o", orderId); W2P(cmd, "@a", amount);
                    cmd.ExecuteNonQuery();
                }
                done.Add(fid.ToString());
            }

            // 记标记，下次启动不再重复补（Exec 按位置绑 @p0/@p1）
            Exec(conn, tx, "INSERT OR REPLACE INTO settings(key,value) VALUES(@p0,@p1)", flagKey, string.Join(",", done));
        }

        private static string GetSettingInTx(SQLiteConnection conn, SQLiteTransaction tx, string key, string fallback)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
                W2P(cmd, "@k", key);
                object v = cmd.ExecuteScalar();
                return (v == null || v == DBNull.Value) ? fallback : Convert.ToString(v);
            }
        }

        private static void W2P(SQLiteCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static string NextOrderNoInTx(SQLiteConnection conn, SQLiteTransaction tx, string prefix, string bizDate)
        {
            string compact = bizDate.Replace("-", "");
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT MAX(CAST(substr(order_no,11) AS INTEGER)) FROM orders WHERE order_no LIKE @p";
                W2P(cmd, "@p", prefix + compact + "-%");
                object v = cmd.ExecuteScalar();
                int next = (v == null || v == DBNull.Value) ? 1 : Convert.ToInt32(v) + 1;
                return prefix + compact + "-" + next.ToString("D2");
            }
        }

        private static void AddColumn(SQLiteConnection conn, SQLiteTransaction tx, string table, string column, string decl)
        {
            if (ColumnExists(conn, tx, table, column)) return;
            Exec(conn, tx, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + decl);
        }

        private static bool ColumnExists(SQLiteConnection conn, SQLiteTransaction tx, string table, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "PRAGMA table_info(" + table + ")";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string c = Convert.ToString(r.GetValue(1));
                        if (string.Equals(c, column, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        private static bool TableIsEmpty(SQLiteConnection conn, SQLiteTransaction tx, string table)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM " + table;
                return Convert.ToInt64(cmd.ExecuteScalar()) == 0L;
            }
        }

        private static void Exec(SQLiteConnection conn, SQLiteTransaction tx, string sql, params object[] args)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                for (int i = 0; i < args.Length; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@p" + i;
                    p.Value = args[i] ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 是否「空库模式」（店主 10-02 定）。判定：exe 同目录存在标记文件 empty-seed.txt。
        /// 用于发行包——首启必须是空库：只种系统必需（店长账号 + 角色权限 + settings），
        /// 跳过全部业务演示数据。开发用 dist\ 不放此文件，保持完整演示数据（冒烟依赖）。
        /// 与 portable.txt 同目录同范式；判定失败一律返回 false（保守走演示种子，绝不误清）。
        /// </summary>
        private static bool EmptySeedMode()
        {
            try
            {
                return System.IO.File.Exists(
                    System.IO.Path.Combine(AppPaths.ExeDir, "empty-seed.txt"));
            }
            catch { return false; }
        }

        private static bool SeedIfEmpty(SQLiteConnection conn, SQLiteTransaction tx)
        {
            bool seeded = false;

            // 发行包「空库模式」（店主 10-02 定：发行包首启必须是空库，会员/项目等一律为空）。
            // exe 同目录放标记文件 empty-seed.txt → 只种「系统必需」（店长登录账号 + 角色权限 +
            // settings 默认值），跳过全部业务演示数据（其余员工 / 服务项目 / 储值方案 / 卡模板 /
            // 积分礼品 / 演示会员）。开发用 dist\ 无此标记 → 保持完整演示数据（冒烟用例依赖）。
            bool emptyMode = EmptySeedMode();

            // ---- 员工账号（初始密码 123456；登录后在「系统设置 · 账号密码」自行修改）----
            if (TableIsEmpty(conn, tx, "staff"))
            {
                string initHash = HashPassword("123456");
                // 店长账号是「系统必需」——没有它无法登录，空库模式也必须种。
                // 默认名不带姓、电话全 0（店主 10-02 定）。
                Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                    "店长", "店长", "0", initHash);
                // 收银账号同为「系统必需」（店主 10-08 定）：新装 / 发行包开箱即有两个可用账号，
                // 前台可直接用收银账号开单，不用先去系统设置里建人。
                // role 必须是角色表里存在的「收银员」——role_perms 有它的行，权限即时生效。
                Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                    "收银", "收银员", "0", initHash);
                if (!emptyMode)
                {
                    // 演示数据一律用明显虚构的姓名 + 138-0000-000x 号段（开源公开版要求）：
                    // 原来用的 139 号段看起来像真实手机号，虽属编造，公开后仍可能被误认。
                    Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                        "演示员工A", "收银员", "13800000002", initHash);
                    Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                        "演示员工B", "收银员", "13800000003", initHash);
                    Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,1)",
                        "演示员工C", "技师", "13800000004", initHash);
                    Exec(conn, tx, "INSERT INTO staff(name,role,phone,pwd_hash,enabled) VALUES(@p0,@p1,@p2,@p3,0)",
                        "演示员工D", "技师", "13800000005", initHash);
                }
                seeded = true;
            }

            // ---- 角色权限矩阵（10 模块 × 3 角色；店长全开锁定）----
            if (TableIsEmpty(conn, tx, "role_perms"))
            {
                // 模块清单一律用 ModuleNamesAll（本文件常量，含全部 11 个模块）。
                // 10-24 踩过：这里曾硬编码 10 个模块，导致第 11 个「积分商城」永远插不进去
                // （cashierOn 里有它也没用，因为 modules 数组里没有）。
                // DAL 不能引用 BLL（下层引用上层成环），故在本文件维护，加模块时两处同步。
                string[] modules = ModuleNamesAll;
                foreach (string m in modules)
                    Exec(conn, tx, "INSERT INTO role_perms(role,module,allowed) VALUES('店长',@p0,1)", m);
                // 店主 11-05 定：收银员默认含「储值与次卡」——收银台会员充值 / 售卡要能用。
                // 该模块名下的管理动作（方案/卡模板增删改、次卡冻结、退款红冲、项目价目维护）
                // 仍由前端 MANAGER_ONLY + 后端接口另绑「系统设置」双重锁死，不随本模块放出去。
                // 与前端 PERM_CASHIER_DEFAULT、后端 CashierDefault 口径一致（三处都要同步）。
                // 店主 10-24：收银员/技师加「积分商城」——客人来兑换时员工能打开商城页面，
                // 但积分规则/礼品增删改在页内按 isManager() 二次门控，员工看不到。
                string[] cashierOn = { "会员管理", "收银开单", "交接班日结", "积分商城", "储值与次卡" };
                foreach (string m in modules)
                    Exec(conn, tx, "INSERT INTO role_perms(role,module,allowed) VALUES('收银员',@p0,@p1)",
                        m, Array.IndexOf(cashierOn, m) >= 0 ? 1 : 0);
                foreach (string m in modules)
                    Exec(conn, tx, "INSERT INTO role_perms(role,module,allowed) VALUES('技师',@p0,@p1)",
                        m, m == "员工提成" || m == "积分商城" ? 1 : 0);
                seeded = true;
            }

            // ---- 服务项目 6 条（演示口径，店主 09-22 定：每样留六）----
            if (!emptyMode && TableIsEmpty(conn, tx, "service_items"))
            {
                object[,] items = {
                    { "洗剪吹", "美发", 38m, "次", 1 }, { "烫发", "美发", 268m, "次", 0 },
                    { "头部护理", "美发", 128m, "次", 1 }, { "面部补水", "美容", 168m, "次", 1 },
                    { "肩颈舒缓", "美容", 198m, "次", 0 }, { "洗发水（500ml）", "商品", 58m, "件", 0 }
                };
                for (int i = 0; i < items.GetLength(0); i++)
                    Exec(conn, tx, "INSERT INTO service_items(name,category,price,unit,is_card_item,enabled,commission_rate) VALUES(@p0,@p1,@p2,@p3,@p4,1,0.05)",
                        items[i, 0], items[i, 1], items[i, 2], items[i, 3], items[i, 4]);
                seeded = true;
            }

            // ---- 储值方案 6 档 ----
            if (!emptyMode && TableIsEmpty(conn, tx, "recharge_packages"))
            {
                decimal[,] pkgs = { { 300m, 0m }, { 500m, 80m }, { 800m, 100m }, { 1000m, 150m }, { 2000m, 400m }, { 3000m, 700m } };
                for (int i = 0; i < pkgs.GetLength(0); i++)
                    Exec(conn, tx, "INSERT INTO recharge_packages(amount,gift,enabled) VALUES(@p0,@p1,@p2)",
                        pkgs[i, 0], pkgs[i, 1], i == 5 ? 0 : 1);
                seeded = true;
            }

            // ---- 卡模板 6 条 ----
            if (!emptyMode && TableIsEmpty(conn, tx, "card_templates"))
            {
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('洗剪吹10次卡','洗剪吹',10,300,12,1)");
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('头部护理8次卡','头部护理',8,800,12,1)");
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('美容护理12次卡','面部补水',12,1680,18,1)");
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('洗剪吹5次卡','洗剪吹',5,168,6,1)");
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('头部护理5次卡','头部护理',5,528,6,1)");
                Exec(conn, tx, "INSERT INTO card_templates(name,item_name,times,price,valid_months,enabled) VALUES('面部补水5次卡','面部补水',5,750,6,0)");
                seeded = true;
            }

            // ---- 积分礼品 6 条 ----
            if (!emptyMode && TableIsEmpty(conn, tx, "point_gifts"))
            {
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('洗发水（500ml）',500,12)");
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('造型发蜡',360,8)");
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('护理精油',880,5)");
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('10元代金券',1000,99)");
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('洗剪吹单次券',300,20)");
                Exec(conn, tx, "INSERT INTO point_gifts(name,points,stock) VALUES('洗剪吹体验券',150,30)");
                seeded = true;
            }

            // ---- 演示会员 6 位（每样留六）：余额与流水一致、次卡 total=remain+核销、积分与流水一致 ----
            if (!emptyMode && TableIsEmpty(conn, tx, "members"))
            {
                string biz = DateTime.Now.ToString("yyyy-MM-dd");
                //            卡号        姓名   手机            等级    余额     积分   状态
                object[,] ms = {
                    { "VIP0001", "演示会员A", "13800000101", "金卡", 1286.50m, 3420, "正常" },
                    { "VIP0002", "演示会员B", "13800000102", "银卡", 356.00m,  640, "正常" },
                    { "VIP0003", "演示会员C", "13800000103", "银卡",  89.90m, 1250, "正常" },
                    { "VIP0004", "演示会员D", "13800000104", "钻石卡", 3260.00m, 8930, "正常" },
                    { "VIP0005", "演示会员E", "13800000105", "普卡",  0m,       210, "挂失" },
                    { "VIP0006", "演示会员F", "13800000106", "金卡",  452.30m, 1830, "正常" }
                };
                for (int i = 0; i < ms.GetLength(0); i++)
                {
                    string tag = i == 0 ? "老客、高消费" : (i == 3 ? "高消费、推荐人" : (i == 5 ? "沉睡" : ""));
                    string bd = new[] { "09-23", "11-02", "09-21", "03-15", "06-30", "12-08" }[i];
                    Exec(conn, tx,
                        "INSERT INTO members(card_no,name,phone,level,tags,balance,points,status,birthday) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)",
                        ms[i, 0], ms[i, 1], ms[i, 2], ms[i, 3], tag, ms[i, 4], ms[i, 5], ms[i, 6], bd);
                    if (Convert.ToDecimal(ms[i, 4]) > 0)
                        Exec(conn, tx,
                            "INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,operator,note) VALUES((SELECT id FROM members WHERE card_no=@p0),@p1,'充值',@p2,@p2,'店长','种子演示：初始充值')",
                            ms[i, 0], biz, ms[i, 4]);
                    int pts = Convert.ToInt32(ms[i, 5]);
                    if (pts > 0)
                        Exec(conn, tx,
                            "INSERT INTO point_flows(member_id,change,reason,balance_after) VALUES((SELECT id FROM members WHERE card_no=@p0),@p1,'种子演示：历史积分',@p1)",
                            ms[i, 0], pts);
                }
                // 次卡：张伟 头部护理8次卡剩2（核销6次留痕）、刘芳 美容护理12次卡剩11（核销1次）、赵丽 洗剪吹10次卡剩3（核销7次）
                Exec(conn, tx,
                    "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status) VALUES((SELECT id FROM members WHERE card_no='VIP0003'),(SELECT id FROM card_templates WHERE name='头部护理8次卡'),8,2,@p0,'正常')",
                    "2026-12-05");
                Exec(conn, tx,
                    "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status) VALUES((SELECT id FROM members WHERE card_no='VIP0004'),(SELECT id FROM card_templates WHERE name='美容护理12次卡'),12,11,@p0,'正常')",
                    "2026-11-02");
                Exec(conn, tx,
                    "INSERT INTO member_cards(member_id,template_id,total,remain,expire_date,status) VALUES((SELECT id FROM members WHERE card_no='VIP0006'),(SELECT id FROM card_templates WHERE name='洗剪吹10次卡'),10,3,@p0,'正常')",
                    "2026-10-30");
                for (int used = 6; used >= 1; used--)
                    Exec(conn, tx,
                        "INSERT INTO card_uses(member_card_id,remain_after,operator) VALUES((SELECT mc.id FROM member_cards mc JOIN members m ON m.id=mc.member_id WHERE m.card_no='VIP0003'),@p0,'店长')",
                        used);
                Exec(conn, tx,
                    "INSERT INTO card_uses(member_card_id,remain_after,operator) VALUES((SELECT mc.id FROM member_cards mc JOIN members m ON m.id=mc.member_id WHERE m.card_no='VIP0004'),@p0,'店长')",
                    12);
                for (int used = 7; used >= 1; used--)
                    Exec(conn, tx,
                        "INSERT INTO card_uses(member_card_id,remain_after,operator) VALUES((SELECT mc.id FROM member_cards mc JOIN members m ON m.id=mc.member_id WHERE m.card_no='VIP0006'),@p0,'店长')",
                        used);
                seeded = true;
            }

            // ---- settings 默认值（不覆盖已存在键）----
            // 只放「有功能在读」的键。这里曾堆积过 7 个无人读的键
            // （shop_address / shop_hours / points_rule_earn / points_rule_birthday /
            //  points_rule_redeem / backup_time / backup_keep）—— 既无 UI 入口、也无任何
            // 逻辑消费，纯给 settings 表灌水。09-29 已清。
            // 约定：新增设置项必须同时接上读点，tools\_settings_check.py 会兜住这个口子。
            string[,] defaults = {
                { "shop_name", "会员软件" }, { "shop_phone", "0" },
                { "day_cut", "02:00" }, { "receipt_enabled", "1" },
                { "receipt_note", "谢谢惠顾，欢迎再次光临" }, { "log_keep_days", "180" },
                { "recharge_commission_rate", "0.03" },
                { "card_commission_mode", "use" },
                // C1 营业时段（10-21 店主定）：开店/收店时间——设置页展示用，
                // 营业日分界仍由 day_cut 决定；收店后录入的单据归下一营业日（既有语义不变）。
                { "open_time", "09:00" }, { "close_time", "22:00" }
            };
            for (int i = 0; i < defaults.GetLength(0); i++)
                Exec(conn, tx, "INSERT OR IGNORE INTO settings(key,value) VALUES(@p0,@p1)", defaults[i, 0], defaults[i, 1]);

            return seeded;
        }

        /// <summary>读取 setting（不存在返回 fallback）。</summary>
        public static string GetSetting(string key, string fallback)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
                var p = cmd.CreateParameter();
                p.ParameterName = "@k";
                p.Value = key;
                cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                return v == null ? fallback : v.ToString();
            }
        }

        /// <summary>
        /// 读 setting（挂到调用方已开启的 conn/txn，不存在返回 fallback）。
        /// 事务内读必须用本重载：另开连接会越过当前事务快照（WAL 下读不到本事务自己的写入，
        /// 且写事务持锁时第二条连接可能死等 → SQLiteException: database is locked）。
        /// </summary>
        public static string GetSetting(SQLiteConnection conn, SQLiteTransaction txn, string key, string fallback)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
                var p = cmd.CreateParameter();
                p.ParameterName = "@k";
                p.Value = key;
                cmd.Parameters.Add(p);
                object v = cmd.ExecuteScalar();
                return v == null ? fallback : v.ToString();
            }
        }

        /// <summary>settings 表 upsert 语句（唯一真源，勿在别处另写一份）。</summary>
        private const string SettingUpsert = "INSERT OR REPLACE INTO settings(key,value) VALUES(@k,@v)";

        /// <summary>写 setting（独立连接，单语句自动提交）。</summary>
        public static void SetSetting(string key, string value)
        {
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SettingUpsert;
                var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; pk.Value = key; cmd.Parameters.Add(pk);
                var pv = cmd.CreateParameter(); pv.ParameterName = "@v"; pv.Value = value; cmd.Parameters.Add(pv);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 写 setting（挂到调用方已开启的 conn/txn）。
        /// 多个 setting 需要原子生效时必须用本重载——不能各自 SetSetting 另开连接：
        /// WAL 下已开启的写事务持有锁，第二条连接会死等 → SQLiteException: database is locked。
        /// </summary>
        public static void SetSetting(SQLiteConnection conn, SQLiteTransaction txn, string key, string value)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = SettingUpsert;
                var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; pk.Value = key; cmd.Parameters.Add(pk);
                var pv = cmd.CreateParameter(); pv.ParameterName = "@v"; pv.Value = value; cmd.Parameters.Add(pv);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
