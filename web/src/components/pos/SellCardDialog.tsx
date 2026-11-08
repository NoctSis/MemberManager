import { useEffect, useState } from 'react'
import { Tags } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Input, Select, Field, Modal } from '@/components/common/ui'
import { projects, cards as apiCards, members as apiMembers, pos as apiPos, bizDate, type ProjectRow } from '@/lib/api'
import type { PosStaffOption } from '@/lib/api'
import { getStaff, getLastOperatorId, setLastOperatorId } from '@/lib/session'
import { fmtMoney } from '@/data/mock'
import type { CardTemplate, Member } from '@/data/mock'

/**
 * 售卡弹窗（共享组件）：收银台与项目列表页共用同一套逻辑。
 *
 * 店主 09-26 定：售卡是「交易动作」，主场在收银台；项目列表页只负责维护项目与次卡套餐。
 * 下拉里列的是「可多次售卖的套餐次卡」——即已配置次卡套餐的项目；选中自动带出次数/总价，可现场改。
 * 经手人必选：店里常共用一个账号登录，登录人≠经单人，售卡要记清是谁卖的（留痕 + 提成按 TA 记）。
 */
export function SellCardDialog({
  open,
  onClose,
  onSold,
  defaultStaffId,
}: {
  open: boolean
  onClose: () => void
  /** 售卡成功回调（外部刷新会员 / 次卡数据） */
  onSold?: () => void
  /** 预选经手人（收银台传本次开单的经办人，保证与这一单一致） */
  defaultStaffId?: number
}) {
  const [items, setItems] = useState<ProjectRow[]>([])
  const [templates, setTemplates] = useState<CardTemplate[]>([])
  const [members, setMembers] = useState<Member[]>([])
  const [staffOptions, setStaffOptions] = useState<PosStaffOption[]>([])

  const [projectName, setProjectName] = useState('')
  const [templateId, setTemplateId] = useState(0)
  const [method, setMethod] = useState('现金')
  const [times, setTimes] = useState('')
  const [total, setTotal] = useState('')
  const [memberId, setMemberId] = useState(0)
  const [staffId, setStaffId] = useState(0)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  // 某项目当前启用的次卡套餐（内部存储，按 itemName 归属项目）
  const pkgOf = (itemName: string): CardTemplate | undefined =>
    templates.find((t) => t.itemName === itemName && t.enabled)
  const sellableItems = items.filter((s) => s.enabled && s.isCardItem && pkgOf(s.name))
  const sellableMembers = members.filter((m) => m.status === '正常')

  // 打开时拉数据 + 重置表单
  useEffect(() => {
    if (!open) return
    setProjectName(''); setTemplateId(0); setTimes(''); setTotal('')
    setMemberId(0); setMethod('现金'); setErrors({}); setBusy(false)
    projects.list().then(setItems).catch(() => setItems([]))
    apiCards.templates().then(setTemplates).catch(() => setTemplates([]))
    apiMembers.search('', '全部状态', '全部标签').then(setMembers).catch(() => setMembers([]))
    apiPos.staffOptions().then(setStaffOptions).catch(() => setStaffOptions([]))
  }, [open])

  // 经手人默认：调用方点名 → 上次开单的人 → 登录本人 → 列表第一位
  useEffect(() => {
    if (!open || staffOptions.length === 0) return
    setStaffId((prev) => {
      if (prev > 0 && staffOptions.some((s) => s.id === prev)) return prev
      if (defaultStaffId && staffOptions.some((s) => s.id === defaultStaffId)) return defaultStaffId
      const last = getLastOperatorId()
      if (last > 0 && staffOptions.some((s) => s.id === last)) return last
      const me = getStaff()?.id ?? 0
      if (me > 0 && staffOptions.some((s) => s.id === me)) return me
      return staffOptions[0]?.id ?? 0
    })
  }, [open, staffOptions, defaultStaffId])

  const timesN = Math.max(0, Math.trunc(Number(times) || 0))
  const totalN = Math.max(0, Number(total) || 0)
  // 删掉赠送/优惠后：总次数 = 购买次数；折合单次 = 总价 ÷ 次数
  const unitN = timesN > 0 ? Math.round((totalN / timesN) * 100) / 100 : 0
  const chosenPkg = projectName ? pkgOf(projectName) : undefined

  const pickProject = (pn: string) => {
    setProjectName(pn)
    const pkg = pkgOf(pn)
    if (pkg) {
      setTemplateId(pkg.id)
      setTimes(String(pkg.times))
      setTotal(String(pkg.price))
    } else {
      setTemplateId(0); setTimes(''); setTotal('')
    }
  }

  const submit = () => {
    const errs: Record<string, string> = {}
    if (!projectName) errs.project = '请选择要卖的套餐次卡'
    if (!memberId) errs.member = '请选择会员（仅正常状态会员可购卡）'
    if (!staffId) errs.staff = '请选择经手人'
    if (!templateId) errs.template = '该项目没有可用的次卡套餐'
    if (timesN <= 0) errs.times = '购买次数必须是正整数'
    if (totalN <= 0) errs.total = '总价必须大于 0'
    setErrors(errs)
    if (Object.keys(errs).length > 0) return
    const m = members.find((x) => x.id === memberId)!
    const staffName = staffOptions.find((s) => s.id === staffId)?.name ?? ''
    setBusy(true)
    apiCards
      .sellCard(memberId, templateId, method, timesN, unitN, 0, 0, staffId)
      .then(() => {
        // 记住这次的经手人：下次开单/建档/售卡默认还是 TA
        setLastOperatorId(staffId)
        toast.success(
          `售卡成功：${m.name}，${projectName} ${timesN} 次，实收 ¥${fmtMoney(totalN)}（${method}）` +
            (staffName ? `，经手人 ${staffName}` : '') +
            `，折合 ¥${fmtMoney(unitN)}/次`,
        )
        onClose()
        onSold?.()
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setBusy(false))
  }

  if (!open) return null

  return (
    <Modal
      title="售卡"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>取消</Button>
          <Button variant="primary" onClick={submit} disabled={!chosenPkg || busy}>
            {chosenPkg ? `确认收款 ¥${fmtMoney(totalN)}` : '确认售卡'}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {sellableItems.length === 0 ? (
          <div className="rounded-[var(--radius-sm)] bg-muted/60 border border-border px-3 py-3 text-xs text-muted-foreground leading-relaxed">
            还没有可售的套餐次卡。<br />
            先去「项目列表」建次卡套餐（勾选「卖次卡套餐」并填次数与总价）。
          </div>
        ) : null}
        <Field label="买哪个项目的次卡" required error={errors.project} hint="只列已配置次卡套餐的项目（套餐在项目「编辑」里维护）">
          <Select value={projectName} onChange={(e) => pickProject(e.target.value)}>
            <option value="" disabled>请选择套餐次卡</option>
            {sellableItems.map((s) => {
              const pkg = pkgOf(s.name)!
              return (
                <option key={s.id} value={s.name}>
                  {s.name} · {pkg.times}次 · ¥{fmtMoney(pkg.price)}（折合 ¥{(pkg.price / pkg.times).toFixed(2)}/次）
                </option>
              )
            })}
          </Select>
        </Field>
        <Field label="会员" required error={errors.member}>
          <Select value={memberId} onChange={(e) => setMemberId(Number(e.target.value))}>
            <option value={0} disabled>请选择会员</option>
            {sellableMembers.map((m) => (
              <option key={m.id} value={m.id}>{m.name}（{m.cardNo} · {m.phone}）</option>
            ))}
          </Select>
        </Field>
        <Field label="经手人" required error={errors.staff} hint="这张卡是谁卖出去的（留痕 + 提成按 TA 记），默认上一次开单的人">
          <Select value={staffId} onChange={(e) => setStaffId(Number(e.target.value))} aria-label="售卡经手人">
            {staffOptions.length === 0 ? (
              <option value={0}>加载中…</option>
            ) : (
              staffOptions.map((s) => (
                <option key={s.id} value={s.id}>{s.name}{s.role ? `（${s.role}）` : ''}</option>
              ))
            )}
          </Select>
        </Field>
        <Field label="收款方式" required>
          <Select value={method} onChange={(e) => setMethod(e.target.value)}>
            <option>现金</option>
            <option>微信</option>
            <option>支付宝</option>
          </Select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="购买次数" required error={errors.times}>
            <Input value={times} onChange={(e) => setTimes(e.target.value)} placeholder="10" inputMode="numeric" className="tabular-nums" />
          </Field>
          <Field label="总价（元）" required error={errors.total} hint="可现场改，推广时直接降价">
            <Input value={total} onChange={(e) => setTotal(e.target.value)} placeholder="300" inputMode="decimal" className="tabular-nums" />
          </Field>
        </div>
        <div className="text-xs bg-muted/60 rounded-[var(--radius-sm)] px-3 py-2.5 space-y-1">
          <div className="flex justify-between">
            <span className="text-muted-foreground">购买 {timesN} 次</span>
            <span className="tabular-nums">总价 ¥{fmtMoney(totalN)}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">折合单次</span>
            <span className="tabular-nums font-medium">¥{fmtMoney(unitN)}/次</span>
          </div>
          <div className="flex justify-between font-semibold border-t border-border pt-1.5">
            <span>实收</span>
            <span className="tabular-nums">¥{fmtMoney(totalN)}</span>
          </div>
          <p className="text-muted-foreground pt-1">
            有效期：自今日（{bizDate()}）起 {chosenPkg?.validMonths ?? 12} 个月；售卡实收计入当日营收，之后核销只扣次数不收款。
          </p>
        </div>
        <p className="text-[11px] text-muted-foreground flex items-start gap-1.5">
          <Tags size={12} className="shrink-0 mt-px" />
          冻结 / 挂失会员不可购卡；下拉中已自动排除。
        </p>
      </div>
    </Modal>
  )
}
