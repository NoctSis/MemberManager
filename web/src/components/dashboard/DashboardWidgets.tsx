import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from 'recharts'
import { Wallet, ShoppingCart, Users, TrendingUp, Cake, Moon, FileSpreadsheet, ArrowRight } from 'lucide-react'
import { toast } from 'sonner'
import { Card, SectionCard, MetricCard, Badge, Button } from '@/components/common/ui'
import { canAlias } from '@/lib/perm'
import { dashboard, type DashboardOverview } from '@/lib/api'
import { fmtMoney } from '@/data/mock'

export function TodayMetrics({ data }: { data: DashboardOverview | null }) {
  const m = data?.metrics
  const revDelta = m && m.RevenueYesterday > 0 ? Math.round(((m.Revenue - m.RevenueYesterday) / m.RevenueYesterday) * 1000) / 10 : null
  const avg = m && m.ConsumeCount > 0 ? Math.round((m.Consume / m.ConsumeCount) * 100) / 100 : 0
  return (
    <div className="grid gap-4 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))]">
      {/* 营收卡：真涨跌才用箭头；昨日无数据时显示中性说明（不挂 ▲） */}
      <MetricCard
        label="今日营收"
        value={fmtMoney(m?.Revenue ?? 0)}
        delta={{ text: revDelta == null ? '昨日无数据' : `较昨日 ${revDelta >= 0 ? '+' : ''}${revDelta}%`, up: (revDelta ?? 0) >= 0, neutral: revDelta == null }}
        icon={<TrendingUp size={17} />}
      />
      {/* 充值/消费/新增：说明性文字无涨跌含义，用中性灰、不加箭头 */}
      <MetricCard label="今日充值（含赠送）" value={fmtMoney(m?.Recharge ?? 0)} delta={{ text: `充值 ${m?.RechargeCount ?? 0} 笔`, up: true, neutral: true }} icon={<Wallet size={17} />} />
      <MetricCard label="今日消费" value={fmtMoney(m?.Consume ?? 0)} delta={{ text: `${m?.ConsumeCount ?? 0} 笔 · 客单价 ¥${fmtMoney(avg)}`, up: true, neutral: true }} icon={<ShoppingCart size={17} />} />
      <MetricCard label="新增会员" value={String(m?.NewMembers ?? 0)} prefix="" delta={{ text: `本月累计 ${m?.NewMembersMonth ?? 0} 人`, up: true, neutral: true }} icon={<Users size={17} />} />
    </div>
  )
}

export function RevenueTrend({ data }: { data: { date: string; revenue: number; recharge: number }[] }) {
  return (
    <SectionCard
      title="近 7 日营收趋势"
      className="col-span-2"
      extra={
        <div className="flex items-center gap-3 text-xs text-muted-foreground">
          <span className="flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-sm bg-primary inline-block" />
            营收
          </span>
          <span className="flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-sm bg-primary/30 inline-block" />
            充值
          </span>
        </div>
      }
      bodyClassName="p-2 pt-3"
    >
      <ResponsiveContainer width="100%" height={230}>
        <AreaChart data={data} margin={{ top: 4, right: 12, bottom: 0, left: -8 }}>
          <defs>
            <linearGradient id="gRev" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity={0.28} />
              <stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity={0.02} />
            </linearGradient>
          </defs>
          <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" vertical={false} />
          <XAxis dataKey="date" tick={{ fontSize: 11, fill: 'hsl(var(--muted-foreground))' }} axisLine={false} tickLine={false} />
          <YAxis tick={{ fontSize: 11, fill: 'hsl(var(--muted-foreground))' }} axisLine={false} tickLine={false} width={52} />
          <Tooltip
            formatter={(v: number, name: string) => [`¥${fmtMoney(v)}`, name === 'revenue' ? '营收' : '充值']}
            contentStyle={{
              background: 'hsl(var(--card))',
              border: '1px solid hsl(var(--border))',
              borderRadius: 'var(--radius-sm)',
              fontSize: 12,
            }}
          />
          <Area type="monotone" dataKey="recharge" stroke="hsl(var(--primary) / 0.35)" fill="hsl(var(--primary) / 0.08)" strokeWidth={1.5} name="recharge" />
          <Area type="monotone" dataKey="revenue" stroke="hsl(var(--primary))" fill="url(#gRev)" strokeWidth={2} name="revenue" />
        </AreaChart>
      </ResponsiveContainer>
    </SectionCard>
  )
}

export function ReminderPanel({ data }: { data: DashboardOverview | null }) {
  const birthday = data?.birthday ?? []
  const sleeping = data?.sleeping ?? []
  const remind = (action: '送祝福' | '唤醒', m: { id: number; name: string }) => {
    const call = action === '送祝福' ? dashboard.bless(m.id, m.name) : dashboard.wake(m.id, m.name)
    call
      .catch((e) => toast.error(String(e)))
  }
  return (
    <div className="flex flex-col gap-4">
      <SectionCard
        title={
          <span className="flex items-center gap-1.5">
            <Cake size={14} className="text-primary" />
            今日生日提醒
          </span>
        }
        extra={<Badge tone="danger">{birthday.length} 人</Badge>}
        bodyClassName="p-2"
      >
        {birthday.length === 0 ? (
          <p className="text-xs text-muted-foreground text-center py-6">今天没有会员生日</p>
        ) : (
          birthday.map((m) => (
            <div key={m.id} className="flex items-center gap-2.5 px-2 py-2 rounded-[var(--radius-sm)] hover:bg-accent/70 transition-colors">
              <div className="w-8 h-8 rounded-full bg-primary/10 text-primary flex items-center justify-center text-xs font-semibold shrink-0">
                {m.name.slice(0, 1)}
              </div>
              <div className="flex-1 min-w-0 leading-tight">
                <p className="text-[13px] font-medium truncate">
                  {m.name} <span className="text-muted-foreground font-normal text-xs">今天生日 · {m.level}</span>
                </p>
                <p className="text-[11px] text-muted-foreground mt-0.5">余额 ¥{fmtMoney(m.balance)}</p>
              </div>
              <Button size="sm" variant="secondary" onClick={() => remind('送祝福', m)}>
                送祝福
              </Button>
            </div>
          ))
        )}
      </SectionCard>

      <SectionCard
        title={
          <span className="flex items-center gap-1.5">
            <Moon size={14} className="text-primary" />
            沉睡会员唤醒
          </span>
        }
        extra={<Badge tone="muted">60 天未到店 {sleeping.length} 人</Badge>}
        bodyClassName="p-2"
      >
        {sleeping.length === 0 ? (
          <p className="text-xs text-muted-foreground text-center py-6">暂无 60 天未到店的会员</p>
        ) : (
          sleeping.map((m) => (
            <div key={m.id} className="flex items-center gap-2.5 px-2 py-2 rounded-[var(--radius-sm)] hover:bg-accent/70 transition-colors">
              <div className="w-8 h-8 rounded-full bg-muted text-muted-foreground flex items-center justify-center text-xs font-semibold shrink-0">
                {m.name.slice(0, 1)}
              </div>
              <div className="flex-1 min-w-0 leading-tight">
                <p className="text-[13px] font-medium truncate">{m.name}</p>
                <p className="text-[11px] text-muted-foreground mt-0.5">
                  {(m.lastVisit || '').slice(0, 10)} · 余额 ¥{fmtMoney(m.balance)}
                </p>
              </div>
              {/* 唤醒按钮：店主 11-04「不接入云端没什么用，隐藏但留接口」。
                  当前只写 op_log（RecordReminderTask），无实际触达能力。
                  接入微信/短信后取消注释即恢复。API 已就绪：dashboard.wake() */}
              {/* <Button size="sm" variant="ghost" onClick={() => remind('唤醒', m)}>
                唤醒 <ArrowRight size={12} />
              </Button> */}
            </div>
          ))
        )}
      </SectionCard>
    </div>
  )
}

const quickActions = [
  { key: 'pos', label: '收银开单', desc: '组合支付 · 挂单', icon: <ShoppingCart size={18} /> },
  { key: 'recharge', label: '会员充值', desc: '充值送 · 留痕', icon: <Wallet size={18} /> },
  { key: 'new-member', label: '新建会员', desc: '建档 · 打标签', icon: <Users size={18} /> },
  { key: 'staff', label: '员工管理', desc: '登记做项目的人', icon: <Users size={18} /> },
  { key: 'daily', label: '营业日对账', desc: '按支付方式对账', icon: <FileSpreadsheet size={18} /> },
  { key: 'export', label: '导出日报', desc: 'Excel 一键导出', icon: <TrendingUp size={18} /> },
]

export function QuickActions({
  onNavigate,
  pendingHeld,
}: {
  onNavigate: (key: string) => void
  pendingHeld: number
}) {
  // 无权限的快捷入口不显示（与左侧导航同一套矩阵判定），避免出现点了进不进的入口
  const visible = quickActions.filter((a) => canAlias(a.key))
  return (
    <SectionCard title="快捷操作与待办" bodyClassName="p-4">
      <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))]">
        {visible.map((a) => (
          <button
            key={a.key}
            onClick={() => onNavigate(a.key)}
            className="flex flex-col items-start gap-2 p-3 rounded-[var(--radius-sm)] border border-border bg-card hover:border-primary hover:shadow-[var(--focus-ring)] active:bg-accent transition-all text-left"
          >
            <span className="w-9 h-9 rounded-[var(--radius-sm)] bg-primary/10 text-primary flex items-center justify-center">
              {a.icon}
            </span>
            <span className="leading-tight">
              <span className="block text-[13px] font-semibold">{a.label}</span>
              <span className="block text-[11px] text-muted-foreground mt-0.5">{a.desc}</span>
            </span>
          </button>
        ))}
      </div>

      {/* 挂单提示卡（店主 09-27 定：退款提示卡已删，退款在储值管理页由店长处理） */}
      <div className="grid grid-cols-1 gap-3 mt-4">
        <Card className="flex items-center gap-3 px-4 py-3">
          <span className="w-8 h-8 rounded-[var(--radius-sm)] bg-muted text-muted-foreground flex items-center justify-center">
            <ShoppingCart size={15} />
          </span>
          <div className="flex-1 leading-tight">
            <p className="text-[13px] font-medium">
              挂单 <span className="text-primary font-bold">{pendingHeld}</span> 笔待取
            </p>
            <p className="text-[11px] text-muted-foreground mt-0.5">{pendingHeld > 0 ? '有客人中断结账的账单待取单' : '暂无待取挂单'}</p>
          </div>
          <Button size="sm" variant="secondary" onClick={() => onNavigate('pos')}>
            去取单
          </Button>
        </Card>
      </div>
    </SectionCard>
  )
}
