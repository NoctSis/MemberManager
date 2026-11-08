// 后端 API 层：把 C# BLL 返回映射为界面（mock.ts）的既有类型
import { invoke } from '@/lib/bridge'
import { getStaff } from '@/lib/session'
import type {
  Member, ServiceItem, RechargePackage, RechargeRecord, CardTemplate,
  MemberCard, CardUseRecord, PointRecord, PointGift, StaffAccount, OpLog, BackupVersion,
  MemberLevel,
} from '@/data/mock'

/* ---------- 通用 ---------- */

// H3（10-13）：营业日日切点改读 settings.day_cut（如 "02:00"→2），不再硬编码凌晨 2 点。
// 之前设置页改日切点形同虚设：前端 api.ts / DashboardPage 都写死 < 2。
// 这里用模块级缓存 _dayCutHour：默认 2（与历史行为一致，未初始化时不突变），
// 由 initDayCut() 在登录后从 system.status().DayCut 拉一次写入。
// 营业日算法与后端 Fmt.BizDateOf 逐字对齐：当前时刻减 cutHour 再取日期
// （整数小时下等价于「hour < cutHour 则前一天」，但非整数/跨午夜时更稳）。
let _dayCutHour = 2
function parseDayCut(s: string): number {
  const h = parseInt((s || '').split(':')[0] || '', 10)
  return Number.isFinite(h) && h >= 0 && h <= 23 ? h : 2
}
/** 登录后调用一次，把日切点缓存填好；失败保持默认 2，绝不抛给页面。 */
export async function initDayCut(): Promise<void> {
  try {
    const s = await invoke<{ DayCut: string }>('system', 'GetStatus', [])
    _dayCutHour = parseDayCut(s?.DayCut || '02:00')
  } catch {
    _dayCutHour = 2
  }
}
export function getDayCutHour(): number { return _dayCutHour }

export function bizDate(): string {
  const shifted = new Date(Date.now() - _dayCutHour * 3600 * 1000)
  const y = shifted.getFullYear()
  const m = String(shifted.getMonth() + 1).padStart(2, '0')
  const d = String(shifted.getDate()).padStart(2, '0')
  return `${y}-${m}-${d}`
}

const op = (): string => getStaff()?.name ?? '系统'

/* ---------- 登录 ---------- */

export interface StaffInfoDto { Id: number; Name: string; Role: string }

export const auth = {
  // LoginName=登录名（唯一，输这个登录）；Name=真名（可重复，只作显示）
  staffList: (): Promise<{ Id: number; Name: string; LoginName: string; Role: string; Enabled: boolean }[]> =>
    invoke('auth', 'ListEnabledStaff', []),
  login: async (name: string, pwd: string): Promise<{ id: number; name: string; role: string }> => {
    const r = await invoke<StaffInfoDto>('auth', 'LoginEx', [name, pwd])
    return { id: r.Id, name: r.Name, role: r.Role }
  },
  // 后端清 Session 并写一条 op_logs 留痕（店长共享账号时谁登出的可追溯）；前端随后清本地会话对象
  logout: (): Promise<void> => invoke('auth', 'Logout', []),
}

/* ---------- 会员 ---------- */

interface MemberRowDto {
  Id: number; CardNo: string; Name: string; Phone: string; Level: string; Tags: string
  Balance: number; Points: number; Status: string; Birthday: string; Remark: string
  CreatedAt: string; LastVisit: string; CardSummary: string
  CreatedBy?: number; CreatedByName?: string
  TotalRecharge?: number // 累计实收充值（type='充值'，赠送不算）——等级就是按它自动判定的
  DeletedAt?: string; DeletedReason?: string // S2 注销（店主 10-15）
}

function mapMember(r: MemberRowDto): Member {
  const cardRemain = Number(/剩\s*(\d+)\s*次/.exec(r.CardSummary)?.[1] ?? 0)
  return {
    id: r.Id,
    cardNo: r.CardNo,
    name: r.Name,
    phone: r.Phone ?? '',
    tags: (r.Tags ?? '').split('、').filter(Boolean),
    balance: r.Balance,
    points: r.Points,
    cardRemain,
    cardName: cardRemain > 0 ? r.CardSummary.split('·')[0].trim() : '',
    status: r.Status,
    level: r.Level,
    totalRecharge: r.TotalRecharge ?? 0,
    lastVisit: r.LastVisit ?? '',
    birthday: r.Birthday ? r.Birthday.slice(5, 10) : '',
    remark: r.Remark ?? '',
    createdByName: r.CreatedByName ?? '',
    deletedAt: r.DeletedAt ?? '',
    deletedReason: r.DeletedReason ?? '',
  }
}

export const members = {
  search: async (kw: string, status: string, tag: string): Promise<Member[]> => {
    const rows = await invoke<MemberRowDto[]>('members', 'SearchMembers', [
      kw, status === '全部状态' ? '' : status, tag === '全部标签' ? '' : tag, 500,
    ])
    return (rows ?? []).map(mapMember)
  },
  // L2 会员列表分页（店主 10-18 前端批）：按 limit/offset 取单页明细 + 全量统计（Total/BalanceSum/Abnormal 不受翻页影响）。
  // 口径与 search 完全一致（含已注销放行）；total 走后端全量 count，绝不从本页行数推。
  searchPage: async (kw: string, status: string, tag: string, limit: number, offset: number) => {
    const r = await invoke<{ Rows: MemberRowDto[]; Total: number; BalanceSum: number; Abnormal: number; Deleted: number }>(
      'members', 'SearchMembersPage',
      [kw, status === '全部状态' ? '' : status, tag === '全部标签' ? '' : tag, limit, offset],
    )
    return {
      rows: (r?.Rows ?? []).map(mapMember),
      total: r?.Total ?? 0,
      balanceSum: r?.BalanceSum ?? 0,
      abnormal: r?.Abnormal ?? 0,
      deleted: r?.Deleted ?? 0,
    }
  },
  flows: (id: number, offset = 0): Promise<{ Time: string; Type: string; Amount: number; BalanceAfter: number; Operator: string; Note: string; OrderNo: string }[]> =>
    invoke('members', 'LoadMemberFlows', [id, 200, offset]),
  orders: (id: number, offset = 0): Promise<{ OrderNo: string; BizDate: string; Kind: string; Receivable: number; Status: string; PaySummary: string }[]> =>
    invoke('members', 'LoadMemberOrders', [id, 100, offset]),
  check: (id: number): Promise<{ Recharge: number; Gift: number; Consume: number; Refund: number; Adjust: number; Computed: number; Actual: number; Ok: boolean }> =>
    invoke('members', 'GetBalanceCheck', [id]),
  // createdByStaffId=0 → 后端自动回落「当前登录账号」；姓名以后端按员工表取到的快照为准。
  // 不收等级：店主 2026-09-26 定「完全自动，不许手改」——等级由后端按累计实收充值判定后回写。
  create: async (f: { name: string; phone: string; birthday: string; tags: string; remark: string; initAmount: number; initGift?: number; createdByStaffId?: number; createdByName?: string }): Promise<Member> => {
    const r = await invoke<MemberRowDto>('members', 'CreateMember', [
      f.name, f.phone, f.birthday, f.tags, f.remark, f.initAmount, f.initGift ?? 0,
      f.createdByStaffId ?? 0, f.createdByName ?? '',
    ])
    return mapMember(r)
  },
  changeStatus: (id: number, action: string): Promise<void> => invoke('members', 'ChangeStatus', [id, action]),
  // 编辑基本资料（不涉资金）：姓名/手机号/生日/标签/备注。等级不收参——完全自动，不许手改。
  // 后端按字段差异写 op_logs 留痕；手机号唯一校验由后端排除本人后拦截。
  update: async (f: {
    id: number
    name: string
    phone: string
    birthday: string
    tags: string
    remark: string
  }): Promise<void> => {
    await invoke('members', 'UpdateMember', [f.id, f.name, f.phone, f.birthday, f.tags, f.remark])
  },
  tags: (): Promise<string[]> => invoke('members', 'ListTags', []),
  // S2（店主 10-15）余额核销：把剩余余额作坏账抹平（不退现金），写一条负数「调整」流水。
  // 店长专属、原因必填；返回核销掉的金额。
  writeOffBalance: (id: number, reason: string): Promise<number> =>
    invoke('members', 'WriteOffBalance', [id, reason]),
  // S2（店主 10-15）注销会员：软删除，记录全留可查、手机号与卡号不释放。
  // **余额必须先核销为 0**（后端拦截），店长专属、原因必填。
  deleteMember: (id: number, reason: string): Promise<void> =>
    invoke('members', 'DeleteMember', [id, reason]),
}

/* ---------- 会员等级制度 ---------- */

export const levels = {
  /** 制度列表（含停用），按门槛升序；带各级现有会员数 */
  list: async (): Promise<MemberLevel[]> => {
    const rows = await invoke<{ Id: number; Name: string; MinRecharge: number; Enabled: boolean; MemberCount: number }[]>('levels', 'List', [])
    return (rows ?? []).map((r) => ({
      id: r.Id, name: r.Name, minRecharge: r.MinRecharge, enabled: r.Enabled, memberCount: r.MemberCount ?? 0,
    }))
  },
  /** 新增 / 改名 / 改门槛 / 启停。id=0 表示新增。返回等级实际发生变化的会员数 */
  save: (id: number, name: string, minRecharge: number, enabled: boolean): Promise<number> =>
    invoke('levels', 'Save', [id, name, minRecharge, enabled]),
  /** 删除等级（后端保证至少留一个）。返回等级实际发生变化的会员数 */
  remove: (id: number): Promise<number> => invoke('levels', 'Delete', [id]),
}

/**
 * 按制度解析「当前等级 + 下一档」，与后端 LevelRules.Resolve 同口径：
 * 门槛升序遍历，取「启用且门槛 ≤ 累计实收」中门槛最高的一档；一档都不达标则落入门级（门槛最低那档）。
 * 前端只用于展示与建档预览，判定结果一律由后端写库。
 */
export function resolveLevel(all: MemberLevel[], totalRecharge: number): { current: MemberLevel | null; next: MemberLevel | null } {
  if (all.length === 0) return { current: null, next: null }
  const sorted = [...all].sort((a, b) => a.minRecharge - b.minRecharge || a.id - b.id)
  let floor: MemberLevel | null = null
  let best: MemberLevel | null = null
  for (const l of sorted) {
    if (!floor) floor = l
    if (l.enabled && totalRecharge >= l.minRecharge) best = l
  }
  const cur = best ?? floor
  if (!cur) return { current: null, next: null }
  const nxt = sorted.find((l) => l.enabled && l.id !== cur.id && l.minRecharge > cur.minRecharge) ?? null
  return { current: cur, next: nxt }
}

/* ---------- 收银 ---------- */

interface BillingMemberDto { Id: number; CardNo: string; Name: string; Phone: string; Balance: number; Points: number; CardRemain: number }
interface ItemRowDto { Id: number; Name: string; Category: string; Price: number; Unit: string; IsCardItem: boolean }
interface HeldRowDto { Id: number; OrderNo: string; MemberName: string; Amount: number; ItemCount: number; CreatedAt: string; MemberId: number | null; StaffName: string }
interface CardRowDto { Id: number; CardName: string; Remain: number; Total: number; ExpireDate: string; ItemName: string; UnitPrice: number; PurchaseTimes: number; GiftTimes: number }
interface StaffOptionDto { Id: number; Name: string; Role: string; LoginName?: string }
interface TakenDto { OrderId: number; OrderNo: string; MemberId: number | null; StaffId: number; StaffName: string; Lines: { ItemId: number; Qty: number; ByCard: boolean; Name: string; Price: number; Unit: string; IsCardItem: boolean }[] }

export interface PosMember {
  id: number; cardNo: string; name: string; phone: string
  balance: number; points: number; cardRemain: number
}

export interface PosCard {
  id: number; cardName: string; remain: number; total: number; expireDate: string
  itemName: string; unitPrice: number
  purchaseTimes: number; giftTimes: number
}
export interface PosStaffOption {
  id: number; name: string; role: string
  /** 登录账号（全库唯一）。姓名可重复，两个同名员工靠它区分（店主 11-05）。 */
  loginName: string
}

export const pos = {
  items: async (): Promise<ServiceItem[]> => {
    const rows = await invoke<ItemRowDto[]>('pos', 'ListItems', ['全部'])
    return rows.map((r) => ({ id: r.Id, name: r.Name, category: r.Category, price: r.Price, unit: r.Unit, isCardItem: r.IsCardItem }))
  },
  searchMembers: async (kw: string): Promise<PosMember[]> => {
    const rows = await invoke<BillingMemberDto[]>('pos', 'SearchBillingMembers', [kw, 5])
    return rows.map((r) => ({ id: r.Id, cardNo: r.CardNo, name: r.Name, phone: r.Phone, balance: r.Balance, points: r.Points, cardRemain: r.CardRemain }))
  },
  getMember: async (id: number): Promise<PosMember> => {
    const r = await invoke<BillingMemberDto>('pos', 'GetBillingMember', [id])
    return { id: r.Id, cardNo: r.CardNo, name: r.Name, phone: r.Phone, balance: r.Balance, points: r.Points, cardRemain: r.CardRemain }
  },
  // 可用次卡明细：收银台展开看「哪几张卡、各剩几次、何时到期」，不用再翻会员档案
  memberCards: async (memberId: number): Promise<PosCard[]> => {
    const rows = await invoke<CardRowDto[]>('pos', 'ListMemberCards', [memberId])
    return rows.map((r) => ({
      id: r.Id, cardName: r.CardName, remain: r.Remain, total: r.Total,
      expireDate: r.ExpireDate, itemName: r.ItemName, unitPrice: r.UnitPrice,
      purchaseTimes: r.PurchaseTimes ?? r.Total, giftTimes: r.GiftTimes ?? 0,
    }))
  },
  // 可选操作员（启用中的员工）：本次开单是谁经手。
  // 注意：C# 侧 JToken.FromObject 吐 PascalCase(Id/Name/Role)，必须重映射成 camelCase，否则 option 全空。
  staffOptions: async (): Promise<PosStaffOption[]> => {
    const rows = await invoke<StaffOptionDto[]>('pos', 'ListStaffOptions', [])
    return rows.map((r) => ({ id: r.Id, name: r.Name, role: r.Role, loginName: r.LoginName ?? '' }))
  },
  held: async (): Promise<{ id: number; orderNo: string; memberName: string; amount: number; itemCount: number; time: string; staffName: string }[]> => {
    const rows = await invoke<HeldRowDto[]>('pos', 'ListHeldOrders', [])
    return rows.map((r) => ({ id: r.Id, orderNo: r.OrderNo, memberName: r.MemberName, amount: r.Amount, itemCount: r.ItemCount, time: (r.CreatedAt ?? '').slice(11, 16), staffName: r.StaffName ?? '' }))
  },
  hold: async (lines: { itemId: number; qty: number; byCard: boolean }[], memberId: number | null, staffId: number | null): Promise<string> =>
    invoke('pos', 'HoldOrder', [
      lines.map((l) => ({ ItemId: l.itemId, Qty: l.qty, ByCard: l.byCard })),
      memberId,
      staffId,
    ]),
  // 作废挂单（收银台「清空」用在取回来的账单上）；返回原 H 单号，后端只做软标记、不删行
  voidHeld: async (id: number): Promise<string> =>
    invoke('pos', 'VoidHeldOrder', [id]),
  take: async (id: number) => {
    const r = await invoke<TakenDto>('pos', 'TakeOrder', [id])
    return {
      orderId: r.OrderId,
      memberId: r.MemberId,
      staffId: r.StaffId ?? 0,
      staffName: r.StaffName ?? '',
      lines: r.Lines.map((l) => ({
        item: { id: l.ItemId, name: l.Name, category: '美发', price: l.Price, unit: l.Unit, isCardItem: l.IsCardItem } as ServiceItem,
        qty: l.Qty,
        byCard: l.ByCard,
      })),
    }
  },
  checkout: async (lines: { item: ServiceItem; qty: number; byCard: boolean }[], memberId: number | null, payBalance: number, payCash: number, staffId: number | null, heldOrderId: number | null) => {
    const args = lines.map((l) => ({ ItemId: l.item.id, Qty: l.qty, ByCard: l.byCard }))
    return invoke<{ OrderNo: string; Subtotal: number; CardCovered: number; Receivable: number; BalanceUsed: number; BalanceBack: number; CashIn: number; Change: number; CardUsedTimes: number }>(
      'pos', 'Checkout', [args, memberId, payBalance, payCash, heldOrderId, staffId],
    )
  },
  snapshot: (): Promise<{ Revenue: number; Count: number; BalanceRatio: number; RefundPending: number }> =>
    invoke('pos', 'GetShiftSnapshot', [bizDate()]),
}

/* ---------- 工作台 ---------- */

interface MetricsDto { Revenue: number; RevenueCount: number; RevenueYesterday: number; Recharge: number; RechargeCount: number; Consume: number; ConsumeCount: number; NewMembers: number; NewMembersMonth: number }
interface ReminderDto { MemberId: number; Name: string; Sub: string; Balance: number }

export interface DashboardOverview {
  metrics: MetricsDto
  trend: { date: string; revenue: number; recharge: number }[]
  birthday: { id: number; name: string; level: string; balance: number }[]
  sleeping: { id: number; name: string; level: string; balance: number; lastVisit: string }[]
  pendingHeld: number
}

export const dashboard = {
  /** 挂单待取笔数（轻量：单表 COUNT，侧栏角标刷新用它，别为此拉整个 overview）。 */
  countPending: async (): Promise<number> => Number(await invoke<number>('dashboard', 'CountPendingOrders', [])) || 0,
  overview: async (): Promise<DashboardOverview> => {
    const d = bizDate()
    const [metrics, trendRaw, birthday, sleeping, pendingHeld] = await Promise.all([
      invoke<MetricsDto>('dashboard', 'GetTodayMetrics', [d, 2]),
      invoke<{ BizDate: string; Revenue: number; Recharge: number }[]>('dashboard', 'GetTrend', [7, d]),
      invoke<ReminderDto[]>('dashboard', 'GetBirthdayToday', []),
      invoke<ReminderDto[]>('dashboard', 'GetSleepingMembers', [60]),
      invoke<number>('dashboard', 'CountPendingOrders', []),
    ])
    return {
      metrics,
      trend: (trendRaw ?? []).map((t) => ({
        date: t.BizDate.slice(5, 10),
        revenue: t.Revenue,
        recharge: t.Recharge,
      })),
      birthday: (birthday ?? []).map((r) => ({ id: r.MemberId, name: r.Name, level: r.Sub, balance: r.Balance })),
      sleeping: (sleeping ?? []).map((r) => ({
        id: r.MemberId, name: r.Name, level: r.Sub.split(' ')[0] ?? r.Sub, balance: r.Balance, lastVisit: r.Sub,
      })),
      pendingHeld,
    }
  },
  bless: (memberId: number, name: string): Promise<void> => invoke('dashboard', 'RecordReminderTask', ['送祝福', memberId, name]),
  wake: (memberId: number, name: string): Promise<void> => invoke('dashboard', 'RecordReminderTask', ['唤醒', memberId, name]),
}

/* ---------- 储值 ---------- */

/** 导出结果。Count=本次导出行数（两 sheet 合计）；Truncated=触到单次上限，需换时间段再导。 */
export interface ExportResult {
  Path: string
  Name: string
  Size: number
  Period: string
  Count: number
  Truncated: boolean
}

export const storedvalue = {
  packages: async (): Promise<RechargePackage[]> => {
    const rows = await invoke<{ Id: number; Amount: number; Gift: number; Enabled: boolean }[]>('storedvalue', 'Packages', [])
    return rows.map((r) => ({ id: r.Id, amount: r.Amount, gift: r.Gift, enabled: r.Enabled }))
  },
  savePackage: (id: number, amount: number, gift: number, enabled: boolean): Promise<void> =>
    invoke('storedvalue', 'SavePackage', [id, amount, gift, enabled]),
  // 软删除（店主 09-27 定）：写 deleted_at + enabled=0，历史流水不受影响
  deletePackage: (id: number): Promise<void> =>
    invoke('storedvalue', 'DeletePackage', [id]),
  // 分页：limit/offset 供「加载更多」增量拉取；总数走 recordsCount（分页只截明细，总数必须全量算）
  records: async (limit = 100, offset = 0): Promise<RechargeRecord[]> => {
    const rows = await invoke<{ Id: number; MemberId: number; Time: string; MemberName: string; CardNo: string; Amount: number; Gift: number; Method: string; Operator: string; Status: string; RefundNote: string }[]>('storedvalue', 'Records', [limit, offset])
    return rows.map((r) => ({ id: r.Id, memberId: r.MemberId, time: r.Time, memberName: r.MemberName, cardNo: r.CardNo, amount: r.Amount, gift: r.Gift, method: r.Method, operator: r.Operator, status: r.Status === '已退款' ? '已退款' : '正常', refundNote: r.RefundNote ?? undefined }))
  },
  recordsCount: (): Promise<number> => invoke('storedvalue', 'RecordsCount', []),
  metrics: (): Promise<{ TodayRecharge: number; TodayGift: number; MonthRecharge: number; Liability: number }> =>
    invoke('storedvalue', 'Metrics', []),
  recharge: (memberId: number, amount: number, gift: number, method: string): Promise<string> =>
    invoke('storedvalue', 'Recharge', [memberId, amount, gift, method, op()]),
  /** 可指定经手人的充值：operatorStaffId>0 时提成/流水归点名员工（店里常共用账号，登录人≠经单人）。
   *  前端传 op() 作为 operatorName（经手人姓名由后端从 staff 表按 id 取快照，防伪造） */
  rechargeByStaff: (memberId: number, amount: number, gift: number, method: string, operatorStaffId: number): Promise<string> =>
    invoke('storedvalue', 'RechargeByStaff', [memberId, amount, gift, method, op(), operatorStaffId]),
  refund: (memberId: number, amount: number, gift: number, reason: string): Promise<void> =>
    invoke('storedvalue', 'Refund', [memberId, amount, gift, reason, op()]),
  // 导出真 .xlsx（走 ExcelWriter，与营业报表同构）；saveDir 为本次临时目录（空=用全局默认 export_dir）
  exportRecords: (saveDir?: string): Promise<ExportResult> =>
    invoke('storedvalue', 'ExportRecords', [op(), saveDir || '']),
  // M9（店主 10-15）：按时间段分段导出——单次每类最多 5000 行，触限 Truncated=true，换时间段再导即可。
  // fromDate / toDate 为空串表示该端不限（两头都空=全量）。
  exportRecordsRange: (fromDate: string, toDate: string, saveDir?: string): Promise<ExportResult> =>
    invoke('storedvalue', 'ExportRecordsRange', [op(), saveDir || '', fromDate || '', toDate || '']),
}

/* ---------- 次卡 ---------- */

export const cards = {
  templates: async (): Promise<CardTemplate[]> => {
    const rows = await invoke<{ Id: number; Name: string; ItemName: string; Times: number; Price: number; ValidMonths: number; Enabled: boolean }[]>('cards', 'Templates', [])
    return rows.map((r) => ({ id: r.Id, name: r.Name, itemName: r.ItemName, times: r.Times, price: r.Price, validMonths: r.ValidMonths, enabled: r.Enabled }))
  },
  saveTemplate: (id: number, name: string, itemName: string, times: number, price: number, validMonths: number, enabled: boolean): Promise<void> =>
    invoke('cards', 'SaveTemplate', [id, name, itemName, times, price, validMonths, enabled]),
  memberCards: async (): Promise<MemberCard[]> => {
    const rows = await invoke<{ Id: number; MemberName: string; CardNo: string; TemplateName: string; Total: number; Remain: number; ExpireDate: string; Status: string; PurchaseTimes: number; GiftTimes: number }[]>('cards', 'MemberCards', [])
    return rows.map((r) => ({ id: r.Id, memberName: r.MemberName, cardNo: r.CardNo, templateName: r.TemplateName, total: r.Total, remain: r.Remain, expireDate: r.ExpireDate, status: r.Status as MemberCard['status'], purchaseTimes: r.PurchaseTimes ?? r.Total, giftTimes: r.GiftTimes ?? 0 }))
  },
  // 分页：limit/offset 供「加载更多」增量拉取；总数走 useRecordsCount
  useRecords: async (limit = 50, offset = 0): Promise<CardUseRecord[]> => {
    const rows = await invoke<{ Time: string; MemberName: string; CardName: string; ItemName: string; RemainAfter: number; Operator: string }[]>('cards', 'UseRecords', [limit, offset])
    return rows.map((r) => ({ time: r.Time, memberName: r.MemberName, cardName: r.CardName, itemName: r.ItemName, remainAfter: r.RemainAfter, operator: r.Operator }))
  },
  useRecordsCount: (): Promise<number> => invoke('cards', 'UseRecordsCount', []),
  // 指定月份（yyyy-MM）的次卡核销次数 —— 「本月消耗次数」（店主 10-08）
  useRecordsCountByMonth: (month: string): Promise<number> => invoke('cards', 'UseRecordsCountByMonth', [month]),
  // 售卡：operatorStaffId = 点名经手人（0/缺省 → 后端回落登录账号）。
  // 店里常共用一个账号登录，登录人≠经单人，所以必须显式传经手人，后端按 id 取姓名快照落库。
  sellCard: (memberId: number, templateId: number, method: string, times: number, unitPrice: number, giftTimes: number, discount: number, operatorStaffId: number): Promise<string> =>
    invoke('cards', 'SellCard', [memberId, templateId, method, op(), times, unitPrice, giftTimes, discount, operatorStaffId]),
  setCardStatus: (cardId: number, action: '冻结' | '解冻'): Promise<void> => invoke('cards', 'SetCardStatus', [cardId, action]),
}

/* ---------- 积分 ---------- */

export const points = {
  gifts: async (): Promise<PointGift[]> => {
    const rows = await invoke<{ Id: number; Name: string; Points: number; Stock: number }[]>('points', 'Gifts', [])
    return rows.map((r) => ({ id: r.Id, name: r.Name, points: r.Points, stock: r.Stock }))
  },
  // 分页：limit/offset 供「加载更多」增量拉取；总数走 recordsCount
  records: async (limit = 100, offset = 0): Promise<PointRecord[]> => {
    const rows = await invoke<{ Time: string; MemberName: string; Change: number; Reason: string; BalanceAfter: number }[]>('points', 'Records', [limit, offset])
    return rows.map((r) => ({ time: r.Time, memberName: r.MemberName, change: r.Change, reason: r.Reason, balanceAfter: r.BalanceAfter }))
  },
  recordsCount: (): Promise<number> => invoke('points', 'RecordsCount', []),
  rules: (): Promise<{ Earn: string; Birthday: string; Redeem: string; Valid: string; PerYuan: number; BirthdayMult: number; PointsPerYuan: number; ValidMonths: number; Enabled: boolean; EarnConsume: boolean; EarnRecharge: boolean }> => invoke('points', 'GetRules', []),
  savePointConfig: (perYuan: number, birthdayMult: number, pointsPerYuan: number, validMonths: number, enabled: boolean, earnConsume: boolean, earnRecharge: boolean): Promise<void> => invoke('points', 'SavePointConfig', [perYuan, birthdayMult, pointsPerYuan, validMonths, enabled, earnConsume, earnRecharge, op()]),
  exchange: (memberId: number, giftId: number): Promise<number> => invoke('points', 'Exchange', [memberId, giftId, op()]),
  /** 新增/编辑礼品（id=0 新增）。店主 10-24：原先礼品只有种子数据，无法自己加。 */
  saveGift: (id: number, name: string, points: number, stock: number): Promise<void> =>
    invoke('points', 'SaveGift', [id, name, points, stock, op()]),
  /** 删除礼品：无兑换历史的直接删；有历史的改为「已下架」（库存 0、改名），保住历史记录。 */
  deleteGift: (id: number): Promise<void> => invoke('points', 'DeleteGift', [id, op()]),
  /** 手工调整积分（正数加、负数扣）。默认仍按规则自动给分，这是补充手段。 */
  adjustPoints: (memberId: number, change: number, reason: string): Promise<number> =>
    invoke('points', 'AdjustPoints', [memberId, change, reason, op()]),
  /** 兑换记录台账 */
  exchanges: async (limit = 100, offset = 0): Promise<{ id: number; time: string; memberName: string; giftName: string; points: number; operator: string }[]> => {
    const rows = await invoke<{ Id: number; Time: string; MemberName: string; GiftName: string; Points: number; Operator: string }[]>('points', 'Exchanges', [limit, offset])
    return rows.map((r) => ({ id: r.Id, time: r.Time, memberName: r.MemberName, giftName: r.GiftName, points: r.Points, operator: r.Operator }))
  },
  exchangesCount: (): Promise<number> => invoke('points', 'ExchangesCount', []),
}

/* ---------- 提成 ---------- */

export const commission = {
  details: async (month: string): Promise<{ id: number; date: string; orderNo: string; staff: string; staffId: number; type: '充值' | '消费' | '售卡'; kind: string; amount: number; rate: number; commission: number; share: string; settled: boolean }[]> => {
    const rows = await invoke<{ Id: number; Date: string; OrderNo: string; Staff: string; StaffId: number; Type: string; Kind: string; Amount: number; Rate: number; Commission: number; Share: string; Settled: boolean }[]>('commission', 'Details', [month, 200])
    return rows.map((r) => ({ id: r.Id, date: r.Date, orderNo: r.OrderNo, staff: r.Staff, staffId: r.StaffId, type: r.Type as '充值' | '消费' | '售卡', kind: r.Kind ?? '', amount: r.Amount, rate: r.Rate, commission: r.Commission, share: r.Share, settled: r.Settled }))
  },
  ranks: async (month: string): Promise<{ name: string; rechargeAmount: number; consumeAmount: number; commission: number; orders: number; settled: number; unsettled: number }[]> => {
    const rows = await invoke<{ Name: string; RechargeAmount: number; ConsumeAmount: number; Commission: number; Orders: number; Settled: number; Unsettled: number }[]>('commission', 'Ranks', [month])
    return rows.map((r) => ({ name: r.Name, rechargeAmount: r.RechargeAmount, consumeAmount: r.ConsumeAmount, commission: r.Commission, orders: r.Orders, settled: r.Settled, unsettled: r.Unsettled }))
  },
  /** 按项目看业绩/提成（店主 10-10）：当月各在售项目做了多少业绩、估算提成多少。提成是估算（项目率），结算实际以个人/项目比例为准。 */
  projectBreakdown: async (month: string): Promise<{ item: string; unit: string; qty: number; perf: number; commission: number; rate: number; mode: string; perCount: number; orders: number; onSale: boolean }[]> => {
    const rows = await invoke<{ Item: string; Unit: string; Qty: number; Perf: number; Commission: number; Rate: number; Mode: string; PerCount: number; Orders: number; OnSale: boolean }[]>('commission', 'ProjectBreakdown', [month])
    return rows.map((r) => ({ item: r.Item, unit: r.Unit, qty: r.Qty, perf: r.Perf, commission: r.Commission, rate: r.Rate, mode: r.Mode === 'count' ? 'count' : 'percent', perCount: r.PerCount ?? 0, orders: r.Orders, onSale: r.OnSale }))
  },
  /** 按员工汇总某月提成（T6 对账面板主表）。一行一人，业绩与未结/已结金额分列。 */
  staffSummary: async (month: string): Promise<{ staffId: number; name: string; rechargeAmount: number; consumeAmount: number; unsettledCount: number; unsettledAmount: number; settledAmount: number; totalOrders: number }[]> => {
    const rows = await invoke<{ StaffId: number; Name: string; RechargeAmount: number; ConsumeAmount: number; UnsettledCount: number; UnsettledAmount: number; SettledAmount: number; TotalOrders: number }[]>('commission', 'StaffSummary', [month])
    return rows.map((r) => ({ staffId: r.StaffId, name: r.Name, rechargeAmount: r.RechargeAmount, consumeAmount: r.ConsumeAmount, unsettledCount: r.UnsettledCount, unsettledAmount: r.UnsettledAmount, settledAmount: r.SettledAmount, totalOrders: r.TotalOrders }))
  },
  /** 按员工结算（T6）：只结指定员工，不碰他人。actual < 0 表示不登记实付差异（实付按应结）。 */
  settleByStaff: (month: string, staffIds: string, actual: number, reason: string): Promise<{ Count: number; Amount: number; Actual: number; Diff: number; Period: string }> =>
    invoke('commission', 'SettleByStaff', [month, staffIds, actual, reason, op()]),
  /** 结算批次历史（T6）：从 op_logs 读「结算提成」记录 */
  settleHistory: async (limit: number): Promise<{ id: number; time: string; operator: string; period: string; staffCount: number; count: number; amount: number; actual: number; diff: number; reason: string; staffNames: string }[]> => {
    const rows = await invoke<{ Id: number; Time: string; Operator: string; Period: string; StaffCount: number; Count: number; Amount: number; Actual: number; Diff: number; Reason: string; StaffNames: string }[]>('commission', 'SettleHistory', [limit])
    return rows.map((r) => ({ id: r.Id, time: r.Time, operator: r.Operator, period: r.Period, staffCount: r.StaffCount, count: r.Count, amount: r.Amount, actual: r.Actual, diff: r.Diff, reason: r.Reason, staffNames: r.StaffNames }))
  },
  settleAll: (month: string): Promise<void> => invoke('commission', 'SettleAll', [month, op()]),
  rates: (): Promise<number[]> => invoke('commission', 'GetRates', []),
  saveRates: (r: number, c: number): Promise<void> => invoke('commission', 'SaveRates', [r, c, op()]),
  // 次卡提成结算方式（M11，店主 10-14）：sale=售卡时全算 / use=核销时全算 / split=两段各半
  cardMode: (): Promise<string> => invoke('commission', 'GetCardCommissionMode', []),
  saveCardMode: (mode: string): Promise<void> => invoke('commission', 'SaveCardCommissionMode', [mode, op()]),
  /** 读取某员工的逐项提成比例（员工管理矩阵回填）。 */
  getStaffItemRates: (staffId: number): Promise<{ staffId: number; itemId: number; value: number; updatedAt: string }[]> =>
    invoke('commission', 'GetStaffItemRates', [staffId]),
  /** 保存某员工的逐项提成比例（员工管理矩阵）。rows: {itemId, value}，value<=0 表示清除该项个人配置。 */
  saveStaffItemRates: (staffId: number, rows: { itemId: number; value: number }[]): Promise<void> =>
    invoke('commission', 'SaveStaffItemRates', [staffId, rows, op()]),
  /** 导出某月提成 Excel（明细 + 排名两表）。saveDir 为本次临时目录（空=全局默认 export_dir） */
  exportExcel: (month: string, saveDir?: string): Promise<{ Path: string; Name: string; Size: number; Period: string }> =>
    invoke('commission', 'ExportCommissionExcel', [month, op(), saveDir || '']),
}

/* ---------- 权限与日志 ---------- */

export const perm = {
  staffList: async (): Promise<StaffAccount[]> => {
    const rows = await invoke<{ Id: number; Name: string; LoginName: string; Role: string; Phone: string; Enabled: boolean; Rate: number; Orders: number; Amount: number; HasPwd: boolean; Custom: boolean; BaseSalary: number }[]>('perm', 'StaffList', [])
    return rows.map((r) => ({
      id: r.Id,
      name: r.Name,
      loginName: r.LoginName ?? '',
      role: r.Role as StaffAccount['role'],
      phone: r.Phone ?? '',
      enabled: r.Enabled,
      custom: r.Custom ?? false,
      rate: r.Rate ?? -1,
      orders: r.Orders ?? 0,
      amount: r.Amount ?? 0,
      hasPwd: r.HasPwd ?? false,
      baseSalary: r.BaseSalary ?? 0,
    }))
  },
  /** 新增/编辑员工。rate < 0 表示清除个人比例、沿用全局比例；baseSalary 默认 -1 = 不动底薪列 */
  saveStaff: (id: number, name: string, role: string, phone: string, rate: number, enabled: boolean, baseSalary: number = -1): Promise<void> =>
    invoke('perm', 'SaveStaff', [id, name, role, phone, rate, enabled, op(), baseSalary]),
  deleteStaff: (id: number): Promise<void> => invoke('perm', 'DeleteStaff', [id, op()]),
  /** 新增/编辑登录账号。pwd 留空表示不修改密码。返回账号 id（用于紧接着落按人权限） */
  saveAccount: (id: number, name: string, role: string, phone: string, pwd: string, enabled: boolean): Promise<number> =>
    invoke('perm', 'SaveAccount', [id, name, role, phone, pwd, enabled, op()]),
  /**
   * 某账号的按人权限（设置页「权限」弹窗用）。
   * Allowed[模块名] = 生效权限：staff_perms 有记录则按人，否则回落该角色默认。
   */
  accountPerms: (id: number): Promise<{ StaffId: number; Name: string; Role: string; Custom: boolean; Modules: string[]; Allowed: Record<string, boolean> }> =>
    invoke('perm', 'AccountPerms', [id]),
  /** 覆盖式设置某账号的权限：modules = 已开通的模块名，其余模块自动落 false */
  setAccountPerms: (id: number, modules: string[]): Promise<void> =>
    invoke('perm', 'SetAccountPerms', [id, modules, op()]),
  /** 当前登录账号的生效权限模块名（登录时拉一次，供 canModule 按人门控） */
  myPerms: (): Promise<string[]> => invoke('perm', 'MyPerms', []),
  toggleStaff: (id: number): Promise<void> => invoke('perm', 'ToggleStaff', [id, op()]),
  resetPwd: (id: number): Promise<void> => invoke('perm', 'ResetPwd', [id, op()]),
  logs: async (kw: string): Promise<OpLog[]> => {
    const rows = await invoke<{ Time: string; Operator: string; Module: string; Action: string; Detail: string; Result: string }[]>('perm', 'Logs', [kw, 200])
    return rows.map((r) => ({ time: r.Time, operator: r.Operator, module: r.Module, action: r.Action, detail: r.Detail, result: r.Result as OpLog['result'] }))
  },
}

/* ---------- 备份 ---------- */

export const backup = {
  list: async (): Promise<BackupVersion[]> => {
    const rows = await invoke<{ Id: number; Time: string; Type: string; Size: string; Note: string; FileName: string; Encrypted: boolean }[]>('backup', 'List', [])
    return rows.map((r) => ({ id: r.Id, time: r.Time, type: r.Type as BackupVersion['type'], size: r.Size, note: r.Note, fileName: r.FileName, encrypted: !!r.Encrypted })) as BackupVersion[]
  },
  backupNow: (manual: boolean): Promise<string> => invoke('backup', 'BackupNow', [manual, op()]),
  /** 恢复备份；加密备份（.dbk）必须带密码，明文备份传空串 */
  restore: (fileName: string, password = ''): Promise<string> => invoke('backup', 'Restore', [fileName, op(), password]),
  // D4: delete a backup version (never touches the live database)
  remove: (fileName: string): Promise<void> => invoke('backup', 'Delete', [fileName, op()]),
  // D3: daily auto backup config + tick
  getAuto: async (): Promise<{ enabled: boolean; hour: number; lastDate: string; today: string }> => {
    const r = await invoke<{ Enabled: boolean; Hour: number; LastDate: string; Today: string }>('backup', 'GetAuto', [])
    return { enabled: r.Enabled, hour: r.Hour, lastDate: r.LastDate ?? '', today: r.Today ?? '' }
  },
  setAuto: (enabled: boolean, hour: number): Promise<void> => invoke('backup', 'SetAuto', [enabled, hour, op()]),
  autoTick: (): Promise<string> => invoke('backup', 'AutoTick', [op()]),

  /* --- 备份加密（店主 11-08） --- */

  /** 加密状态：是否开启、有没有设过密码、密码提示、加密/明文备份各有几份 */
  encryptStatus: async (): Promise<{ enabled: boolean; hasPassword: boolean; hint: string; backupCount: number; plainCount: number }> => {
    const r = await invoke<{ Enabled: boolean; HasPassword: boolean; Hint: string; BackupCount: number; PlainCount: number }>('backup', 'EncryptStatus', [])
    return { enabled: !!r.Enabled, hasPassword: !!r.HasPassword, hint: r.Hint ?? '', backupCount: r.BackupCount || 0, plainCount: r.PlainCount || 0 }
  },
  /** 设置 / 修改备份密码（已开启时要带旧密码） */
  setPassword: (oldPassword: string, newPassword: string, hint: string): Promise<void> =>
    invoke('backup', 'SetPassword', [oldPassword, newPassword, hint, op()]),
  /** 关闭加密（要当前密码）；已有加密备份不受影响 */
  disableEncrypt: (password: string): Promise<void> => invoke('backup', 'DisableEncrypt', [password, op()]),
  verifyPassword: (password: string): Promise<boolean> => invoke('backup', 'VerifyBackupPassword', [password]),

  /* --- 导出 / 导入（店主 11-05） --- */

  /** 当前库所有表（含中文名、分组、行数、是否默认勾选） */
  tables: async (): Promise<BackupTable[]> => {
    const rows = await invoke<{ Name: string; Label: string; Group: string; Count: number; DefaultOn: boolean }[]>('backup', 'Tables', [])
    return rows.map((r) => ({ name: r.Name, label: r.Label, group: r.Group, count: r.Count, defaultOn: r.DefaultOn }))
  },
  /** 按勾选导出为 .db；返回实际导出的表数与自动补上的依赖表 */
  exportSelected: async (tables: string[], targetPath: string): Promise<BackupExportResult> => {
    const r = await invoke<{ FileName: string; SizeKb: number; TableCount: number; TotalTables: number; Added: string[] }>(
      'backup', 'ExportSelected', [tables, targetPath, op()],
    )
    return { fileName: r.FileName, sizeKb: r.SizeKb, tableCount: r.TableCount, totalTables: r.TotalTables, added: r.Added ?? [] }
  },
  /** 自动识别一个 .db：能不能导入、是不是完整备份、里面有多少数据 */
  inspect: async (path: string): Promise<BackupInspect> => {
    const r = await invoke<{
      IsThisSystem: boolean; IsFull: boolean; FileName: string; SizeKb: number
      TableCount: number; MemberCount: number; OrderCount: number
      FileTime: string; Missing: string[]; Error: string
    }>('backup', 'Inspect', [path])
    return {
      isThisSystem: r.IsThisSystem, isFull: r.IsFull, fileName: r.FileName, sizeKb: r.SizeKb,
      tableCount: r.TableCount, memberCount: r.MemberCount, orderCount: r.OrderCount,
      fileTime: r.FileTime ?? '', missing: r.Missing ?? [], error: r.Error ?? '',
    }
  },
  /** 把备份列表里勾中的版本原样另存到指定文件夹（可多选）；返回实际导出的文件名 */
  exportVersions: async (fileNames: string[], targetDir: string): Promise<{ count: number; dir: string; files: string[] }> => {
    const r = await invoke<{ Count: number; Dir: string; Files: string[] }>('backup', 'ExportVersions', [fileNames, targetDir, op()])
    return { count: r.Count, dir: r.Dir, files: r.Files ?? [] }
  },
  /** 从任意位置的 .db 按表恢复（文件里没有的表保持当前数据；恢复前自动拍快照） */
  importFile: async (path: string): Promise<BackupImportResult> => {
    const r = await invoke<{
      Snapshot: string; TotalTables: number
      Restored: { Label: string; Count: number }[]
      Kept: string[]
    }>('backup', 'Import', [path, op()])
    return {
      snapshot: r.Snapshot,
      totalTables: r.TotalTables,
      restored: (r.Restored ?? []).map((x) => ({ label: x.Label, count: x.Count })),
      kept: r.Kept ?? [],
    }
  },
}

/** 备份页可勾选的表 */
export interface BackupTable {
  name: string
  label: string
  group: string
  count: number
  defaultOn: boolean
}

export interface BackupExportResult {
  fileName: string
  sizeKb: number
  tableCount: number
  totalTables: number
  added: string[]
}

export interface BackupInspect {
  isThisSystem: boolean
  isFull: boolean
  fileName: string
  sizeKb: number
  tableCount: number
  memberCount: number
  orderCount: number
  fileTime: string
  missing: string[]
  error: string
}

/** 按表恢复的结果：恢复了哪些（含行数）、哪些文件里没有（当前数据原样保留） */
export interface BackupImportResult {
  snapshot: string
  totalTables: number
  restored: { label: string; count: number }[]
  kept: string[]
}

/* ---------- 设置 ---------- */

export const settings = {
  getAll: (): Promise<Record<string, string>> => invoke('settings', 'GetAll', []),
  save: (kv: Record<string, string>): Promise<void> => invoke('settings', 'Save', [kv, op()]),
}

/* ---------- 操作日志保留期（店主 11-05） ---------- */

export interface LogStats {
  total: number
  oldest: string
  newest: string
  keepDays: number
  overdue: number
  lastPurge: string
}

export const logs = {
  /** 当前日志量：总条数 / 最早一条 / 按保留期应清多少 */
  stats: async (): Promise<LogStats> => {
    const r = await invoke<{ Total: number; Oldest: string; Newest: string; KeepDays: number; Overdue: number; LastPurge: string }>('logs', 'Stats', [])
    return {
      total: r.Total ?? 0,
      oldest: r.Oldest ?? '',
      newest: r.Newest ?? '',
      keepDays: r.KeepDays ?? 180,
      overdue: r.Overdue ?? 0,
      lastPurge: r.LastPurge ?? '',
    }
  },
  /** 按保留天数清掉更老的日志（只删操作日志，业务流水一条不动）。返回删掉多少条 + 剩余多少条。 */
  purge: async (keepDays: number): Promise<{ removed: number; remain: number; keepDays: number; cutoff: string }> => {
    const r = await invoke<{ Removed: number; Remain: number; KeepDays: number; Cutoff: string }>('logs', 'Purge', [keepDays, op()])
    return { removed: r.Removed ?? 0, remain: r.Remain ?? 0, keepDays: r.KeepDays ?? 0, cutoff: r.Cutoff ?? '' }
  },
}

/* ---------- 系统状态 / 真实数据位置 ---------- */

export interface SysStatus {
  BizDate: string
  DayCut: string
  DbOk: boolean
  JournalMode: string
  LastBackup: string
  ShopName: string
  ShopPhone: string
  /** 发行形态（后端按磁盘/注册表事实判定）：portable 便携版 / win7 Win7 版 / normal 普通版 / loose 独立运行 */
  RunModeKey: string
  RunModeText: string
  RunModeDesc: string
  /** 界面真正在用的浏览器内核，如「随包 109.0.1518.140」「系统 131.0.2903.112」 */
  WebViewKernel: string
  Portable: boolean
  RootDir: string
  DataDir: string
  LogDir: string
  BackupDir: string
  DbPath: string
  ExportDir: string
  /** 程序真实版本，如 "1.0.3"（后端读 AssemblyFileVersion） */
  Version: string
}

export const system = {
  status: (): Promise<SysStatus> => invoke('system', 'GetStatus', []),
  saveShop: (shopName: string, shopPhone: string): Promise<void> =>
    invoke('system', 'SaveShop', [shopName, shopPhone]),
  /** 用系统默认程序打开程序生成的文件（导出 Excel 等），后端限定只能开数据目录下的文件 */
  openPath: (path: string): Promise<string> => invoke('system', 'OpenPath', [path]),
  /** 弹出文件夹选择对话框，返回选中的路径；用户取消返回空字符串 */
  pickFolder: (): Promise<string> => invoke('system', 'PickFolder', []),
  /** 弹「另存为」对话框（Windows 原生），返回完整路径；取消返回空串 */
  pickSaveFile: (defaultName: string, filter = ''): Promise<string> =>
    invoke('system', 'PickSaveFile', [defaultName, filter]),
  /** 弹「打开」对话框（Windows 原生），返回完整路径；取消返回空串 */
  pickOpenFile: (filter = ''): Promise<string> => invoke('system', 'PickOpenFile', [filter]),
  /** 当前生效的默认导出目录（全局 export_dir，未设则兜底 data\exports） */
  exportDir: (): Promise<string> => invoke('system', 'GetExportDir', []),
  /** 用系统默认浏览器打开外部链接（项目主页等）。后端只放行 http/https */
  openUrl: (url: string): Promise<void> => invoke('system', 'OpenUrl', [url]),
  /** 重启软件：导入数据后设置/账号/权限都换了，必须让内存里的旧值丢掉（后端启动新进程后退出当前进程） */
  restart: (): Promise<void> => invoke('system', 'Restart', []),
  /** 打印当前页面（收银小票）：走 WebView2 原生 ShowPrintUI，弹系统打印对话框；无实体机可选「Microsoft Print to PDF」 */
  print: (): Promise<void> => invoke('shell', 'Print', []),
  /** 开机自启动状态：读壳层的 HKCU Run 键（不需要管理员权限）；返回注册表里的真实状态 */
  autostart: (): Promise<boolean> => invoke('shell', 'GetAutostart', []),
  /** 开关开机自启动；返回改完之后回读的真实状态（失败会 reject，前端据此回滚开关） */
  setAutostart: (on: boolean): Promise<boolean> => invoke('shell', 'SetAutostart', [on]),
}

/* ---------- 消费记录（全局单据查询） ---------- */

export interface OrderRecord {
  Id: number
  OrderNo: string
  Time: string
  BizDate: string
  MemberName: string
  MemberCard: string
  Level: string
  Kind: string
  Subtotal: number
  CardCovered: number
  Receivable: number
  Items: string
  Pays: string
  Operator: string
  Status: string
}

export interface RecordTotals {
  Count: number
  Receivable: number
  CardCovered: number
  Balance: number
  Cash: number
  Other: number
}

export interface OrderDetail {
  Id: number
  OrderNo: string
  Time: string
  MemberName: string
  Kind: string
  Subtotal: number
  CardCovered: number
  Receivable: number
  Status: string
  Items: { ItemName: string; Price: number; Qty: number; ByCard: boolean; Staff: string }[]
  Pays: { Method: string; Amount: number }[]
}

export const records = {
  query: (
    fromDate: string,
    toDate: string,
    keyword: string,
    kind: string,
    status: string,
    operatorName: string,
    level: string,
    customer: string,
    limit = 500,
    offset = 0,
  ): Promise<{ Rows: OrderRecord[]; Total: RecordTotals }> =>
    invoke('records', 'Query', [fromDate, toDate, keyword, kind, status, operatorName, level, customer, limit, offset]),
  detail: (orderId: number): Promise<OrderDetail> => invoke('records', 'Detail', [orderId]),
  operators: (): Promise<string[]> => invoke('records', 'Operators', []),
  levels: (): Promise<string[]> => invoke('records', 'Levels', []),
}

/* ---------- 项目列表 ---------- */

export interface ProjectRow extends ServiceItem { enabled: boolean }

export const projects = {
  list: async (): Promise<ProjectRow[]> => {
    const rows = await invoke<{ Id: number; Name: string; Category: string; Price: number; Unit: string; IsCardItem: boolean; Enabled: boolean; CardTimes: number; CommissionRate: number; CommissionMode: string; CommissionPerCount: number }[]>('projects', 'ListItems', ['全部'])
    return rows.map((r) => ({ id: r.Id, name: r.Name, category: r.Category, price: r.Price, unit: r.Unit, isCardItem: r.IsCardItem, enabled: r.Enabled, cardTimes: r.CardTimes ?? 0, commissionRate: r.CommissionRate ?? 0.05, commissionMode: (r.CommissionMode === 'count' ? 'count' : 'percent'), commissionPerCount: r.CommissionPerCount ?? 0 }))
  },
  save: (id: number, name: string, category: string, price: number, unit: string, isCardItem: boolean, enabled: boolean, cardTimes: number, commissionRate: number, commissionMode: 'percent' | 'count', commissionPerCount: number): Promise<void> =>
    invoke('projects', 'SaveItem', [id, name, category, price, unit, isCardItem, enabled, cardTimes, commissionRate, commissionMode, commissionPerCount]),
  setEnabled: (id: number, enabled: boolean): Promise<void> => invoke('projects', 'SetEnabled', [id, enabled]),
  // 删除项目 = 软删除（写 deleted_at + enabled=0 + 套餐停售）：不再售卖，历史订单/流水/提成保留
  remove: (id: number): Promise<void> => invoke('projects', 'DeleteItem', [id]),
}

/* ---------- 报表 ---------- */

export const reports = {
  summary: (range: string): Promise<{ Revenue: number; Recharge: number; Consume: number; SellCard: number; Refund: number; Avg: number }> =>
    invoke('reports', 'RangeSummary', [range]),
  // 当日营收 / 当日现金：按营业日汇总（§十五 15.3 补回删掉的「营业日速览」信息）
  dailySummary: (bizDate: string): Promise<{ Revenue: number; Cash: number }> =>
    invoke('reports', 'DailySummary', [bizDate]),
  // 注：「营业报表」四工作表后端（ReportService.ExportExcel）及其消费的 Trend / PayBreakdown /
  // OrderCountByDay 已于 10-09 一并删除（店主定：总收入并入员工业绩透视，不再单出营业报表）。
  // 导出只留两个：exportStaffDetail（员工业绩透视）+ commission.exportExcel（提成明细+排名）。
  ranks: (month: string) => commission.ranks(month),
  /** 员工当日明细：谁做了什么、多少钱。返回 PascalCase→camelCase 映射后的行 */
  staffActivity: async (date: string): Promise<{ staffId: number; name: string; role: string; ordersCount: number; itemsCount: number; consumeAmount: number; rechargeCount: number; rechargeAmount: number; sellCardCount: number; sellCardAmount: number; totalAmount: number }[]> => {
    const rows = await invoke<{ StaffId: number; Name: string; Role: string; OrdersCount: number; ItemsCount: number; ConsumeAmount: number; RechargeCount: number; RechargeAmount: number; SellCardCount: number; SellCardAmount: number; TotalAmount: number }[]>('reports', 'StaffActivity', [date])
    return (rows ?? []).map((r) => ({
      staffId: r.StaffId,
      name: r.Name,
      role: r.Role,
      ordersCount: r.OrdersCount,
      itemsCount: r.ItemsCount,
      consumeAmount: r.ConsumeAmount,
      rechargeCount: r.RechargeCount,
      rechargeAmount: r.RechargeAmount,
      sellCardCount: r.SellCardCount,
      sellCardAmount: r.SellCardAmount,
      totalAmount: r.TotalAmount,
    }))
  },
  /** 导出员工业绩明细 Excel（扁平透视表，员工为列；时间段必填；saveDir 可选=全局默认 export_dir）。
   * 10-09：旧的「营业报表」四工作表导出（exportExcel）已删，业绩总收入并入此透视表。 */
  exportStaffDetail: (from: string, to: string, saveDir?: string): Promise<{ Path: string; Name: string; Size: number; Period: string }> =>
    invoke('reports', 'ExportStaffDetailExcel', [from, to, op(), saveDir || '']),
}
