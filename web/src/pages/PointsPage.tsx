import { useEffect, useMemo, useState, memo, useRef } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import { Gift, Cake, Moon, Settings2, ShoppingBag, AlertCircle, MessageCircleHeart, Search } from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, SectionCard, Badge, Input, Select, Field, Modal, MetricCard, DataTable,
  tdCls, numCls, cx,
} from '@/components/common/ui'
import { LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { points as apiPoints, dashboard as apiDashboard, members as apiMembers } from '@/lib/api'
import { usePagedNumbered, useAutoPageSize } from '@/lib/paged'
import { PagedBar } from '@/components/common/paged'
import { fmtMoney } from '@/data/mock'
import type { PointGift, PointRecord, Member } from '@/data/mock'
import { isManager } from '@/lib/session'

type Tab = 'records' | 'mall' | 'exchanges' | 'marketing'

/** 积分明细 5 列预算（≈850px，最小窗口内不横滚）；原因列弹性截断。 */
const POINT_COLS: DataTableCol[] = [
  { key: 'time', title: '时间', width: 150, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'member', title: '会员', width: 150, truncate: true, cellClass: 'font-medium' },
  { key: 'change', title: '积分变动', width: 110, headClass: 'text-right', cellClass: numCls },
  { key: 'reason', title: '原因', truncate: true, cellClass: 'text-muted-foreground' },
  { key: 'balance', title: '变动后余额', width: 130, headClass: 'text-right', cellClass: numCls },
]

/** 兑换记录台账（店主 10-24）：谁、什么时候、换了什么、扣了多少分、谁经手。 */
const EXCHANGE_COLS: DataTableCol[] = [
  { key: 'time', title: '时间', width: 150, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'member', title: '会员', width: 160, truncate: true, cellClass: 'font-medium' },
  { key: 'gift', title: '礼品', truncate: true },
  { key: 'points', title: '扣积分', width: 110, headClass: 'text-right', cellClass: numCls },
  { key: 'operator', title: '经手人', width: 110, truncate: true, cellClass: 'text-xs' },
]

const BIRTHDAY_COLS: DataTableCol[] = [
  { key: 'member', title: '会员', truncate: true, cellClass: 'font-medium' },
  { key: 'action', title: '操作', width: 110, headClass: 'text-right', cellClass: 'text-right' },
]

const SLEEPING_COLS: DataTableCol[] = [
  { key: 'member', title: '会员', width: 150, truncate: true, cellClass: 'font-medium' },
  { key: 'last', title: '最近到店', width: 150, cellClass: 'tabular-nums text-destructive' },
  { key: 'balance', title: '余额', width: 130, headClass: 'text-right', cellClass: numCls },
  { key: 'action', title: '操作', width: 110, headClass: 'text-right', cellClass: 'text-right' },
]

export function PointsPage() {
  // 店主 10-24：默认进「积分商城」——这是店员/顾客最常看的页面（能换什么）
  const [tab, setTab] = useState<Tab>('mall')
  // 店主 10-24：非店长（收银员/技师）只看商城——能换什么、怎么兑换。
  // 调整积分 / 礼品增删改 / 积分规则 / 营销任务 / 兑换台账都是管理功能，只给店长。
  const mgr = isManager()
  const [rules, setRules] = useState({ earn: '', birthday: '', redeem: '', valid: '' })
  const [rulesOpen, setRulesOpen] = useState(false)
  const [rulePerYuan, setRulePerYuan] = useState('1')
  const [ruleBirthdayMult, setRuleBirthdayMult] = useState('2')
  const [rulePointsPerYuan, setRulePointsPerYuan] = useState('100')
  const [ruleValidMonths, setRuleValidMonths] = useState('12')
  const [ruleEnabled, setRuleEnabled] = useState(true)
  const [ruleEarnConsume, setRuleEarnConsume] = useState(true)
  const [ruleEarnRecharge, setRuleEarnRecharge] = useState(true)
  const [ruleError, setRuleError] = useState('')

  const [recordKeyword, setRecordKeyword] = useState('')
  const [gifts, setGifts] = useState<PointGift[]>([])
  const [members, setMembers] = useState<Member[]>([])
  const [birthday, setBirthday] = useState<{ id: number; name: string; level: string }[]>([])
  const [sleeping, setSleeping] = useState<{ id: number; name: string; lastVisit: string; balance: number }[]>([])

  const [redeemGift, setRedeemGift] = useState<PointGift | null>(null)
  const [redeemMemberId, setRedeemMemberId] = useState(0)
  const [redeemError, setRedeemError] = useState('')

  // 礼品管理（店主 10-24）：原先只能看和兑换，加不了新礼品
  const [giftDraft, setGiftDraft] = useState<{ id: number; name: string; points: string; stock: string } | null>(null)
  const [giftErr, setGiftErr] = useState<Record<string, string>>({})
  const [giftSaving, setGiftSaving] = useState(false)
  const [giftDelTarget, setGiftDelTarget] = useState<PointGift | null>(null)

  // 手工调整积分（店主 10-24）：默认按规则自动给分，这是补充手段（活动/补偿/纠错）
  const [adjOpen, setAdjOpen] = useState(false)
  const [adjMemberId, setAdjMemberId] = useState(0)
  const [adjChange, setAdjChange] = useState('')
  const [adjReason, setAdjReason] = useState('')
  const [adjErr, setAdjErr] = useState<Record<string, string>>({})
  const [adjSaving, setAdjSaving] = useState(false)

  // 兑换记录台账（店主 10-24）
  const [exchanges, setExchanges] = useState<{ id: number; time: string; memberName: string; giftName: string; points: number; operator: string }[]>([])

  const load = () => {
    apiPoints.gifts().then(setGifts).catch((e) => toast.error(String(e)))
    apiPoints.exchanges(200, 0).then(setExchanges).catch(() => setExchanges([]))
    apiPoints.rules().then((r) => { setRules({ earn: r.Earn, birthday: r.Birthday, redeem: r.Redeem, valid: r.Valid }) }).catch(() => setRules({ earn: '', birthday: '', redeem: '', valid: '' }))
    apiDashboard.overview().then((o) => { setBirthday(o.birthday); setSleeping(o.sleeping) }).catch(() => { setBirthday([]); setSleeping([]) })
    apiMembers.search('', '全部状态', '全部标签').then(setMembers).catch(() => setMembers([]))
  }
  useEffect(load, [])

  // 表格区高度 → 一页装几行（店主 11-04：装满窗口就翻页，不在页内滚动）
  const bodyRef = useRef<HTMLDivElement>(null)
  const autoSize = useAutoPageSize(bodyRef)

  // 积分明细 B2 页码：按 offset/limit 取单页；
  // total 走后端 recordsCount 全量条数（分页只截明细，不能拿前端 reduce 当总数）
  const page = usePagedNumbered<PointRecord>(
    (limit, offset) =>
      Promise.all([apiPoints.records(limit, offset), apiPoints.recordsCount()]).then(
        ([rows, total]) => ({ rows, total }),
      ),
    10,
    autoSize,
  )
  useEffect(() => {
    void page.reload()
  }, [page.reload])
  useEffect(() => {
    if (page.error) toast.error(page.error)
  }, [page.error])

  // 积分明细是本页唯一的过滤大表（最多 100 行可见）。过滤提到 useMemo，
  // 避免每次渲染都重建数组；行内没有交互按钮，PointRow 只吃 r 一个稳定引用即可命中 memo。
  const filteredRecords = useMemo(() => {
    const kw = recordKeyword.trim().toLowerCase()
    if (!kw) return page.rows
    return page.rows.filter(
      (r) => r.memberName.toLowerCase().includes(kw) || r.reason.toLowerCase().includes(kw),
    )
  }, [page.rows, recordKeyword])

  const chosen = members.find((m) => m.id === redeemMemberId)
  const redeemable = members.filter((m) => m.status === '正常')

  const submitRules = () => {
    const py = Number(rulePerYuan), bm = Number(ruleBirthdayMult), ppy = Number(rulePointsPerYuan), vm = Number(ruleValidMonths)
    if (!Number.isFinite(py) || py <= 0) { setRuleError('「多少元得 1 分」必须大于 0'); return }
    if (!Number.isFinite(bm) || bm < 1) { setRuleError('生日倍率不能小于 1'); return }
    if (!Number.isFinite(ppy) || ppy <= 0 || !Number.isInteger(ppy)) { setRuleError('「多少分抵 1 元」必须为正整数'); return }
    if (!Number.isFinite(vm) || vm < 0 || !Number.isInteger(vm)) { setRuleError('有效期必须为非负整数（0 = 永久）'); return }
    apiPoints
      .savePointConfig(py, bm, ppy, vm, ruleEnabled, ruleEarnConsume, ruleEarnRecharge)
      .then(() => {
        toast.success('积分规则已保存，对之后的单据生效（历史积分不重算）')
        setRulesOpen(false)
        setRuleError('')
        load()
      })
      .catch((e) => setRuleError(String(e)))
  }

  const submitRedeem = () => {
    if (!redeemGift) return
    if (!redeemMemberId) {
      setRedeemError('请选择兑换会员')
      return
    }
    if (!chosen || chosen.points < redeemGift.points) {
      setRedeemError(
        `积分不足：${chosen?.name ?? ''} 当前 ${chosen?.points ?? 0} 分，本次兑换需 ${redeemGift.points} 分，还差 ${redeemGift.points - (chosen?.points ?? 0)} 分`,
      )
      return
    }
    apiPoints
      .exchange(redeemMemberId, redeemGift.id)
      .then((after) => {
        toast.success(
          `兑换成功：${chosen.name} 用 ${redeemGift.points} 积分兑换「${redeemGift.name}」，剩余 ${after} 分，兑换单据已生成`,
        )
        setRedeemGift(null)
        setRedeemMemberId(0)
        setRedeemError('')
        load()
        void page.reload() // 兑换生成新的负向流水，回到第一页让它显示在最上
      })
      .catch((e) => setRedeemError(String(e)))
  }

  // ---- 礼品管理（店主 10-24）----
  const openGiftNew = () => {
    setGiftDraft({ id: 0, name: '', points: '', stock: '' })
    setGiftErr({})
  }
  const openGiftEdit = (g: PointGift) => {
    setGiftDraft({ id: g.id, name: g.name, points: String(g.points), stock: String(g.stock) })
    setGiftErr({})
  }
  const submitGift = () => {
    if (!giftDraft || giftSaving) return
    const errs: Record<string, string> = {}
    const name = giftDraft.name.trim()
    const pts = Number(giftDraft.points)
    const stk = Number(giftDraft.stock)
    if (!name) errs.name = '礼品名称不能为空'
    else if (name.length > 20) errs.name = '不能超过 20 个字'
    if (!Number.isFinite(pts) || pts <= 0) errs.points = '所需积分须为正整数'
    else if (!Number.isInteger(pts)) errs.points = '所需积分须为整数'
    if (giftDraft.stock.trim() === '') errs.stock = '库存不能为空'
    else if (!Number.isFinite(stk) || stk < 0) errs.stock = '库存不能为负数'
    else if (!Number.isInteger(stk)) errs.stock = '库存须为整数'
    setGiftErr(errs)
    if (Object.keys(errs).length > 0) return

    setGiftSaving(true)
    apiPoints
      .saveGift(giftDraft.id, name, pts, stk)
      .then(() => {
        toast.success(giftDraft.id === 0 ? `已添加礼品「${name}」` : `已保存礼品「${name}」`)
        setGiftDraft(null)
        load()
      })
      .catch((e) => setGiftErr({ name: String(e) }))
      .finally(() => setGiftSaving(false))
  }
  const confirmDeleteGift = () => {
    if (!giftDelTarget) return
    apiPoints
      .deleteGift(giftDelTarget.id)
      .then(() => {
        toast.success(`已删除礼品「${giftDelTarget.name}」`)
        setGiftDelTarget(null)
        load()
      })
      .catch((e) => toast.error(String(e)))
  }

  // ---- 手工调整积分（店主 10-24）----
  const openAdjust = () => {
    setAdjMemberId(0)
    setAdjChange('')
    setAdjReason('')
    setAdjErr({})
    setAdjOpen(true)
  }
  const submitAdjust = () => {
    if (adjSaving) return
    const errs: Record<string, string> = {}
    const ch = Number(adjChange)
    if (!adjMemberId) errs.member = '请选择会员'
    if (adjChange.trim() === '') errs.change = '请填写调整分值（正数加分、负数扣分）'
    else if (!Number.isInteger(ch) || ch === 0) errs.change = '须为非零整数'
    if (!adjReason.trim()) errs.reason = '请填写原因'
    setAdjErr(errs)
    if (Object.keys(errs).length > 0) return

    setAdjSaving(true)
    apiPoints
      .adjustPoints(adjMemberId, ch, adjReason.trim())
      .then((after) => {
        const nm = members.find((m) => m.id === adjMemberId)?.name ?? ''
        toast.success(`已调整：${nm} ${ch > 0 ? '+' : ''}${ch} 分，调整后 ${after} 分`)
        setAdjOpen(false)
        load()
        void page.reload()
      })
      .catch((e) => setAdjErr({ change: String(e) }))
      .finally(() => setAdjSaving(false))
  }

  return (
    <PageFill minWidth={1030}>
      {/* 自适应布局（店主 11-04）：除页签内容外全部 shrink-0，页签内容 flex-1 吃满剩余高度 */}
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            积分与营销
          </h1>
          <p className="text-xs text-muted-foreground mt-1">{mgr ? '积分规则与兑换' : '积分商城'}</p>
        </div>
        {mgr ? (
          <Button variant="secondary" onClick={() => { setRulesOpen(true); apiPoints.rules().then((r) => { setRulePerYuan(String(r.PerYuan)); setRuleBirthdayMult(String(r.BirthdayMult)); setRulePointsPerYuan(String(r.PointsPerYuan)); setRuleValidMonths(String(r.ValidMonths)); setRuleEnabled(r.Enabled); setRuleEarnConsume(r.EarnConsume); setRuleEarnRecharge(r.EarnRecharge) }) }}>
            <Settings2 size={14} /> 积分规则设置
          </Button>
        ) : null}
      </div>

      {/* 当前积分规则：非店长隐藏（管理视角信息，客人不需要看） */}
      {mgr ? (
        <SectionCard className="shrink-0" title="当前积分规则" bodyClassName="px-4 py-3">
          <div className="grid grid-cols-2 gap-3 text-xs">
            <p className="flex items-start gap-2">
              <Badge tone="primary">获取</Badge>
              <span className="text-muted-foreground leading-5">{rules.earn || '—'}</span>
            </p>
            <p className="flex items-start gap-2">
              <Badge tone="primary">生日</Badge>
              <span className="text-muted-foreground leading-5">{rules.birthday || '—'}</span>
            </p>
            <p className="flex items-start gap-2">
              <Badge tone="primary">兑换</Badge>
              <span className="text-muted-foreground leading-5">{rules.redeem || '—'}</span>
            </p>
            <p className="flex items-start gap-2">
              <Badge tone="primary">有效期</Badge>
              <span className="text-muted-foreground leading-5">{rules.valid || '—'}</span>
            </p>
          </div>
        </SectionCard>
      ) : null}

      {/* 指标卡：非店长只看礼品种类（其他是经营数据，属店长视角） */}
      <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))] shrink-0">
        {mgr ? <MetricCard label="积分明细条数" value={String(page.total)} prefix="" icon={<Gift size={17} />} /> : null}
        <MetricCard label="礼品种类" value={String(gifts.length)} prefix="" icon={<ShoppingBag size={17} />} />
        {mgr ? <MetricCard label="本月生日会员" value={String(birthday.length)} prefix="" icon={<Cake size={17} />} /> : null}
        {mgr ? <MetricCard label="沉睡会员（60天未到店）" value={String(sleeping.length)} prefix="" icon={<Moon size={17} />} /> : null}
      </div>

      <div className="flex gap-1 shrink-0">
        {/* 店主 10-24：非店长只给「积分商城」——客人/店员视角只看能换什么 */}
        {((mgr
          ? [['records', '积分明细'], ['mall', '积分商城'], ['exchanges', '兑换记录'], ['marketing', '营销任务']]
          : [['mall', '积分商城']]) as [Tab, string][]).map(([k, label]) => (
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

      {/* 页签内容区：flex-1 吃满剩余高度；内容超出时内部滚动（短页签不留空档、长页签不被裁） */}
      <div className="page-body overflow-y-auto flex flex-col">
      {mgr && tab === 'records' ? (
        <SectionCard title="积分明细" extra={
          <span className="flex items-center gap-2">
            {/* 手工调整（店主 10-24）：活动奖励 / 补偿 / 纠错。非店长看不到这个 tab，双保险 */}
            {mgr ? (
              <Button variant="secondary" size="sm" onClick={openAdjust} title="手工加分或扣分（活动奖励、补偿、纠错）">
                调整积分
              </Button>
            ) : null}
            <span className="relative block">
              <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
              <Input
                value={recordKeyword}
                onChange={(e) => setRecordKeyword(e.target.value)}
                placeholder="搜索会员 / 原因"
                aria-label="搜索积分明细"
                className="h-7 w-48 pl-7 text-xs"
              />
            </span>
          </span>
        } className="flex flex-col" bodyClassName="p-0 flex-1 min-h-0 flex flex-col">
          {/* 常驻包裹层：供 useAutoPageSize 量「一页装几行」 */}
          <div ref={bodyRef} className="flex-1 min-h-0 flex flex-col">
          <DataTable
            cols={POINT_COLS}
            rows={filteredRecords}
            rowKey={(r, i) => i}
            renderRow={(r, i) => <PointRow key={i} r={r} />}
            plain
            fill
            empty={
              page.loading && page.rows.length === 0 ? (
                <div className="py-8 text-center text-xs text-muted-foreground">加载中…</div>
              ) : (
                <div className="py-8 text-center text-xs text-muted-foreground">
                  {recordKeyword.trim() ? '没有匹配的积分流水，换个关键词试试' : '暂无积分流水；充值/消费后自动累计'}
                </div>
              )
            }
          />
          </div>
          {page.total > 0 ? (
            <PagedBar
              className="shrink-0"
              total={page.total}
              page={page.page}
              pageCount={page.pageCount}
              pageSize={page.pageSize}
              goToPage={page.goToPage}
              onPageSizeChange={page.setPageSize}
              loading={page.loading}
            />
          ) : null}
        </SectionCard>
      ) : null}

      {tab === 'mall' ? (
        <>
          <div className="flex items-center justify-between">
            <span className="text-xs text-muted-foreground">
              共 {gifts.length} 种礼品
            </span>
            {/* 店主 10-24：礼品增删改只给店长，员工/客人只能看和兑换 */}
            {mgr ? (
              <Button variant="primary" size="sm" onClick={openGiftNew}>
                <Gift size={13} /> 新增礼品
              </Button>
            ) : null}
          </div>
          {gifts.length === 0 ? (
            <div className="py-10 text-center text-sm text-muted-foreground">
              还没有礼品，点右上角「新增礼品」建第一个
            </div>
          ) : (
            <div className="grid gap-4 [grid-template-columns:repeat(auto-fit,minmax(180px,1fr))]">
              {gifts.map((g) => (
                <div
                  key={g.id}
                  className="bg-card border border-border rounded-[var(--radius-md)] shadow-[var(--elev-ring)] p-4 flex flex-col gap-2 hover:border-primary/40 transition-colors"
                >
                  <div className="w-10 h-10 rounded-[var(--radius-sm)] bg-primary/10 text-primary flex items-center justify-center">
                    <Gift size={19} />
                  </div>
                  <p className="text-sm font-semibold leading-tight">{g.name}</p>
                  <p className="text-xs text-primary font-bold tabular-nums">{g.points} 积分</p>
                  <p className="text-[11px] text-muted-foreground">
                    库存 {g.stock} {g.stock <= 5 ? <span className="text-destructive ml-1">库存紧张</span> : null}
                  </p>
                  <Button
                    variant="secondary"
                    size="sm"
                    className="mt-auto"
                    disabled={g.stock === 0}
                    onClick={() => { setRedeemGift(g); setRedeemMemberId(0); setRedeemError('') }}
                  >
                    <ShoppingBag size={12} /> 兑换
                  </Button>
                  {mgr ? (
                    <div className="flex gap-1">
                      <Button variant="ghost" size="sm" className="flex-1" onClick={() => openGiftEdit(g)}>
                        编辑
                      </Button>
                      <Button variant="ghost" size="sm" className="flex-1" onClick={() => setGiftDelTarget(g)}>
                        删除
                      </Button>
                    </div>
                  ) : null}
                </div>
              ))}
            </div>
          )}
        </>
      ) : null}

      {mgr && tab === 'exchanges' ? (
        <SectionCard
          title="兑换记录"
          extra={<span className="text-[11px] text-muted-foreground">共 {exchanges.length} 条</span>}
          bodyClassName="p-0"
        >
          <DataTable
            cols={EXCHANGE_COLS}
            rows={exchanges}
            rowKey={(e) => e.id}
            fill
            plain
            render={(e) => [
              e.time && e.time.length >= 16 ? e.time.substring(0, 16) : e.time,
              e.memberName,
              e.giftName,
              <span key="p" className="text-primary font-semibold tabular-nums">-{e.points}</span>,
              e.operator,
            ]}
            empty={
              <div className="py-8 text-center text-sm text-muted-foreground">
                还没有兑换记录
              </div>
            }
          />
        </SectionCard>
      ) : null}

      {mgr && tab === 'marketing' ? (
        <div className="grid grid-cols-2 gap-4">
          <SectionCard
            title="今日生日提醒"
            extra={<Badge tone="primary">{birthday.length} 位</Badge>}
            bodyClassName="p-0"
          >
            <DataTable
              cols={BIRTHDAY_COLS}
              rows={birthday}
              rowKey={(m) => m.id}
              fill
              render={(m) => [
                <span key="m" className="flex items-center gap-1.5">
                  <Cake size={13} className="text-primary" /> {m.name}（{m.level}）
                </span>,
                <Button key="a" variant="ghost" size="sm" onClick={() => apiDashboard.bless(m.id, m.name).then(() => toast.success(`已为「${m.name}」登记生日祝福任务`)).catch((e) => toast.error(String(e)))}>
                  <MessageCircleHeart size={12} /> 送祝福
                </Button>,
              ]}
              empty={<div className="py-6 text-center text-xs text-muted-foreground">今天没有会员生日</div>}
            />
          </SectionCard>

          <SectionCard
            title="沉睡会员唤醒（60 天未到店）"
            bodyClassName="p-0"
          >
            <DataTable
              cols={SLEEPING_COLS}
              rows={sleeping}
              rowKey={(m) => m.id}
              plain
              fill
              render={(m) => [
                m.name,
                m.lastVisit,
                `¥${fmtMoney(m.balance)}`,
                <Button key="a" variant="ghost" size="sm" onClick={() => apiDashboard.wake(m.id, m.name).then(() => toast.success(`已为「${m.name}」登记唤醒任务`)).catch((e) => toast.error(String(e)))}>
                  <MessageCircleHeart size={12} /> 去唤醒
                </Button>,
              ]}
              empty={<div className="py-6 text-center text-xs text-muted-foreground">暂无 60 天未到店的会员</div>}
            />
          </SectionCard>
        </div>
      ) : null}
      </div>

      {rulesOpen ? (
        <Modal
          title="积分规则设置"
          onClose={() => setRulesOpen(false)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setRulesOpen(false)}>取消</Button>
              <Button variant="primary" onClick={submitRules}>保存规则</Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="rounded-[var(--radius-sm)] border border-border bg-muted/40 px-3 py-2.5 space-y-2">
              <label className="flex items-center gap-2 text-sm font-medium cursor-pointer">
                <input type="checkbox" checked={ruleEnabled} onChange={(e) => setRuleEnabled(e.target.checked)} className="accent-[hsl(var(--primary))]" />
                启用积分
                <span className="text-[11px] text-muted-foreground font-normal">停用后不再累加新积分</span>
              </label>
              <div className={cx('flex flex-col gap-1.5 pl-6 transition-opacity', !ruleEnabled && 'opacity-50 pointer-events-none')}>
                <label className="flex items-center gap-2 text-sm cursor-pointer">
                  <input type="checkbox" checked={ruleEarnConsume} onChange={(e) => setRuleEarnConsume(e.target.checked)} className="accent-[hsl(var(--primary))]" />
                  消费可获得积分（收银结账时按实收累加）
                </label>
                <label className="flex items-center gap-2 text-sm cursor-pointer">
                  <input type="checkbox" checked={ruleEarnRecharge} onChange={(e) => setRuleEarnRecharge(e.target.checked)} className="accent-[hsl(var(--primary))]" />
                  充值可获得积分（储值充值与建档初始充值）
                </label>
              </div>
            </div>
            <Field label="多少元得 1 积分" required hint="消费/充值每满此金额即累加 1 分">
              <Input type="number" min="0.01" step="0.01" value={rulePerYuan} onChange={(e) => setRulePerYuan(e.target.value)} />
            </Field>
            <Field label="生日倍率" required hint="生日当天积分按此倍数计算（1 = 不加倍）">
              <Input type="number" min="1" step="0.5" value={ruleBirthdayMult} onChange={(e) => setRuleBirthdayMult(e.target.value)} />
            </Field>
            <Field label="多少积分抵 1 元" required hint="仅限积分商城兑换，不可直接抵现">
              <Input type="number" min="1" step="1" value={rulePointsPerYuan} onChange={(e) => setRulePointsPerYuan(e.target.value)} />
            </Field>
            <Field label="有效期（月）" required hint="0 = 永久有效，到期自动失效">
              <Input type="number" min="0" max="120" step="1" value={ruleValidMonths} onChange={(e) => setRuleValidMonths(e.target.value)} />
            </Field>
            {ruleError ? (
              <p className="text-xs text-destructive flex items-center gap-1.5" role="alert">
                <AlertCircle size={13} /> {ruleError}
              </p>
            ) : null}
            <p className="text-[11px] text-muted-foreground">修改后只对新单据生效，历史积分不重算。</p>
          </div>
        </Modal>
      ) : null}

      {redeemGift ? (
        <Modal
          title={`兑换「${redeemGift.name}」（${redeemGift.points} 积分）`}
          onClose={() => setRedeemGift(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setRedeemGift(null)}>取消</Button>
              <Button variant="primary" onClick={submitRedeem}>确认兑换</Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="兑换会员" required>
              <Select value={redeemMemberId} onChange={(e) => { setRedeemMemberId(Number(e.target.value)); setRedeemError('') }}>
                <option value={0} disabled>请选择会员</option>
                {redeemable.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}（{m.points.toLocaleString('zh-CN')} 分{m.points < redeemGift.points ? ' · 不足' : ''}）
                  </option>
                ))}
              </Select>
            </Field>
            {chosen ? (
              <p className="text-xs bg-muted/60 rounded-[var(--radius-sm)] px-3 py-2 tabular-nums">
                兑换后剩余：{chosen.points.toLocaleString('zh-CN')} − {redeemGift.points} ={' '}
                <span className={cx('font-bold', chosen.points - redeemGift.points < 0 ? 'text-destructive' : 'text-primary')}>
                  {(chosen.points - redeemGift.points).toLocaleString('zh-CN')}
                </span>{' '}
                分
              </p>
            ) : null}
            {redeemError ? (
              <div className="rounded-[var(--radius-sm)] bg-destructive/8 border border-destructive/30 px-2.5 py-2 text-[11px] text-destructive flex items-start gap-1.5" role="alert">
                <AlertCircle size={13} className="shrink-0 mt-px" /> {redeemError}
              </div>
            ) : null}
          </div>
        </Modal>
      ) : null}

      {/* 礼品新增/编辑（店主 10-24） */}
      {giftDraft ? (
        <Modal
          title={giftDraft.id === 0 ? '新增礼品' : `编辑礼品 · ${giftDraft.name}`}
          onClose={() => setGiftDraft(null)}
          width={420}
          footer={
            <>
              <Button variant="secondary" onClick={() => setGiftDraft(null)}>
                取消
              </Button>
              <Button variant="primary" onClick={submitGift} disabled={giftSaving}>
                {giftSaving ? '保存中…' : giftDraft.id === 0 ? '确认添加' : '保存'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="礼品名称" required error={giftErr.name}>
              <Input
                value={giftDraft.name}
                onChange={(e) => setGiftDraft({ ...giftDraft, name: e.target.value })}
                placeholder="如 洗发水 500ml"
                autoFocus
              />
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="所需积分" required error={giftErr.points}>
                <Input
                  value={giftDraft.points}
                  onChange={(e) => setGiftDraft({ ...giftDraft, points: e.target.value })}
                  placeholder="如 500"
                  className="tabular-nums"
                  inputMode="numeric"
                />
              </Field>
              <Field label="库存" required error={giftErr.stock}>
                <Input
                  value={giftDraft.stock}
                  onChange={(e) => setGiftDraft({ ...giftDraft, stock: e.target.value })}
                  placeholder="如 20"
                  className="tabular-nums"
                  inputMode="numeric"
                />
              </Field>
            </div>
          </div>
        </Modal>
      ) : null}

      {/* 手工调整积分（店主 10-24） */}
      {adjOpen ? (
        <Modal
          title="调整积分"
          onClose={() => setAdjOpen(false)}
          width={440}
          footer={
            <>
              <Button variant="secondary" onClick={() => setAdjOpen(false)}>
                取消
              </Button>
              <Button variant="primary" onClick={submitAdjust} disabled={adjSaving}>
                {adjSaving ? '提交中…' : '确认调整'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="会员" required error={adjErr.member}>
              <Select
                value={adjMemberId}
                onChange={(e) => setAdjMemberId(Number(e.target.value))}
                aria-label="选择会员"
              >
                <option value={0}>请选择会员</option>
                {members.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}（{m.cardNo} · {m.points ?? 0} 分）
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="调整分值（正数加分 / 负数扣分）" required error={adjErr.change}>
              <Input
                value={adjChange}
                onChange={(e) => setAdjChange(e.target.value)}
                placeholder="如 100 或 -50"
                className="tabular-nums"
                inputMode="numeric"
              />
            </Field>
            <Field label="原因" required error={adjErr.reason}>
              <Input
                value={adjReason}
                onChange={(e) => setAdjReason(e.target.value)}
                placeholder="如 双十一活动奖励、上次少算补偿"
              />
            </Field>
            <p className="text-[11px] text-muted-foreground">
              调整会写入积分流水和操作日志，可查。日常给分仍按积分规则自动计算，这里只用于活动奖励、补偿或纠错。
            </p>
          </div>
        </Modal>
      ) : null}

      {/* 删除礼品确认 */}
      {giftDelTarget ? (
        <Modal
          title="删除礼品"
          onClose={() => setGiftDelTarget(null)}
          width={420}
          footer={
            <>
              <Button variant="secondary" onClick={() => setGiftDelTarget(null)}>
                取消
              </Button>
              <Button variant="primary" onClick={confirmDeleteGift}>
                确认删除
              </Button>
            </>
          }
        >
          <p className="text-sm">
            确定删除礼品「<span className="font-semibold">{giftDelTarget.name}</span>」吗？
          </p>
          <p className="text-[11px] text-muted-foreground mt-2">
            如果这个礼品已经被兑换过，会改为「下架」处理（库存清零、不再出现在兑换列表），历史兑换记录完整保留。
          </p>
        </Modal>
      ) : null}
    </PageFill>
  )
}

/**
 * 积分明细单行：行内没有交互按钮，props 只有 r 一个，且分页/筛选时
 * page.rows 里的对象引用不变，memo 因此命中、不随行数放大重渲。
 */
const PointRow = memo(function PointRow({ r }: { r: PointRecord }) {
  return (
    <tr className="even:bg-muted/40">
      <td className={cx(tdCls, 'text-muted-foreground tabular-nums')}>{r.time}</td>
      <td className={cx(tdCls, 'font-medium')}>{r.memberName}</td>
      <td className={cx(tdCls, numCls, r.change >= 0 ? 'text-primary' : 'text-destructive')}>
        {r.change >= 0 ? `+${r.change}` : r.change}
      </td>
      <td className={cx(tdCls, 'text-muted-foreground')}>{r.reason}</td>
      <td className={cx(tdCls, numCls)}>{r.balanceAfter.toLocaleString('zh-CN')}</td>
    </tr>
  )
})
