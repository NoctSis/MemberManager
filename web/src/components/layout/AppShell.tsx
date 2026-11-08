import {
  LayoutDashboard,
  Users,
  ShoppingCart,
  ShoppingBag,
  Wallet,
  Percent,
  Gift,
  BarChart3,
  ShieldCheck,
  DatabaseBackup,
  Settings,
  Store,
  Receipt,
  ChevronDown,
  LogOut,
} from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { cx } from '@/components/common/ui'
import { getStaff } from '@/lib/session'
import { can } from '@/lib/perm'
import { system as apiSystem, getDayCutHour } from '@/lib/api'

/** 今天（YYYY-MM-DD）：底部状态栏「营业日」用。
 * H3：按日切点（day_cut）换算，与后端 Fmt.BizDateOf 同算法，不再显示裸日期。 */
function todayStr(): string {
  const d = new Date(Date.now() - getDayCutHour() * 3600 * 1000)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

export interface NavItem {
  key: string
  label: string
  icon: ReactNode
  group: string
  ready: boolean // 设计稿是否已产出
}

export const navItems: NavItem[] = [
  { key: 'dashboard', label: '主工作台', icon: <LayoutDashboard size={17} />, group: '营业', ready: true },
  { key: 'pos', label: '收银开单', icon: <ShoppingCart size={17} />, group: '营业', ready: true },
  { key: 'projects', label: '项目列表', icon: <ShoppingBag size={17} />, group: '营业', ready: true },
  { key: 'records', label: '消费记录', icon: <Receipt size={17} />, group: '营业', ready: true },
  { key: 'members', label: '会员管理', icon: <Users size={17} />, group: '会员', ready: true },
  { key: 'stored-value', label: '会员资产', icon: <Wallet size={17} />, group: '会员', ready: true },
  { key: 'points', label: '积分商城', icon: <Gift size={17} />, group: '会员', ready: true },
  { key: 'staff', label: '员工管理', icon: <Percent size={17} />, group: '经营', ready: true },
  { key: 'reports', label: '报表中心', icon: <BarChart3 size={17} />, group: '经营', ready: true },
  { key: 'permission', label: '权限与日志', icon: <ShieldCheck size={17} />, group: '系统', ready: true },
  { key: 'backup', label: '数据备份', icon: <DatabaseBackup size={17} />, group: '系统', ready: true },
  { key: 'settings', label: '系统设置', icon: <Settings size={17} />, group: '系统', ready: true },
]

const groupOrder = ['营业', '会员', '经营', '系统']

export function AppShell({
  active,
  onNavigate,
  pendingCount,
  onLogout,
  children,
}: {
  active: string
  onNavigate: (key: string) => void
  pendingCount: number
  onLogout: () => void
  children: ReactNode
}) {
  // 店名来自系统设置（保存后由设置页派发 mm:shop 事件即时刷新）
  const [shopName, setShopName] = useState<string>('')
  useEffect(() => {
    apiSystem
      .status()
      .then((s) => setShopName(s.ShopName || ''))
      .catch(() => setShopName(''))
    const onShop = (e: Event) => setShopName((e as CustomEvent<string>).detail ?? '')
    window.addEventListener('mm:shop', onShop)
    return () => window.removeEventListener('mm:shop', onShop)
  }, [])

  // 头像下拉（店主 09-27 定：点头像 → 退出账号下拉菜单）
  const [accountMenuOpen, setAccountMenuOpen] = useState(false)
  const accountMenuRef = useRef<HTMLDivElement | null>(null)
  useEffect(() => {
    if (!accountMenuOpen) return
    const onDown = (e: MouseEvent) => {
      if (accountMenuRef.current && !accountMenuRef.current.contains(e.target as Node)) {
        setAccountMenuOpen(false)
      }
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [accountMenuOpen])

  return (
    <div className="h-screen w-screen flex flex-col bg-background text-foreground overflow-hidden" style={{ fontFamily: 'var(--font-body)' }}>
      {/* 顶部工具栏 */}
      <header className="h-13 shrink-0 bg-card border-b border-border flex items-center px-4 gap-4 print:hidden" style={{ height: 52 }}>
        <div className="flex items-center gap-2.5">
          <div className="w-8 h-8 rounded-[var(--radius-sm)] bg-primary text-primary-foreground flex items-center justify-center shadow-sm">
            <Store size={17} />
          </div>
          <div className="leading-tight">
            <p className="text-sm font-bold" style={{ fontFamily: 'var(--font-display)' }}>
              收银系统
            </p>
            <p className="text-[11px] text-muted-foreground">{shopName || '收银系统'} · 单机版</p>
          </div>
        </div>

        {/* 顶栏全局搜索已按店主 09-25 决定移除；消息铃铛无后端、点了无反馈，店主 09-27 定删除。
            头像区点头像下拉「退出账号」（店主 09-27 加）：退出后跳回登录页，可再选别的账号。 */}
        <div className="flex items-center gap-1.5 ml-auto">
          <div className="w-px h-5 bg-border mx-1" />
          <div className="relative" ref={accountMenuRef}>
            <button
              type="button"
              onClick={() => setAccountMenuOpen((v) => !v)}
              aria-haspopup="menu"
              aria-expanded={accountMenuOpen}
              className="flex items-center gap-2 pl-1 pr-1.5 py-0.5 rounded-[var(--radius-sm)] hover:bg-accent transition-colors"
            >
              <div className="w-7 h-7 rounded-full bg-primary/15 text-primary flex items-center justify-center text-xs font-semibold">
                {(getStaff()?.name ?? '张').slice(0, 1)}
              </div>
              <div className="leading-tight text-left">
                <p className="text-xs font-medium">{getStaff()?.name ?? '店长'}</p>
                <p className="text-[10px] text-muted-foreground">{getStaff()?.role ?? '店长'}权限</p>
              </div>
              <ChevronDown size={13} className="text-muted-foreground" />
            </button>
            {accountMenuOpen ? (
              <div
                role="menu"
                className="absolute right-0 mt-1.5 w-44 bg-card border border-border rounded-[var(--radius-sm)] shadow-[var(--elev-raised)] py-1 z-50"
              >
                <div className="px-3 py-1.5 border-b border-border mb-1">
                  <p className="text-xs font-medium truncate">{getStaff()?.name ?? '—'}</p>
                  <p className="text-[10px] text-muted-foreground">{getStaff()?.role ?? '—'}权限</p>
                </div>
                <button
                  type="button"
                  role="menuitem"
                  onClick={() => { setAccountMenuOpen(false); onLogout() }}
                  className="w-full flex items-center gap-2 h-8 px-3 text-[13px] text-destructive hover:bg-accent transition-colors"
                >
                  <LogOut size={13} /> 退出账号
                </button>
              </div>
            ) : null}
          </div>
        </div>
      </header>

      <div className="flex-1 flex min-h-0">
        {/* 左侧导航 */}
        <nav className="w-44 shrink-0 bg-card border-r border-border overflow-y-auto py-2.5 px-2 print:hidden" aria-label="主导航">
          {groupOrder.map((group) => {
            // 按角色权限过滤：无该模块授权的页签整条隐藏，整组为空时连分组标题一起隐藏
            const items = navItems.filter((n) => n.group === group && can(n.key))
            if (items.length === 0) return null
            return (
              <div key={group} className="mb-1.5">
                <p className="px-2 pt-2 pb-1 text-[10px] font-medium text-muted-foreground/80">{group}</p>
                {items.map((item) => {
                  const isActive = item.key === active
                  return (
                    <button
                      key={item.key}
                      onClick={() => onNavigate(item.key)}
                      aria-current={isActive ? 'page' : undefined}
                      className={cx(
                        'w-full flex items-center gap-2.5 h-9 px-2.5 mb-0.5 rounded-[var(--radius-sm)] text-[13px] transition-colors text-left',
                        isActive
                          ? 'bg-primary text-primary-foreground font-semibold shadow-sm'
                          : 'text-foreground/80 hover:bg-accent hover:text-accent-foreground',
                      )}
                    >
                      <span className={cx(isActive ? 'text-primary-foreground' : 'text-muted-foreground')}>
                        {item.icon}
                      </span>
                      <span className="flex-1 truncate">{item.label}</span>
                      {item.key === 'pos' && pendingCount > 0 ? (
                        <span
                          className={cx(
                            'min-w-[18px] h-[18px] px-1 rounded-[var(--radius-pill)] text-[10px] font-semibold flex items-center justify-center',
                            isActive ? 'bg-primary-foreground/25 text-primary-foreground' : 'bg-destructive/10 text-destructive',
                          )}
                        >
                          {pendingCount}
                        </span>
                      ) : null}
                      {!item.ready && !isActive ? (
                        <span className="text-[10px] text-muted-foreground/60 border border-border rounded px-1 leading-4">
                          待设计
                        </span>
                      ) : null}
                    </button>
                  )
                })}
              </div>
            )
          })}
        </nav>

        {/* 内容区 */}
        <main className="flex-1 min-w-0 overflow-y-auto">{children}</main>
      </div>

      {/* 底部状态栏 */}
      <footer className="h-7 shrink-0 bg-card border-t border-border flex items-center px-4 text-[11px] text-muted-foreground gap-4 print:hidden">
        <span>
          操作员：{getStaff()?.name ?? '—'}（{getStaff()?.role ?? '—'}权限）
        </span>
        <span className="w-px h-3 bg-border" />
        <span>营业日 {todayStr()}</span>
        <span className="w-px h-3 bg-border" />
        <span className="flex items-center gap-1">
          <span className="w-1.5 h-1.5 rounded-full bg-primary inline-block" />
          数据库正常（WAL）
        </span>
        <span className="w-px h-3 bg-border" />
        <span>自动备份：每日 20:00 · 留 30 版 + 月存</span>
        <span className="ml-auto">单机版 · v1.0</span>
      </footer>
    </div>
  )
}
