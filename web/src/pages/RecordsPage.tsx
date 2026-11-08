import { useEffect, useRef, useState } from 'react'
import { Search, Receipt, CalendarDays, Users } from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, Card, Badge, Input, Select, Modal, MetricCard, DataTable,
  thCls, tdCls, numCls, cx,
} from '@/components/common/ui'
import { LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { records as apiRecords, bizDate, type OrderRecord, type OrderDetail, type RecordTotals } from '@/lib/api'
import { usePagedNumbered, useAutoPageSize } from '@/lib/paged'
import { PagedBar } from '@/components/common/paged'
import { PageFill, PageHeader, PageToolbar, PageBody, PageFooter } from '@/components/common/PageLayout'
import { takePendingArgs } from '@/lib/navbus'
import { fmtMoney } from '@/data/mock'

/** YYYY-MM-DD */
function ymd(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

type Preset = '今日' | '本周' | '本月' | '全部'

/** 单据明细列宽预算：10 列合计 990px。
 *  QA(11-04 店主)：最小窗口下最右「状态」被窗口切掉一半文字——表 fixed 宽度超了容器，
 *  而 DataTable 外层 overflow-x-hidden，超出部分直接裁掉不是滚动。
 *  实测容器可见宽约 1018px（探针 tw=1064 / tw2=1018，被裁 46px）。
 *  按店主说的「把前面空档收缩一下，按合适的间距分配」：
 *  收掉有空档的列（会员/等级/类型/项目/支付方式/经手人），单号时间本来就贴着内容不动，
 *  状态留足「已退款」三字。合计 990 < 1018，留 28px 余量防滚动条吃宽度。
 */
const RECORD_COLS: DataTableCol[] = [
  { key: 'no', title: '单号', width: 112, cellClass: 'tabular-nums text-xs text-muted-foreground' },
  { key: 'time', title: '时间', width: 116, cellClass: 'tabular-nums text-xs text-muted-foreground' },
  // 会员/经手人是最大空档：内容多为 2~3 字，原 108/70 放着一大块白
  { key: 'member', title: '会员', width: 90, truncate: true, cellClass: 'font-medium' },
  { key: 'level', title: '等级', width: 56 },
  { key: 'kind', title: '类型', width: 54 },
  { key: 'items', title: '项目', width: 218, truncate: true, cellClass: 'text-xs text-muted-foreground' },
  { key: 'amount', title: '应收', width: 88, headClass: 'text-right', cellClass: cx(numCls, 'font-semibold') },
  { key: 'pays', title: '支付方式', width: 120, truncate: true, cellClass: 'text-xs text-muted-foreground' },
  { key: 'operator', title: '经手人', width: 56, truncate: true, cellClass: 'text-xs' },
  // 状态留 80：徽标「已退款」三字 + px-2 + 单元格 px-3，再窄又会被切
  { key: 'status', title: '状态', width: 80 },
]

/** 后端没返回 Total 时的兜底空汇总，保证指标卡不炸 */
const EMPTY_TOTAL: RecordTotals = { Count: 0, Receivable: 0, CardCovered: 0, Balance: 0, Cash: 0, Other: 0 }

/** 预设时段 → 起止日期（全部则空串 = 不限）。
 * M5：区间对齐**营业日**（`bizDate()`），与后端按 `orders.biz_date` 筛选同一口径——
 * 否则日切点前后（如 0 点~凌晨切点前的单 biz_date 仍算前一天）用自然日历会漏算。 */
function presetRange(p: Preset): [string, string] {
  const today = bizDate()
  if (p === '今日') return [today, today]
  if (p === '本周') {
    const d = new Date(today + 'T00:00:00')
    const dow = (d.getDay() + 6) % 7 // 周一为一周起点
    d.setDate(d.getDate() - dow)
    return [ymd(d), today]
  }
  if (p === '本月') return [today.slice(0, 8) + '01', today]
  return ['', '']
}

export function RecordsPage() {
  const [preset, setPreset] = useState<Preset>('本月')
  const [from, setFrom] = useState<string>('')
  const [to, setTo] = useState<string>('')
  const [kwInput, setKwInput] = useState<string>('')
  const [keyword, setKeyword] = useState<string>('')
  const [kind, setKind] = useState<string>('全部类型')
  const [status, setStatus] = useState<string>('全部状态')
  const [operator, setOperator] = useState<string>('全部员工')
  const [operators, setOperators] = useState<string[]>([])
  const [level, setLevel] = useState<string>('全部等级')
  const [levels, setLevels] = useState<string[]>([])
  const [customer, setCustomer] = useState<string>('全部')  // 全部 / 会员 / 散客
  const [detail, setDetail] = useState<OrderDetail | null>(null)

  useEffect(() => {
    apiRecords.operators().then(setOperators).catch(() => setOperators([]))
    apiRecords.levels().then(setLevels).catch(() => setLevels([]))
    // 员工列表点「交易记录」跳进来时，预填操作员筛选
    const req = takePendingArgs('records')
    if (req?.operator) {
      setOperator(req.operator)
      setPreset('全部') // 不限日期，否则可能一条都查不到
    }
  }, [])

  // 切预设时同步写入起止日期（自定义修改起止则自动切到「自定义」）
  useEffect(() => {
    const [f, t] = presetRange(preset)
    setFrom(f)
    setTo(t)
  }, [preset])

  // 表格区高度 → 一页装几行（店主 11-04：装满窗口就翻页，不在页内滚动）。
  // bodyRef 挂在常驻包裹层上（空数据时也在），高度由 flex 布局决定、与内容无关。
  const bodyRef = useRef<HTMLDivElement>(null)
  const autoSize = useAutoPageSize(bodyRef)

  // B2 页码分页：单据明细按 offset/limit 取单页，改任一筛选条件回第 1 页重取。
  // 上方指标卡取后端 QueryResult.Total（命中筛选条件的全量汇总），不受翻页影响。
  // 第三个参数 autoSize 让每页行数跟随窗口高度；用户在「每页」下拉手动选过就不再跟随。
  const page = usePagedNumbered<OrderRecord, RecordTotals>(
    (limit, offset) =>
      apiRecords
        .query(from, to, keyword, kind, status, operator, level, customer, limit, offset)
        .then((r) => ({ rows: r.Rows ?? [], total: r.Total?.Count ?? 0, meta: r.Total ?? EMPTY_TOTAL })),
    10,
    autoSize,
  )
  useEffect(() => {
    void page.reload()
  }, [page.reload, from, to, keyword, kind, status, operator, level, customer])
  useEffect(() => {
    if (page.error) toast.error(page.error)
  }, [page.error])

  const rows = page.rows
  const loading = page.loading

  const resetFilter = () => {
    setPreset('本月')
    setKwInput('')
    setKeyword('')
    setKind('全部类型')
    setStatus('全部状态')
    setOperator('全部员工')
    setLevel('全部等级')
    setCustomer('全部')
  }

  const dirty =
    preset !== '本月' || kwInput !== '' || kind !== '全部类型' || status !== '全部状态' ||
    operator !== '全部员工' || level !== '全部等级' || customer !== '全部'

  const sum = page.meta ?? EMPTY_TOTAL

  const openDetail = (r: OrderRecord) => {
    apiRecords
      .detail(r.Id)
      .then(setDetail)
      .catch((e) => toast.error(String(e)))
  }

  const statusTone = (s: string) => (s === '正常' ? 'primary' : s === '挂单' ? 'muted' : 'danger')

  return (
    // 自适应布局（店主 10-24：全屏后下方空白）：
    // 外层 flex 纵向 + 高度 = 视口减 AppShell 顶栏，页面自身不再滚动；
    // 表格区 flex-1 min-h-0 + fill → 吃掉标题/筛选/汇总/页码条之外的所有高度。
    // 好处：不用算「该减多少 px」，任何窗口高度都对，且全站可复用同一套写法。
    <PageFill minWidth={1030}>
      <PageHeader title="消费记录">
        {dirty ? (
          <Button variant="ghost" onClick={resetFilter}>
            清空筛选
          </Button>
        ) : null}
      </PageHeader>

      <PageToolbar>
      <Card className="px-3 py-2.5 flex items-center gap-2.5 flex-wrap">
        <CalendarDays size={15} className="text-muted-foreground" />
        <div className="flex gap-1">
          {(['今日', '本周', '本月', '全部'] as Preset[]).map((p) => (
            <button
              key={p}
              onClick={() => setPreset(p)}
              className={cx(
                'h-7 px-3 text-xs rounded-[var(--radius-pill)] border transition-colors',
                preset === p
                  ? 'bg-primary border-primary text-primary-foreground font-semibold'
                  : 'bg-card border-input text-muted-foreground hover:text-foreground hover:border-primary/40',
              )}
            >
              {p}
            </button>
          ))}
        </div>
        <span className="w-px h-4 bg-border" />
        <Input type="date" value={from} onChange={(e) => { setFrom(e.target.value); setPreset('全部') }} className="h-7 w-32 text-xs" aria-label="开始日期" />
        <span className="text-xs text-muted-foreground">至</span>
        <Input type="date" value={to} onChange={(e) => { setTo(e.target.value); setPreset('全部') }} className="h-7 w-32 text-xs" aria-label="结束日期" />
        <span className="w-px h-4 bg-border" />
        <div className="relative">
          <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={kwInput}
            onChange={(e) => setKwInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') setKeyword(kwInput) }}
            placeholder="会员姓名 / 手机 / 卡号 / 单号"
            aria-label="搜索消费记录"
            className="h-7 w-56 pl-7 text-xs"
          />
        </div>
        <Button variant="secondary" size="sm" onClick={() => setKeyword(kwInput)}>
          搜索
        </Button>
        <span className="w-px h-4 bg-border" />
        <Select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="类型筛选" className="h-7 text-xs w-24">
          <option>全部类型</option>
          <option>消费</option>
          <option>充值</option>
          <option>售卡</option>
        </Select>
        <Select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="状态筛选" className="h-7 text-xs w-24">
          <option>全部状态</option>
          <option>正常</option>
          <option>挂单</option>
          <option>已退款</option>
        </Select>
        <Select
          value={customer}
          onChange={(e) => setCustomer(e.target.value)}
          aria-label="客户类型筛选"
          className="h-7 text-xs w-20"
        >
          <option>全部</option>
          <option>会员</option>
          <option>散客</option>
        </Select>
        <Select
          value={level}
          onChange={(e) => setLevel(e.target.value)}
          aria-label="会员等级筛选"
          className="h-7 text-xs w-24"
        >
          <option>全部等级</option>
          {levels.map((lv) => <option key={lv}>{lv}</option>)}
        </Select>
        <Select value={operator} onChange={(e) => setOperator(e.target.value)} aria-label="经手人筛选" className="h-7 text-xs w-28">
          <option>全部员工</option>
          {operators.map((o) => <option key={o}>{o}</option>)}
        </Select>
      </Card>
      </PageToolbar>

      <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))] shrink-0">
        <MetricCard label="单据笔数" value={`${sum.Count} 笔`} icon={<Receipt size={17} />} />
        <MetricCard label="应收合计" value={fmtMoney(sum.Receivable)} icon={<Receipt size={17} />} />
        <MetricCard label="会员余额支付" value={fmtMoney(sum.Balance)} icon={<Users size={17} />} />
        <MetricCard label="现金" value={fmtMoney(sum.Cash)} icon={<Receipt size={17} />} />
        <MetricCard label="其他（微信/支付宝）" value={fmtMoney(sum.Other)} icon={<Receipt size={17} />} />
      </div>

      {/* 表格卡片：Card（无内边距）+ flex-1，和会员页同构 → 页码条贴底不受内容影响 */}
      <PageBody className="overflow-hidden flex flex-col">
        {/* 标题栏 */}
        <div className="flex items-center gap-1.5 px-4 h-11 border-b border-border shrink-0">
          <Receipt size={14} className="text-primary" />
          <h3 className="text-sm font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>单据明细</h3>
          {sum.CardCovered > 0 ? (
            <span className="ml-1 text-[11px] font-normal text-muted-foreground">
              （含次卡抵扣 ¥{fmtMoney(sum.CardCovered)}，核销不计收入）
            </span>
          ) : null}
        </div>
        {/* 常驻包裹层：空数据时也在，供 useAutoPageSize 量「一页装几行」 */}
        <div ref={bodyRef} className="flex-1 min-h-0 flex flex-col">
        {loading && rows.length === 0 ? (
          <div className="py-10 text-center text-sm text-muted-foreground">加载中…</div>
        ) : rows.length === 0 ? (
          <div className="py-10 text-center">
            <p className="text-sm text-muted-foreground">
              {dirty ? '该筛选条件下没有单据，试试放宽时间范围或清空筛选' : '还没有任何单据，去「收银开单」做第一笔'}
            </p>
          </div>
        ) : (
          <DataTable
            cols={RECORD_COLS}
            rows={rows}
            rowKey={(r) => r.Id}
            fill
            rowClassName="cursor-pointer hover:bg-accent/60"
            rowOnClick={openDetail}
            render={(r) => [
              r.OrderNo,
              // B2（10-21）：时间显示到分钟（created_at 到秒，秒位无信息量还挤列宽）
              r.Time && r.Time.length >= 16 ? r.Time.substring(0, 16) : r.Time,
              <span key="m">
                {r.MemberName}
                {r.MemberCard ? <span className="ml-1 text-[10px] text-muted-foreground">{r.MemberCard}</span> : null}
              </span>,
              r.Level ? <Badge key="l" tone="muted">{r.Level}</Badge> : <span className="text-muted-foreground">—</span>,
              <Badge key="k" tone={r.Kind === '消费' ? 'muted' : 'primary'}>{r.Kind}</Badge>,
              r.Items || '—',
              `¥${fmtMoney(r.Receivable)}`,
              r.Pays || '—',
              r.Operator,
              <Badge key="s" tone={statusTone(r.Status) as 'primary' | 'muted' | 'danger'}>{r.Status}</Badge>,
            ]}
          />
        )}
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
            loading={loading}
          />
        ) : null}
      </PageBody>

      {detail ? (
        <Modal
          title={`单据详情 · ${detail.OrderNo}`}
          onClose={() => setDetail(null)}
          width={560}
          footer={
            <Button variant="secondary" onClick={() => setDetail(null)}>
              关闭
            </Button>
          }
        >
          <div className="space-y-3">
            <div className="grid grid-cols-3 gap-3 text-xs">
              <p><span className="text-muted-foreground">时间</span><br /><span className="tabular-nums">{detail.Time}</span></p>
              <p><span className="text-muted-foreground">会员</span><br />{detail.MemberName}</p>
              <p><span className="text-muted-foreground">状态</span><br /><Badge tone={statusTone(detail.Status) as 'primary' | 'muted' | 'danger'}>{detail.Status}</Badge></p>
            </div>

            <div>
              <p className="text-xs text-muted-foreground mb-1.5">项目明细</p>
              <table className="w-full border-collapse">
                <thead>
                  <tr>
                    <th className={thCls}>项目</th>
                    <th className={cx(thCls, 'text-right')}>单价</th>
                    <th className={cx(thCls, 'text-center')}>数量</th>
                    <th className={thCls}>谁做的</th>
                    <th className={cx(thCls, 'text-right')}>小计</th>
                  </tr>
                </thead>
                <tbody>
                  {detail.Items.length === 0 ? (
                    <tr><td className={tdCls} colSpan={5}><span className="text-xs text-muted-foreground">无项目明细（充值单）</span></td></tr>
                  ) : (
                    detail.Items.map((it, i) => (
                      <tr key={i} className="even:bg-muted/40">
                        <td className={cx(tdCls, 'font-medium')}>
                          {it.ItemName}
                          {it.ByCard ? <Badge tone="primary">次卡抵扣</Badge> : null}
                        </td>
                        <td className={cx(tdCls, numCls)}>¥{fmtMoney(it.Price)}</td>
                        <td className={cx(tdCls, 'text-center tabular-nums')}>{it.Qty}</td>
                        <td className={cx(tdCls, 'text-xs')}>{it.Staff}</td>
                        <td className={cx(tdCls, numCls)}>¥{fmtMoney(it.Price * it.Qty)}</td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>

            <div className="rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3.5 py-2.5 text-xs space-y-1">
              <p className="flex justify-between"><span className="text-muted-foreground">合计</span><span className="tabular-nums">¥{fmtMoney(detail.Subtotal)}</span></p>
              {detail.CardCovered > 0 ? (
                <p className="flex justify-between"><span className="text-muted-foreground">次卡抵扣</span><span className="tabular-nums">−¥{fmtMoney(detail.CardCovered)}</span></p>
              ) : null}
              <p className="flex justify-between font-semibold"><span>应收</span><span className="tabular-nums text-primary">¥{fmtMoney(detail.Receivable)}</span></p>
            </div>

            <div>
              <p className="text-xs text-muted-foreground mb-1.5">支付方式</p>
              {detail.Pays.length === 0 ? (
                <p className="text-xs text-muted-foreground">无支付记录</p>
              ) : (
                <div className="space-y-1">
                  {detail.Pays.map((p, i) => (
                    <p key={i} className="flex justify-between text-xs tabular-nums">
                      <span className="text-muted-foreground">{p.Method}</span>
                      <span className="font-medium">¥{fmtMoney(p.Amount)}</span>
                    </p>
                  ))}
                </div>
              )}
            </div>
          </div>
        </Modal>
      ) : null}
    </PageFill>
  )
}
