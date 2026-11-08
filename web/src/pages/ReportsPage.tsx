import { useEffect, useState } from 'react'
import { PageScroll } from '@/components/common/PageLayout'
import { FileSpreadsheet, CalendarDays, TrendingUp, Wallet, ShoppingCart, Undo2, Receipt, Folder, Banknote } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Card, MetricCard, cx } from '@/components/common/ui'
import { RevenuePie, RankBoard, StaffActivityTable, ProjectBreakdownTable } from '@/components/reports/ReportWidgets'
import { StaffCommissionPanel } from '@/components/reports/StaffCommissionPanel'
import { reports as apiReports, system as apiSystem, commission as apiCommission } from '@/lib/api'
import { useExportDir } from '@/lib/useExportDir'
import { fmtMoney } from '@/data/mock'

const ranges = ['今日', '本周', '本月']

type StaffActivityRow = {
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
}

export function ReportsPage() {
  const [range, setRange] = useState<string>('本月')
  const [selectedDate, setSelectedDate] = useState<string>('') // 营业日，YYYY-MM-dd
  const [exportFrom, setExportFrom] = useState<string>('')   // 员工业绩导出时间段起点
  const [exportTo, setExportTo] = useState<string>('')       // 员工业绩导出时间段终点
  const [exportingDetail, setExportingDetail] = useState(false)
  // 默认导出位置（4 个导出共享，改一次即成新默认；营业报表 + 员工业绩都落这里）
  const { dir: exportDir, change: chooseExportDir } = useExportDir()
  const [summary, setSummary] = useState<{ Revenue: number; Recharge: number; Consume: number; SellCard: number; Refund: number; Avg: number } | null>(null)
  const [daily, setDaily] = useState<{ Revenue: number; Cash: number } | null>(null)
  const [ranks, setRanks] = useState<{ name: string; rechargeAmount: number; consumeAmount: number; commission: number; orders: number }[]>([])
  const [projBreak, setProjBreak] = useState<{ item: string; unit: string; qty: number; perf: number; commission: number; rate: number; mode: string; perCount: number; orders: number; onSale: boolean }[]>([])
  const [activity, setActivity] = useState<StaffActivityRow[]>([])
  // 初始化：把当前营业日作为默认日期；员工业绩导出时间段默认"本月1日 ~ 今天"（店主"本月工作流水"的默认口径）
  useEffect(() => {
    apiSystem.status().then((s) => {
      const d = s.BizDate || new Date().toISOString().slice(0, 10)
      setSelectedDate(d)
      setExportFrom(d.slice(0, 8) + '01')
      setExportTo(d)
    }).catch(() => {})
  }, [])

  // 汇总卡：跟随相对区间按钮
  useEffect(() => {
    apiReports.summary(range).then(setSummary).catch((e) => toast.error(String(e)))
  }, [range])

  // 按选定日期刷新的数据：员工当日明细 + 员工业绩排名（所属月）
  // （原「营业日速览」卡片 10-08 已按店主要求删除，日结快照接口随之不再调用）
  useEffect(() => {
    if (!selectedDate) return
    apiReports.staffActivity(selectedDate).then(setActivity).catch(() => setActivity([]))
    apiReports.dailySummary(selectedDate).then(setDaily).catch(() => setDaily({ Revenue: 0, Cash: 0 }))
    const month = selectedDate.slice(0, 7)
    apiReports.ranks(month).then(setRanks).catch(() => setRanks([]))
    apiCommission.projectBreakdown(month).then(setProjBreak).catch(() => setProjBreak([]))
  }, [selectedDate])

  const data = {
    // 总营收 = 后端 RangeSummary.Revenue（已含充值，店主 10-13 定）。
    // 10-13 前这里是 Revenue + Recharge 手工相加（那时后端 Revenue 不含充值）；
    // 后端口径统一后必须去掉相加，否则充值被算两遍。
    revenue: fmtMoney(summary?.Revenue ?? 0),
    recharge: fmtMoney(summary?.Recharge ?? 0),
    consume: fmtMoney(summary?.Consume ?? 0),
    refund: fmtMoney(summary?.Refund ?? 0),
    avg: fmtMoney(summary?.Avg ?? 0),
    dailyRevenue: fmtMoney(daily?.Revenue ?? 0),
    dailyCash: fmtMoney(daily?.Cash ?? 0),
  }

  // 营收构成三块（店主 10-10 定：不按「散客 vs 会员」分，按「钱是干什么的」分——
  // 散单本来就是做项目的钱，散客/会员都做项目，不该拿「散单/会员」当分类名。
  //   项目/服务 = orders.kind='消费'（做项目·买商品，散客+会员都算）
  //   售卡      = orders.kind='售卡'（卖次卡 / 套餐）
  //   会员充值  = orders.kind='充值'（会员储值，属预存不是营收，但店主点名要看，故进图）
  // 三块金额由后端 RangeSummary 显式统计（Summary.SellCard），不再前端倒推。
  // 三类恒传（不在这里过滤 0 值）——环上会滤掉 0 值分片，但图例恒列三行，
  // 这样「项目/服务 / 售卡 / 会员充值」的分类永远看得见，不会因为某类为 0 就整行消失。
  const pieData = [
    { name: '项目/服务', hint: '做项目·买商品（散客+会员）', value: summary?.Consume ?? 0 },
    { name: '售卡', hint: '卖次卡 / 套餐', value: summary?.SellCard ?? 0 },
    { name: '会员充值', hint: '会员储值', value: summary?.Recharge ?? 0 },
  ]

  /** 导出员工业绩明细 Excel（横向透视表，员工为列），落默认导出位置，成功后打开。
   * 10-09：旧的「营业报表」四工作表导出已删（总收入并入业绩透视），此处只留业绩导出。 */
  const doExportStaffDetail = () => {
    if (!exportFrom || !exportTo) {
      toast.error('请先选择导出的起止日期')
      return
    }
    if (exportingDetail) return
    setExportingDetail(true)
    apiReports
      .exportStaffDetail(exportFrom, exportTo, exportDir || undefined)
      .then((r) => {
        // 原链末环为toast.success(...)，按 §二十八 删除（Excel 已自动打开、
        // 路径在页头「导出到」处可见）。删后必须让 catch 直接接在 openPath 之后，
        // 否则打开失败不再被捕获。
        return apiSystem.openPath(r.Path)
      })
      .catch((e) => toast.error('导出失败：' + String(e)))
      .finally(() => setExportingDetail(false))
  }

  return (
    <PageScroll minWidth={1030}>
      <div className="flex items-end justify-between print:hidden">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            报表中心
          </h1>
        </div>
        <div className="flex items-center gap-2">
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
          <Button variant="primary" onClick={doExportStaffDetail} disabled={exportingDetail} title="按时间段导出员工业绩表 Excel（员工为列，日期为行）">
            <FileSpreadsheet size={14} /> {exportingDetail ? '导出中…' : '导出业绩'}
          </Button>
        </div>
      </div>

      {/* 员工提成对账（店主 10-08 定）：一行一人 + 展开逐笔 + 按人对账结算 + 结算记录。
          移到最顶——对账是报表页最核心的操作，提成表导出也在此面板标题栏。 */}
      <StaffCommissionPanel defaultMonth={selectedDate ? selectedDate.slice(0, 7) : ''} />

      {/* 筛选条：相对区间 + 营业日 + 打印时间段 */}
      <Card className="px-3 py-2.5 flex items-center gap-2.5 flex-wrap print:hidden">
        <CalendarDays size={15} className="text-muted-foreground" />
        <div className="flex gap-1">
          {ranges.map((r) => (
            <button
              key={r}
              onClick={() => setRange(r)}
              className={cx(
                'h-7 px-3 text-xs rounded-[var(--radius-pill)] border transition-colors',
                range === r
                  ? 'bg-primary border-primary text-primary-foreground font-semibold'
                  : 'bg-card border-input text-muted-foreground hover:text-foreground hover:border-primary/40',
              )}
            >
              {r}
            </button>
          ))}
        </div>
        <span className="w-px h-4 bg-border" />
        <span className="text-xs text-muted-foreground">营业日</span>
        <input
          type="date"
          value={selectedDate}
          onChange={(e) => setSelectedDate(e.target.value)}
          className="h-7 px-2 text-xs bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
          aria-label="选择营业日"
        />
        <span className="w-px h-4 bg-border" />
        <span className="text-xs text-muted-foreground">导出时间段</span>
        <input
          type="date"
          value={exportFrom}
          onChange={(e) => setExportFrom(e.target.value)}
          className="h-7 px-2 text-xs bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
          aria-label="导出起始日"
        />
        <span className="text-xs text-muted-foreground">→</span>
        <input
          type="date"
          value={exportTo}
          onChange={(e) => setExportTo(e.target.value)}
          className="h-7 px-2 text-xs bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
          aria-label="导出结束日"
        />
      </Card>

      {/* 汇总卡 */}
      <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))]">
        <MetricCard label={`总营收（${range}）`} value={data.revenue} icon={<TrendingUp size={17} />} />
        <MetricCard label="储值充值" value={data.recharge} icon={<Wallet size={17} />} />
        <MetricCard label="服务与商品消费" value={data.consume} icon={<ShoppingCart size={17} />} />
        <MetricCard label="退款合计" value={data.refund} icon={<Undo2 size={17} />} />
        <MetricCard label="客单价" value={data.avg} icon={<Receipt size={17} />} />
        <MetricCard label="当日营收" value={data.dailyRevenue} icon={<CalendarDays size={17} />} />
        <MetricCard label="当日现金" value={data.dailyCash} icon={<Banknote size={17} />} />
      </div>

      {/* 图表区（店主 10-08 定）：
          「每日营收」柱状图删掉（不需要）；「支付方式」表整个删掉（对账归日结页）；
          腾出来的位置给「员工业绩榜」——它占两列，是这块的主角。 */}
      <div className="grid grid-cols-3 gap-4">
        <RevenuePie data={pieData} range={range} />
        <RankBoard data={ranks} month={selectedDate ? selectedDate.slice(0, 7) : ''} />
        {/* 各项目业绩·提成（店主 10-10）：隔开放在业绩榜下面，独占整行，回答「哪个项目赚得多」 */}
        <ProjectBreakdownTable data={projBreak} month={selectedDate ? selectedDate.slice(0, 7) : ''} />
      </div>

      {/* 员工当日明细 —— 店主最关心的「谁做了什么、给会员加了多少钱」 */}
      <StaffActivityTable data={activity} date={selectedDate} />

    </PageScroll>
  )
}
