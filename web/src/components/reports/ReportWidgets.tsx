import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } from 'recharts'
import { SectionCard, Badge, DataTable, numCls, cx, LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { fmtMoney } from '@/data/mock'

const tooltipStyle = {
  background: 'hsl(var(--card))',
  border: '1px solid hsl(var(--border))',
  borderRadius: 'var(--radius-sm)',
  fontSize: 12,
} as const

/** 图表配色：三块必须是三个能一眼分开的颜色（同色系深浅看不出谁是谁，店主 10-08 否掉了）。
    按「类别名」上色而不是按下标——环上会过滤掉 0 值分片，用下标会导致颜色和图例对不上。 */
const CHART_COLORS = ['hsl(var(--chart-1))', 'hsl(var(--chart-2))', 'hsl(var(--chart-3))']
const CATEGORY_COLORS: Record<string, string> = {
  '项目/服务': CHART_COLORS[0],
  售卡: CHART_COLORS[1],
  会员充值: CHART_COLORS[2],
}
const colorOf = (name: string, i: number) => CATEGORY_COLORS[name] ?? CHART_COLORS[i % CHART_COLORS.length]

/** 员工业绩榜每人专属色（12 色循环，避免 4 名起全灰分不清谁是谁） */
const EMPLOYEE_COLORS = [
  'hsl(210 85% 55%)', 'hsl(340 80% 55%)', 'hsl(150 60% 45%)',
  'hsl(280 60% 55%)', 'hsl(35 90% 50%)', 'hsl(190 70% 50%)',
  'hsl(0 75% 55%)', 'hsl(260 50% 55%)', 'hsl(120 50% 45%)',
  'hsl(25 80% 50%)', 'hsl(300 50% 50%)', 'hsl(170 50% 45%)',
]

/**
 * 营收构成环形图。
 * 三块固定为「散单 / 项目 / 充值」—— 对应系统三类单据 kind='消费' / '售卡' / '充值'，
 * 与上方指标卡同源，合计 = 散单 + 项目 + 充值。
 * 每块都写明「色块 + 名称 + 口径说明 + 金额 + 占比」，扇区内再压一个百分比，
 * 与下方图例的百分比对上就能确认哪块是谁（店主 10-08：「饼图没写明哪些是哪些」）。
 */
export function RevenuePie({
  data,
  range,
}: {
  data: { name: string; value: number; hint?: string }[]
  range: string
}) {
  const total = data.reduce((s, d) => s + d.value, 0)
  // 环上只画有数的分片（0 值分片会画出空档）；图例恒列全部三类，
  // 这样店主任何时候都能看到「散单 / 项目 / 充值」这三类都在，而不是某类为 0 时整行消失。
  const slices = data.filter((d) => d.value > 0)
  return (
    <SectionCard
      title={`营收构成（${range}）`}
      extra={<span className="text-[11px] text-muted-foreground">合计收款 ¥{fmtMoney(total)}</span>}
      bodyClassName="p-3"
    >
      {total <= 0 ? (
        <div className="h-[236px] flex items-center justify-center px-3">
          <p className="text-xs text-muted-foreground text-center leading-relaxed">
            {range}暂无营收数据
            <br />
            产生消费、售卡或充值后自动统计
          </p>
        </div>
      ) : (
        <>
          <div className="relative">
            <ResponsiveContainer width="100%" height={168}>
              <PieChart>
                <Pie
                  data={slices}
                  dataKey="value"
                  nameKey="name"
                  innerRadius={48}
                  outerRadius={74}
                  paddingAngle={2}
                  strokeWidth={0}
                  /* 扇区内压百分比（太小的块压不下就不压），与下方图例的百分比一一对应。
                     只有一块时不压——那是 100%，图例已经写了，压在环上还会挤到环心的合计。 */
                  label={({ cx, cy, midAngle, innerRadius, outerRadius, percent }: any) => {
                    if (slices.length < 2 || !percent || percent < 0.08) return null
                    const rad = Math.PI / 180
                    const r = innerRadius + (outerRadius - innerRadius) * 0.5
                    return (
                      <text
                        x={cx + r * Math.cos(-midAngle * rad)}
                        y={cy + r * Math.sin(-midAngle * rad)}
                        fill="#fff"
                        textAnchor="middle"
                        dominantBaseline="central"
                        fontSize={11}
                        fontWeight={700}
                      >
                        {`${Math.round(percent * 100)}%`}
                      </text>
                    )
                  }}
                  labelLine={false}
                >
                  {slices.map((d, i) => (
                    <Cell key={d.name} fill={colorOf(d.name, i)} />
                  ))}
                </Pie>
                <Tooltip
                  formatter={(v: number, n: string) => [`¥${fmtMoney(v)}　${total > 0 ? ((v / total) * 100).toFixed(1) : '0.0'}%`, n]}
                  contentStyle={tooltipStyle}
                />
              </PieChart>
            </ResponsiveContainer>
            {/* 环心：合计收款（三块之和，含充值） */}
            <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none">
              <span className="text-[10px] text-muted-foreground">合计收款</span>
              <span className="text-sm font-bold text-foreground tabular-nums">¥{fmtMoney(total)}</span>
            </div>
          </div>
          {/* 自绘图例：色块与扇区同色，写明名称 / 口径 / 金额 / 占比。
              恒列全部类别（某类为 0 也列出来，显示 ¥0.00 并压暗），这样「哪三类」永远是明确的。 */}
          <ul className="mt-2.5 space-y-1">
            {data.map((d, i) => {
              const empty = d.value <= 0
              return (
                <li key={d.name} className={cx('flex items-center gap-2 text-xs', empty && 'opacity-55')}>
                  <span
                    className="w-2.5 h-2.5 rounded-[3px] shrink-0"
                    style={{ background: empty ? 'hsl(var(--border))' : colorOf(d.name, i) }}
                  />
                  <span className="font-medium text-foreground">{d.name}</span>
                  {d.hint ? <span className="text-[10px] text-muted-foreground">{d.hint}</span> : null}
                  <span className="ml-auto tabular-nums text-muted-foreground">¥{fmtMoney(d.value)}</span>
                  <span className="tabular-nums font-semibold text-foreground w-10 text-right">
                    {total > 0 ? `${((d.value / total) * 100).toFixed(1)}%` : '—'}
                  </span>
                </li>
              )
            })}
          </ul>
        </>
      )}
    </SectionCard>
  )
}

/** 奖牌色：金 / 银 / 铜（前三名大卡用） */
const MEDALS = [
  { bg: 'hsl(45 93% 47%)', label: 'NO.1' },
  { bg: 'hsl(215 13% 62%)', label: 'NO.2' },
  { bg: 'hsl(28 68% 52%)', label: 'NO.3' },
]

/** 员工当日明细 10 列预算（≈928px，col-span-3 区域下不横滚） */
const ACTIVITY_COLS: DataTableCol[] = [
  { key: 'no', title: '序号', width: 48 },
  { key: 'staff', title: '员工', width: 90, cellClass: 'font-semibold' },
  { key: 'role', title: '角色', width: 80 },
  { key: 'items', title: '服务项目', width: 90, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'consume', title: '服务金额', width: 110, headClass: 'text-right', cellClass: numCls },
  { key: 'rc', title: '充值笔数', width: 80, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'ra', title: '充值金额', width: 110, headClass: 'text-right', cellClass: numCls },
  { key: 'sc', title: '售卡笔数', width: 80, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'sa', title: '售卡金额', width: 110, headClass: 'text-right', cellClass: numCls },
  { key: 'total', title: '合计营收', width: 120, headClass: 'text-right', cellClass: cx(numCls, 'text-primary font-semibold') },
]

/** 各项目业绩 7 列预算（≈1050px，col-span-3 区域下不横滚） */
const PROJECT_COLS: DataTableCol[] = [
  { key: 'item', title: '项目', truncate: true, cellClass: 'font-medium' },
  { key: 'unit', title: '单位', width: 90, headClass: 'text-right', cellClass: 'text-muted-foreground' },
  { key: 'qty', title: '单量', width: 90, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'perf', title: '业绩（元）', width: 200, headClass: 'text-right', cellClass: numCls },
  { key: 'mode', title: '提成方式', width: 150, headClass: 'text-right', cellClass: cx(numCls, 'text-muted-foreground') },
  { key: 'commission', title: '估算提成（元）', width: 140, headClass: 'text-right', cellClass: cx(numCls, 'text-primary font-semibold') },
  { key: 'onsale', title: '在售', width: 100 },
]

/**
 * 员工业绩榜（店主 10-08 定：删掉「每日营收」柱状图后，这块顶上图表区并做突出）。
 * 前三名 = 三张大卡（奖牌徽章 / 姓名 / 业绩金额 / 构成条 / 单数 / 提成），第 4 名起走紧凑列表。
 * 排序由后端 Ranks 保证：业绩（充值+消费）倒序，业绩并列时提成倒序破平。
 */
export function RankBoard({
  data,
  month,
}: {
  data: { name: string; rechargeAmount: number; consumeAmount: number; commission: number; orders: number; settled?: number; unsettled?: number }[]
  month: string
}) {
  const rows = data.map((s) => ({ ...s, total: s.rechargeAmount + s.consumeAmount }))
  const max = Math.max(1, ...rows.map((r) => r.total))
  const top = rows.slice(0, 3)
  const rest = rows.slice(3)
  const sumTotal = rows.reduce((s, r) => s + r.total, 0)
  const sumCommission = rows.reduce((s, r) => s + r.commission, 0)
  const sumOrders = rows.reduce((s, r) => s + r.orders, 0)
  // B1（10-21）：已结/未结合计——发工资对账一眼看清「结了多少、还欠多少」
  const sumSettled = rows.reduce((s, r) => s + (r.settled ?? 0), 0)
  const sumUnsettled = rows.reduce((s, r) => s + (r.unsettled ?? 0), 0)

  return (
    <SectionCard
      title={`员工业绩榜（${month}）`}
      extra={<span className="text-[11px] text-muted-foreground">按业绩倒序 · 按单实时核算，红冲同笔扣回</span>}
      className="col-span-2"
      bodyClassName="p-3"
    >
      {rows.length === 0 ? (
        <div className="h-[236px] flex items-center justify-center px-3">
          <p className="text-xs text-muted-foreground text-center leading-relaxed">
            {month} 暂无业绩数据
            <br />
            发生散单消费或会员充值后自动计提
          </p>
        </div>
      ) : (
        <>
          {/* 前三名大卡 */}
          <div className={cx('grid gap-2.5', top.length === 3 ? 'grid-cols-3' : top.length === 2 ? 'grid-cols-2' : 'grid-cols-1')}>
            {top.map((s, i) => {
              const wR = (s.rechargeAmount / max) * 100
              const wC = (s.consumeAmount / max) * 100
              return (
                <div
                  key={s.name}
                  className="rounded-[var(--radius-sm)] border border-border bg-muted/30 px-3 py-3"
                >
                  <div className="flex items-center gap-2.5">
                    {/* 姓名首字头像圈，底色/字色都跟奖牌色走 */}
                    <span
                      className="inline-flex items-center justify-center rounded-full text-sm font-bold shrink-0"
                      style={{ background: MEDALS[i].bg + '2e', color: MEDALS[i].bg, width: 34, height: 34 }}
                    >
                      {s.name.slice(0, 1)}
                    </span>
                    <div className="min-w-0">
                      <span
                        className="inline-flex items-center justify-center rounded-[var(--radius-pill)] text-[10px] font-bold text-white tabular-nums"
                        style={{ background: MEDALS[i].bg, height: 17, padding: '0 7px' }}
                      >
                        {MEDALS[i].label}
                      </span>
                      <div className="text-sm font-semibold text-foreground truncate mt-0.5">{s.name}</div>
                    </div>
                  </div>
                  <div className="mt-2 text-xl font-bold text-primary tabular-nums leading-none">
                    ¥{fmtMoney(s.total)}
                  </div>
                  {/* 构成条：充值业绩（青绿，与饼图「充值」同色）+ 消费业绩（蓝，与饼图「散单」同色） */}
                  <span className="flex h-1.5 mt-2 rounded-[var(--radius-pill)] overflow-hidden bg-border">
                    <span className="h-full" style={{ width: `${wR}%`, background: 'hsl(var(--chart-3))' }} />
                    <span className="h-full" style={{ width: `${wC}%`, background: 'hsl(var(--chart-1))' }} />
                  </span>
                  <div className="mt-2 flex items-center gap-2 text-[10px] text-muted-foreground tabular-nums">
                    <span className="inline-flex items-center gap-1">
                      <span className="w-1.5 h-1.5 rounded-[2px]" style={{ background: 'hsl(var(--chart-3))' }} />
                      充值 ¥{fmtMoney(s.rechargeAmount)}
                    </span>
                    <span className="inline-flex items-center gap-1">
                      <span className="w-1.5 h-1.5 rounded-[2px]" style={{ background: 'hsl(var(--chart-1))' }} />
                      消费 ¥{fmtMoney(s.consumeAmount)}
                    </span>
                  </div>
                  <div className="mt-1.5 flex items-center justify-between text-[10px] text-muted-foreground tabular-nums">
                    <span>{s.orders} 单</span>
                    <span>
                      提成 <b className="text-foreground font-semibold">¥{fmtMoney(s.commission)}</b>
                    </span>
                  </div>
                </div>
              )
            })}
          </div>

          {/* 第 4 名起：紧凑列表 */}
          {rest.length > 0 ? (
            <div className="mt-2.5 border-t border-border pt-1">
              {rest.map((s, i) => {
                const wR = (s.rechargeAmount / max) * 100
                const wC = (s.consumeAmount / max) * 100
                return (
                  <div key={s.name} className="flex items-center gap-2.5 h-9 border-b border-border/50 last:border-b-0">
                    <span
                      className="inline-flex items-center justify-center rounded-full text-[11px] font-bold tabular-nums shrink-0"
                      style={{ background: EMPLOYEE_COLORS[i % EMPLOYEE_COLORS.length] + '2e', color: EMPLOYEE_COLORS[i % EMPLOYEE_COLORS.length], width: 20, height: 20 }}
                    >
                      {i + 4}
                    </span>
                    <span className="text-xs font-medium text-foreground w-20 truncate">{s.name}</span>
                    <span className="flex h-1.5 flex-1 min-w-[60px] rounded-[var(--radius-pill)] overflow-hidden bg-border">
                      <span className="h-full" style={{ width: `${wR}%`, background: 'hsl(var(--chart-3))' }} />
                      <span className="h-full" style={{ width: `${wC}%`, background: 'hsl(var(--chart-1))' }} />
                    </span>
                    <span className="text-xs font-semibold text-foreground tabular-nums w-20 text-right">
                      ¥{fmtMoney(s.total)}
                    </span>
                    <span className="text-[10px] text-muted-foreground tabular-nums w-10 text-right">{s.orders} 单</span>
                    <span className="text-[10px] text-muted-foreground tabular-nums w-16 text-right">
                      提成 ¥{fmtMoney(s.commission)}
                    </span>
                  </div>
                )
              })}
            </div>
          ) : null}

          {/* 全店合计条：人数 / 总业绩 / 总单数 / 总提成（把卡片高度填平，也省得自己心算） */}
          <div className="mt-2.5 rounded-[var(--radius-sm)] bg-muted/50 px-3 h-9 flex items-center gap-4 text-[11px] text-muted-foreground tabular-nums">
            <span>
              全店 <b className="text-foreground font-semibold">{rows.length}</b> 人
            </span>
            <span className="w-px h-3.5 bg-border" />
            <span>
              业绩合计 <b className="text-foreground font-semibold">¥{fmtMoney(sumTotal)}</b>
            </span>
            <span className="w-px h-3.5 bg-border" />
            <span>
              <b className="text-foreground font-semibold">{sumOrders}</b> 单
            </span>
            <span className="ml-auto">
              提成合计 <b className="text-primary font-semibold">¥{fmtMoney(sumCommission)}</b>
            </span>
          </div>
          {/* B1：已结/未结拆分条——和 op_logs 结算批次对得上，发工资用 */}
          <div className="mt-1 rounded-[var(--radius-sm)] bg-muted/50 px-3 h-9 flex items-center gap-4 text-[11px] text-muted-foreground tabular-nums">
            <span>
              其中已结 <b className="text-foreground font-semibold">¥{fmtMoney(sumSettled)}</b>
            </span>
            <span className="w-px h-3.5 bg-border" />
            <span>
              未结 <b className="text-foreground font-semibold">¥{fmtMoney(sumUnsettled)}</b>
            </span>
            <span className="ml-auto text-[10px]">未结的结算后计入当月应发</span>
          </div>
        </>
      )}
    </SectionCard>
  )
}

/**
 * 员工当日明细表：报表页专用。
 * 一行 = 一个员工在指定营业日做了什么（服务项目 / 服务金额 / 充值 / 售卡），
 * 合计营收 = 消费 + 充值 + 售卡。空态提示、合计行都在内部处理，调用方只传 data + date。
 */
export function StaffActivityTable({
  data,
  date,
}: {
  data: {
    staffId: number
    name: string
    role: string
    ordersCount: number
    itemsCount: number
    consumeAmount: number
    rechargeCount: number
    rechargeAmount: number
    sellCardCount: number
    sellCardAmount: number
    totalAmount: number
  }[]
  date: string
}) {
  const totalItems = data.reduce((s, r) => s + r.itemsCount, 0)
  const totalConsume = data.reduce((s, r) => s + r.consumeAmount, 0)
  const totalRechargeCount = data.reduce((s, r) => s + r.rechargeCount, 0)
  const totalRecharge = data.reduce((s, r) => s + r.rechargeAmount, 0)
  const totalSellCardCount = data.reduce((s, r) => s + r.sellCardCount, 0)
  const totalSellCard = data.reduce((s, r) => s + r.sellCardAmount, 0)
  const grandTotal = data.reduce((s, r) => s + r.totalAmount, 0)
  return (
    <SectionCard
      title="员工当日明细"
      extra={
        <span className="text-[11px] text-muted-foreground">
          营业日 <span className="font-semibold text-foreground tabular-nums">{date}</span>　·　{data.length} 位员工
        </span>
      }
      bodyClassName="p-0"
    >
      <DataTable
        cols={ACTIVITY_COLS}
        rows={data}
        rowKey={(s) => s.staffId}
        plain
        maxHeight={LIST_MAX_H}
        render={(s, i) => [
          <span key="n" className="inline-flex items-center justify-center rounded-full text-[11px] font-bold tabular-nums bg-muted text-muted-foreground" style={{ width: 22, height: 22 }}>
            {i + 1}
          </span>,
          <span key="name" className="font-semibold">{s.name}</span>,
          <Badge key="role" tone={s.role === '店长' ? 'primary' : 'muted'}>{s.role}</Badge>,
          <span key="items" className="text-muted-foreground">{s.itemsCount}<span className="ml-1 text-[10px]">项</span></span>,
          <span key="consume">¥{fmtMoney(s.consumeAmount)}</span>,
          <span key="rc" className="text-muted-foreground">{s.rechargeCount}</span>,
          <span key="ra">¥{fmtMoney(s.rechargeAmount)}</span>,
          <span key="sc" className="text-muted-foreground">{s.sellCardCount}</span>,
          <span key="sa">¥{fmtMoney(s.sellCardAmount)}</span>,
          <span key="total" className="text-primary font-semibold">¥{fmtMoney(s.totalAmount)}</span>,
        ]}
        footRow={data.length > 0 ? (
          <tr className="bg-muted/60">
            <td className="px-3 h-10 text-xs font-semibold text-muted-foreground border-b border-border/60" colSpan={3}>合计</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>{totalItems} 项</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>¥{fmtMoney(totalConsume)}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>{totalRechargeCount}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>¥{fmtMoney(totalRecharge)}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>{totalSellCardCount}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>¥{fmtMoney(totalSellCard)}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-primary font-bold')}>¥{fmtMoney(grandTotal)}</td>
          </tr>
        ) : undefined}
        empty={<div className="py-8 text-center text-xs text-muted-foreground">{date} 暂无营业记录（可能未选日期或该日无单据）</div>}
      />
    </SectionCard>
  )
}

/**
 * 各项目业绩·提成表（店主 10-10 定：「按项目看提成/业绩」，隔开放在业绩榜下面）。
 * 回答「哪个项目赚得多」—— 按摩、推拿、充值的次卡套餐……逐项排出来。
 * 业绩 = 该项目当月实际做了多少钱（精确）；
 * 提成 = 业绩 × 项目自身提成率（**估算**，commissions 只存整单加权率、不存逐行，
 *       且开单时个人比例会覆盖项目率，故此处按项目率估，实际以结算为准）。
 * 前端不摆解释性脚注（店主定则），只在标题栏一句话点明「提成为估算」。
 */
export function ProjectBreakdownTable({
  data,
  month,
}: {
  data: { item: string; unit: string; qty: number; perf: number; commission: number; rate: number; mode: string; perCount: number; orders: number; onSale: boolean }[]
  month: string
}) {
  const rows = data.filter((d) => d.perf > 0 || d.qty > 0)
  const max = Math.max(1, ...rows.map((r) => r.perf))
  const sumPerf = rows.reduce((s, r) => s + r.perf, 0)
  const sumCommission = rows.reduce((s, r) => s + r.commission, 0)
  const offSale = rows.filter((r) => !r.onSale).length

  return (
    <SectionCard
      title={`各项目业绩·提成（${month}）`}
      extra={
        <span className="text-[11px] text-muted-foreground">
          按业绩倒序 · 提成为估算（按项目提成率）{offSale > 0 ? ` · ${offSale} 项已下架` : ''}
        </span>
      }
      className="col-span-3"
      bodyClassName="p-0"
    >
      <DataTable
        cols={PROJECT_COLS}
        rows={rows}
        rowKey={(d) => d.item}
        plain
        maxHeight={LIST_MAX_H}
        rowClassName={(d) => (d.onSale ? undefined : 'opacity-55')}
        render={(d) => [
          d.item || '—',
          d.unit || '—',
          d.qty,
          <span key="perf" className="inline-flex items-center gap-2">
            <span className="flex h-1.5 w-24 rounded-[var(--radius-pill)] overflow-hidden bg-border">
              <span className="h-full" style={{ width: `${(d.perf / max) * 100}%`, background: 'hsl(var(--chart-1))' }} />
            </span>
            <span className="tabular-nums">¥{fmtMoney(d.perf)}</span>
          </span>,
          d.mode === 'count'
            ? <span key="mode">按次 ¥{fmtMoney(d.perCount)}/次</span>
            : <span key="mode">{(d.rate * 100).toFixed(1)}%</span>,
          `¥${fmtMoney(d.commission)}`,
          d.onSale ? <Badge key="s" tone="muted">在售</Badge> : <Badge key="s" tone="primary">已下架</Badge>,
        ]}
        footRow={rows.length > 0 ? (
          <tr className="bg-muted/60">
            <td className="px-3 h-10 text-xs font-semibold text-muted-foreground border-b border-border/60">合计</td>
            <td className="px-3 h-10 border-b border-border/60"></td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>{rows.reduce((s, r) => s + r.qty, 0)}</td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold')}>¥{fmtMoney(sumPerf)}</td>
            <td className="px-3 h-10 border-b border-border/60"></td>
            <td className={cx('px-3 h-10 border-b border-border/60', numCls, 'text-xs font-semibold text-primary')}>¥{fmtMoney(sumCommission)}</td>
            <td className="px-3 h-10 border-b border-border/60"></td>
          </tr>
        ) : undefined}
        empty={<div className="py-8 text-center text-xs text-muted-foreground">{month} 暂无项目消费记录（只做项目并结账后自动统计）</div>}
      />
    </SectionCard>
  )
}
