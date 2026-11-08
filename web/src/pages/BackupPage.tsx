import { useEffect, useMemo, useState } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import {
  DatabaseBackup, History, AlertTriangle, HardDriveDownload, CheckCircle2, Loader2,
  Settings2, Trash2, FileDown, FileUp, XCircle, RotateCcw, Lock, Unlock,
} from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, SectionCard, Badge, Modal, DataTable,
  cx,
} from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { backup as apiBackup, system as apiSystem } from '@/lib/api'
import type { BackupTable, BackupInspect, BackupImportResult } from '@/lib/api'
import type { BackupVersion } from '@/data/mock'

/** 备份版本 6 列预算（≈850px，不横滚）；说明列弹性截断。勾选列给下方「导出选中」用 */
const BACKUP_COLS: DataTableCol[] = [
  { key: 'pick', title: '', width: 34 },
  { key: 'time', title: '备份时间', width: 170, cellClass: 'tabular-nums font-medium' },
  { key: 'type', title: '类型', width: 120 },
  { key: 'size', title: '大小', width: 110, headClass: 'text-right', cellClass: 'text-right tabular-nums text-muted-foreground' },
  { key: 'note', title: '说明', truncate: true, cellClass: 'text-muted-foreground' },
  { key: 'action', title: '操作', width: 220, headClass: 'text-right', cellClass: 'text-right' },
]

/** 导出弹窗的分组顺序与说明文案 */
const GROUP_ORDER = ['业务数据', '系统底座', '其他']
const GROUP_HINT: Record<string, string> = {
  业务数据: '会员、等级、流水、订单、提成、核销',
  系统底座: '缺了恢复出来登不进、项目名空白',
  其他: '日志与统计，一般不用带走',
}

export function BackupPage() {
  const [versions, setVersions] = useState<BackupVersion[]>([])
  const [backingUp, setBackingUp] = useState(false)
  const [restoreTarget, setRestoreTarget] = useState<BackupVersion | null>(null)
  const [restoreConfirmText, setRestoreConfirmText] = useState('')
  // D3 每日自动备份
  const [autoOn, setAutoOn] = useState(false)
  const [autoHour, setAutoHour] = useState('20')
  const [autoLast, setAutoLast] = useState('')
  const [backupDir, setBackupDir] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<BackupVersion | null>(null)

  // 备份加密（店主 11-08）：备份里有全部会员手机号和余额，落地即密文
  const [enc, setEnc] = useState<{ enabled: boolean; hasPassword: boolean; hint: string; backupCount: number; plainCount: number }>(
    { enabled: false, hasPassword: false, hint: '', backupCount: 0, plainCount: 0 },
  )
  const [encOpen, setEncOpen] = useState(false)
  const [encOld, setEncOld] = useState('')
  const [encNew, setEncNew] = useState('')
  const [encNew2, setEncNew2] = useState('')
  const [encHint, setEncHint] = useState('')
  const [encBusy, setEncBusy] = useState(false)
  // 恢复加密备份时要输入的密码
  const [restorePwd, setRestorePwd] = useState('')

  // 导出数据（店主 11-05）：按表勾选，默认勾业务数据 + 系统底座
  const [exportOpen, setExportOpen] = useState(false)
  const [tables, setTables] = useState<BackupTable[]>([])
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [exporting, setExporting] = useState(false)

  // 下方备份列表：勾选若干版本 → 原样另存出去（与上方「导出数据」分工不同：
  // 上方是按表挑内容导当前库，这里是导出已经存在的备份文件本身，可多选）
  const [pickedVersions, setPickedVersions] = useState<Set<string>>(new Set())
  const [exportingVersions, setExportingVersions] = useState(false)

  // 导入备份：Windows 打开对话框 → 自动识别 → 确认恢复
  const [importOpen, setImportOpen] = useState(false)
  const [importPath, setImportPath] = useState('')
  const [inspect, setInspect] = useState<BackupInspect | null>(null)
  const [importing, setImporting] = useState(false)
  const [importConfirm, setImportConfirm] = useState('')
  // 导入完成后的结果（恢复了哪些 / 哪些保持原样）；非空 = 弹窗切到结果视图
  const [importResult, setImportResult] = useState<BackupImportResult | null>(null)

  const canRestore = restoreConfirmText.trim() === '恢复'
  // 店主 11-05：导入不再要求「表齐全」——文件里有什么表就恢复什么表，缺的表保持当前数据，
  // 所以只要文件能打开（无 error）就能导入，不再把「数据导出」一票否决。
  const canImport = !!inspect && !inspect.error && importConfirm.trim() === '导入'

  const load = () => {
    apiBackup.list().then(setVersions).catch((e) => toast.error(String(e)))
    apiSystem.status().then((s) => setBackupDir(s.BackupDir || '')).catch(() => setBackupDir(''))
    apiBackup
      .getAuto()
      .then((a) => { setAutoOn(a.enabled); setAutoHour(String(a.hour)); setAutoLast(a.lastDate || '') })
      .catch(() => {})
    apiBackup.encryptStatus().then(setEnc).catch(() => {})
  }
  useEffect(load, [])
  // 进页面触发一次每日自动备份检查：到点且当天还没备份才会真的执行
  useEffect(() => {
    apiBackup
      .autoTick()
      .then((name) => { if (name) { load() } })
      .catch(() => {})
  }, [])

  // 列表刷新后把已经不存在的文件从勾选里摘掉（删除/轮转后不会留下幽灵勾选）
  useEffect(() => {
    setPickedVersions((prev) => {
      if (prev.size === 0) return prev
      const alive = new Set(versions.map((v) => v.fileName))
      const next = new Set(Array.from(prev).filter((f) => alive.has(f)))
      return next.size === prev.size ? prev : next
    })
  }, [versions])

  const allVersionsPicked = versions.length > 0 && pickedVersions.size === versions.length

  const toggleVersion = (fileName: string) => {
    setPickedVersions((prev) => {
      const next = new Set(prev)
      if (next.has(fileName)) next.delete(fileName)
      else next.add(fileName)
      return next
    })
  }

  const doExportVersions = () => {
    if (pickedVersions.size === 0) { toast.error('先勾选要导出的备份版本'); return }
    apiSystem
      .pickFolder()
      .then((dir) => {
        if (!dir) return
        setExportingVersions(true)
        apiBackup
          .exportVersions(Array.from(pickedVersions), dir)
          .then((r) => {
            setExportingVersions(false)
            setPickedVersions(new Set())
            toast.success(`已导出 ${r.count} 个备份版本 → ${r.dir}`)
          })
          .catch((e) => { setExportingVersions(false); toast.error(String(e)) })
      })
      .catch((e) => toast.error(String(e)))
  }

  const saveAuto = () => {
    const h = Number(autoHour)
    if (!Number.isInteger(h) || h < 0 || h > 23) { toast.error('自动备份时间必须是 0-23 的整数'); return }
    apiBackup
      .setAuto(autoOn, h)
      .then(() => { toast.success(autoOn ? `已开启每日 ${h} 点自动备份` : '已关闭每日自动备份'); load() })
      .catch((e) => toast.error(String(e)))
  }

  const doDelete = () => {    if (!deleteTarget) return
    apiBackup
      .remove(deleteTarget.fileName)
      .then(() => { toast.success(`已删除备份「${deleteTarget.time}」（不影响当前数据）`); setDeleteTarget(null); load() })
      .catch((e) => { toast.error(String(e)); setDeleteTarget(null) })
  }

  const manualBackup = () => {
    if (backingUp) return
    setBackingUp(true)
    toast.loading('正在备份数据库…', { id: 'backup' })
    apiBackup
      .backupNow(true)
      .then(() => {
        setBackingUp(false)
        // 原为toast.success(..., { id: 'backup' })；按§二十八清理后改为直接收掉
        // 「正在备份数据库…」的 loading（否则转圈会一直挂着不消失）。
        // name 已由下方 load() 后的备份列表体现，不再单独弹提示。
        toast.dismiss('backup')
        load()
      })
      .catch((e) => {
        setBackingUp(false)
        toast.error(String(e), { id: 'backup' })
      })
  }

  const doRestore = () => {
    if (!restoreTarget || !canRestore) return
    const found = versions.find((v) => v.id === restoreTarget.id)
    if (!found) return
    if (found.encrypted && !restorePwd) { toast.error('这个备份是加密的，请输入备份密码'); return }
    apiBackup
      .restore(found.fileName, found.encrypted ? restorePwd : '')
      .then(() => {
        toast.success(`已恢复到备份「${restoreTarget.time}」：恢复前数据已自动另存快照，请重启软件`)
        setRestoreTarget(null)
        setRestoreConfirmText('')
        setRestorePwd('')
        load()
      })
      .catch((e) => toast.error(String(e)))
  }

  /* ---------------- 备份加密 ---------------- */

  const openEncDialog = () => {
    setEncOld('')
    setEncNew('')
    setEncNew2('')
    setEncHint(enc.hint || '')
    setEncOpen(true)
  }

  const saveEnc = () => {
    if (encNew.length < 8) { toast.error('备份密码至少 8 位，且不能全是数字'); return }
    if (encNew !== encNew2) { toast.error('两次输入的新密码不一致'); return }
    setEncBusy(true)
    apiBackup
      .setPassword(enc.enabled && enc.hasPassword ? encOld : '', encNew, encHint)
      .then(() => {
        setEncBusy(false)
        setEncOpen(false)
        toast.success('备份密码已设置，之后的备份都会加密保存')
        load()
      })
      .catch((e) => { setEncBusy(false); toast.error(String(e)) })
  }

  const disableEnc = () => {
    if (!encOld) { toast.error('请先在上面填写当前备份密码，再点「关闭加密」'); return }
    apiBackup
      .disableEncrypt(encOld)
      .then(() => { setEncOpen(false); toast.success('已关闭加密，之后的新备份为明文文件'); load() })
      .catch((e) => toast.error(String(e)))
  }

  /* ---------------- 导出数据 ---------------- */

  const openExport = () => {
    apiBackup
      .tables()
      .then((ts) => {
        setTables(ts)
        setPicked(new Set(ts.filter((t) => t.defaultOn).map((t) => t.name)))
        setExportOpen(true)
      })
      .catch((e) => toast.error(String(e)))
  }

  const toggleTable = (name: string) => {
    setPicked((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }

  const grouped = useMemo(() => {
    const m: Record<string, BackupTable[]> = {}
    for (const t of tables) (m[t.group] = m[t.group] || []).push(t)
    return m
  }, [tables])

  const pickedRows = useMemo(
    () => tables.filter((t) => picked.has(t.name)).reduce((s, t) => s + t.count, 0),
    [tables, picked],
  )
  const exportIsFull = tables.length > 0 && picked.size === tables.length
  // 默认组合 = 业务数据 + 系统底座。默认导出属于常态，不该每次飘红；
  // 只有用户把业务表本身取消勾选时才需要醒目警告。
  const defaultSet = useMemo(
    () => new Set(tables.filter((t) => t.defaultOn).map((t) => t.name)),
    [tables],
  )
  const isDefaultPick = picked.size === defaultSet.size && Array.from(defaultSet).every((n) => picked.has(n))

  const doExport = () => {
    if (picked.size === 0) { toast.error('请至少勾选一张表'); return }
    const now = new Date()
    const p = (n: number) => String(n).padStart(2, '0')
    const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}`
    apiSystem
      .pickSaveFile(`收银系统-数据-${stamp}.db`, '数据库文件 (*.db)|*.db')
      .then((path) => {
        if (!path) return
        setExporting(true)
        apiBackup
          .exportSelected(Array.from(picked), path)
          .then((r) => {
            setExporting(false)
            setExportOpen(false)
            toast.success(
              `已导出 ${r.tableCount}/${r.totalTables} 张表 → ${r.fileName}（${r.sizeKb} KB）` +
              (r.added.length ? `；自动补依赖：${r.added.join('、')}` : ''),
            )
          })
          .catch((e) => { setExporting(false); toast.error(String(e)) })
      })
      .catch((e) => toast.error(String(e)))
  }

  /* ---------------- 导入备份 ---------------- */

  const openImport = () => {
    apiSystem
      .pickOpenFile('数据库文件 (*.db)|*.db|所有文件 (*.*)|*.*')
      .then((path) => {
        if (!path) return
        setImportPath(path)
        setImportConfirm('')
        setInspect(null)
        setImportResult(null)
        setImportOpen(true)
        apiBackup
          .inspect(path)
          .then(setInspect)
          .catch((e) => { setImportOpen(false); toast.error(String(e)) })
      })
      .catch((e) => toast.error(String(e)))
  }

  const doImport = () => {
    if (!canImport) return
    setImporting(true)
    apiBackup
      .importFile(importPath)
      .then((r) => {
        setImporting(false)
        setImportResult(r)
        toast.success(`已恢复 ${r.restored.length} 张表，恢复前数据已另存快照`)
        load()
      })
      .catch((e) => { setImporting(false); toast.error(String(e)) })
  }

  const restartApp = () => {
    toast.loading('正在重启…', { id: 'restart' })
    apiSystem.restart().catch((e) => {
      toast.dismiss('restart')
      toast.error(String(e))
    })
  }

  const latest = versions[0]

  return (
    <PageFill minWidth={1080}>
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            数据备份
          </h1>
          <p className="text-xs text-muted-foreground mt-1">手动备份与恢复；恢复前强制生成「恢复前快照」，任何操作都不会丢数据</p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="secondary" onClick={openExport} title="按表勾选导出为一个 .db 文件，保存位置自己选">
            <FileDown size={14} /> 导出数据
          </Button>
          <Button variant="secondary" onClick={openImport} title="从任意位置的 .db 导入，自动识别能不能用">
            <FileUp size={14} /> 导入备份
          </Button>
          <Button variant="primary" onClick={manualBackup} disabled={backingUp}>
            {backingUp ? <Loader2 size={14} className="animate-spin" /> : <DatabaseBackup size={14} />}
            {backingUp ? '正在备份…' : '立即手动备份'}
          </Button>
        </div>
      </div>

      <div className="grid grid-cols-3 gap-4 shrink-0">
        <SectionCard title="备份说明" bodyClassName="px-4 py-3">
          <ul className="space-y-1.5 text-xs text-muted-foreground">
            <li className="flex gap-2"><Badge tone="primary">位置</Badge><span className="break-all font-mono text-[11px]">{backupDir || '读取中…'}</span></li>
            <li className="flex gap-2"><Badge tone="primary">方式</Badge>SQLite 在线备份，不中断营业</li>
            <li className="flex gap-2"><Badge tone="primary">清理</Badge>自动留最近 30 个 + 月初 2 个</li>
          </ul>
        </SectionCard>
        <SectionCard title="最近备份状态" bodyClassName="px-4 py-3">
          {latest ? (
            <>
              <p className="flex items-center gap-2 text-sm font-semibold text-primary">
                <CheckCircle2 size={16} /> 最新备份：{latest.time}（{latest.type}）
              </p>
              <p className="text-xs text-muted-foreground mt-1.5">共保留 {versions.length} 版</p>
            </>
          ) : (
            <p className="text-xs text-muted-foreground">还没有备份</p>
          )}
        </SectionCard>
        <SectionCard title="恢复演练建议" bodyClassName="px-4 py-3">
          <p className="text-xs text-muted-foreground leading-5">
            建议每月做一次「备份恢复演练」：把备份恢复到临时目录并抽查会员余额、当日流水。
          </p>
        </SectionCard>
      </div>

      <SectionCard
        className="shrink-0"
        title={<span className="flex items-center gap-1.5"><Settings2 size={14} className="text-primary" /> 每日自动备份</span>}
        bodyClassName="px-4 py-3"
      >
        <div className="flex flex-wrap items-center gap-4">
          <label className="flex items-center gap-2 text-sm cursor-pointer">
            <input type="checkbox" checked={autoOn} onChange={(e) => setAutoOn(e.target.checked)} className="accent-[hsl(var(--primary))]" />
            每天自动备份一次
          </label>
          <div className="flex items-center gap-2">
            <span className="text-xs text-muted-foreground">执行时间</span>
            <input
              type="number"
              min={0}
              max={23}
              value={autoHour}
              onChange={(e) => setAutoHour(e.target.value)}
              aria-label="自动备份小时"
              className="h-8 w-16 px-2 text-sm tabular-nums bg-card rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary"
            />
            <span className="text-xs text-muted-foreground">点</span>
          </div>
          <Button variant="primary" size="sm" onClick={saveAuto}>保存设置</Button>
          <span className="text-xs text-muted-foreground">
            上次自动备份：{autoLast ? autoLast : '从未'}
          </span>
        </div>
        <p className="text-[11px] text-muted-foreground mt-2">
          程序开着到点即自动备份，一天只做一次；若当天开机已过点，进入本页时会补做一次。
        </p>
      </SectionCard>

      <SectionCard
        className="shrink-0"
        title={<span className="flex items-center gap-1.5"><Lock size={14} className="text-primary" /> 备份加密</span>}
        extra={<Badge tone="primary">{enc.enabled ? '已开启' : '未开启'}</Badge>}
        bodyClassName="px-4 py-3"
      >
        <div className="flex flex-wrap items-center gap-3">
          <Button variant={enc.enabled ? 'secondary' : 'primary'} size="sm" onClick={openEncDialog}>
            {enc.enabled ? <Unlock size={12} /> : <Lock size={12} />}
            {enc.enabled ? '修改密码' : '设置备份密码'}
          </Button>
          <span className="text-xs text-muted-foreground">
            加密备份 <span className="tabular-nums text-foreground">{enc.backupCount}</span> 份
            {enc.plainCount > 0 ? <span className="ml-1">（另有历史明文备份 {enc.plainCount} 份）</span> : null}
          </span>
          {enc.hint ? <span className="text-xs text-muted-foreground">密码提示：{enc.hint}</span> : null}
        </div>
        <p className="text-[11px] text-muted-foreground mt-2">
          开启后备份落地即为密文（AES-256），拷到 U 盘、发微信都打不开，恢复时需要输入密码。
          <span className="text-destructive">密码请自己记牢：忘记密码没有任何后门，那些备份就再也解不开了。</span>
        </p>
      </SectionCard>

      <SectionCard
        className="page-body flex flex-col"
        title={<span className="flex items-center gap-1.5"><History size={14} className="text-primary" /> 备份版本列表</span>}
        extra={
          <>
            <label className="flex items-center gap-1.5 text-[11px] text-muted-foreground cursor-pointer select-none">
              <input
                type="checkbox"
                checked={allVersionsPicked}
                onChange={(e) => setPickedVersions(e.target.checked ? new Set(versions.map((v) => v.fileName)) : new Set())}
                className="accent-[hsl(var(--primary))]"
                aria-label="全选备份版本"
              />
              全选
            </label>
            <Button
              variant="secondary"
              size="sm"
              onClick={doExportVersions}
              disabled={pickedVersions.size === 0 || exportingVersions}
              title="把勾中的备份文件原样复制到你选定的文件夹，可一次导多个"
            >
              {exportingVersions ? <Loader2 size={12} className="animate-spin" /> : <FileDown size={12} />}
              导出选中{pickedVersions.size > 0 ? ` (${pickedVersions.size})` : ''}
            </Button>
          </>
        }
        bodyClassName="p-0 flex-1 min-h-0 flex flex-col"
      >
        <DataTable
          cols={BACKUP_COLS}
          rows={versions}
          rowKey={(v) => v.id}
          plain
          fill
          render={(v, i) => [
            <input
              key="p"
              type="checkbox"
              checked={pickedVersions.has(v.fileName)}
              onChange={() => toggleVersion(v.fileName)}
              className="accent-[hsl(var(--primary))]"
              aria-label={`勾选备份 ${v.time}`}
            />,
            v.time,
            <span key="t" className="inline-flex items-center gap-1.5">
              <Badge tone={v.type === '手动' ? 'solid' : 'primary'}>{v.type}</Badge>
              {v.encrypted ? <span className="inline-flex items-center gap-0.5 text-[10px] text-primary"><Lock size={9} />加密</span> : null}
              {i === 0 ? <span className="text-[10px] text-primary">最新</span> : null}
            </span>,
            v.size,
            v.note,
            <div key="a" className="flex justify-end gap-1">
              <Button
                variant="ghost"
                size="sm"
                onClick={() => { setRestoreTarget(v); setRestoreConfirmText(''); setRestorePwd('') }}
              >
                <HardDriveDownload size={12} /> 恢复到此版本
              </Button>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => setDeleteTarget(v)}
                className="text-destructive hover:text-destructive"
              >
                <Trash2 size={12} /> 删除
              </Button>
            </div>,
          ]}
          empty={<div className="py-8 text-center text-xs text-muted-foreground">暂无备份版本</div>}
        />
      </SectionCard>

      {restoreTarget ? (
        <Modal
          title="恢复备份（高危操作）"
          onClose={() => setRestoreTarget(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setRestoreTarget(null)}>取消</Button>
              <Button
                variant="danger"
                onClick={doRestore}
                disabled={!canRestore || (!!restoreTarget.encrypted && !restorePwd)}
              >
                <HardDriveDownload size={13} /> 确认恢复
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive flex items-center gap-1.5">
                <AlertTriangle size={13} /> 恢复将把全部数据回退到 {restoreTarget.time}，此后发生的业务全部不可见：
              </p>
              <p>① 恢复前系统会先把「当前数据」自动另存为一个新备份（恢复前快照）</p>
              <p>② 恢复过程约数秒，期间不能收银</p>
            </div>
            <label className="block">
              <span className="block text-sm font-medium mb-1.5">
                请输入「恢复」二字以确认 <span className="text-destructive">*</span>
              </span>
              <input
                value={restoreConfirmText}
                onChange={(e) => setRestoreConfirmText(e.target.value)}
                placeholder="恢复"
                aria-label="恢复确认输入"
                className={cx(
                  'h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border',
                  'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
                  restoreConfirmText && !canRestore ? 'border-destructive' : 'border-input',
                )}
              />
              {restoreConfirmText && !canRestore ? (
                <span className="block text-xs text-destructive mt-1.5" role="alert">输入内容不正确，请准确输入「恢复」</span>
              ) : null}
            </label>
            {restoreTarget.encrypted ? (
              <label className="block">
                <span className="block text-sm font-medium mb-1.5">
                  这个备份已加密，请输入备份密码 <span className="text-destructive">*</span>
                </span>
                <input
                  type="password"
                  value={restorePwd}
                  onChange={(e) => setRestorePwd(e.target.value)}
                  placeholder="备份密码"
                  aria-label="备份密码"
                  className={cx(
                    'h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border border-input',
                    'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
                  )}
                />
                {enc.hint ? <span className="block text-xs text-muted-foreground mt-1.5">提示：{enc.hint}</span> : null}
                <span className="block text-[11px] text-muted-foreground mt-1.5">
                  如果这是改密码之前做的备份，请用当时的旧密码。
                </span>
              </label>
            ) : null}
          </div>
        </Modal>
      ) : null}

      {encOpen ? (
        <Modal
          title={enc.enabled ? '修改备份密码' : '设置备份密码'}
          onClose={() => { if (!encBusy) setEncOpen(false) }}
          footer={
            <>
              <Button variant="secondary" onClick={() => setEncOpen(false)} disabled={encBusy}>取消</Button>
              {enc.enabled ? (
                <Button
                  variant="ghost"
                  onClick={disableEnc}
                  disabled={encBusy}
                  className="text-destructive hover:text-destructive"
                  title="关闭后新备份为明文；已有加密备份仍然能恢复"
                >
                  关闭加密
                </Button>
              ) : null}
              <Button variant="primary" onClick={saveEnc} disabled={encBusy}>
                {encBusy ? <Loader2 size={13} className="animate-spin" /> : <Lock size={13} />} 保存
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive flex items-center gap-1.5">
                <AlertTriangle size={13} /> 忘记密码 = 这些备份永久打不开
              </p>
              <p>系统不保存密码原文、也没有万能钥匙。请把密码记在只有你知道的地方。</p>
              <p>自动备份会用本机保存的密码加密；换电脑或重装系统后恢复，需要你亲手输入这个密码。</p>
            </div>

            {enc.enabled && enc.hasPassword ? (
              <label className="block">
                <span className="block text-sm font-medium mb-1.5">当前密码 <span className="text-destructive">*</span></span>
                <input
                  type="password"
                  value={encOld}
                  onChange={(e) => setEncOld(e.target.value)}
                  placeholder="当前备份密码"
                  aria-label="当前备份密码"
                  className="h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow"
                />
              </label>
            ) : null}

            <label className="block">
              <span className="block text-sm font-medium mb-1.5">新密码（至少 8 位，别用纯数字） <span className="text-destructive">*</span></span>
              <input
                type="password"
                value={encNew}
                onChange={(e) => setEncNew(e.target.value)}
                placeholder="新备份密码"
                aria-label="新备份密码"
                className="h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow"
              />
            </label>

            <label className="block">
              <span className="block text-sm font-medium mb-1.5">再输一次 <span className="text-destructive">*</span></span>
              <input
                type="password"
                value={encNew2}
                onChange={(e) => setEncNew2(e.target.value)}
                placeholder="重复新密码"
                aria-label="确认新备份密码"
                className={cx(
                  'h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border',
                  'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
                  encNew2 && encNew2 !== encNew ? 'border-destructive' : 'border-input',
                )}
              />
              {encNew2 && encNew2 !== encNew ? (
                <span className="block text-xs text-destructive mt-1.5" role="alert">两次输入不一致</span>
              ) : null}
            </label>

            <label className="block">
              <span className="block text-sm font-medium mb-1.5">密码提示（可不填）</span>
              <input
                value={encHint}
                onChange={(e) => setEncHint(e.target.value)}
                placeholder="例如：店里开业那年的年份 + 6 位"
                aria-label="密码提示"
                className="h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow"
              />
              <span className="block text-[11px] text-muted-foreground mt-1.5">
                提示是明文，会显示在恢复窗口里，别写密码本身。
              </span>
            </label>

            {enc.enabled ? (
              <p className="text-[11px] text-muted-foreground">
                改密码不会重新加密已有备份：之前那些 .dbk 仍然只能用旧密码恢复。
              </p>
            ) : null}
          </div>
        </Modal>
      ) : null}

      {deleteTarget ? (
        <Modal
          title="删除备份版本"
          onClose={() => setDeleteTarget(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDeleteTarget(null)}>取消</Button>
              <Button variant="danger" onClick={doDelete}>确认删除</Button>
            </>
          }
        >
          <div className="text-xs space-y-2">
            <p>确定删除「{deleteTarget.time}（{deleteTarget.type}）」这个备份吗？</p>
            <p className="text-muted-foreground">
              只删除这一个备份文件，删除后就无法从这一版恢复了。
            </p>
          </div>
        </Modal>
      ) : null}

      {exportOpen ? (
        <Modal
          width={720}
          title="导出数据"
          onClose={() => { if (!exporting) setExportOpen(false) }}
          footer={
            <>
              <span className="mr-auto text-xs text-muted-foreground">
                已选 <span className="text-foreground font-medium tabular-nums">{picked.size}</span>
                <span className="tabular-nums"> / {tables.length}</span> 张表
                <span className="mx-1.5">·</span>
                <span className="tabular-nums">{pickedRows}</span> 行
              </span>
              <Button variant="secondary" onClick={() => setExportOpen(false)} disabled={exporting}>取消</Button>
              <Button variant="primary" onClick={doExport} disabled={exporting || picked.size === 0}>
                {exporting ? <Loader2 size={13} className="animate-spin" /> : <FileDown size={13} />}
                {exporting ? '正在导出…' : '选择位置并导出'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="flex items-center gap-2">
              <Button size="sm" variant="ghost" onClick={() => setPicked(new Set(tables.map((t) => t.name)))}>
                全选
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setPicked(new Set(tables.filter((t) => t.defaultOn).map((t) => t.name)))}>
                <RotateCcw size={12} /> 恢复默认
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setPicked(new Set())}>全不选</Button>
            </div>

            {GROUP_ORDER.map((g) => {
              const rows = grouped[g]
              if (!rows || rows.length === 0) return null
              return (
                <div key={g}>
                  <div className="flex items-baseline gap-2 mb-1.5">
                    <span className="text-xs font-medium text-foreground">{g}</span>
                    <span className="text-[11px] text-muted-foreground">{GROUP_HINT[g]}</span>
                  </div>
                  <div className="grid grid-cols-2 gap-x-4 gap-y-0.5 border border-border rounded-[var(--radius-sm)] px-2 py-1.5">
                    {rows.map((t) => (
                      <label key={t.name} className="flex items-center gap-2 text-xs cursor-pointer px-1 py-1 rounded-[var(--radius-sm)] hover:bg-accent">
                        <input
                          type="checkbox"
                          checked={picked.has(t.name)}
                          onChange={() => toggleTable(t.name)}
                          className="accent-[hsl(var(--primary))]"
                        />
                        <span className="flex-1 truncate">{t.label}</span>
                        <span className="tabular-nums text-muted-foreground">{t.count}</span>
                      </label>
                    ))}
                  </div>
                </div>
              )
            })}

            {exportIsFull ? (
              <p className="text-[11px] text-muted-foreground flex items-center gap-1.5">
                <CheckCircle2 size={12} className="text-primary" /> 表齐全，这个文件可以当完整备份用，导入后能直接恢复系统。
              </p>
            ) : isDefaultPick ? (
              <p className="text-[11px] text-muted-foreground flex items-center gap-1.5">
                <CheckCircle2 size={12} className="text-primary" /> 已含全部业务与系统数据，只是不带操作日志与统计；要能一键恢复请点「全选」。
              </p>
            ) : (
              <p className="text-[11px] text-destructive flex items-center gap-1.5">
                <XCircle size={12} /> 少勾了 {tables.length - picked.size} 张表，这个文件只能查看，导入时会被拒绝。
              </p>
            )}
          </div>
        </Modal>
      ) : null}

      {importOpen ? (
        <Modal
          width={620}
          title={importResult ? '导入完成' : '导入备份'}
          onClose={() => { if (!importing) setImportOpen(false) }}
          footer={
            importResult ? (
              <>
                <Button variant="secondary" onClick={() => setImportOpen(false)}>稍后自己重启</Button>
                <Button variant="primary" onClick={restartApp}>
                  <RotateCcw size={13} /> 立即重启
                </Button>
              </>
            ) : (
              <>
                <Button variant="secondary" onClick={() => setImportOpen(false)} disabled={importing}>取消</Button>
                <Button variant="danger" onClick={doImport} disabled={!canImport || importing}>
                  {importing ? <Loader2 size={13} className="animate-spin" /> : <HardDriveDownload size={13} />}
                  {importing ? '正在导入…' : '确认导入'}
                </Button>
              </>
            )
          }
        >
          {importResult ? (
            <div className="space-y-3">
              <div className="rounded-[var(--radius-sm)] border border-primary/30 bg-primary/5 px-3 py-2.5 text-xs space-y-1" role="status">
                <p className="font-semibold text-primary flex items-center gap-1.5">
                  <CheckCircle2 size={13} /> 按表恢复完成
                </p>
                <p>
                  恢复了 <b className="text-foreground">{importResult.restored.length}</b> 张表；
                  文件里没有的 <b className="text-foreground">{importResult.kept.length}</b> 张表<b className="text-foreground">保持当前数据没动</b>。
                </p>
              </div>

              <div>
                <p className="text-xs font-medium mb-1.5">已恢复的表</p>
                <div className="max-h-[200px] overflow-auto rounded-[var(--radius-sm)] border border-border">
                  <table className="w-full border-collapse text-xs">
                    <thead className="sticky top-0 bg-muted">
                      <tr>
                        <th className="text-left px-2.5 py-1.5 font-medium text-muted-foreground">表</th>
                        <th className="text-right px-2.5 py-1.5 font-medium text-muted-foreground w-24">行数</th>
                      </tr>
                    </thead>
                    <tbody>
                      {importResult.restored.map((t) => (
                        <tr key={t.label} className="border-t border-border">
                          <td className="px-2.5 py-1.5">{t.label}</td>
                          <td className="px-2.5 py-1.5 text-right tabular-nums">{t.count}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>

              {importResult.kept.length > 0 ? (
                <p className="text-[11px] text-muted-foreground leading-relaxed">
                  文件里没有、保持原样：{importResult.kept.join('、')}
                </p>
              ) : null}

              <div className="rounded-[var(--radius-sm)] bg-muted/50 px-3 py-2.5 text-[11px] text-muted-foreground leading-relaxed">
                账号、权限、设置这几类如果被替换，要重启才生效（页面里还是旧值）。
                恢复前的数据已存成快照「{importResult.snapshot}」，随时能在备份列表里恢复回来。
              </div>
            </div>
          ) : !inspect ? (
            <div className="flex items-center gap-2 text-xs text-muted-foreground py-6 justify-center">
              <Loader2 size={14} className="animate-spin" /> 正在识别文件…
            </div>
          ) : inspect.error ? (
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive flex items-center gap-1.5">
                <XCircle size={13} /> 这个文件不能导入
              </p>
              <p className="text-muted-foreground">{inspect.error}</p>
            </div>
          ) : (
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs">
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">文件</span><span className="truncate font-mono text-[11px]">{inspect.fileName}</span></div>
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">大小</span><span className="tabular-nums">{inspect.sizeKb} KB</span></div>
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">修改时间</span><span className="tabular-nums">{inspect.fileTime}</span></div>
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">表数</span><span className="tabular-nums">{inspect.tableCount}</span></div>
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">会员</span><span className="tabular-nums">{inspect.memberCount} 人</span></div>
                <div className="flex gap-2"><span className="text-muted-foreground shrink-0">订单</span><span className="tabular-nums">{inspect.orderCount} 单</span></div>
              </div>

              {inspect.isFull ? (
                <>
                  <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
                    <p className="font-semibold text-destructive flex items-center gap-1.5">
                      <AlertTriangle size={13} /> 完整备份：文件里的表会被整表替换成文件里的数据
                    </p>
                    <p>① 导入前系统会先把「当前数据」自动另存为恢复前快照</p>
                    <p>② 导入完成后建议重启软件，让各页面重新读数</p>
                  </div>
                  <label className="block">
                    <span className="block text-sm font-medium mb-1.5">
                      请输入「导入」二字以确认 <span className="text-destructive">*</span>
                    </span>
                    <input
                      value={importConfirm}
                      onChange={(e) => setImportConfirm(e.target.value)}
                      placeholder="导入"
                      aria-label="导入确认输入"
                      className={cx(
                        'h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border',
                        'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
                        importConfirm && !canImport ? 'border-destructive' : 'border-input',
                      )}
                    />
                    {importConfirm && !canImport ? (
                      <span className="block text-xs text-destructive mt-1.5" role="alert">输入内容不正确，请准确输入「导入」</span>
                    ) : null}
                  </label>
                </>
              ) : (
                <>
                  <div className="rounded-[var(--radius-sm)] border border-border bg-muted/40 px-3 py-2.5 text-xs space-y-1.5" role="status">
                    <p className="font-semibold text-foreground flex items-center gap-1.5">
                      <AlertTriangle size={13} /> 这是「数据导出」，按表恢复
                    </p>
                    <p className="text-muted-foreground">
                      文件里只有 {inspect.tableCount} 张表；<b className="text-foreground">这 {inspect.tableCount} 张会被替换成文件里的数据，
                      其余的表保持当前数据不动</b>——所以账号、项目、设置都还在，导入后照样能登录开单。
                    </p>
                    <p className="text-muted-foreground">文件里没有：{inspect.missing.join('、')}。</p>
                  </div>
                  <label className="block">
                    <span className="block text-sm font-medium mb-1.5">
                      请输入「导入」二字以确认 <span className="text-destructive">*</span>
                    </span>
                    <input
                      value={importConfirm}
                      onChange={(e) => setImportConfirm(e.target.value)}
                      placeholder="导入"
                      aria-label="导入确认输入"
                      className={cx(
                        'h-9 w-full px-3 text-sm bg-card rounded-[var(--radius-sm)] border',
                        'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
                        importConfirm && !canImport ? 'border-destructive' : 'border-input',
                      )}
                    />
                    {importConfirm && !canImport ? (
                      <span className="block text-xs text-destructive mt-1.5" role="alert">输入内容不正确，请准确输入「导入」</span>
                    ) : null}
                  </label>
                </>
              )}
            </div>
          )}
        </Modal>
      ) : null}
    </PageFill>
  )
}
