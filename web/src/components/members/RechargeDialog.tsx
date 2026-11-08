import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { Button, Input, Select, Field, Modal } from '@/components/common/ui'
import { storedvalue } from '@/lib/api'
import type { PosStaffOption } from '@/lib/api'
import { getStaff, getLastOperatorId, setLastOperatorId } from '@/lib/session'
import { fmtMoney } from '@/data/mock'
import type { RechargePackage } from '@/data/mock'

/** 充值完成后交给调用方的信息（小票 / 刷新余额 / 提示都用它）。 */
export interface RechargeDone {
  orderNo: string
  amount: number
  gift: number
  memberName: string
  memberCard: string
  method: string
  operator: string
}

/**
 * 会员充值弹窗（收银台入口）。与会员管理页的充值走**同一个后端**
 * `storedvalue.RechargeByStaff`：金额与赠送同事务写流水、更新余额、记提成，
 * 恒等式「余额 = 充值 + 赠送 − 消费 − 退款 ± 调整」不变。
 * 经手人单独可点名，绝不拿登录账号顶替（一店常共用一个账号）。
 */
export function RechargeDialog({
  open, member, onClose, onDone, staffOptions, defaultStaffId,
}: {
  open: boolean
  member: { id: number; name: string; balance: number; cardNo?: string; status?: string } | null
  onClose: () => void
  onDone: (r: RechargeDone) => void
  staffOptions: PosStaffOption[]
  defaultStaffId: number
}) {
  const [packages, setPackages] = useState<RechargePackage[]>([])
  const [pkgId, setPkgId] = useState<string>('')
  const [amount, setAmount] = useState<string>('0')
  const [method, setMethod] = useState<string>('现金')
  const [staffId, setStaffId] = useState<number>(0)
  const [busy, setBusy] = useState<boolean>(false)

  useEffect(() => {
    if (!open) return
    storedvalue
      .packages()
      .then((ps) => setPackages(ps.filter((p) => p.enabled)))
      .catch(() => setPackages([]))
    setPkgId('')
    setAmount('0')
    setMethod('现金')
    setStaffId(defaultStaffId || getLastOperatorId())
    // 只在弹窗打开那一刻重置，defaultStaffId 变化不该打断正在填的表单
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const pkg = packages.find((p) => String(p.id) === pkgId) ?? null
  const gift = pkg?.gift ?? 0

  const submit = () => {
    if (!member || busy) return
    const n = Number(amount)
    if (!amount || isNaN(n) || n <= 0) {
      toast.error('请输入大于 0 的充值金额')
      return
    }
    if (member.status && member.status !== '正常') {
      toast.error('冻结/挂失会员不能充值')
      return
    }
    const opName = staffOptions.find((s) => s.id === staffId)?.name || getStaff()?.name || '—'
    setBusy(true)
    storedvalue
      .rechargeByStaff(member.id, n, gift, method, staffId)
      .then((orderNo) => {
        if (staffId > 0) setLastOperatorId(staffId)
        onDone({
          orderNo,
          amount: n,
          gift,
          memberName: member.name,
          memberCard: member.cardNo ?? '',
          method,
          operator: opName,
        })
        onClose()
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setBusy(false))
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`会员充值 · ${member?.name ?? ''}`}
      width={440}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>取消</Button>
          <Button variant="primary" onClick={submit} disabled={busy || !member}>
            {busy ? '处理中…' : '确认充值'}
          </Button>
        </>
      }
    >
      <div className="rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3.5 py-2.5 mb-4 text-xs">
        当前余额 <b className="text-primary">¥{fmtMoney(member?.balance ?? 0)}</b>
        {packages.length > 0 ? ` · 有 ${packages.length} 档方案可选，也可直接手填金额` : ' · 无已配置方案，仅可手填金额'}
      </div>
      <Field label="经手人">
        <Select value={String(staffId)} onChange={(e) => setStaffId(Number(e.target.value))}>
          <option value="0">登录账号（{getStaff()?.name ?? '—'}）</option>
          {staffOptions.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}（{s.role}{s.loginName ? ` · ${s.loginName}` : ''}）
            </option>
          ))}
        </Select>
      </Field>
      <Field label="充值方案">
        <Select
          value={pkgId}
          onChange={(e) => {
            const id = e.target.value
            setPkgId(id)
            const p = packages.find((x) => String(x.id) === id)
            setAmount(p ? String(p.amount) : '0')
          }}
        >
          <option value="">手动填写（无赠送）</option>
          {packages.map((p) => (
            <option key={p.id} value={String(p.id)}>充 ¥{fmtMoney(p.amount)} 送 ¥{fmtMoney(p.gift)}</option>
          ))}
        </Select>
      </Field>
      <Field label="充值金额（元）" required>
        <Input
          value={amount}
          onChange={(e) => { setAmount(e.target.value); setPkgId('') }}
          placeholder="0"
          className="tabular-nums"
          inputMode="decimal"
        />
      </Field>
      {pkg ? (
        <div className="mt-2 rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3 py-2 text-xs">
          按方案：充 <b className="text-primary">¥{fmtMoney(pkg.amount)}</b> 送 <b className="text-primary">¥{fmtMoney(pkg.gift)}</b>
          {pkg.gift > 0
            ? <span className="ml-1 text-muted-foreground">（赠送比例 {((pkg.gift / pkg.amount) * 100).toFixed(1)}%）</span>
            : <span className="ml-1 text-muted-foreground">（该档位无赠送）</span>}
        </div>
      ) : Number(amount) > 0 ? (
        <div className="mt-2 rounded-[var(--radius-sm)] bg-muted/60 border border-input px-3 py-2 text-xs text-muted-foreground">
          手动金额：¥{fmtMoney(Number(amount))}，本次不赠送（如需按方案赠送请下拉选择档位）
        </div>
      ) : null}
      <Field label="收款方式">
        <Select value={method} onChange={(e) => setMethod(e.target.value)}>
          <option>现金</option>
          <option>微信</option>
          <option>支付宝</option>
        </Select>
      </Field>
      <p className="text-xs text-muted-foreground mt-3">
        金额与赠送金额同事务写入流水并更新余额，可在会员详情「余额对账」中逐笔核对。
      </p>
    </Modal>
  )
}
