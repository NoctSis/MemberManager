import { useEffect, useState } from 'react'
import { PageScroll } from '@/components/common/PageLayout'
import {
  Store, Clock, Printer, Database, Info, Save, AlertCircle,
  Users, UserPlus, KeyRound, Trash2, ShieldCheck, Lock, Check, KeySquare, Power, Folder,
} from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, SectionCard, Input, Select, Field, Modal, Badge, DataTable,
  tdCls, cx,
} from '@/components/common/ui'
import { LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { settings as apiSettings, perm as apiPerm, system as apiSystem, logs as apiLogs, initDayCut } from '@/lib/api'
import type { LogStats } from '@/lib/api'

/**
 * 开源仓库地址。放在「关于」里，一是让店主知道这软件从哪来、出事找谁，
 * 二是换电脑时能自己下到最新版 —— 软件不联网，没法自动检查更新，
 * 把这个链接摆出来是最省事的替代。
 */
const REPO_URL = 'https://github.com/NoctSis/MemberManager'

/** 账号密码 5 列预算（≈760px，不横滚）；操作列 4 按钮预留 280 */
const ACCOUNT_COLS: DataTableCol[] = [
  { key: 'name', title: '姓名', width: 150, cellClass: 'font-medium' },
  { key: 'role', title: '角色', width: 110 },
  { key: 'phone', title: '手机号', width: 130, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'status', title: '状态', width: 90 },
  { key: 'action', title: '操作', width: 280, headClass: 'text-right', cellClass: 'text-right' },
]
import { useExportDir } from '@/lib/useExportDir'
import { getStaff } from '@/lib/session'
import { refreshMatrix } from '@/lib/perm'
import type { StaffAccount } from '@/data/mock'

interface AccountDraft {
  id: number
  name: string
  role: StaffAccount['role']
  phone: string
  pwd: string
  enabled: boolean
  /** 该账号已勾选的功能模块（新建时按默认给，编辑时读后端按人权限） */
  perms: string[]
}

// 后端模块清单（与 PermissionService.ModuleNames 一致）；「退款/改价」锁死店长专属
const PERM_MODULES = ['会员管理', '收银开单', '退款/改价', '储值与次卡', '交接班日结', '报表导出', '员工提成', '权限设置', '数据备份', '系统设置', '积分商城']
const PERM_REFUND = '退款/改价'
// 新收银员的默认权限：店主 11-05 定「充值一定要有」——与 DAL cashierOn、BLL CashierDefault 三处同源
const PERM_CASHIER_DEFAULT = ['会员管理', '收银开单', '交接班日结', '积分商城', '储值与次卡']
// 勾选清单的通俗说明（界面上每个模块带一句大白话，避免店主分不清哪个是干嘛的）
const PERM_DESC: Record<string, string> = {
  会员管理: '查会员、查档案、建档案',
  收银开单: '开单、结账、挂单、取单',
  '退款/改价': '充值退款（红冲）',
  储值与次卡: '充值与售卡属「收银开单」，此处只管方案/套餐/价目维护（限店长）',
  交接班日结: '交接班、日结',
  报表导出: '看报表、导 Excel',
  员工提成: '提成比例与结算',
  权限设置: '管账号与权限',
  数据备份: '备份与恢复',
  系统设置: '店铺与系统配置',
  // 店主 10-24：积分商城独立成模块——员工可进商城兑换，管理功能页内按店长门控
  积分商城: '看积分商城、给客人兑换',
}

// 路径由后端按「便携优先」实时给出：绿色模式时就在 exe 旁边，非便携时在当前用户的 %LocalAppData%
const FALLBACK_PATHS = {
  db: '',
  logs: '',
  backups: '',
}

// 功能权限勾选清单（账号弹窗里用）。
// 单独抽成一个组件而不是内嵌在 Modal 里：role 是「本组件的入参」，类型收窄干净，
// 不会出现「外层已经判过 role 不是店长、内层再判一次被 TS 认为永远为假」的问题。
function PermPicker({
  role,
  perms,
  onChange,
}: {
  role: '店长' | '收银员' | '技师'
  perms: string[]
  onChange: (next: string[]) => void
}) {
  const isBoss = role === '店长'
  const toggle = (m: string) => {
    if (isBoss) {
      toast.info('店长拥有全部功能，无法增减')
      return
    }
    if (m === PERM_REFUND) {
      toast.info('「退款/改价」仅限店长操作，无法授权给店员')
      return
    }
    const has = perms.indexOf(m) >= 0
    onChange(has ? perms.filter((x) => x !== m) : [...perms, m])
  }
  return (
    <div className="pt-1">
      <div className="flex items-center justify-between mb-2">
        <span className="flex items-center gap-1.5 text-sm font-medium text-foreground">
          <KeySquare size={14} className="text-primary" /> 功能权限
          <span className="text-[11px] font-normal text-muted-foreground">
            {isBoss
              ? '店长拥有全部功能，无需勾选'
              : `已勾选 ${perms.length} 项，其余一律不给`}
          </span>
        </span>
        {isBoss ? null : (
          <span className="flex gap-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onChange([...PERM_CASHIER_DEFAULT])}
              title="恢复收银员默认权限"
            >
              重置默认
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onChange(PERM_MODULES.filter((m) => m !== PERM_REFUND))}
              title="除退款以外的全部功能"
            >
              全开
            </Button>
          </span>
        )}
      </div>
      <div className="grid grid-cols-2 gap-1.5 max-h-60 overflow-y-auto pr-1">
        {PERM_MODULES.map((m) => {
          const on = perms.indexOf(m) >= 0
          const locked = isBoss || (!isBoss && m === PERM_REFUND)
          return (
            <button
              key={m}
              type="button"
              onClick={() => toggle(m)}
              disabled={locked && !on}
              title={locked && !on ? '无法改动' : PERM_DESC[m]}
              className={cx(
                'flex items-center gap-2 rounded-[var(--radius-sm)] border px-2.5 py-2 text-left transition-colors',
                on
                  ? 'border-primary bg-primary/8'
                  : 'border-input hover:border-primary/40',
                locked && !on && 'opacity-50 cursor-not-allowed hover:border-input',
              )}
            >
              <span
                className={cx(
                  'w-4 h-4 rounded-[3px] border inline-flex items-center justify-center shrink-0',
                  on ? 'bg-primary border-primary' : 'border-input bg-card',
                )}
              >
                {on ? <Check size={11} className="text-primary-foreground" /> : null}
              </span>
              <span className="min-w-0 flex-1">
                <span className={cx('block text-xs font-medium', on ? 'text-foreground' : 'text-muted-foreground')}>
                  {m}
                </span>
                <span className="block text-[10px] text-muted-foreground leading-tight">{PERM_DESC[m]}</span>
              </span>
              {m === PERM_REFUND && !isBoss ? <Lock size={11} className="text-muted-foreground/70 shrink-0" /> : null}
            </button>
          )
        })}
      </div>
      <p className="text-[11px] text-muted-foreground mt-2 leading-relaxed">
        勾选的功能该账号才看得到、才用得了，没勾的一律进不去页面、看不到按钮。
        {isBoss ? '店长的功能无法增减。' : '「退款/改价」仅限店长，无法授权给店员。'}
      </p>
    </div>
  )
}

/**
 * 账号管理面板（纯展示，状态由 SettingsPage 持有）。
 * 只管「能登录本系统的人」：店长 / 收银员。技师只在员工管理登记，不进这里。
 */
function AccountPanel({
  accounts,
  onNewAccount,
  onEditAccount,
  onToggleAccount,
  onDeleteAccount,
  onEditPerms,
}: {
  accounts: StaffAccount[]
  onNewAccount: () => void
  onEditAccount: (a: StaffAccount) => void
  onToggleAccount: (a: StaffAccount) => void
  onDeleteAccount: (a: StaffAccount) => void
  onEditPerms: (a: StaffAccount) => void
}) {
  return (
    <SectionCard
      title={
        <span className="flex items-center gap-1.5">
          <Users size={14} className="text-primary" /> 登录账号
          <span className="ml-1 text-[11px] font-normal text-muted-foreground">
            只管「谁能进系统」——做项目的人请在「员工管理」登记
          </span>
        </span>
      }
      extra={
        <Button variant="primary" size="sm" onClick={onNewAccount}>
          <UserPlus size={13} /> 添加账号
        </Button>
      }
      bodyClassName="p-0"
    >
      <DataTable
        cols={ACCOUNT_COLS}
        rows={accounts}
        rowKey={(a) => a.id}
        plain
        maxHeight={LIST_MAX_H}
        render={(a) => [
          a.name,
          <Badge key="r" tone={a.role === '店长' ? 'primary' : 'muted'}>
            {a.role === '店长' ? <ShieldCheck size={11} className="mr-1" /> : null}
            {a.role}
          </Badge>,
          a.phone || '—',
          a.enabled ? <Badge key="st" tone="primary">启用</Badge> : <Badge key="st" tone="danger">已停用</Badge>,
          <span key="a" className="inline-flex gap-1">
            <Button variant="secondary" size="sm" onClick={() => onEditPerms(a)}>
              <KeySquare size={12} /> 功能权限
            </Button>
            <Button variant="ghost" size="sm" onClick={() => onEditAccount(a)}>
              <KeyRound size={12} /> 改密码
            </Button>
            <Button variant="ghost" size="sm" onClick={() => onToggleAccount(a)}>
              {a.enabled ? '停用' : '启用'}
            </Button>
            <Button variant="ghost" size="sm" onClick={() => onDeleteAccount(a)}>
              <Trash2 size={12} /> 删除
            </Button>
          </span>,
        ]}
        empty={<div className="py-6 text-center text-sm text-muted-foreground">正在读取账号…</div>}
      />
    </SectionCard>
  )
}

export function SettingsPage() {
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [dayCut, setDayCut] = useState('02:00')
  // C1 营业时段（10-21 店主定）：开店/收店时间，展示口径用
  const [openTime, setOpenTime] = useState('09:00')
  const [closeTime, setCloseTime] = useState('22:00')
  const [printReceipt, setPrintReceipt] = useState(true)
  const [receiptNote, setReceiptNote] = useState('谢谢惠顾，欢迎再次光临')
  const [logKeepDays, setLogKeepDays] = useState('180')
  // 操作日志现状（条数 / 最早一条 / 按保留期该清多少）—— 让店主看得见「这数填了确实在起作用」
  const [logStats, setLogStats] = useState<LogStats | null>(null)
  const [purging, setPurging] = useState(false)

  const refreshLogStats = () => {
    apiLogs.stats().then(setLogStats).catch(() => {})
  }
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [paths, setPaths] = useState(FALLBACK_PATHS)
  // 发行形态（后端按磁盘/注册表事实判定）：便携版 / Win7 版 / 普通版 / 独立运行
  const [runMode, setRunMode] = useState<{ key: string; text: string; desc: string; kernel: string }>({
    key: 'portable',
    text: '读取中…',
    desc: '',
    kernel: '',
  })
  const [loaded, setLoaded] = useState(false)
  // 程序真实版本（后端 AssemblyFileVersion）。原来这里是写死的「v1.0」，
  // 发到 1.0.3 界面上还显示 1.0，店主报问题时说不清用的是哪一版。
  const [version, setVersion] = useState('')
  // 开机自启存在注册表（壳层），与 settings 表无关 —— 单独加载；读不到就按未开启显示
  const [autostart, setAutostart] = useState(false)
  // 默认导出位置（全局 export_dir；4 个导出默认都落这里，改一次即成新默认）—— 复用共享 hook，单一来源
  const { dir: exportDir, change: chooseExportDir } = useExportDir()

  useEffect(() => {
    apiSystem.autostart().then(setAutostart).catch(() => {})
  }, [])

  useEffect(() => {
    apiSystem
      .status()
      .then((s) => {
        setPaths({ db: s.DbPath, logs: s.LogDir, backups: s.BackupDir })
        setRunMode({
          key: s.RunModeKey || 'portable',
          text: s.RunModeText || '便携版',
          desc: s.RunModeDesc || '',
          kernel: s.WebViewKernel || '',
        })
        if (s.ShopName) setName(s.ShopName)
        if (s.ShopPhone) setPhone(s.ShopPhone)
        setVersion(s.Version || '')
      })
      .catch(() => setPaths(FALLBACK_PATHS))
  }, [])

  // ---------- 账号密码 ----------
  const [accounts, setAccounts] = useState<StaffAccount[]>([])
  const [accDraft, setAccDraft] = useState<AccountDraft | null>(null)
  const [accErrors, setAccErrors] = useState<Record<string, string>>({})
  // 是否店长（登录账号管理仅店长可见）
  const isManager = getStaff()?.role === '店长'

  const loadAccounts = () => {
    apiPerm
      .staffList()
      // 账号密码区只管「操作系统的人」（店长 / 收银员）；技师只在员工管理登记，不进这里
      .then((all) => setAccounts(all.filter((a) => a.role === '店长' || a.role === '收银员')))
      .catch(() => setAccounts([]))
  }
  useEffect(loadAccounts, [])

  const openNewAccount = () => {
    // 新建账号默认只给「用得上的」：会员管理 + 收银开单（修改类一律不给，要放开就在下面打勾）
    setAccDraft({ id: 0, name: '', role: '收银员', phone: '', pwd: '', enabled: true, perms: [...PERM_CASHIER_DEFAULT] })
    setAccErrors({})
  }

  const openEditAccount = (a: StaffAccount) => {
    setAccDraft({ id: a.id, name: a.name, role: a.role, phone: a.phone ?? '', pwd: '', enabled: a.enabled, perms: [] })
    setAccErrors({})
  }

  const submitAccount = () => {
    if (!accDraft) return
    const next: Record<string, string> = {}
    if (!accDraft.name.trim()) next.name = '姓名不能为空'
    if (accDraft.phone.trim() && !/^1\d{10}$/.test(accDraft.phone.trim())) next.phone = '手机号必须为 11 位数字且以 1 开头'
    if (accDraft.id === 0 && accDraft.pwd.length < 6) next.pwd = '新建账号须设置至少 6 位的初始密码'
    if (accDraft.pwd && accDraft.pwd.length < 6) next.pwd = '密码至少 6 位'
    setAccErrors(next)
    if (Object.keys(next).length > 0) return
    const id = accDraft.id
    apiPerm
      .saveAccount(id, accDraft.name.trim(), accDraft.role, accDraft.phone.trim(), accDraft.pwd, accDraft.enabled)
      .then((newId) => {
        // 账号落库后再落按人权限：勾了什么给什么，没勾的模块一律不给
        return apiPerm.setAccountPerms(newId, accDraft.perms).then(() => newId)
      })
      .then((newId) => {
        toast.success(
          id === 0
            ? `已添加登录账号「${accDraft.name.trim()}」，开通 ${accDraft.perms.length} 项功能`
            : `已保存「${accDraft.name.trim()}」的账号与权限`,
        )
        setAccDraft(null)
        loadAccounts()
        refreshMatrix()
      })
      .catch((e) => setAccErrors({ name: String(e) }))
  }

  /** 单独改某个账号的功能权限（账号行的「权限」按钮） */
  const openPerms = (a: StaffAccount) => {
    setAccDraft({
      id: a.id,
      name: a.name,
      role: a.role,
      phone: a.phone ?? '',
      pwd: '',
      enabled: a.enabled,
      perms: a.role === '店长' ? [...PERM_MODULES] : [...PERM_CASHIER_DEFAULT],
    })
    setAccErrors({})
    apiPerm
      .accountPerms(a.id)
      .then((d) => {
        setAccDraft((prev) => (prev && prev.id === a.id ? { ...prev, perms: d.Modules.filter((m) => d.Allowed[m] === true) } : prev))
      })
      .catch(() => {
        // 后端读不到时按角色默认展示，保存时以勾选为准
      })
  }

  const savePerms = () => {
    if (!accDraft) return
    apiPerm
      .setAccountPerms(accDraft.id, accDraft.perms)
      .then(() => {
        setAccDraft(null)
        loadAccounts()
        refreshMatrix()
      })
      .catch((e) => toast.error(String(e)))
  }

  /** 开机自启动：写壳层注册表（HKCU Run），点击立即生效；失败按注册表回读值回滚开关 */
  const toggleAutostart = () => {
    const next = !autostart
    setAutostart(next)
    apiSystem
      .setAutostart(next)
      .then((actual) => {
        setAutostart(actual)
        toast.success(actual ? '已开启：下次开机自动打开主界面' : '已关闭开机自启动')
      })
      .catch((e) => {
        setAutostart(!next)
        toast.error(String(e))
      })
  }

  const toggleAccount = (a: StaffAccount) => {
    apiPerm
      .toggleStaff(a.id)
      .then(() => {
        loadAccounts()
      })
      .catch((e) => toast.error(String(e)))
  }

  const deleteAccount = (a: StaffAccount) => {
    apiPerm
      .deleteStaff(a.id)
      .then(() => {
        loadAccounts()
      })
      .catch((e) => toast.error(String(e)))
  }

  useEffect(() => {
    apiSettings
      .getAll()
      .then((d) => {
        if (d.shop_name) setName(d.shop_name)
        if (d.shop_phone) setPhone(d.shop_phone)
        if (d.day_cut) setDayCut(d.day_cut)
        if (d.open_time) setOpenTime(d.open_time)
        if (d.close_time) setCloseTime(d.close_time)
        if (d.receipt_enabled) setPrintReceipt(d.receipt_enabled === '1')
        if (d.receipt_note) setReceiptNote(d.receipt_note)
        if (d.log_keep_days) setLogKeepDays(d.log_keep_days)
        setLoaded(true)
        refreshLogStats()
      })
      .catch((e) => {
        setLoaded(true)
        toast.error(String(e))
      })
  }, [])

  const save = () => {
    const errs: Record<string, string> = {}
    if (!name.trim()) errs.name = '店铺名称不能为空'
    if (!phone.trim()) errs.phone = '联系电话不能为空'
    if (!/^\d+$/.test(logKeepDays.trim()) || Number(logKeepDays) < 30) {
      errs.logKeepDays = '日志保留天数须为不小于 30 的整数'
    }
    setErrors(errs)
    if (Object.keys(errs).length > 0) {
      toast.error('有必填项未通过校验，请检查标红的输入框')
      return
    }
    setSaving(true)
    // 店名/电话走 saveShop：它带校验（店名必填 1~20 字、电话 ≤30 字）且单事务原子落库。
    // 注意 saveShop 写的就是 settings 表同一对行（key=shop_name / shop_phone），
    // 所以不要再把这两项塞进下面的 settings.save 批量里 —— 那是同一值的重复写，
    // 还会让「谁是权威」变模糊（校验只有一处才不歧义）。
    apiSystem
      .saveShop(name.trim(), phone.trim())
      .then(() =>
        apiSettings.save({
          day_cut: dayCut,
          open_time: openTime,
          close_time: closeTime,
          receipt_enabled: printReceipt ? '1' : '0',
          receipt_note: receiptNote.trim(),
          log_keep_days: logKeepDays.trim(),
        }),
      )
      .then(() => {
        setSaving(false)
        // H3：日切点存进 settings 后立刻刷新前端缓存，主工作台/报表的营业日即时生效，不用重登
        void initDayCut()
        refreshLogStats()
        // 通知顶栏/登录页立即刷新店名
        window.dispatchEvent(new CustomEvent<string>('mm:shop', { detail: name.trim() }))
      })
      .catch((e) => {
        setSaving(false)
        toast.error(String(e))
      })
  }

  /**
   * 立即按当前填的保留天数清一次（店主 11-05：「到了 180 天（或设置的数值）就开始删最原始的」）。
   * 程序每天启动会自动跑一遍；这个按钮是「现在就清」+「看看清完剩多少」。
   * 只删操作日志，业务流水/订单/会员余额一条都不动。
   */
  const purgeLogs = () => {
    const days = Number(logKeepDays)
    if (!/^\d+$/.test(logKeepDays.trim()) || !Number.isFinite(days) || days < 30) {
      toast.error('先把「保留天数」填成不小于 30 的整数，再点清理')
      return
    }
    setPurging(true)
    apiLogs
      .purge(days)
      .then((r) => {
        setPurging(false)
        refreshLogStats()
        toast.success(
          r.removed > 0
            ? `已清理 ${r.removed} 条超过 ${r.keepDays} 天的日志，剩余 ${r.remain} 条`
            : `没有超过 ${r.keepDays} 天的日志，无需清理（当前 ${r.remain} 条）`,
        )
      })
      .catch((e) => {
        setPurging(false)
        toast.error(String(e))
      })
  }

  return (
    <PageScroll minWidth={1080}>
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            系统设置
          </h1>
        </div>
        <Button variant="primary" onClick={save} disabled={saving || !loaded}>
          <Save size={14} /> {saving ? '保存中…' : '保存设置'}
        </Button>
      </div>

      {/* 店长专属：登录账号（新建 / 改名 / 停用 / 按人授权都在这里） */}
      {isManager && (
        <AccountPanel
          accounts={accounts}
          onNewAccount={openNewAccount}
          onEditAccount={openEditAccount}
          onToggleAccount={toggleAccount}
          onDeleteAccount={deleteAccount}
          onEditPerms={openPerms}
        />
      )}

      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-4">
          <SectionCard
            title={<span className="flex items-center gap-1.5"><Store size={14} className="text-primary" /> 店铺信息</span>}
            bodyClassName="px-4 py-3.5 space-y-3"
          >
            <Field label="店铺名称" required error={errors.name}>
              <Input value={name} onChange={(e) => setName(e.target.value)} />
            </Field>
            <Field label="联系电话" required error={errors.phone}>
              <Input value={phone} onChange={(e) => setPhone(e.target.value)} />
            </Field>
          </SectionCard>

          <SectionCard
            title={<span className="flex items-center gap-1.5"><Clock size={14} className="text-primary" /> 营业设置</span>}
            bodyClassName="px-4 py-3.5 space-y-3"
          >
            <Field label="日切点（营业日分界时间）">
              <Select value={dayCut} onChange={(e) => setDayCut(e.target.value)}>
                <option>02:00</option>
                <option>03:00</option>
                <option>04:00</option>
              </Select>
            </Field>
            <p className="text-[11px] text-muted-foreground">
              凌晨 {dayCut} 前发生的单据计入前一营业日；超过日切点未日结，程序启动时自动补日结并提示。
            </p>
            {/* C1 营业时段：展示口径（几点开门/关门），报表与日结的分界仍以日切点为准 */}
            <div className="grid grid-cols-2 gap-3">
              <Field label="开店时间">
                <input
                  type="time"
                  value={openTime}
                  onChange={(e) => setOpenTime(e.target.value)}
                  className="h-9 w-full px-2 text-sm bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
                  aria-label="开店时间"
                />
              </Field>
              <Field label="收店时间">
                <input
                  type="time"
                  value={closeTime}
                  onChange={(e) => setCloseTime(e.target.value)}
                  className="h-9 w-full px-2 text-sm bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
                  aria-label="收店时间"
                />
              </Field>
            </div>
            <p className="text-[11px] text-muted-foreground">
              一天 {openTime} 开门、{closeTime} 打烊；报表「某日」= 当日开店至次日凌晨 {dayCut}。
            </p>
            <label className="flex items-center justify-between rounded-[var(--radius-sm)] border border-input px-3 h-10 cursor-pointer hover:border-primary/40 transition-colors">
              <span className="flex items-center gap-2 text-sm">
                <Printer size={14} className="text-muted-foreground" /> 小票打印
                <span className="text-[10px] text-muted-foreground border border-border rounded px-1 leading-4">默认开启</span>
              </span>
              <button
                role="switch"
                aria-checked={printReceipt}
                aria-label="小票打印开关"
                onClick={() => {
                  const next = !printReceipt
                  setPrintReceipt(next)
                  toast.info(next ? '小票打印已开启，结账完成后可直接打印（无打印机时可选「Microsoft Print to PDF」验证版式）' : '小票打印已关闭')
                }}
                className={cx(
                  'w-9 h-5 rounded-[var(--radius-pill)] relative transition-colors',
                  printReceipt ? 'bg-primary' : 'bg-muted border border-input',
                )}
              >
                <span
                  className={cx(
                    'absolute top-0.5 w-4 h-4 rounded-full bg-card shadow transition-all',
                    printReceipt ? 'left-[18px]' : 'left-0.5',
                  )}
                />
              </button>
            </label>
            <label className="flex items-center justify-between rounded-[var(--radius-sm)] border border-input px-3 h-10 cursor-pointer hover:border-primary/40 transition-colors">
              <span className="flex items-center gap-2 text-sm">
                <Power size={14} className="text-muted-foreground" /> 开机自启动
                <span className="text-[10px] text-muted-foreground">开机后自动打开主界面</span>
              </span>
              <button
                role="switch"
                aria-checked={autostart}
                aria-label="开机自启动开关"
                onClick={toggleAutostart}
                className={cx(
                  'w-9 h-5 rounded-[var(--radius-pill)] relative transition-colors',
                  autostart ? 'bg-primary' : 'bg-muted border border-input',
                )}
              >
                <span
                  className={cx(
                    'absolute top-0.5 w-4 h-4 rounded-full bg-card shadow transition-all',
                    autostart ? 'left-[18px]' : 'left-0.5',
                  )}
                />
              </button>
            </label>
            <Field label="小票尾部备注（启用打印后生效）">
              <Input value={receiptNote} onChange={(e) => setReceiptNote(e.target.value)} disabled={!printReceipt} />
            </Field>
          </SectionCard>

        </div>

        <div className="space-y-4">
          <SectionCard
            title={<span className="flex items-center gap-1.5"><Database size={14} className="text-primary" /> 数据与日志</span>}
            bodyClassName="px-4 py-3.5 space-y-3"
          >
            <div className="text-xs space-y-1.5">
              {/* 运行模式 = 当前实际装的是哪一种（便携包/安装包），不给切换入口：
                  后端按 {exe}\unins000.exe、系统版本、{exe}\runtime\ 三项事实判定，
                  见 Common\RunProfile。装出来的版本也带 portable.txt，不能拿它当依据。 */}
              <div className="space-y-0.5">
                <p className="flex justify-between gap-3 items-baseline">
                  <span className="text-muted-foreground shrink-0">运行模式</span>
                  <span className="text-primary">{runMode.text}</span>
                </p>
                {runMode.desc ? (
                  <p className="text-[10px] text-muted-foreground text-right leading-snug">{runMode.desc}</p>
                ) : null}
              </div>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">浏览器内核</span>
                <span className="text-right break-all">{runMode.kernel || '读取中…'}</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">数据库位置</span>
                <span className="font-mono text-[11px] text-right break-all">{paths.db || '读取中…'}</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">日志位置</span>
                <span className="font-mono text-[11px] text-right break-all">{paths.logs || '读取中…'}</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">备份位置</span>
                <span className="font-mono text-[11px] text-right break-all">{paths.backups || '读取中…'}</span>
              </p>
              {/* 导出目录是长路径（安装目录\exports），单独占一行右对齐、自由换行；
                  「更换」跟着标签走，避免路径折行时把按钮挤到半个字的位置（11-05 装出来实测发现）。 */}
              <div className="space-y-1">
                <p className="flex justify-between gap-3 items-center">
                  <span className="text-muted-foreground">默认导出位置</span>
                  <button
                    type="button"
                    onClick={chooseExportDir}
                    className="inline-flex items-center gap-1 h-6 px-2 shrink-0 rounded-[var(--radius-sm)] text-[11px] border border-input bg-card hover:bg-accent hover:text-accent-foreground transition-colors"
                  >
                    <Folder size={11} /> 更换
                  </button>
                </p>
                <p className="font-mono text-[11px] text-right break-all leading-snug">{exportDir || '读取中…'}</p>
              </div>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">存储引擎</span>
                <span className="text-right">SQLite WAL · 同步级别 FULL · 单机离线</span>
              </p>
            </div>
            <Field label="操作日志保留天数" required error={errors.logKeepDays}>
              <div className="flex items-center gap-2">
                <Input
                  value={logKeepDays}
                  onChange={(e) => setLogKeepDays(e.target.value)}
                  inputMode="numeric"
                  className="w-24"
                />
                <span className="text-xs text-muted-foreground">天</span>
                <Button size="sm" variant="secondary" onClick={purgeLogs} disabled={purging || !loaded}>
                  <Trash2 size={12} /> {purging ? '清理中…' : '立即清理'}
                </Button>
              </div>
            </Field>
            <p className="text-[11px] text-muted-foreground leading-relaxed -mt-1">
              {logStats
                ? `当前 ${logStats.total} 条操作日志${logStats.oldest ? `，最早一条 ${logStats.oldest}` : ''}；` +
                  `程序每次启动会按「保留 ${logStats.keepDays} 天」自动删掉更老的记录` +
                  `${logStats.overdue > 0 ? `，现在有 ${logStats.overdue} 条已过期` : '，目前没有过期记录'}` +
                  `${logStats.lastPurge ? `；上次清理 ${logStats.lastPurge}` : ''}。`
                : '正在读取日志量…'}
              <br />
              只清操作日志（谁改了什么），会员余额流水、订单、提成一条都不动。
            </p>
          </SectionCard>

          <SectionCard
            title={<span className="flex items-center gap-1.5"><Info size={14} className="text-primary" /> 关于</span>}
            bodyClassName="px-4 py-3"
          >
            {/* 258 宽的下半栏放不下「四段横排」：原来 flex + gap-6 会把每段压到 ~100px，
                「单店单机版」被拆成一行一个字。改成与其他卡片一致的「左标签 / 右值」竖排。 */}
            <div className="text-xs space-y-1.5">
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">版本</span>
                <span className="text-right">收银系统 v{version || '—'}（单店单机版）</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">运行环境</span>
                <span className="text-right break-words">Windows 7 SP1 / 10 / 11 · .NET Framework 4.8 · x86</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">数据存储</span>
                <span className="text-right">SQLite（本机，不上传云端）</span>
              </p>
              <p className="flex justify-between gap-3">
                <span className="text-muted-foreground shrink-0">联网</span>
                <span className="text-right text-primary inline-flex items-center gap-1 justify-end">
                  <AlertCircle size={12} /> 完全离线可用，无需联网
                </span>
              </p>
              {/* 软件不联网就查不到新版，这里给个能点的入口：
                  点一下用系统浏览器打开项目主页，能下载最新安装包、看改了什么。 */}
              <p className="flex justify-between gap-3 items-start">
                <span className="text-muted-foreground shrink-0">项目主页</span>
                <button
                  type="button"
                  onClick={() =>
                    apiSystem
                      .openUrl(REPO_URL)
                      .catch((e) => toast.error(String(e) + '（地址：' + REPO_URL + '）'))
                  }
                  className="text-primary hover:underline text-right break-all leading-snug"
                  title={'在浏览器中打开：' + REPO_URL + '\n源码、更新记录、问题反馈、最新安装包'}
                >
                  github.com/NoctSis/MemberManager
                </button>
              </p>
            </div>
          </SectionCard>
        </div>
      </div>

      {accDraft ? (
        <Modal
          title={accDraft.id === 0 ? '添加登录账号' : `账号与权限 · ${accDraft.name}`}
          onClose={() => setAccDraft(null)}
          width={620}
          footer={
            <>
              <Button variant="secondary" onClick={() => setAccDraft(null)}>
                取消
              </Button>
              <Button variant="primary" onClick={accDraft.id === 0 ? submitAccount : savePerms}>
                <KeyRound size={13} /> {accDraft.id === 0 ? '确认添加' : '保存权限'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="姓名" required error={accErrors.name}>
              <Input value={accDraft.name} onChange={(e) => setAccDraft({ ...accDraft, name: e.target.value })} autoFocus />
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="角色">
                <Select value={accDraft.role} onChange={(e) => setAccDraft({ ...accDraft, role: e.target.value as StaffAccount['role'] })}>
                  <option>收银员</option>
                  <option>店长</option>
                </Select>
              </Field>
              <Field label="手机号" error={accErrors.phone}>
                <Input
                  value={accDraft.phone}
                  onChange={(e) => setAccDraft({ ...accDraft, phone: e.target.value })}
                  placeholder="选填"
                  className="tabular-nums"
                />
              </Field>
            </div>
            <Field label={accDraft.id === 0 ? '初始密码' : '新密码（留空则不改）'} required={accDraft.id === 0} error={accErrors.pwd}>
              <Input
                type="password"
                value={accDraft.pwd}
                onChange={(e) => setAccDraft({ ...accDraft, pwd: e.target.value })}
                placeholder="至少 6 位"
              />
            </Field>
            <label className="flex items-center justify-between rounded-[var(--radius-sm)] border border-input px-3 h-10 cursor-pointer hover:border-primary/40 transition-colors">
              <span className="text-sm">启用（停用后立即无法登录，历史单据保留）</span>
              <button
                role="switch"
                aria-checked={accDraft.enabled}
                aria-label="账号启用状态"
                onClick={() => setAccDraft({ ...accDraft, enabled: !accDraft.enabled })}
                className={cx(
                  'w-9 h-5 rounded-[var(--radius-pill)] relative transition-colors',
                  accDraft.enabled ? 'bg-primary' : 'bg-muted border border-input',
                )}
              >
                <span
                  className={cx(
                    'absolute top-0.5 w-4 h-4 rounded-full bg-card shadow transition-all',
                    accDraft.enabled ? 'left-[18px]' : 'left-0.5',
                  )}
                />
              </button>
            </label>

            <PermPicker role={accDraft.role} perms={accDraft.perms} onChange={(p) => setAccDraft({ ...accDraft, perms: p })} />
          </div>
        </Modal>
      ) : null}

    </PageScroll>
  )
}
