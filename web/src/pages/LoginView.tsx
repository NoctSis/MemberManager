import { useEffect, useRef, useState } from 'react'
import { Store, Lock, UserRound } from 'lucide-react'
import { Button, Input, Select, Field } from '@/components/common/ui'
import { auth, system as apiSystem } from '@/lib/api'
import { setStaff, SAVED_LOGIN_KEY, DEFAULT_PWD, pwdTipOffKey } from '@/lib/session'

// 登录页：验证阶段自动填入账号密码，回车即可进入（店主 09-22 定）
// 记住密码：所有账号（含店长）都可勾选保存；保存的是登录凭据本身，界面上始终掩码显示，
// 只用于下次自动填充，不提供任何查看明文的入口（店主 10-08 定）。

export function LoginView({
  onLogin,
}: {
  /**
   * needPwdTip = 本次登录后要不要弹「建议改密码」提醒。
   * 判定放在登录页，是因为只有这里同时握有「登录用的明文密码」和「账号角色」。
   */
  onLogin: (s: { id: number; name: string; role: string }, needPwdTip: boolean) => void
}) {
  const [name, setName] = useState('')
  const [pwd, setPwd] = useState('')
  const [accounts, setAccounts] = useState<{ Name: string; LoginName: string; Role: string }[]>([])
  const [remember, setRemember] = useState(false) // 不勾 = 下次要密码；勾了 = 保存（店主 11-05）
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [shopName, setShopName] = useState('')

  // 探针开关只在启动时读一次：?probe=1 走自动登录；&probePwdTip=1 才会额外弹出改密码提醒
  // （探针专用 URL 参数，由 C# 侧 MM_PROBE_PWD_TIP=1 时拼上；自动登录本身是「店长+初始密码」，
  //  默认不弹，否则会挡住所有常规截图）。生产环境 URL 不含该参数，行为零影响。
  const probe = useRef({
    probe: window.location.search.includes('probe=1'),
    tip: new URLSearchParams(window.location.search).get('probePwdTip') === '1',
  })

  // 店名读真实设置；登录页在鉴权前也能走桥读库，失败则留空不影响登录
  useEffect(() => {
    apiSystem.status().then((s) => setShopName(s.ShopName || '')).catch(() => setShopName(''))
  }, [])

  // 登录账号下拉：读真实库（系统设置里新增/停用的账号立刻反映到这里）
  // 只显示店长和收银员；技师只在员工管理登记，不进这里
  useEffect(() => {
    auth.staffList().then((list) => {
      const rows = (list ?? []).filter((r) => r.Enabled !== false && (r.Role === '店长' || r.Role === '收银员'))
      // 登录值用唯一 LoginName（同名员工靠它区分）；Name 只作显示
      setAccounts(rows.map((r) => ({ Name: r.Name, LoginName: r.LoginName || r.Name, Role: r.Role })))
      if (rows.length > 0) setName((prev) => prev || (rows[0].LoginName || rows[0].Name))
    }).catch(() => setAccounts([]))
  }, [])

  useEffect(() => {
    try {
      const saved = JSON.parse(localStorage.getItem(SAVED_LOGIN_KEY) ?? 'null') as { name: string; pwd: string } | null
      if (saved?.name && saved?.pwd) {
        setName(saved.name)
        setPwd(saved.pwd)
        setRemember(true)
      }
    } catch { /* 忽略 */ }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 界面自检探针（?probe=1）：干净 WebView 数据目录无登录态，自动用首个启用账号 + 默认密码进入，
  // 以便 probe 能截图登录后的真实页面；?probeUser=账号名 可指定角色（验证权限视图）。
  // 正常（非探针）登录流程完全不受影响。
  useEffect(() => {
    if (!window.location.search.includes('probe=1')) return
    const probeUser = new URLSearchParams(window.location.search).get('probeUser') ?? ''
    auth.staffList().then((list) => {
      const rows = (list ?? []).filter((r) => r.Enabled !== false)
      const target = probeUser ? rows.find((r) => r.Name === probeUser) : undefined
      const pick = target ?? rows[0]
      if (pick) submit(pick.Name, '123456')
    }).catch(() => { /* 探针环境桥不可用时保持登录页，不报错 */ })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const submit = async (n?: string, p?: string) => {
    const uname = (n ?? name).trim()
    const upwd = p ?? pwd
    if (!uname || !upwd) {
      setError('请输入账号与密码')
      return
    }
    setBusy(true)
    setError('')
    try {
      const info = await auth.login(uname, upwd)
      setStaff(info)
      // 所有账号都可记住密码（含店长，店主 10-08 定）
      if (remember) {
        localStorage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ name: uname, pwd: upwd }))
      } else {
        localStorage.removeItem(SAVED_LOGIN_KEY)
      }
      // 改密码提醒：仅店长 + 本次用的还是初始密码。
      // 探针模式下改成「预置 mm_probe_pwd_tip=1 才弹」——否则自动登录每次都是
      // 店长 + 初始密码，每张截图都会糊一层弹窗；需要单独验样式时再预置这个键。
      const tipWanted =
        info.role === '店长' &&
        upwd === DEFAULT_PWD &&
        localStorage.getItem(pwdTipOffKey(info.id)) !== '1'
      const showTip = probe.current.probe ? probe.current.tip : tipWanted
      onLogin(info, showTip)
      // 登录成功后清掉 hash，避免与页内导航冲突。
      // 关键：用 replaceState 而非 pushState——replaceState 不触发 hashchange，
      // 不会把 AppRoot 刚根据 URL 初始化的 active 又重置回 dashboard（09-27 修）。
      if (window.location.hash) history.replaceState(null, '', window.location.pathname + window.location.search)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="h-screen w-screen flex items-center justify-center bg-background" style={{ fontFamily: 'var(--font-body)' }}>
      <div className="w-[380px] bg-card border border-border rounded-[var(--radius-md)] shadow-[var(--elev-raised)] p-7">
        <div className="flex items-center gap-3 mb-6">
          <div className="w-11 h-11 rounded-[var(--radius-sm)] bg-primary text-primary-foreground flex items-center justify-center shadow-sm">
            <Store size={22} />
          </div>
          <div className="leading-tight">
            <p className="text-lg font-bold" style={{ fontFamily: 'var(--font-display)' }}>收银系统</p>
            <p className="text-xs text-muted-foreground mt-0.5">{shopName || '收银系统'} · 单机版</p>
          </div>
        </div>

        <form
          className="space-y-3.5"
          onSubmit={(e) => { e.preventDefault(); submit() }}
        >
          <Field label="员工账号" required>
            <div className="relative">
              <UserRound size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
              <Select value={name} onChange={(e) => setName(e.target.value)} className="pl-8" aria-label="员工账号">
                {accounts.length === 0 ? (
                  <option value="">读取中…</option>
                ) : (
                  accounts.map((a) => (
                    <option key={a.LoginName} value={a.LoginName}>
                      {a.LoginName === a.Name
                        ? `${a.Name}（${a.Role}）`
                        : `${a.LoginName} — ${a.Name}（${a.Role}）`}
                    </option>
                  ))
                )}
              </Select>
            </div>
          </Field>
          <Field label="登录密码" required>
            <div className="relative">
              <Lock size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
              <Input
                type="password"
                value={pwd}
                onChange={(e) => setPwd(e.target.value)}
                className="pl-8"
                aria-label="登录密码"
              />
            </div>
          </Field>

          {error ? (
            <p className="text-xs text-destructive" role="alert">{error}</p>
          ) : null}

          <label className="flex items-center gap-2 text-xs text-muted-foreground select-none">
            <input
              type="checkbox"
              checked={remember}
              onChange={(e) => setRemember(e.target.checked)}
              className="accent-[hsl(var(--primary))]"
            />
            记住密码
          </label>

          <Button variant="primary" className="w-full" type="submit" disabled={busy}>
            {busy ? '登录中…' : '登 录'}
          </Button>
        </form>
      </div>
    </div>
  )
}
