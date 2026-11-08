// 「收银系统」界面类型定义与公共工具
// 数据现全部来自本地数据库（经 JS 桥调用 C# BLL），此文件只保留类型与格式化工具

export interface Member {
  id: number
  cardNo: string
  name: string
  phone: string
  tags: string[]
  balance: number
  points: number
  cardRemain: number // 次卡剩余次数，0 表示无次卡
  cardName: string // 次卡名称
  status: string // 正常 / 冻结 / 挂失
  level: string
  totalRecharge: number // 累计实收充值（赠送不计入），等级完全由它自动判定
  lastVisit: string
  birthday: string
  remark?: string // 备注（编辑档案用；未取到时按空处理）
  createdByName?: string // 建档经办人姓名（历史档案为 undefined → 界面显示「历史档案」）
  deletedAt?: string // 注销时间（空/undefined = 未注销）。注销后列表默认看不到，仅在「已注销」筛选里出现
  deletedReason?: string // 注销原因（店长填写，写入操作日志）
}

export interface MemberFlow {
  date: string
  type: string
  amount: number
  balance: number
  operator: string
  note: string
  /** B2（10-21）：关联单号（消费/退款/红冲流水有；充值/调整类为空） */
  orderNo?: string
}

/* ========== 会员等级制度 ========== */

/**
 * 等级制度定义（店主 2026-09-26 定）：
 * 等级不手工选择，按「累计实收充值」达标自动升档；赠送金额不计入门槛。
 * 制度可改名字、改门槛、启停、删除；一变就全体会员同步重算。
 */
export interface MemberLevel {
  id: number
  name: string
  minRecharge: number // 累计实收充值门槛（元），门槛必须互不相同
  enabled: boolean
  memberCount: number // 当前挂在这个等级名下的会员数
}

export interface ServiceItem {
  id: number
  name: string
  category: string
  price: number
  unit: string
  isCardItem: boolean // 是否次卡项目
  cardTimes?: number // 次卡项目：每做一次核销掉几次（默认 1；非次卡项为 0）
  commissionRate?: number // 项目提成率 0~1（0%~100%），消费结账逐行按它计提；0 不提。老数据默认 0.05
  /** 提成方式：'percent'=按价格百分比（默认）；'count'=按次卡核销次数算固定金额 */
  commissionMode?: 'percent' | 'count'
  /** 按次金额：仅 mode='count' 生效，每做一次（核销一次）提成的固定金额 */
  commissionPerCount?: number
  enabled?: boolean // 项目列表中启停状态（收银页展示恒为启用）
}

export const fmtMoney = (n: number): string =>
  n.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

/* ========== 储值管理 ========== */

export interface RechargePackage {
  id: number
  amount: number
  gift: number
  enabled: boolean
}

export interface RechargeRecord {
  id: number
  memberId?: number
  time: string
  memberName: string
  cardNo: string
  amount: number
  gift: number
  method: string
  operator: string
  status: '正常' | '已退款'
  refundNote?: string
}

/* ========== 次卡 / 期限卡 ========== */

export interface CardTemplate {
  id: number
  name: string
  itemName: string
  times: number
  price: number
  validMonths: number
  enabled: boolean
}

export interface MemberCard {
  id: number
  memberName: string
  cardNo: string
  templateName: string
  total: number
  remain: number
  expireDate: string
  status: '正常' | '已用完' | '已过期' | '冻结'
  purchaseTimes: number
  giftTimes: number
}

export interface CardUseRecord {
  time: string
  memberName: string
  cardName: string
  itemName: string
  remainAfter: number
  operator: string
}

/* ========== 积分与营销 ========== */

export interface PointRecord {
  time: string
  memberName: string
  change: number
  reason: string
  balanceAfter: number
}

export interface PointGift {
  id: number
  name: string
  points: number
  stock: number
}

/* ========== 交接班 / 日结 ========== */

export interface HandoverLog {
  id: number
  time: string
  from: string
  to: string
  cashDiff: number
  status: string
}

/* ========== 员工提成 ========== */

export interface CommissionDetail {
  id: number
  date: string
  orderNo: string
  staff: string
  type: '充值' | '消费'
  amount: number
  rate: number
  commission: number
  share: string
  settled: boolean
}

/* ========== 权限与日志 ========== */

export interface StaffAccount {
  id: number
  name: string
  /** 登录账号（全库唯一）。**姓名可重复，登录认它** —— 两个同名员工靠这一列区分（店主 11-05） */
  loginName?: string
  role: '店长' | '收银员' | '技师'
  phone: string
  enabled: boolean
  /** 个人提成比例（0-1）；-1 表示沿用全局比例 */
  rate?: number
  /** 经手单据数（交易记录） */
  orders?: number
  /** 经手业绩合计（元） */
  amount?: number
  /** 是否已单独配置过权限（true=按人授权已生效，false=沿用角色默认） */
  custom?: boolean
  /** 是否已设登录密码（true=登录账号，false=纯登记员工/技师） */
  hasPwd?: boolean
  /** 逐项提成比例（员工管理矩阵）：itemId 0=充值、>0=项目 id；value 百分比模式为 0~1、按次模式为单次金额 */
  itemRates?: { itemId: number; value: number }[]
  /** A2 底薪（10-21）：默认 0 = 不启用；非 0 时工资应发 = 底薪 + 已结提成 */
  baseSalary?: number
}

export interface OpLog {
  time: string
  operator: string
  module: string
  action: string
  detail: string
  result: '成功' | '拒绝'
}

/* ========== 数据备份 ========== */

export interface BackupVersion {
  id: number
  time: string
  type: '自动' | '手动' | '恢复前快照'
  size: string
  note: string
  fileName?: string
  /** true = 加密备份（.dbk），恢复时需要输入备份密码 */
  encrypted?: boolean
}
