import { useCallback, useEffect, useMemo, useState, memo, useRef } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import {
  Wallet, Gift, Plus, Pause, Play, Undo2, CheckCircle2, AlertTriangle, Search,
  Pencil, Trash2, Download, Ticket, CalendarClock, Snowflake, Folder,
} from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, SectionCard, Badge, Input, Select, Field, Modal, EmptyState, MetricCard, DataTable,
  tdCls, numCls, cx,
} from '@/components/common/ui'
import { LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { storedvalue, cards as apiCards, system } from '@/lib/api'
import { useExportDir } from '@/lib/useExportDir'
import { PagedBar } from '@/components/common/paged'
import { useAutoPageSize } from '@/lib/paged'
import { canModule } from '@/lib/perm'
import { fmtMoney } from '@/data/mock'
import type { RechargePackage, RechargeRecord, MemberCard, CardUseRecord } from '@/data/mock'

type Tab = 'plans' | 'cards' | 'records'

/** 合并「会员核销记录」行类型：储值充值 与 次卡核销 统一列 */
type MergedRecord =
  | { kind: '储值'; key: string; time: string; memberName: string; cardNo: string; content: string; amountText: string; status: '正常' | '已退款'; operator: string; refundNote?: string; raw: RechargeRecord }
  | { kind: '次卡'; key: string; time: string; memberName: string; cardNo: string; content: string; amountText: string; status: '正常'; operator: string; raw: CardUseRecord }

const cardStatusTone: Record<string, 'primary' | 'danger' | 'muted'> = {
  正常: 'primary',
  已过期: 'danger',
  冻结: 'muted',
  已用完: 'muted',
}

// —— 列宽预算（最小窗口 main 可用宽约 1088px）——
/** 充值方案 5 列 */
const PLAN_COLS: DataTableCol[] = [
  { key: 'amount', title: '充值金额', width: 140, headClass: 'text-right', cellClass: numCls },
  { key: 'gift', title: '赠送金额', width: 140, headClass: 'text-right', cellClass: cx(numCls, 'text-primary') },
  { key: 'ratio', title: '赠送比例', width: 130, headClass: 'text-right', cellClass: 'text-muted-foreground' },
  { key: 'status', title: '状态', width: 110 },
  { key: 'action', title: '操作', headClass: 'text-right', cellClass: 'text-right' },
]

/** 会员卡 9 列：预算 1040px，不横滚 */
const CARD_COLS: DataTableCol[] = [
  { key: 'member', title: '会员', width: 120, truncate: true, cellClass: 'font-medium' },
  { key: 'cardNo', title: '卡号', width: 110, cellClass: 'text-muted-foreground' },
  { key: 'name', title: '卡名称', width: 150, truncate: true },
  { key: 'total', title: '总次数', width: 90, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'used', title: '已消耗', width: 90, headClass: 'text-right', cellClass: cx(numCls, 'text-foreground') },
  { key: 'remain', title: '剩余', width: 90, headClass: 'text-right', cellClass: numCls },
  { key: 'expire', title: '有效期至', width: 130, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'status', title: '状态', width: 110 },
  { key: 'action', title: '操作', width: 150, headClass: 'text-right', cellClass: 'text-right' },
]

/** 核销记录 9 列：预算约 1050px，不横滚；内容列弹性截断 */
const RECORD_COLS: DataTableCol[] = [
  { key: 'kind', title: '类型', width: 70 },
  { key: 'time', title: '时间', width: 130, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'member', title: '会员', width: 130, truncate: true, cellClass: 'font-medium' },
  { key: 'cardNo', title: '卡号 / 卡名称', width: 130, truncate: true, cellClass: 'text-muted-foreground' },
  { key: 'content', title: '内容', truncate: true },
  { key: 'amount', title: '金额 / 次数', width: 130, headClass: 'text-right', cellClass: numCls },
  { key: 'status', title: '状态', width: 110 },
  { key: 'operator', title: '操作员', width: 100, cellClass: 'text-xs' },
  { key: 'action', title: '操作', width: 130, headClass: 'text-right', cellClass: 'text-right' },
]

export function MemberAssetsPage() {
  const [tab, setTab] = useState<Tab>('plans')

  // —— 储值方案 ——
  const [packages, setPackages] = useState<RechargePackage[]>([])
  const [svMetrics, setSvMetrics] = useState<{ TodayRecharge: number; TodayGift: number; MonthRecharge: number; Liability: number } | null>(null)
  const [planOpen, setPlanOpen] = useState(false)
  const [planEditingId, setPlanEditingId] = useState(0)
  const [planAmount, setPlanAmount] = useState('')
  const [planGift, setPlanGift] = useState('')
  const [planEnabled, setPlanEnabled] = useState(true)
  const [planErrors, setPlanErrors] = useState<{ amount?: string; gift?: string }>({})
  const [planDeleteTarget, setPlanDeleteTarget] = useState<RechargePackage | null>(null)
  const [refundTarget, setRefundTarget] = useState<RechargeRecord | null>(null)
  const [refundReason, setRefundReason] = useState('')
  const [refundError, setRefundError] = useState('')
  const canRefund = canModule('退款/改价')
  const [exporting, setExporting] = useState(false)
  const [exportOpen, setExportOpen] = useState(false)
  const [expFrom, setExpFrom] = useState('')
  const [expTo, setExpTo] = useState('')
  const [expErr, setExpErr] = useState('')
  // B3（10-21）：按月导出为主入口（月选择 → 自动算月首月末走 Range 接口），区间留作补充
  const [expMonth, setExpMonth] = useState('')
  // 默认导出位置（4 个导出共享，改一次即成新默认）
  const { dir: exportDir, change: chooseExportDir } = useExportDir()

  // —— 次卡 ——
  const [cards, setCards] = useState<MemberCard[]>([])
  const [cardStatusFilter, setCardStatusFilter] = useState('全部')
  const [cardKeyword, setCardKeyword] = useState('')
  // 本月次卡核销次数（店主 10-08：要能看见「一个月消耗的卡次数」）
  const [monthUse, setMonthUse] = useState(0)

  // —— 会员核销记录（合并） ——
  const [recKeyword, setRecKeyword] = useState('')
  const [recTypeFilter, setRecTypeFilter] = useState<'全部' | '储值' | '次卡'>('全部')

  const load = useCallback(() => {
    storedvalue.packages().then(setPackages).catch((e) => toast.error(String(e)))
    storedvalue.metrics().then(setSvMetrics).catch(() => setSvMetrics(null))
    apiCards.memberCards().then(setCards).catch(() => setCards([]))
    // 「本月」按营业日所在自然月算（与报表页 RangeSummary 口径一致）
    system
      .status()
      .then((s) => s.BizDate || '')
      .catch(() => '')
      .then((bizDate) => {
        const ym = (bizDate || new Date().toISOString().slice(0, 10)).slice(0, 7)
        return apiCards.useRecordsCountByMonth(ym)
      })
      .then(setMonthUse)
      .catch(() => setMonthUse(0))
  }, [])
  useEffect(load, [])

  // 会员核销记录（储值 + 次卡合并）：两路各自**全量**拉取到内存，前端合并 + 排序后按 B2 页码切片。
  // 合并表无法用两个独立 offset 做真分页，故走「内存全量 + 前端切片」；单店数据量内存无压力。
  const [rechargeRows, setRechargeRows] = useState<RechargeRecord[]>([])
  const [useRows, setUseRows] = useState<CardUseRecord[]>([])
  const [recLoading, setRecLoading] = useState(true)
  // 表格区高度 → 一页装几行（店主 11-04：装满窗口就翻页）。用户在「每页」下拉手动选过就不再跟随。
  const bodyRef = useRef<HTMLDivElement>(null)
  const autoSize = useAutoPageSize(bodyRef)
  const recManual = useRef(false)
  const [recPage, setRecPage] = useState(1)
  const [recPageSize, setRecPageSize] = useState(10)
  useEffect(() => {
    if (recManual.current) return
    if (autoSize > 0 && autoSize !== recPageSize) setRecPageSize(autoSize)
  }, [autoSize]) // eslint-disable-line react-hooks/exhaustive-deps

  const loadRecords = useCallback(() => {
    setRecLoading(true)
    setRecPage(1)
    Promise.all([
      storedvalue.records(10000, 0),
      apiCards.useRecords(10000, 0),
    ])
      .then(([r, u]) => {
        setRechargeRows(r)
        setUseRows(u)
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setRecLoading(false))
  }, [])
  useEffect(() => {
    void loadRecords()
  }, [loadRecords])

  // —— 指标 ——
  const todayRecharge = svMetrics?.TodayRecharge ?? 0
  const monthRecharge = svMetrics?.MonthRecharge ?? 0
  const liability = svMetrics?.Liability ?? 0
  const normalCount = cards.filter((c) => c.status === '正常').length
  const remainSum = cards.filter((c) => c.status === '正常').reduce((s, c) => s + c.remain, 0)
  const inTwoMonths = new Date()
  inTwoMonths.setMonth(inTwoMonths.getMonth() + 2)
  const twoMonthLimit = inTwoMonths.toISOString().slice(0, 10)
  const expiringCount = cards.filter((c) => c.status === '正常' && c.expireDate <= twoMonthLimit).length

  // 次卡消耗统计（店主 10-08）：会员卡的消耗（每卡 total−remain）+ 次数项目的消耗（按卡名称汇总）。
  // total−remain 恒等于该卡累计核销次数（恒等式 total = remain + Σuses）。
  const totalUse = cards.reduce((s, c) => s + Math.max(0, c.total - c.remain), 0)
  const itemUseMap: Record<string, number> = {}
  for (const c of cards) {
    const k = c.templateName || '未命名项目'
    itemUseMap[k] = (itemUseMap[k] ?? 0) + Math.max(0, c.total - c.remain)
  }
  const itemUse = Object.keys(itemUseMap)
    .map((name) => ({ name, used: itemUseMap[name] }))
    .filter((it) => it.used > 0)
    .sort((a, b) => b.used - a.used)

  // —— 充值方案 CRUD ——
  const openNewPlan = () => {
    setPlanEditingId(0)
    setPlanAmount('')
    setPlanGift('')
    setPlanEnabled(true)
    setPlanErrors({})
    setPlanOpen(true)
  }
  const openEditPlan = (p: RechargePackage) => {
    setPlanEditingId(p.id)
    setPlanAmount(String(p.amount))
    setPlanGift(String(p.gift))
    setPlanEnabled(p.enabled)
    setPlanErrors({})
    setPlanOpen(true)
  }
  const togglePlan = (p: RechargePackage) => {
    storedvalue
      .savePackage(p.id, p.amount, p.gift, !p.enabled)
      .then(() => {
        toast.success(p.enabled ? `「充${p.amount}送${p.gift}」已停用，收银台不再展示` : `「充${p.amount}送${p.gift}」已启用`)
        load()
      })
      .catch((e) => toast.error(String(e)))
  }
  const submitPlan = () => {
    const amount = Number(planAmount)
    const gift = Number(planGift)
    const errs: { amount?: string; gift?: string } = {}
    if (!planAmount.trim() || !Number.isFinite(amount) || amount <= 0) errs.amount = '请输入大于 0 的充值金额'
    if (!planGift.trim() || !Number.isFinite(gift) || gift < 0) errs.gift = '赠送金额不能为空且不能为负数'
    if (!errs.amount && packages.some((p) => p.amount === amount && p.id !== planEditingId)) {
      errs.amount = '该金额档位已存在，请直接编辑或更换金额'
    }
    setPlanErrors(errs)
    if (Object.keys(errs).length > 0) return
    const isNew = planEditingId <= 0
    storedvalue
      .savePackage(planEditingId, amount, gift, planEnabled)
      .then(() => {
        toast.success(isNew
          ? `新方案「充${amount}送${gift}」已保存${planEnabled ? '并启用' : '但保持停用'}`
          : `方案「充${amount}送${gift}」已更新${planEnabled ? '' : '（保持停用）'}`)
        setPlanOpen(false)
        setPlanAmount('')
        setPlanGift('')
        setPlanErrors({})
        load()
      })
      .catch((e) => setPlanErrors({ amount: String(e) }))
  }
  const submitDeletePlan = () => {
    if (!planDeleteTarget) return
    storedvalue
      .deletePackage(planDeleteTarget.id)
      .then(() => {
        setPlanDeleteTarget(null)
        load()
      })
      .catch((e) => toast.error(String(e)))
  }

  // —— 退款红冲 ——
  const submitRefund = () => {
    if (!refundTarget) return
    if (!refundReason.trim()) {
      setRefundError('退款必须填写原因（写入操作日志，不可省略）')
      return
    }
    storedvalue
      .refund(refundTarget.memberId, refundTarget.amount, refundTarget.gift, refundReason.trim())
      .then(() => {
        toast.success(`已退款：¥${fmtMoney(refundTarget.amount)} 本金与赠送 ¥${fmtMoney(refundTarget.gift)} 同笔红冲回滚，原记录保留可追溯`)
        setRefundTarget(null)
        setRefundReason('')
        setRefundError('')
        load()
        loadRecords() // 退款生成红冲流水，重新拉全量并回第 1 页让它显示在最上
        setRecPage(1)
      })
      .catch((e) => setRefundError(String(e)))
  }

  // —— 导出（M9：单次每类有 5000 行上限；B3 主入口=按月，一个月一段不会触限）——
  const openExport = () => {
    // 默认选中当前营业月，打开就能导
    setExpMonth(new Date().toISOString().slice(0, 7))
    setExpFrom('')
    setExpTo('')
    setExpErr('')
    setExportOpen(true)
  }
  const submitExport = async () => {
    if (exporting) return
    setExpErr('')
    const f = expFrom.trim()
    const t = expTo.trim()
    // 按月模式：月串 → 月首/月末，走 Range 接口
    const mo = expMonth.trim()
    if (mo) {
      if (!/^\d{4}-\d{2}$/.test(mo)) { setExpErr('月份格式不对'); return }
      const y = parseInt(mo.slice(0, 4), 10)
      const m = parseInt(mo.slice(5), 10)
      const last = new Date(y, m, 0).getDate()
      setExporting(true)
      try {
        const r = await storedvalue.exportRecordsRange(`${mo}-01`, `${mo}-${String(last).padStart(2, '0')}`, exportDir || undefined)
        setExportOpen(false)
        try { await system.openPath(r.Path) } catch { /* 打开失败不阻断 */ }
        if (r.Truncated) {
          toast.warning(`已导出 ${r.Count} 行，达到单次上限；本月数据超出，请联系开发分段导出。`)
        } else {
          toast.success(`已导出 ${mo} 全月 ${r.Count} 行`)
        }
      } catch (e) {
        toast.error(String(e))
      } finally {
        setExporting(false)
      }
      return
    }
    if (f && t && f > t) {
      setExpErr('开始日期不能晚于结束日期')
      return
    }
    setExporting(true)
    try {
      const r = f || t
        ? await storedvalue.exportRecordsRange(f, t, exportDir || undefined)
        : await storedvalue.exportRecords(exportDir || undefined)
      setExportOpen(false)
      try { await system.openPath(r.Path) } catch { /* 打开失败不阻断 */ }
      if (r.Truncated) {
        toast.warning(`已导出 ${r.Count} 行，达到单次上限；请缩短时间段，分几次导出剩余部分。`)
      } else {
        toast.success(`已导出 ${r.Count} 行${r.Period ? `（${r.Period}）` : ''}`)
      }
    } catch (e) {
      toast.error(String(e))
    } finally {
      setExporting(false)
    }
  }

  // —— 次卡冻结 / 解冻 ——
  const freezeCard = useCallback((c: MemberCard) => {
    const action = c.status === '冻结' ? '解冻' : '冻结'
    apiCards
      .setCardStatus(c.id, action)
      .then(() => {
        toast.success(`${c.memberName} 的「${c.templateName}」已${action === '冻结' ? '冻结（剩余次数保留）' : '解冻'}`)
        load()
      })
      .catch((e) => toast.error(String(e)))
  }, [load])

  // 开退款确认框：清空上一次的原因与报错，避免上一笔的记录残留。
  const openRefund = useCallback((rec: RechargeRecord) => {
    setRefundTarget(rec)
    setRefundReason('')
    setRefundError('')
  }, [])

  // —— 会员卡列表筛选：状态 + 关键词（会员名 / 卡号 / 卡名称）——
  const filteredCards = useMemo(() => {
    let result = cards
    if (cardStatusFilter !== '全部') result = result.filter((c) => c.status === cardStatusFilter)
    const kw = cardKeyword.trim().toLowerCase()
    if (kw) {
      result = result.filter((c) =>
        c.memberName.toLowerCase().includes(kw)
        || c.cardNo.toLowerCase().includes(kw)
        || c.templateName.toLowerCase().includes(kw))
    }
    return result
  }, [cards, cardStatusFilter, cardKeyword])

  // —— 会员核销记录合并（前端全量合并 + 排序，B2 页码在过滤后切片）——
  const mergedRecords = useMemo<MergedRecord[]>(() => {
    const list: MergedRecord[] = []
    for (const r of rechargeRows) {
      list.push({
        kind: '储值',
        key: 'r' + r.id,
        time: r.time,
        memberName: r.memberName,
        cardNo: r.cardNo,
        content: r.gift > 0 ? '充值 + 赠送' : '充值',
        amountText: r.gift > 0 ? `¥${fmtMoney(r.amount)}（送 ¥${fmtMoney(r.gift)}）` : `¥${fmtMoney(r.amount)}`,
        status: r.status,
        operator: r.operator,
        refundNote: r.refundNote,
        raw: r,
      })
    }
    for (let i = 0; i < useRows.length; i++) {
      const u = useRows[i]
      list.push({
        kind: '次卡',
        key: 'u' + i,
        time: u.time,
        memberName: u.memberName,
        cardNo: u.cardName,
        content: '核销「' + u.itemName + '」',
        amountText: `剩 ${u.remainAfter} 次`,
        status: '正常',
        operator: u.operator,
        raw: u,
      })
    }
    // 时间倒序
    list.sort((a, b) => (a.time < b.time ? 1 : a.time > b.time ? -1 : 0))
    return list
  }, [rechargeRows, useRows])

  const filteredRecords = useMemo(() => {
    const k = recKeyword.trim().toLowerCase()
    return mergedRecords.filter((r) => {
      if (recTypeFilter !== '全部' && r.kind !== recTypeFilter) return false
      if (!k) return true
      return r.memberName.toLowerCase().includes(k) || r.cardNo.toLowerCase().includes(k)
    })
  }, [mergedRecords, recKeyword, recTypeFilter])

  // 过滤后按 B2 页码切片（页码/每页条数变化即换页；筛选一变回到第 1 页由 useEffect 处理）
  const recTotal = filteredRecords.length
  const recPageCount = Math.max(1, Math.ceil(recTotal / Math.max(1, recPageSize)))
  const recSafePage = Math.min(recPage, recPageCount)
  const recRows = useMemo(
    () => filteredRecords.slice((recSafePage - 1) * recPageSize, recSafePage * recPageSize),
    [filteredRecords, recSafePage, recPageSize],
  )
  // 换筛选/换每页条数时回第 1 页
  useEffect(() => {
    setRecPage(1)
  }, [recKeyword, recTypeFilter, recPageSize])

  // 明细区头部的两路笔数说明（全量值，不随翻页变）
  const rechargeCount = rechargeRows.length
  const useCount = useRows.length

  return (
    <PageFill minWidth={1080}>
      {/* 自适应布局（店主 11-04）：固定块 shrink-0，页签内容 flex-1 吃满剩余高度 */}
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            会员资产
          </h1>
        </div>
      </div>

      <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))] shrink-0">
        <MetricCard label="今日充值" value={fmtMoney(todayRecharge)} icon={<Wallet size={17} />} />
        <MetricCard label="本月充值累计" value={fmtMoney(monthRecharge)} icon={<Wallet size={17} />} />
        <MetricCard label="会员余额负债" value={fmtMoney(liability)} icon={<CheckCircle2 size={17} />} />
        <MetricCard label="生效中的卡" value={String(normalCount)} prefix="" icon={<Ticket size={17} />} />
        <MetricCard label="剩余次数合计" value={String(remainSum)} prefix="" icon={<CheckCircle2 size={17} />} />
        <MetricCard label="两个月内到期" value={String(expiringCount)} prefix="" icon={<CalendarClock size={17} />} />
      </div>

      <div className="flex gap-1 flex-wrap shrink-0">
        {([
          ['plans', '充值方案'],
          ['cards', '会员卡列表'],
          ['records', '会员核销记录'],
        ] as [Tab, string][]).map(([k, label]) => (
          <button
            key={k}
            onClick={() => setTab(k)}
            className={cx(
              'h-8 px-4 text-[13px] rounded-[var(--radius-pill)] border transition-colors',
              tab === k
                ? 'bg-primary border-primary text-primary-foreground font-semibold'
                : 'bg-card border-input text-muted-foreground hover:text-foreground hover:border-primary/40',
            )}
          >
            {label}
          </button>
        ))}
      </div>

      {/* 页签内容区：flex-1 吃满剩余高度，超出内部滚动 */}
      <div className="page-body overflow-y-auto flex flex-col">
      {tab === 'plans' ? (
        <SectionCard
          title="充值方案"
          extra={
            <Button variant="primary" size="sm" onClick={openNewPlan}>
              <Plus size={13} /> 新增方案
            </Button>
          }
          bodyClassName="p-0"
        >
          <DataTable
            cols={PLAN_COLS}
            rows={packages}
            rowKey={(p) => p.id}
            plain
            fill
            render={(p) => [
              `¥${fmtMoney(p.amount)}`,
              p.gift > 0 ? `¥${fmtMoney(p.gift)}` : '—',
              p.gift > 0 ? `${((p.gift / p.amount) * 100).toFixed(1)}%` : '—',
              p.enabled ? <Badge key="s" tone="primary">启用中</Badge> : <Badge key="s" tone="muted">已停用</Badge>,
              <div key="a" className="inline-flex items-center gap-1">
                <Button variant="ghost" size="sm" onClick={() => openEditPlan(p)}>
                  <Pencil size={12} /> 编辑
                </Button>
                <Button variant="ghost" size="sm" onClick={() => togglePlan(p)}>
                  {p.enabled ? <><Pause size={12} /> 停用</> : <><Play size={12} /> 启用</>}
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  className="text-destructive hover:text-destructive"
                  onClick={() => setPlanDeleteTarget(p)}
                >
                  <Trash2 size={12} /> 删除
                </Button>
              </div>,
            ]}
            empty={<div className="py-8 text-center text-xs text-muted-foreground">还没有充值方案，点右上角「新增方案」建立第一个档位</div>}
          />
          <div className="px-4 py-2.5 border-t border-border text-[11px] text-muted-foreground flex items-start gap-1.5">
            <AlertTriangle size={13} className="shrink-0 mt-px text-muted-foreground" />
            退卡规则：会员退卡时赠送金额按剩余本金比例扣回，明细写入操作日志。
          </div>
        </SectionCard>
      ) : null}

      {tab === 'cards' ? (
        <SectionCard
          title="会员卡列表"
          extra={
            <div className="flex items-center gap-2">
              <Select value={cardStatusFilter} onChange={(e) => setCardStatusFilter(e.target.value)} aria-label="按状态筛选" className="h-7 text-xs">
                <option>全部</option>
                <option>正常</option>
                <option>已过期</option>
                <option>冻结</option>
                <option>已用完</option>
              </Select>
              <span className="relative block">
                <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
                <Input
                  value={cardKeyword}
                  onChange={(e) => setCardKeyword(e.target.value)}
                  placeholder="搜索会员 / 卡号 / 卡名称"
                  aria-label="搜索会员卡"
                  className="h-7 w-48 pl-7 text-xs"
                />
              </span>
            </div>
          }
          bodyClassName="p-0"
        >
          {/* 消耗概览（店主 10-08）：一个月消耗的卡次数（本月消耗）+ 次数项目的消耗（按卡名称汇总） */}
          {cards.length > 0 ? (
            <div className="px-4 py-2.5 border-b border-border flex items-center gap-x-4 gap-y-1.5 flex-wrap text-[11px] text-muted-foreground">
              <span>
                本月消耗 <b className="text-foreground font-semibold tabular-nums">{monthUse}</b> 次
              </span>
              <span className="w-px h-3.5 bg-border" />
              <span>
                累计消耗 <b className="text-foreground font-semibold tabular-nums">{totalUse}</b> 次
              </span>
              <span className="w-px h-3.5 bg-border" />
              <span className="flex items-center gap-x-2 gap-y-1 flex-wrap">
                <span>按次数项目：</span>
                {itemUse.length === 0 ? (
                  <span className="text-muted-foreground/70">暂无核销</span>
                ) : (
                  itemUse.map((it) => (
                    <span
                      key={it.name}
                      className="inline-flex items-center gap-1 px-2 h-5 rounded-[var(--radius-pill)] bg-muted text-foreground/80"
                    >
                      {it.name}
                      <b className="text-foreground tabular-nums">{it.used}</b>次
                    </span>
                  ))
                )}
              </span>
            </div>
          ) : null}
          {filteredCards.length === 0 ? (
            <EmptyState title="该状态下暂无次卡" desc="换个状态筛选试试" />
          ) : (
            <DataTable
              cols={CARD_COLS}
              rows={filteredCards}
              rowKey={(c) => c.id}
              renderRow={(c) => <CardRow key={c.id} c={c} onFreeze={freezeCard} />}
              plain
              fill
            />
          )}
        </SectionCard>
      ) : null}

      {tab === 'records' ? (
        <SectionCard
          title="会员核销记录"
          extra={
            <div className="flex items-center gap-2">
              <span className="text-[11px] text-muted-foreground">
                储值 {rechargeCount} 笔 · 次卡 {useCount} 笔
              </span>
              <Select value={recTypeFilter} onChange={(e) => setRecTypeFilter(e.target.value as '全部' | '储值' | '次卡')} aria-label="按类型筛选" className="h-7 text-xs">
                <option value="全部">全部类型</option>
                <option value="储值">仅储值</option>
                <option value="次卡">仅次卡</option>
              </Select>
              <span className="relative block">
                <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
                <Input
                  value={recKeyword}
                  onChange={(e) => setRecKeyword(e.target.value)}
                  placeholder="搜索姓名 / 卡号"
                  aria-label="搜索会员核销记录"
                  className="h-7 w-48 pl-7 text-xs"
                />
              </span>
              <Button
                variant="secondary"
                size="sm"
                onClick={openExport}
                disabled={exporting}
                title="导出会员核销记录（储值充值 + 次卡核销），可按时间段分段导出"
              >
                <Download size={13} /> {exporting ? '导出中…' : '导出核销'}
              </Button>
              <span className="inline-flex items-center gap-1.5 min-w-0 text-[11px] text-muted-foreground">
                <span className="truncate max-w-[180px]" title={exportDir || undefined}>
                  导出到 <span className="text-foreground">{exportDir || '默认位置'}</span>
                </span>
                <button
                  type="button"
                  onClick={chooseExportDir}
                  className="inline-flex items-center gap-1 h-6 px-1.5 rounded-[var(--radius-sm)] border border-input bg-card hover:bg-accent hover:text-accent-foreground transition-colors shrink-0"
                  title="更换默认导出位置"
                >
                  <Folder size={11} /> 更换
                </button>
              </span>
            </div>
          }
          className="flex flex-col"
          bodyClassName="p-0 flex-1 min-h-0 flex flex-col"
        >
          {/* 常驻包裹层：供 useAutoPageSize 量「一页装几行」 */}
          <div ref={bodyRef} className="flex-1 min-h-0 flex flex-col">
          {recLoading && mergedRecords.length === 0 ? (
            <div className="py-10 text-center text-sm text-muted-foreground">加载中…</div>
          ) : filteredRecords.length === 0 ? (
            <EmptyState title="没有匹配的会员核销记录" desc="换个关键词或切换类型试试" />
          ) : (
            <DataTable
              cols={RECORD_COLS}
              rows={recRows}
              rowKey={(r) => r.key}
              renderRow={(r) => <RecordRow key={r.key} r={r} canRefund={canRefund} onRefund={openRefund} />}
              fill
            />
          )}
          </div>
          {recTotal > 0 ? (
            <PagedBar
              className="shrink-0"
              total={recTotal}
              page={recSafePage}
              pageCount={recPageCount}
              pageSize={recPageSize}
              goToPage={setRecPage}
              onPageSizeChange={(n) => { recManual.current = true; setRecPageSize(n); setRecPage(1) }}
              loading={recLoading}
            />
          ) : null}
        </SectionCard>
      ) : null}
      </div>

      {planOpen ? (
        <Modal
          title={planEditingId > 0 ? '编辑充值方案' : '新增充值方案'}
          onClose={() => setPlanOpen(false)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setPlanOpen(false)}>取消</Button>
              <Button variant="primary" onClick={submitPlan}>
                {planEditingId > 0 ? '保存修改' : '保存并启用'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="充值金额（元）" required error={planErrors.amount}>
              <Input value={planAmount} onChange={(e) => setPlanAmount(e.target.value)} placeholder="如 500" inputMode="decimal" />
            </Field>
            <Field label="赠送金额（元）" required error={planErrors.gift}>
              <Input value={planGift} onChange={(e) => setPlanGift(e.target.value)} placeholder="如 80，不赠送填 0" inputMode="decimal" />
            </Field>
            <Field label="启用状态">
              <div className="flex items-center gap-3 h-9 px-3 bg-muted/40 rounded-[var(--radius-sm)] border border-input">
                <label className="flex items-center gap-2 cursor-pointer select-none text-sm">
                  <input
                    type="checkbox"
                    checked={planEnabled}
                    onChange={(e) => setPlanEnabled(e.target.checked)}
                    className="accent-[hsl(var(--primary))] w-4 h-4"
                  />
                  {planEnabled ? '启用中（收银台与会员充值弹窗可见）' : '停用（保留在方案列表，但收银台不展示）'}
                </label>
              </div>
            </Field>
            {planAmount && planGift && Number(planAmount) > 0 && Number(planGift) >= 0 ? (
              <p className="text-xs text-muted-foreground bg-muted/60 rounded-[var(--radius-sm)] px-2.5 py-2">
                预览：充 ¥{fmtMoney(Number(planAmount) || 0)} 送 ¥{fmtMoney(Number(planGift) || 0)}，
                赠送比例 {Number(planAmount) > 0 ? (((Number(planGift) || 0) / Number(planAmount)) * 100).toFixed(1) : '0.0'}%
              </p>
            ) : null}
            <p className="text-[11px] text-muted-foreground">
              {planEditingId > 0
                ? '修改立即生效；金额变更不追溯历史充值记录，历史记录按当时的档位留痕。'
                : '方案保存后立即出现在收银台与会员充值弹窗；停用不影响历史流水。'}
            </p>
          </div>
        </Modal>
      ) : null}

      {planDeleteTarget ? (
        <Modal
          title="删除充值方案"
          onClose={() => setPlanDeleteTarget(null)}
          width={460}
          footer={
            <>
              <Button variant="secondary" onClick={() => setPlanDeleteTarget(null)}>取消</Button>
              <Button variant="danger" onClick={submitDeletePlan}>
                <Trash2 size={13} /> 确认删除
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive flex items-center gap-1.5">
                <AlertTriangle size={13} /> 即将删除的方案：
              </p>
              <p className="text-foreground">
                充 <b>¥{fmtMoney(planDeleteTarget.amount)}</b> 送 <b>¥{fmtMoney(planDeleteTarget.gift)}</b>
                {planDeleteTarget.enabled ? '（当前启用中）' : '（已停用）'}
              </p>
            </div>
            <ul className="text-xs text-foreground/90 space-y-1 leading-relaxed">
              <li>· 收银台与会员充值弹窗立即不再展示该档位</li>
              <li>· 历史充值记录按当时的档位金额留痕，不受影响</li>
              <li>· 因档位金额唯一，被删金额短时间内无法新建同金额方案（如需恢复请让店长清库）</li>
            </ul>
          </div>
        </Modal>
      ) : null}

      {exportOpen ? (
        <Modal
          title="导出会员核销记录"
          onClose={() => setExportOpen(false)}
          width={460}
          footer={
            <>
              <Button variant="secondary" onClick={() => setExportOpen(false)}>取消</Button>
              <Button variant="primary" onClick={submitExport} disabled={exporting}>
                <Download size={13} /> {exporting ? '导出中…' : '确认导出'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            {/* B3：按月导出（主入口）——一个月一段，不会触 5000 上限，查账也按月对 */}
            <Field label="按月导出（推荐）" error={expErr}>
              <div className="flex items-center gap-2">
                <input
                  type="month"
                  value={expMonth}
                  onChange={(e) => { setExpMonth(e.target.value); setExpFrom(''); setExpTo('') }}
                  className="h-9 px-2 text-sm bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
                  aria-label="选择导出月份"
                />
                {expMonth ? (
                  <button onClick={() => setExpMonth('')} className="text-xs text-muted-foreground hover:text-foreground">改用自定义区间</button>
                ) : null}
              </div>
            </Field>
            <div className="flex gap-2">
              <div className="flex-1">
                <Field label="开始日期（自定义区间，选填）">
                  <Input type="date" value={expFrom} onChange={(e) => { setExpFrom(e.target.value); setExpMonth('') }} aria-label="导出开始日期" />
                </Field>
              </div>
              <div className="flex-1">
                <Field label="结束日期（选填）">
                  <Input type="date" value={expTo} onChange={(e) => { setExpTo(e.target.value); setExpMonth('') }} aria-label="导出结束日期" />
                </Field>
              </div>
            </div>
            <p className="text-[11px] text-muted-foreground">
              选月份 = 导出该月全月；自定义区间优先于月份；两头都空 = 导出全部（单次每类上限 5000 行）。
            </p>
            <p className="text-[11px] text-muted-foreground">
              导出到 <span className="text-foreground">{exportDir || '默认位置'}</span>
            </p>
          </div>
        </Modal>
      ) : null}

      {refundTarget ? (
        <Modal
          title="充值退款确认（红冲）"
          onClose={() => setRefundTarget(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setRefundTarget(null)}>取消</Button>
              <Button variant="danger" onClick={submitRefund}>
                <Undo2 size={13} /> 确认退款
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive flex items-center gap-1.5">
                <AlertTriangle size={13} /> 退款将同笔回滚以下内容：
              </p>
              <p className="text-foreground">
                ① 会员 {refundTarget.memberName}（{refundTarget.cardNo}）余额扣回本金 ¥{fmtMoney(refundTarget.amount)}
              </p>
              <p className="text-foreground">② 同步扣回赠送 ¥{fmtMoney(refundTarget.gift)}（余额不足时拒绝执行）</p>
              <p className="text-foreground">③ 另生成一条红冲流水，原记录保留可追溯，不删除任何数据</p>
            </div>
            <Field label="退款原因" required error={refundError}>
              <Select value={refundReason} onChange={(e) => { setRefundReason(e.target.value); setRefundError('') }}>
                <option value="" disabled>请选择原因</option>
                <option value="会员要求退卡">会员要求退卡</option>
                <option value="收银员操作失误">收银员操作失误</option>
                <option value="重复收款">重复收款</option>
                <option value="其他（详情见备注）">其他</option>
              </Select>
            </Field>
          </div>
        </Modal>
      ) : null}
    </PageFill>
  )
}

/**
 * 会员卡单行：filteredCards 变化时只影响「哪些行显示」，已显示行对象引用不变，
 * onFreeze 由父级 useCallback 提供稳定引用，memo 因此命中、不随行数放大重渲。
 */
const CardRow = memo(function CardRow({
  c,
  onFreeze,
}: {
  c: MemberCard
  onFreeze: (c: MemberCard) => void
}) {
  return (
    <tr className="even:bg-muted/40">
      <td className={cx(tdCls, 'font-medium')}>{c.memberName}</td>
      <td className={cx(tdCls, 'text-muted-foreground')}>{c.cardNo}</td>
      <td className={tdCls}>{c.templateName}</td>
      <td className={cx(tdCls, numCls, 'text-muted-foreground')}>
        {c.total}
        {c.giftTimes > 0 ? <span className="ml-1 text-[10px] text-primary">含赠{c.giftTimes}</span> : null}
      </td>
      {/* 已消耗 = 总次数 − 剩余（= 该卡累计核销次数，店主 10-08 要看每张卡的消耗） */}
      <td className={cx(tdCls, numCls, 'text-foreground')}>{Math.max(0, c.total - c.remain)}</td>
      <td className={cx(tdCls, numCls, c.remain <= 2 && c.status === '正常' ? 'text-destructive font-semibold' : 'text-primary font-semibold')}>
        {c.remain}
      </td>
      <td className={cx(tdCls, 'tabular-nums text-muted-foreground')}>{c.expireDate}</td>
      <td className={tdCls}>
        <Badge tone={cardStatusTone[c.status] ?? 'muted'}>{c.status}</Badge>
        {c.status === '正常' && c.remain <= 2 ? (
          <span className="ml-1.5 text-[10px] text-destructive">余次不足</span>
        ) : null}
      </td>
      <td className={cx(tdCls, 'text-right')}>
        {c.status === '正常' || c.status === '冻结' ? (
          <Button variant="ghost" size="sm" onClick={() => onFreeze(c)}>
            <Snowflake size={12} /> {c.status === '冻结' ? '解冻' : '冻结'}
          </Button>
        ) : (
          <span className="text-[11px] text-muted-foreground">
            {c.status === '已过期' ? '到期封存' : '已完结，仅可查询'}
          </span>
        )}
      </td>
    </tr>
  )
})

/**
 * 核销记录单行：记录最多（储值+次卡合并、可无限「加载更多」），记忆化收益最大。
 * canRefund 是布尔原值，memo 比较无引用问题。
 */
const RecordRow = memo(function RecordRow({
  r,
  canRefund,
  onRefund,
}: {
  r: MergedRecord
  canRefund: boolean
  onRefund: (rec: RechargeRecord) => void
}) {
  const isRefund = r.kind === '储值' && r.status === '已退款'
  const isUse = r.kind === '次卡'
  return (
    <tr className={cx('even:bg-muted/40', isRefund && 'bg-destructive/5')}>
      <td className={tdCls}>
        {isUse ? <Badge tone="muted">次卡</Badge> : <Badge tone="primary">储值</Badge>}
      </td>
      <td className={cx(tdCls, 'text-muted-foreground tabular-nums')}>{r.time}</td>
      <td className={cx(tdCls, 'font-medium')}>{r.memberName}</td>
      <td className={cx(tdCls, 'text-muted-foreground')}>{r.cardNo}</td>
      <td className={tdCls}>{r.content}</td>
      <td className={cx(tdCls, numCls, isRefund && 'line-through text-muted-foreground')}>
        {r.amountText}
      </td>
      <td className={tdCls}>
        {isUse ? (
          <Badge tone="primary">正常</Badge>
        ) : isRefund ? (
          <Badge tone="danger">已退款（红冲）</Badge>
        ) : (
          <Badge tone="primary">正常</Badge>
        )}
      </td>
      <td className={tdCls}>{r.operator}</td>
      <td className={cx(tdCls, 'text-right')}>
        {isUse ? (
          <span className="text-[11px] text-muted-foreground">与收银单据一一对应</span>
        ) : isRefund ? (
          <span className="text-[11px] text-muted-foreground">{r.refundNote || '—'}</span>
        ) : canRefund ? (
          <Button
            variant="ghost"
            size="sm"
            className="text-destructive hover:text-destructive"
            onClick={() => onRefund(r.raw)}
          >
            <Undo2 size={12} /> 退款
          </Button>
        ) : (
          <span className="text-[11px] text-muted-foreground">—</span>
        )}
      </td>
    </tr>
  )
})
