namespace MemberManager.DAL
{
    /// <summary>
    /// 建表 SQL。结构按《界面交接包》第三章 DDL 原样 + 开发文档 §5.3 变更单 4 处
    /// （店主 2026-09-21 裁决 #2 批准）。执行时包 IF NOT EXISTS 以支持重复启动，字段定义一字不改。
    /// </summary>
    public static class Sql
    {
        public const string Ddl = @"
CREATE TABLE IF NOT EXISTS members(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  card_no TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL, phone TEXT UNIQUE, level TEXT NOT NULL DEFAULT '普卡',
  tags TEXT DEFAULT '',
  balance REAL NOT NULL DEFAULT 0 CHECK(balance IS NOT NULL),
  points INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT '正常' CHECK(status IN('正常','冻结','挂失')),
  birthday TEXT, remark TEXT,
  created_by INTEGER, created_by_name TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);

CREATE TABLE IF NOT EXISTS member_flows(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  member_id INTEGER NOT NULL REFERENCES members(id),
  biz_date TEXT NOT NULL,
  type TEXT NOT NULL CHECK(type IN('充值','赠送','消费','退款','调整','红冲')),
  amount REAL NOT NULL,
  balance_after REAL NOT NULL,
  order_id INTEGER, operator TEXT NOT NULL, note TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TRIGGER IF NOT EXISTS flows_no_update BEFORE UPDATE ON member_flows BEGIN SELECT RAISE(ABORT,'流水禁止修改'); END;
CREATE TRIGGER IF NOT EXISTS flows_no_delete BEFORE DELETE ON member_flows BEGIN SELECT RAISE(ABORT,'流水禁止删除'); END;

CREATE TABLE IF NOT EXISTS service_items(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL, category TEXT NOT NULL,
  price REAL NOT NULL CHECK(price>=0), unit TEXT NOT NULL DEFAULT '次',
  is_card_item INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1,
  commission_rate REAL NOT NULL DEFAULT 0.05
);

CREATE TABLE IF NOT EXISTS orders(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  order_no TEXT NOT NULL UNIQUE,
  biz_date TEXT NOT NULL, member_id INTEGER REFERENCES members(id),
  staff_id INTEGER NOT NULL REFERENCES staff(id),
  subtotal REAL NOT NULL, card_covered REAL NOT NULL DEFAULT 0, receivable REAL NOT NULL,
  kind TEXT NOT NULL DEFAULT '消费' CHECK(kind IN('消费','充值','售卡')),
  status TEXT NOT NULL DEFAULT '正常' CHECK(status IN('正常','挂单','已退款')),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TABLE IF NOT EXISTS order_items(
  id INTEGER PRIMARY KEY AUTOINCREMENT, order_id INTEGER NOT NULL REFERENCES orders(id),
  item_name TEXT NOT NULL, price REAL NOT NULL, qty INTEGER NOT NULL DEFAULT 1,
  by_card INTEGER NOT NULL DEFAULT 0,
  staff_id INTEGER REFERENCES staff(id)
);
CREATE TABLE IF NOT EXISTS payments(
  id INTEGER PRIMARY KEY AUTOINCREMENT, order_id INTEGER NOT NULL REFERENCES orders(id),
  method TEXT NOT NULL CHECK(method IN('会员余额','现金','次卡抵扣','微信','支付宝')),
  amount REAL NOT NULL CHECK(amount>=0)
);

CREATE TABLE IF NOT EXISTS card_templates(
  id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, item_name TEXT NOT NULL,
  times INTEGER NOT NULL CHECK(times>0), price REAL NOT NULL CHECK(price>0),
  valid_months INTEGER NOT NULL DEFAULT 12, enabled INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS member_cards(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  member_id INTEGER NOT NULL REFERENCES members(id), template_id INTEGER NOT NULL REFERENCES card_templates(id),
  total INTEGER NOT NULL, remain INTEGER NOT NULL CHECK(remain>=0),
  expire_date TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT '正常' CHECK(status IN('正常','已用完','已过期','冻结'))
);
CREATE TABLE IF NOT EXISTS card_uses(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  member_card_id INTEGER NOT NULL REFERENCES member_cards(id),
  order_id INTEGER REFERENCES orders(id), remain_after INTEGER NOT NULL,
  operator TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);

CREATE TABLE IF NOT EXISTS point_flows(
  id INTEGER PRIMARY KEY AUTOINCREMENT, member_id INTEGER NOT NULL REFERENCES members(id),
  change INTEGER NOT NULL, reason TEXT NOT NULL, balance_after INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TABLE IF NOT EXISTS point_gifts(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, points INTEGER NOT NULL, stock INTEGER NOT NULL CHECK(stock>=0));
CREATE TABLE IF NOT EXISTS gift_exchanges(id INTEGER PRIMARY KEY AUTOINCREMENT, member_id INTEGER NOT NULL REFERENCES members(id), gift_id INTEGER NOT NULL REFERENCES point_gifts(id), points INTEGER NOT NULL, operator TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')));

CREATE TABLE IF NOT EXISTS recharge_packages(id INTEGER PRIMARY KEY AUTOINCREMENT, amount REAL NOT NULL UNIQUE CHECK(amount>0), gift REAL NOT NULL DEFAULT 0 CHECK(gift>=0), enabled INTEGER NOT NULL DEFAULT 1);

CREATE TABLE IF NOT EXISTS staff(
  id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL,
  role TEXT NOT NULL CHECK(role IN('店长','收银员','技师')),
  phone TEXT, pwd_hash TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS role_perms(role TEXT NOT NULL, module TEXT NOT NULL, allowed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(role,module));
CREATE TABLE IF NOT EXISTS op_logs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  operator TEXT NOT NULL, module TEXT NOT NULL, action TEXT NOT NULL,
  detail TEXT, result TEXT NOT NULL CHECK(result IN('成功','拒绝')),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TRIGGER IF NOT EXISTS logs_no_delete BEFORE DELETE ON op_logs BEGIN SELECT RAISE(ABORT,'日志禁止删除'); END;

CREATE TABLE IF NOT EXISTS daily_settlements(
  id INTEGER PRIMARY KEY AUTOINCREMENT, biz_date TEXT NOT NULL UNIQUE,
  total_revenue REAL NOT NULL, pay_snapshot TEXT NOT NULL,
  cash_system REAL NOT NULL, cash_actual REAL NOT NULL, cash_diff REAL NOT NULL, diff_reason TEXT,
  operator TEXT NOT NULL, note TEXT,
  settled_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TABLE IF NOT EXISTS handover_logs(
  id INTEGER PRIMARY KEY AUTOINCREMENT, from_staff TEXT NOT NULL, to_staff TEXT NOT NULL,
  cash_diff REAL NOT NULL DEFAULT 0, reason TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TABLE IF NOT EXISTS commissions(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  order_id INTEGER NOT NULL REFERENCES orders(id), staff_id INTEGER NOT NULL REFERENCES staff(id),
  type TEXT NOT NULL CHECK(type IN('充值','消费')),
  amount REAL NOT NULL, rate REAL NOT NULL, commission REAL NOT NULL,
  share TEXT NOT NULL DEFAULT '100%', settled INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
";

        /// <summary>开发文档 §5.4 查询索引（DDL 之外补充）。</summary>
        public const string Indexes = @"
CREATE INDEX IF NOT EXISTS idx_members_phone ON members(phone);
CREATE INDEX IF NOT EXISTS idx_members_name ON members(name);
CREATE INDEX IF NOT EXISTS idx_flows_member_date ON member_flows(member_id, biz_date);
CREATE INDEX IF NOT EXISTS idx_orders_bizdate ON orders(biz_date);
CREATE INDEX IF NOT EXISTS idx_orders_member ON orders(member_id);
CREATE INDEX IF NOT EXISTS idx_payments_order ON payments(order_id);
CREATE INDEX IF NOT EXISTS idx_orderitems_order ON order_items(order_id);
CREATE INDEX IF NOT EXISTS idx_carduses_card ON card_uses(member_card_id);
CREATE INDEX IF NOT EXISTS idx_comm_staff ON commissions(staff_id);
CREATE INDEX IF NOT EXISTS idx_comm_settled ON commissions(settled);
CREATE INDEX IF NOT EXISTS idx_oplogs_created ON op_logs(created_at);
CREATE INDEX IF NOT EXISTS idx_giftex_member ON gift_exchanges(member_id);
";
    }
}
