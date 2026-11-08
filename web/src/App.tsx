import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import type { ComponentType, LazyExoticComponent, ReactNode } from 'react'
import { Toaster } from 'sonner'
import { ShieldOff } from 'lucide-react'
import { AppShell } from '@/components/layout/AppShell'
import { PageSkeleton } from '@/components/common/ui'
import { PasswordTipDialog } from '@/components/common/PasswordTipDialog'
import { LoginView } from '@/pages/LoginView'
import { dashboard, auth, initDayCut } from '@/lib/api'
import { setStaff, SAVED_LOGIN_KEY } from '@/lib/session'
import { can, loadMatrix } from '@/lib/perm'
import { peekPendingNav } from '@/lib/navbus'
import { initScrollDrag } from '@/lib/scrollDrag'

// 路由级懒加载（O1，09-29）：12 个页面按需分包，recharts 及其 d3/lodash 依赖
// 随之移出首屏（visualizer 实测合计占渲染体积 59%）。
// 登录页 / 外壳 / 骨架屏保持即时加载——登录前就要渲染，不能等 chunk。
// props 用 any：仅 DashboardPage 需要 onNavigate，其余 11 页无 props。
const Pages: Record<string, LazyExoticComponent<ComponentType<any>>> = {
  dashboard: lazy(() => import('@/pages/DashboardPage').then((m) => ({ default: m.DashboardPage }))),
  pos: lazy(() => import('@/pages/PosPage').then((m) => ({ default: m.PosPage }))),
  members: lazy(() => import('@/pages/MembersPage').then((m) => ({ default: m.MembersPage }))),
  'stored-value': lazy(() => import('@/pages/MemberAssetsPage').then((m) => ({ default: m.MemberAssetsPage }))),
  points: lazy(() => import('@/pages/PointsPage').then((m) => ({ default: m.PointsPage }))),
  projects: lazy(() => import('@/pages/ProjectsPage').then((m) => ({ default: m.ProjectsPage }))),
  records: lazy(() => import('@/pages/RecordsPage').then((m) => ({ default: m.RecordsPage }))),
  staff: lazy(() => import('@/pages/StaffPage').then((m) => ({ default: m.StaffPage }))),
  permission: lazy(() => import('@/pages/PermissionPage').then((m) => ({ default: m.PermissionPage }))),
  backup: lazy(() => import('@/pages/BackupPage').then((m) => ({ default: m.BackupPage }))),
  settings: lazy(() => import('@/pages/SettingsPage').then((m) => ({ default: m.SettingsPage }))),
  reports: lazy(() => import('@/pages/ReportsPage').then((m) => ({ default: m.ReportsPage }))),
}

// 快捷操作 → 导航映射
const aliasMap: Record<string, string> = {
  // 11-05：充值入口落收银台（会员资产页店长专属），与 lib/perm.ts 的 ALIAS_OF 同步
  recharge: 'pos',
  'new-member': 'members',
  daily: 'reports',
  export: 'reports',
}

interface Staff { id: number; name: string; role: string }

/** 权限兜底页：深链/跨页跳转指向无权限页签时展示，而不是直接渲染该页面 */
function NoAccess() {
  return (
    <div className="h-full flex items-center justify-center px-6">
      <div className="text-center max-w-xs">
        <div className="w-12 h-12 rounded-full bg-muted flex items-center justify-center mx-auto mb-3">
          <ShieldOff size={22} className="text-muted-foreground" />
        </div>
        <p className="text-sm font-semibold">当前账号无权访问此页面</p>
        <p className="text-xs text-muted-foreground mt-1.5 leading-relaxed">
          请从左侧选择你有权限的功能；如需调整，请联系店长在「权限与日志」修改角色权限。
        </p>
      </div>
    </div>
  )
}

export function AppRoot() {
  const [staff, setStaffState] = useState<Staff | null>(null)
  const [pendingCount, setPendingCount] = useState(0)
  const [pageLoading, setPageLoading] = useState<boolean>(false)
  // 店长用初始密码登录时弹一次改密码提醒（非强制，可直接关掉）
  const [showPwdTip, setShowPwdTip] = useState(false)
  const timerRef = useRef<number | null>(null)

  // 支持 #页签 直达（如 index.html#stored-value），探针/深链用
  const [active, setActive] = useState<string>(() => {
    const h = window.location.hash.replace('#', '')
    return h || 'dashboard'
  })

  const navigate = (key: string) => {
    const target = aliasMap[key] ?? key
    if (target === active) return
    setActive(target)
    // 同步写 hash（用 replaceState 避免触发 hashchange，防止下面监听器把刚切好的 active 又重置一遍）
    try { window.history.replaceState(null, '', '#' + target) } catch { /* file:// 等无 history 场景忽略 */ }
    // 切页显示骨架屏（查询异步完成即渲染）
    setPageLoading(true)
    if (timerRef.current) window.clearTimeout(timerRef.current)
    timerRef.current = window.setTimeout(() => setPageLoading(false), 380)
  }

  // 侧栏「收银开单」的挂单角标：只认库里的 COUNT，不猜。
  // 10-05 店主报「挂单的数字不会自动消掉」——原来只在登录时取一次，之后挂单/取单/结账都不动它，
  // 于是一旦挂过单，数字就一直挂在那里。现在改成：登录时取、切页时取、收到 mm:held 时取。
  const refreshPending = () => {
    dashboard
      .countPending()
      .then((n) => setPendingCount(n))
      .catch(() => { /* 桥不通不能把角标卡死，保持原值 */ })
  }

  // 跨页跳转总线（如：员工列表点「交易记录」直达消费记录并预填操作员）
  useEffect(() => {
    const onNav = () => {
      const req = peekPendingNav()
      if (req) navigate(req.page)
    }
    window.addEventListener('mm:nav', onNav)
    return () => window.removeEventListener('mm:nav', onNav)
  })

  // 挂单数量变化总线：收银台挂单/取单/结账后派发 mm:held，角标立刻重算
  useEffect(() => {
    if (!staff) return
    const onHeld = () => refreshPending()
    window.addEventListener('mm:held', onHeld)
    return () => window.removeEventListener('mm:held', onHeld)
  })

  // 切页兜底：别的路径（如首页卡片、交接班）动了挂单也能对上
  useEffect(() => {
    if (staff) refreshPending()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, staff])

  // hash 双向同步（店主 09-27 补）：
  //  深链直达 / 探针 / 用户改 URL hash 时，active 要跟着变；
  //  反过来 navigate() 也会用 replaceState 同步 hash，两边不会打架。
  useEffect(() => {
    const onHash = () => {
      const h = window.location.hash.replace('#', '')
      if (h && h !== active) setActive(h)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [active])

  const onLogin = (s: Staff, needPwdTip: boolean) => {
    setStaff(s)           // 写入会话（操作员名供各页使用）
    setStaffState(s)     // 更新 React state，触发重渲染
    if (needPwdTip) setShowPwdTip(true)
    // H3：登录后把日切点（settings.day_cut）拉进前端缓存，各页 bizDate() 才算对
    // （不 await——营业日缓存填不填都不阻塞登录，填不到就回退默认 2 点）
    void initDayCut()
    // 拉取权限矩阵；完成后重设会话对象再触发一次渲染，导航即按角色重新过滤
    void loadMatrix().then(() => {
      setStaff({ ...s })
      setStaffState({ ...s })
      // 11-04 店主：切换账号后若当前页签对新账号无权限，别停在「无法访问」空白页——
      // 直接回主工作台（人人都能看）。原来 active 不跟着重置，切到权限更小的账号
      // 就留在原来那个页签上，只看到一个打不开的兜底页。
      if (!can(active)) navigate('dashboard')
    })
    // 挂单待办角标
    refreshPending()
  }

  // 退出账号（店主 09-27 定：点头像下拉「退出账号」→ 回登录页）
  // 先让后端清 Session 并写一条 op_logs 留痕；失败不阻塞前端清本地状态（桥不通也不能被卡住登不出）
  const onLogout = () => {
    void auth.logout().catch(() => { /* 后端写日志失败不影响登出 */ })
    setStaff(null)
    setStaffState(null)
    setPendingCount(0)
    setShowPwdTip(false)
  }

  // 全局拖拽滚动（09-29 店主定）：屏蔽文字选中后，按住鼠标拖页面即翻页。
  // 挂在 AppRoot 而非各页面，登录页也能用；监听挂在 document/window 上，与切页无关。
  useEffect(() => {
    initScrollDrag()
  }, [])

  useEffect(() => {
    return () => {
      if (timerRef.current) window.clearTimeout(timerRef.current)
    }
  }, [])

  if (!staff) {
    return (
      <>
        <LoginView onLogin={onLogin} />
        <Toaster
          position="top-center"
          richColors
          toastOptions={{ style: { fontFamily: 'var(--font-body)', fontSize: 13, borderRadius: 'var(--radius-sm)' } }}
        />
      </>
    )
  }

  const ActivePage = Pages[active] ?? Pages.dashboard

  // 路由兜底：无权限的页签不渲染（导航已过滤，这里防深链 / 跨页跳转越权）——
  // 权限不过时懒加载 chunk 也不会被请求。
  // Suspense 兜住懒加载 chunk（前端跑在虚拟主机 app.local，本地磁盘近乎即时），
  // fallback 复用 PageSkeleton，与切页那 380ms 过渡骨架同一视觉，无跳变。
  const content: ReactNode = !can(active)
    ? <NoAccess />
    : pageLoading
      ? <PageSkeleton />
      : <Suspense fallback={<PageSkeleton />}>
          <ActivePage onNavigate={navigate} />
        </Suspense>

  return (
    <>
      <AppShell active={active} onNavigate={navigate} pendingCount={pendingCount} onLogout={onLogout}>
        {content}
      </AppShell>
      <Toaster
        position="top-center"
        richColors
        toastOptions={{
          style: { fontFamily: 'var(--font-body)', fontSize: 13, borderRadius: 'var(--radius-sm)' },
        }}
      />
      {showPwdTip ? (
        <PasswordTipDialog
          staff={staff}
          onClose={() => setShowPwdTip(false)}
          onChanged={(pwd) => {
            // 改密后「记住密码」里存的还是旧值，不同步的话下次自动填充会直接失败
            try {
              const saved = JSON.parse(localStorage.getItem(SAVED_LOGIN_KEY) ?? 'null') as {
                name: string
                pwd: string
              } | null
              if (saved?.name === staff.name) {
                localStorage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ name: staff.name, pwd }))
              }
            } catch {
              /* 忽略 */
            }
          }}
        />
      ) : null}
    </>
  )
}
