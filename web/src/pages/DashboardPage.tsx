import { useEffect, useState } from 'react'
import { PageScroll } from '@/components/common/PageLayout'
import { TodayMetrics, RevenueTrend, ReminderPanel, QuickActions } from '@/components/dashboard/DashboardWidgets'
import { dashboard, getDayCutHour, type DashboardOverview } from '@/lib/api'
import { toast } from 'sonner'

export function DashboardPage({ onNavigate }: { onNavigate: (key: string) => void }) {
  const [data, setData] = useState<DashboardOverview | null>(null)
  const [stamp, setStamp] = useState('')

  const load = () => {
    dashboard
      .overview()
      .then((o) => {
        setData(o)
        setStamp(new Date().toTimeString().slice(0, 5))
      })
      .catch((e) => toast.error(String(e)))
  }
  useEffect(() => {
    load()
    const t = window.setInterval(load, 60000)
    return () => window.clearInterval(t)
  }, [])

  const d = data

  return (
    <PageScroll minWidth={1080}>
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            主工作台
          </h1>
          <p className="text-xs text-muted-foreground mt-1">{bizDateLabel()} · 今日营业中</p>
        </div>
        <p className="text-xs text-muted-foreground">
          数据截至 <span className="tabular-nums font-medium text-foreground">{stamp || '—'}</span> · 每 60 秒自动刷新
        </p>
      </div>

      <TodayMetrics data={d} />
      <div className="grid grid-cols-3 gap-4">
        <RevenueTrend data={d?.trend ?? []} />
        <ReminderPanel data={d} />
      </div>
      <QuickActions onNavigate={onNavigate} pendingHeld={d?.pendingHeld ?? 0} />
    </PageScroll>
  )
}

function bizDateLabel(): string {
  // H3：日切点读缓存（登录后 initDayCut 填好），与后端 Fmt.BizDateOf 同算法——
  // 当前时刻减日切小时再取日期，不再写死凌晨 2 点。
  const shifted = new Date(Date.now() - getDayCutHour() * 3600 * 1000)
  const now = shifted
  const week = ['日', '一', '二', '三', '四', '五', '六'][now.getDay()]
  return `${now.getFullYear()}年${now.getMonth() + 1}月${now.getDate()}日 星期${week}`
}
