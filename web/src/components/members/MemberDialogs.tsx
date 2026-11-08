import { useEffect, useState } from 'react'
import { Modal, Button, Field, Input, Select, Badge, StatusTag, cx, thCls, tdCls, numCls } from '@/components/common/ui'
import { members as apiMembers, pos as apiPos, resolveLevel, storedvalue } from '@/lib/api'
import type { PosStaffOption } from '@/lib/api'
import { getStaff, getLastOperatorId, isManager } from '@/lib/session'
import { fmtMoney } from '@/data/mock'
import type { Member, MemberFlow, MemberLevel, RechargePackage } from '@/data/mock'
import { Wallet, CheckCircle2, Pencil, X, Plus } from 'lucide-react'

/** 备注多行输入框统一样式（新建/编辑档案共用，避免逐字重复） */
const REMARK_TEXTAREA_CLS =
  'w-full px-3 py-2 text-sm bg-card text-foreground rounded-[var(--radius-sm)] border border-input placeholder:text-muted-foreground/70 focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow resize-none'

// ---------- 会员详情（余额对账恒等式 + 消费记录，真数据） ----------
export function MemberDetailDialog({
  member,
  onClose,
  onRecharge,
  onStatusChange,
  onEdit,
  onMemberChanged,
}: {
  member: Member | null
  onClose: () => void
  onRecharge: (m: Member) => void
  onStatusChange: (m: Member, action: string) => void
  /** 打开「编辑档案」：改标签/生日/备注等（不涉资金；等级自动判定，不可手改） */
  onEdit: (m: Member) => void
  /** 注销/核销成功后刷新列表（调用方重拉） */
  onMemberChanged?: () => void
}) {
  const [tab, setTab] = useState<string>('profile')
  const [flows, setFlows] = useState<MemberFlow[]>([])
  const [orders, setOrders] = useState<{ OrderNo: string; BizDate: string; Kind: string; Receivable: number; Status: string; PaySummary: string }[]>([])
  const [check, setCheck] = useState<{ Recharge: number; Gift: number; Consume: number; Refund: number; Adjust: number; Computed: number; Actual: number; Ok: boolean } | null>(null)
  // S2（店主 10-15）：会员归档 —— 店长专属，一个确认弹窗，原因必填。
  // 店主 10-15 要求「两个按钮合为一个」：归档时若还有余额，后端一并作坏账核销，前端不再分两步。
  const [deleteOpen, setDeleteOpen] = useState<boolean>(false)
  const [deleteReason, setDeleteReason] = useState<string>('')
  const [dangerBusy, setDangerBusy] = useState<boolean>(false)
  const [dangerErr, setDangerErr] = useState<string>('')
  const isMgr = isManager()

  useEffect(() => {
    if (!member) return
    setTab('profile')
    setFlows([]); setOrders([]); setCheck(null)
    apiMembers.flows(member.id).then((fs) =>
      setFlows((fs ?? []).map((f) => ({ date: f.Time, type: f.Type, amount: f.Amount, balance: f.BalanceAfter, operator: f.Operator, note: f.Note ?? '', orderNo: f.OrderNo ?? '' }))),
    ).catch(() => setFlows([]))
    apiMembers.orders(member.id).then((os) => setOrders(os ?? [])).catch(() => setOrders([]))
    apiMembers.check(member.id).then(setCheck).catch(() => setCheck(null))
  }, [member?.id])

  if (!member) return null

  const tabs = [
    { key: 'profile', label: '基本资料' },
    { key: 'recon', label: '余额对账' },
    { key: 'records', label: '消费记录' },
  ]

  const canFreeze = member.status === '正常'
  const canUnfreeze = member.status === '冻结'
  const canReport = member.status === '正常' || member.status === '冻结'
  const canUnloss = member.status === '挂失'

  return (
    <Modal
      open
      onClose={onClose}
      title={`会员详情 · ${member.name}（${member.cardNo}）`}
      width={640}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>关闭</Button>
          <Button variant="secondary" onClick={() => onEdit(member)}>
            <Pencil size={14} /> 编辑档案
          </Button>
          {canFreeze ? <Button variant="secondary" onClick={() => onStatusChange(member, '冻结')}>冻结</Button> : null}
          {canUnfreeze ? <Button variant="secondary" onClick={() => onStatusChange(member, '解冻')}>解冻</Button> : null}
          {canReport ? <Button variant="secondary" onClick={() => onStatusChange(member, '挂失')}>挂失</Button> : null}
          {canUnloss ? <Button variant="secondary" onClick={() => onStatusChange(member, '解挂')}>解挂</Button> : null}
          {/* 会员归档（店主 10-15：两个按钮合为一个）：余额和档案一次处理完。
              入口用最不打眼的 ghost，强调只留给二次确认弹窗——入口安静、确认慎重。 */}
          {isMgr && !member.deletedAt ? (
            <Button
              variant="ghost"
              disabled={member.status !== '正常'}
              title="会员归档：剩余余额一并作坏账清理（不退现金），档案从列表移走，记录全部保留可查"
              onClick={() => { setDeleteReason(''); setDangerErr(''); setDeleteOpen(true) }}
            >
              会员归档
            </Button>
          ) : null}
          <Button variant="primary" disabled={member.status !== '正常'} onClick={() => onRecharge(member)}>
            <Wallet size={14} /> 充值
          </Button>
        </>
      }
    >
      {/* 已归档提示：一行小字说明白就行，不用色块 */}
      {member.deletedAt ? (
        <p className="mb-3 text-[11px] text-muted-foreground">
          该会员已于 {member.deletedAt} 归档（{member.deletedReason || '—'}）。记录全部保留可查，手机号与卡号不再释放。
        </p>
      ) : null}

      {/* 会员归档确认 */}
      {deleteOpen ? (
        <Modal
          open
          onClose={() => setDeleteOpen(false)}
          title={`会员归档 · ${member.name}`}
          width={480}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDeleteOpen(false)}>取消</Button>
              <Button
                variant="danger"
                disabled={dangerBusy || !deleteReason.trim()}
                onClick={() => {
                  setDangerBusy(true); setDangerErr('')
                  apiMembers
                    .deleteMember(member.id, deleteReason.trim())
                    .then(() => {
                      setDeleteOpen(false)
                      onMemberChanged?.()
                      onClose()
                    })
                    .catch((e) => setDangerErr(String(e)))
                    .finally(() => setDangerBusy(false))
                }}
              >
                {dangerBusy ? '处理中…' : '确认归档'}
              </Button>
            </>
          }
        >
          <div className="space-y-3 text-sm">
            <div className="rounded-[var(--radius-sm)] border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs space-y-1" role="alert">
              <p className="font-semibold text-destructive">归档后从列表移走，不能再充值/开单</p>
              {member.balance !== 0 ? (
                <p className="text-foreground">
                  剩余余额 <b className="tabular-nums">¥{fmtMoney(member.balance)}</b> 清理为 0（不退现金，会写一条可查流水）。
                </p>
              ) : null}
              <p className="text-foreground">历史记录全部保留（「已归档」可查）；手机号与卡号永久占用。</p>
            </div>
            <Field label="归档原因" required error={dangerErr}>
              <Input
                value={deleteReason}
                onChange={(e) => setDeleteReason(e.target.value)}
                placeholder="如：老客户已搬走、明确不再到店、重复档案清理"
                maxLength={200}
              />
            </Field>
            <p className="text-[11px] text-muted-foreground">此操作仅限店长，原因会写入操作日志，不可省略。</p>
          </div>
        </Modal>
      ) : null}
      <div className="flex items-center gap-3 mb-4">
        <div className="w-12 h-12 rounded-full bg-primary/10 text-primary flex items-center justify-center text-lg font-bold shrink-0">
          {member.name.slice(0, 1)}
        </div>
        <div className="flex-1">
          <p className="text-base font-semibold flex items-center gap-2">
            {member.name} <StatusTag status={member.status} /> <Badge tone="primary">{member.level}</Badge>
          </p>
          <p className="text-xs text-muted-foreground mt-1 tabular-nums">
            {member.phone} · 生日 {member.birthday || '—'} · 最近到店 {(member.lastVisit || '—').slice(0, 10)}
          </p>
        </div>
        <div className="text-right leading-tight">
          <p className="text-xs text-muted-foreground">当前余额</p>
          <p className="text-xl font-bold text-primary tabular-nums">¥{fmtMoney(member.balance)}</p>
        </div>
      </div>

      <div className="flex gap-1 border-b border-border mb-4">
        {tabs.map((t) => (
          <button
            key={t.key}
            onClick={() => setTab(t.key)}
            className={cx(
              'px-3 h-8 text-[13px] rounded-t-[var(--radius-sm)] transition-colors -mb-px border-b-2',
              tab === t.key
                ? 'text-primary border-primary font-semibold bg-primary/5'
                : 'text-muted-foreground border-transparent hover:text-foreground hover:bg-accent/60',
            )}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'profile' ? (
        <div className="grid grid-cols-2 gap-x-6 gap-y-3 text-sm">
          {[
            ['卡号', member.cardNo],
            ['等级', `${member.level}（累计充值 ¥${fmtMoney(member.totalRecharge)}）`],
            ['积分', member.points.toLocaleString('zh-CN')],
            ['次卡', member.cardRemain > 0 ? `${member.cardName} · 剩 ${member.cardRemain} 次` : '无'],
            ['标签', member.tags.length > 0 ? member.tags.join('、') : '无'],
            ['建档人', member.createdByName || '历史档案（未记录经办人）'],
          ].map(([k, v]) => (
            <div key={k as string} className="flex gap-2">
              <span className="text-muted-foreground w-16 shrink-0 text-xs leading-6">{k}</span>
              <span className="text-foreground text-xs leading-6">{v as string}</span>
            </div>
          ))}
        </div>
      ) : null}

      {tab === 'recon' ? (
        <div>
          <div className="rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3.5 py-3 mb-4">
            <p className="text-xs font-semibold text-primary flex items-center gap-1.5 mb-1.5">
              <CheckCircle2 size={14} /> 余额恒等式核对（充值 + 赠送 − 消费 + 退款调整 = 当前余额）
            </p>
            {check ? (
              <p className="text-[13px] tabular-nums text-foreground">
                ¥{fmtMoney(check.Recharge)} + ¥{fmtMoney(check.Gift)} − ¥{fmtMoney(check.Consume)} + ¥{fmtMoney(check.Refund + check.Adjust)} ={' '}
                <b className="text-primary">¥{fmtMoney(check.Actual)}</b>
                <span className={cx('text-xs ml-2', check.Ok ? 'text-primary' : 'text-destructive')}>
                  {check.Ok ? '✓ 逐笔流水可核对，分毫不差' : '✗ 账目不平，请联系店长核查'}
                </span>
              </p>
            ) : (
              <p className="text-xs text-muted-foreground">对账数据加载中…</p>
            )}
          </div>
          <table className="w-full border-collapse">
            <thead>
              <tr>
                <th className={thCls}>时间</th>
                <th className={thCls}>类型</th>
                <th className={cx(thCls, 'text-right')}>变动金额</th>
                <th className={cx(thCls, 'text-right')}>变动后余额</th>
                <th className={thCls}>单号</th>
                <th className={thCls}>操作员</th>
                <th className={thCls}>说明</th>
              </tr>
            </thead>
            <tbody>
              {flows.length === 0 ? (
                <tr><td className={tdCls} colSpan={7}><span className="text-xs text-muted-foreground">暂无流水</span></td></tr>
              ) : (
                flows.map((f, i) => (
                  <tr key={i} className="even:bg-muted/40">
                    <td className={cx(tdCls, 'tabular-nums text-muted-foreground text-xs')}>{f.date && f.date.length >= 16 ? f.date.substring(0, 16) : f.date}</td>
                    <td className={tdCls}>
                      <Badge tone={f.type === '消费' ? 'muted' : f.type === '退款' || f.type === '红冲' ? 'danger' : 'primary'}>{f.type}</Badge>
                    </td>
                    <td className={cx(tdCls, numCls, f.amount < 0 ? 'text-destructive' : 'text-primary')}>
                      {f.amount > 0 ? '+' : ''}
                      {fmtMoney(f.amount)}
                    </td>
                    <td className={cx(tdCls, numCls)}>{fmtMoney(f.balance)}</td>
                    <td className={cx(tdCls, 'text-xs tabular-nums text-muted-foreground')}>{f.orderNo || '—'}</td>
                    <td className={cx(tdCls, 'text-xs')}>{f.operator}</td>
                    <td className={cx(tdCls, 'text-xs text-muted-foreground max-w-44 truncate')}>{f.note}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      ) : null}

      {tab === 'records' ? (
        <table className="w-full border-collapse">
          <thead>
            <tr>
              <th className={thCls}>单号</th>
              <th className={thCls}>日期</th>
              <th className={thCls}>类型</th>
              <th className={cx(thCls, 'text-right')}>金额</th>
              <th className={thCls}>支付方式</th>
              <th className={thCls}>状态</th>
            </tr>
          </thead>
          <tbody>
            {orders.length === 0 ? (
              <tr><td className={tdCls} colSpan={6}><span className="text-xs text-muted-foreground">暂无消费单据</span></td></tr>
            ) : (
              orders.map((r) => (
                <tr key={r.OrderNo} className="even:bg-muted/40">
                  <td className={cx(tdCls, 'tabular-nums text-xs text-muted-foreground')}>{r.OrderNo}</td>
                  <td className={cx(tdCls, 'tabular-nums text-xs')}>{r.BizDate}</td>
                  <td className={cx(tdCls, 'text-xs')}>{r.Kind}</td>
                  <td className={cx(tdCls, numCls)}>¥{fmtMoney(r.Receivable)}</td>
                  <td className={cx(tdCls, 'text-xs')}>{r.PaySummary || '—'}</td>
                  <td className={tdCls}>
                    <Badge tone={r.Status === '已退款' ? 'danger' : 'primary'}>{r.Status}</Badge>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      ) : null}
    </Modal>
  )
}

// ---------- 新建会员（真建档：卡号自动分配） ----------

/** 今天（YYYY-MM-DD），用于日期控件的 max：生日不允许晚于今天 */
function todayStr(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

interface NewMemberDialogProps {
  open: boolean
  onClose: () => void
  onCreated: (m: Member) => void
  /** 建档经办人候选（启用中的员工）；收银台可传入开单用的同一份下拉数据，避免二次请求 */
  staffOptions?: PosStaffOption[]
  /** 确认按钮文案：会员管理用「保存建档」，收银台用「建档并开单」 */
  submitLabel?: string
  /** 打开时预选的建档经办人（收银台传「本次开单」的操作员，保证与这一单是谁经手一致） */
  defaultCreatorId?: number
  /** 等级制度门槛表：仅用于实时预览「这次充多少会到几级」，等级本身不手工选择 */
  levels?: MemberLevel[]
}

export function NewMemberDialog({ open, onClose, onCreated, staffOptions, submitLabel, defaultCreatorId, levels }: NewMemberDialogProps) {
  const [displayName, setDisplayName] = useState<string>('')
  const [phone, setPhone] = useState<string>('')
  const [birthday, setBirthday] = useState<string>('')
  const [initAmount, setInitAmount] = useState<string>('')
  const [selectedPkgId, setSelectedPkgId] = useState<string>('')
  const [packages, setPackages] = useState<RechargePackage[]>([])
  const [tags, setTags] = useState<string>('')
  const [remark, setRemark] = useState<string>('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  // 建档经办人 id：0 仅表示「员工列表还没取回来」，一律按登录账号兜底提交
  const [creatorId, setCreatorId] = useState<number>(0)
  const [staff, setStaff] = useState<PosStaffOption[]>([])
  const [busy, setBusy] = useState<boolean>(false)

  const currentStaff = getStaff()?.name ?? '系统'
  const meId = getStaff()?.id ?? 0
  const creatorName = staff.find((s) => s.id === creatorId)?.name ?? currentStaff

  // 默认经办人取值顺序：收银台点名（本次开单的人）→ 上一次开单的人 → 登录本人 → 列表第一位。
  // 不再放「当前登录账号」这个 value=0 的伪选项：它和列表里同一个人会并列出现两遍。
  const resolveCreator = (list: PosStaffOption[]): number => {
    if (defaultCreatorId && list.some((s) => s.id === defaultCreatorId)) return defaultCreatorId
    const last = getLastOperatorId()
    if (last > 0 && list.some((s) => s.id === last)) return last
    if (meId > 0 && list.some((s) => s.id === meId)) return meId
    return list[0]?.id ?? 0
  }

  useEffect(() => {
    if (!open) return
    setErrors({})
    setInitAmount('')
    setSelectedPkgId('')
    const adopt = (list: PosStaffOption[]) => {
      setStaff(list)
      setCreatorId(resolveCreator(list))
    }
    if (staffOptions && staffOptions.length > 0) {
      adopt(staffOptions)
    } else {
      apiPos.staffOptions().then(adopt).catch(() => setStaff([]))
    }
    // 加载储值方案档位（含禁用？后端已过滤，前端只用启用中的）
    storedvalue.packages().then((ps) => setPackages(ps.filter((p) => p.enabled))).catch(() => setPackages([]))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  if (!open) return null

  // 档位选择：选中即自动填金额+赠送；手改金额则清除档位选择、赠送归 0
  const selectedPkg = packages.find((p) => String(p.id) === selectedPkgId) ?? null
  const selectedGift = selectedPkg?.gift ?? 0

  const pickPkg = (id: string) => {
    setSelectedPkgId(id)
    if (id) {
      const pkg = packages.find((p) => String(p.id) === id)
      if (pkg) setInitAmount(String(pkg.amount))
    } else {
      setInitAmount('')
    }
  }

  const onInitAmountChange = (e: { target: { value: string } }) => {
    setInitAmount(e.target.value)
    setSelectedPkgId('')
  }

  const submit = () => {
    const next: Record<string, string> = {}
    if (!displayName.trim()) next.displayName = '姓名不能为空'
    if (!/^1\d{10}$/.test(phone.trim())) next.phone = '手机号必须为 11 位数字且以 1 开头'
    if (initAmount && (isNaN(Number(initAmount)) || Number(initAmount) < 0)) next.initAmount = '首次充值金额必须为非负数字'
    // 金额上限（10-15 店主反馈后补）：超了后端积分会算成int 溢出，报一句谁也看不懂的错。
    // 这里先把话说明白——超过上限九成是手滑（比如把手机号填进了金额框）。
    if (initAmount && Number(initAmount) > 99999999) next.initAmount = '金额过大（上限 99,999,999 元），请确认是否输错'
    setErrors(next)
    if (Object.keys(next).length > 0) return
    // 经办人点名了但下拉数据没取到（网络/库异常）时退回登录账号，不留空经办人
    const effId = creatorName ? creatorId : 0
    setBusy(true)
    apiMembers
      .create({
        name: displayName.trim(),
        phone: phone.trim(),
        birthday,
        tags: tags.trim(),
        remark: remark.trim(),
        initAmount: initAmount ? Number(initAmount) : 0,
        initGift: selectedGift,
        createdByStaffId: effId,
        createdByName: effId > 0 ? creatorName : '',
      })
      .then((m) => {
        onCreated(m)
        setDisplayName(''); setPhone(''); setInitAmount(''); setSelectedPkgId(''); setTags(''); setRemark(''); setBirthday(''); setErrors({}); setCreatorId(0)
      })
      .catch((e) => setErrors({ phone: String(e) }))
      .finally(() => setBusy(false))
  }

  // 等级不手工选择：按「本次首次充值」实时预览会到几档（后端落库时按同一口径再算一次）
  const initNum = Number(initAmount)
  const levelPreview = resolveLevel(levels ?? [], Number.isFinite(initNum) && initNum > 0 ? initNum : 0)

  return (
    <Modal
      open
      onClose={onClose}
      title="新建会员"
      width={520}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" onClick={submit} disabled={busy}>
            {busy ? '建档中…' : (submitLabel ?? '保存建档')}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-2 gap-x-4 gap-y-3.5">
        <div className="col-span-2">
          <Field
            label="建档经办人"
            required
            hint="默认带上一次开单的人，可改派"
          >
            <Select value={creatorId} onChange={(e) => setCreatorId(Number(e.target.value))} aria-label="建档经办人">
              {staff.length === 0 && <option value={0}>加载中…</option>}
              {staff.map((s) => (
                <option key={s.id} value={s.id}>{s.name}{s.role ? `（${s.role}）` : ''}</option>
              ))}
            </Select>
          </Field>
        </div>
        <Field label="姓名" required error={errors.displayName}>
          <Input
            value={displayName}
            placeholder="请输入会员姓名"
            onChange={(e) => setDisplayName(e.target.value)}
            aria-invalid={!!errors.displayName}
          />
        </Field>
        <Field label="手机号" required error={errors.phone}>
          <Input
            value={phone}
            placeholder="11 位手机号，作为唯一标识"
            onChange={(e) => setPhone(e.target.value)}
            aria-invalid={!!errors.phone}
            className="tabular-nums"
          />
        </Field>
        <Field label="生日">
          <Input type="date" value={birthday} max={todayStr()} onChange={(e) => setBirthday(e.target.value)} />
        </Field>
        <Field label="标签">
          <Input placeholder="多个标签用顿号分隔" value={tags} onChange={(e) => setTags(e.target.value)} />
        </Field>
        <div className="col-span-2">
          <Field label="首次充值方案">
            <Select value={selectedPkgId} onChange={(e) => pickPkg(e.target.value)}>
              <option value="">手动填写（无赠送）</option>
              {packages.map((p) => (
                <option key={p.id} value={String(p.id)}>
                  {`充 ¥${fmtMoney(p.amount)} 送 ¥${fmtMoney(p.gift)}`}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        <div className="col-span-2">
          <Field label="首次充值金额（元）" error={errors.initAmount} hint="等级按累计实收自动判定，赠送金额不计入门槛">
            <div className="flex items-center gap-3">
              <Input
                value={initAmount}
                placeholder="选填，0 或留空则不充值"
                onChange={onInitAmountChange}
                className="flex-1 tabular-nums"
              />
              <div className="w-44 shrink-0 h-9 px-2.5 flex items-center justify-end gap-1.5 rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20">
                <span className="text-[11px] text-muted-foreground whitespace-nowrap">建档后等级</span>
                <span className="text-xs font-semibold text-primary whitespace-nowrap">
                  {levelPreview.current ? levelPreview.current.name : '—'}
                </span>
              </div>
            </div>
          </Field>
        </div>
        {selectedPkg ? (
          <div className="col-span-2">
            <div className="rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3 py-2 text-xs">
              按方案：充 <b className="text-primary">¥{fmtMoney(selectedPkg.amount)}</b> 送 <b className="text-primary">¥{fmtMoney(selectedPkg.gift)}</b>
              {selectedPkg.gift > 0
                ? <span className="ml-1 text-muted-foreground">（赠送比例 {((selectedPkg.gift / selectedPkg.amount) * 100).toFixed(1)}%）</span>
                : <span className="ml-1 text-muted-foreground">（该档位无赠送）</span>}
            </div>
          </div>
        ) : (
          initAmount && Number(initAmount) > 0 ? (
            <div className="col-span-2">
              <div className="rounded-[var(--radius-sm)] bg-muted/60 border border-input px-3 py-2 text-xs text-muted-foreground">
                手动金额：¥{fmtMoney(Number(initAmount))}，本次不赠送（如需按方案赠送请下拉选择档位）
              </div>
            </div>
          ) : null
        )}
        <div className="col-span-2">
          <Field label="备注">
            <textarea
              rows={2}
              placeholder="选填"
              value={remark}
              onChange={(e) => setRemark(e.target.value)}
              className={REMARK_TEXTAREA_CLS}
            />
          </Field>
        </div>
      </div>
    </Modal>
  )
}

// ---------- 编辑会员档案（改标签/生日/备注等，不涉资金；等级自动判定，不可手改） ----------

/**
 * 编辑会员档案。新建会员时能填标签，之前却没有任何地方能改回来——这个弹窗补上。
 *  · 复用后端 UpdateMember（6 参），字段级差异由后端写 op_logs 留痕，前端不自己算；
 *  · 标签做成「可删标签 + 回车/顿号新增 + 库里已有标签一键补」，比顿号纯文本好改得多；
 *  · 等级只读展示：店主 2026-09-26 定「完全自动，不许手改」，按累计实收充值自动判定；
 *  · 余额/积分/次卡一概不碰（要动钱走充值/调整，走流水），所以本弹窗无金额输入；
 *  · 保存后按本地表单值拼回最新档案交给调用方即时刷新，库里的真数据由调用方 load() 拉取。
 */
export function EditMemberDialog({
  member,
  onClose,
  onSaved,
  levels,
}: {
  member: Member | null
  onClose: () => void
  onSaved: (m: Member) => void
  /** 等级制度门槛表：只用于展示「当前等级 / 距下一档还差多少」 */
  levels?: MemberLevel[]
}) {
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [birthday, setBirthday] = useState('')
  const [remark, setRemark] = useState('')
  const [tagList, setTagList] = useState<string[]>([])
  const [tagDraft, setTagDraft] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  // 库里已有的标签（不含当前会员自己选的），方便一键补上而不是重新造词
  const [tagPool, setTagPool] = useState<string[]>([])

  useEffect(() => {
    if (!member) return
    setName(member.name)
    setPhone(member.phone)
    setBirthday(member.birthday)
    setRemark(member.remark ?? '')
    setTagList(member.tags)
    setTagDraft('')
    setErrors({})
    apiMembers.tags().then(setTagPool).catch(() => setTagPool([]))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [member?.id])

  if (!member) return null

  const addTag = (raw: string) => {
    // 顿号/逗号/空格都当分隔符，输入框里可以一次敲「沉睡、高消费」
    for (const t of raw.split(/[、,，\s]+/)) {
      const v = t.trim()
      if (!v || tagList.includes(v) || tagList.length >= 8) continue
      setTagList((prev) => (prev.includes(v) || prev.length >= 8 ? prev : [...prev, v]))
    }
    setTagDraft('')
  }
  const removeTag = (t: string) => setTagList((prev) => prev.filter((x) => x !== t))
  const poolRest = tagPool.filter((t) => !tagList.includes(t)).slice(0, 6)
  // 只读展示：当前等级由累计实收充值推导，这里再算一遍告诉店主「离下一档还差多少」
  const editPreview = resolveLevel(levels ?? [], member.totalRecharge)

  const submit = () => {
    const next: Record<string, string> = {}
    if (!name.trim()) next.name = '姓名不能为空'
    if (!/^1\d{10}$/.test(phone.trim())) next.phone = '手机号必须为 11 位数字且以 1 开头'
    setErrors(next)
    if (Object.keys(next).length > 0) return
    setBusy(true)
    const tagsText = tagList.join('、')
    apiMembers
      .update({ id: member.id, name: name.trim(), phone: phone.trim(), birthday, tags: tagsText, remark: remark.trim() })
      .then(() => {
        onSaved({ ...member, name: name.trim(), phone: phone.trim(), birthday, tags: tagList, remark: remark.trim() })
      })
      .catch((e) => setErrors({ phone: String(e) }))
      .finally(() => setBusy(false))
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={`编辑档案 · ${member.name}（${member.cardNo}）`}
      width={520}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" onClick={submit} disabled={busy}>
            {busy ? '保存中…' : '保存修改'}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-2 gap-x-4 gap-y-3.5">
        <Field label="姓名" required error={errors.name}>
          <Input value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        <Field label="手机号" required error={errors.phone}>
          <Input
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
            aria-invalid={!!errors.phone}
            className="tabular-nums"
            placeholder="11 位手机号，作为唯一标识"
          />
        </Field>
        <Field label="会员等级">
          {/* 上下两行：第一行「当前等级 + 累计充值」，第二行「距下一档还差多少」。
              原来挤在一行 justify-between，半栏宽度放不下就重叠成一团（店主 10-08「看不清」）→ 改竖排。 */}
          <div className="min-h-9 px-3 py-1.5 flex flex-col justify-center gap-0.5 rounded-[var(--radius-sm)] border border-border/70 bg-muted/50">
            <span className="flex items-center gap-2">
              <Badge tone="primary">{member.level}</Badge>
              <span className="text-[11px] text-muted-foreground tabular-nums">
                累计充值 ¥{fmtMoney(member.totalRecharge)}
              </span>
            </span>
            <span className="text-[11px] leading-tight">
              {editPreview.next ? (
                <span className="text-muted-foreground">
                  距「{editPreview.next.name}」还差{' '}
                  <b className="text-foreground font-semibold tabular-nums">
                    ¥{fmtMoney(editPreview.next.minRecharge - member.totalRecharge)}
                  </b>
                </span>
              ) : (
                <span className="text-primary">已是最高档</span>
              )}
            </span>
          </div>
        </Field>
        <Field label="生日">
          <Input type="date" value={birthday} max={todayStr()} onChange={(e) => setBirthday(e.target.value)} />
        </Field>

        <div className="col-span-2">
          <Field
            label={`标签${tagList.length > 0 ? `（${tagList.length}/8）` : ''}`}
          >
            <div className="rounded-[var(--radius-sm)] border border-input bg-card px-2 py-1.5 focus-within:border-primary focus-within:shadow-[var(--focus-ring)] transition-shadow">
              <div className="flex flex-wrap items-center gap-1.5 min-h-8">
                {tagList.length === 0 ? (
                  <span className="text-xs text-muted-foreground/60 py-1 pr-1">
                    标签是自由文本——下方输入后按回车
                  </span>
                ) : (
                  tagList.map((t) => (
                    <span
                      key={t}
                      className={cx(
                        'inline-flex items-center gap-1 h-6 pl-2 pr-1 rounded-[var(--radius-pill)] text-xs font-medium',
                        t === '沉睡' ? 'bg-destructive/10 text-destructive' : 'bg-primary/10 text-primary',
                      )}
                    >
                      {t}
                      <button
                        type="button"
                        aria-label={`移除标签 ${t}`}
                        onClick={() => removeTag(t)}
                        className="w-4 h-4 rounded-full flex items-center justify-center hover:bg-black/10 transition-colors"
                      >
                        <X size={11} />
                      </button>
                    </span>
                  ))
                )}
                <input
                  value={tagDraft}
                  onChange={(e) => setTagDraft(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === '、' || e.key === ',') {
                      e.preventDefault()
                      addTag(tagDraft)
                    } else if (e.key === 'Backspace' && !tagDraft && tagList.length > 0) {
                      removeTag(tagList[tagList.length - 1])
                    }
                  }}
                  onBlur={() => {
                    if (tagDraft.trim()) addTag(tagDraft)
                  }}
                  placeholder="输入标签后回车，或从下方挑一个"
                  aria-label="新增标签"
                  className="flex-1 min-w-24 h-6 bg-transparent text-sm outline-none placeholder:text-muted-foreground/60"
                />
              </div>
            </div>
          </Field>
          {poolRest.length > 0 ? (
            <div className="flex flex-wrap items-center gap-1.5 mt-2 pl-0.5">
              <span className="text-[11px] text-muted-foreground shrink-0">可添加：</span>
              {poolRest.map((t) => (
                <button
                  key={t}
                  type="button"
                  onClick={() => addTag(t)}
                  disabled={tagList.length >= 8}
                  title={`添加「${t}」`}
                  className="inline-flex h-6 items-center gap-1 px-2 rounded-[var(--radius-pill)] text-xs bg-card border border-border/70 text-muted-foreground hover:border-primary/50 hover:bg-primary/5 hover:text-primary transition-colors disabled:opacity-50 disabled:pointer-events-none"
                >
                  <Plus size={11} strokeWidth={2.5} className="shrink-0" />
                  {t}
                </button>
              ))}
            </div>
          ) : null}
        </div>

        <div className="col-span-2">
          <Field label="备注">
            <textarea
              rows={2}
              placeholder="选填"
              value={remark}
              onChange={(e) => setRemark(e.target.value)}
              className={REMARK_TEXTAREA_CLS}
            />
          </Field>
        </div>
      </div>
    </Modal>
  )
}
