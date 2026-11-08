import { useState } from 'react'
import { toast } from 'sonner'
import { Modal, Button, Input, Field } from '@/components/common/ui'
import { perm } from '@/lib/api'

/**
 * 首次登录改密码提醒（仅店长、非强制、可直接关掉）。
 *
 * ★ 触发条件刻意做成「店长 + 本次登录用的仍是初始密码 123456」，
 *   而不是「这个账号第一次登录系统」—— 后者要在后端新增 last_login /
 *   must_change_pwd 字段（动 C# 与 DDL），而提醒的目的本来就是让默认密码
 *   被换掉：密码一改，条件就不成立，提醒自然不再出现。逻辑自洽且零后端改动。
 *
 * 关掉后按账号 id 写 localStorage 标记，本次装机不再打扰；
 * 换机器或清了缓存会再提醒一次（这正是想要的：没改就一直提醒）。
 */
export function PasswordTipDialog({
  staff,
  onClose,
  onChanged,
}: {
  staff: { id: number; name: string; role: string }
  onClose: () => void
  /** 改密成功后回调（调用方用它同步「记住密码」里存的旧值） */
  onChanged?: (pwd: string) => void
}) {
  const [pwd, setPwd] = useState('')
  const [pwd2, setPwd2] = useState('')
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)

  const close = () => {
    try { localStorage.setItem('mm_pwd_tip_off_' + staff.id, '1') } catch { /* 忽略 */ }
    onClose()
  }

  const save = async () => {
    if (pwd.length < 6) { setErr('密码至少 6 位'); return }
    if (pwd !== pwd2) { setErr('两次输入的密码不一致'); return }
    setBusy(true)
    setErr('')
    try {
      // 只改密码列：name/role/phone/enabled 一律沿用账号原值
      // （saveAccount 内部约定：pwd 非空才更新 pwd_hash，其余字段照常回写）
      const mine = (await perm.staffList()).find((r) => r.id === staff.id)
      await perm.saveAccount(
        staff.id,
        mine?.name ?? staff.name,
        mine?.role ?? staff.role,
        mine?.phone ?? '',
        pwd,
        mine?.enabled !== false,
      )
      toast.success('密码已修改，下次登录请用新密码')
      onChanged?.(pwd)
      close()
    } catch (e) {
      setErr(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title="建议修改登录密码"
      width={420}
      onClose={close}
      footer={
        <>
          <Button variant="ghost" onClick={close}>以后再说</Button>
          <Button variant="primary" onClick={save} disabled={busy}>
            {busy ? '保存中…' : '保存'}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <p className="text-[13px] text-foreground leading-relaxed">
          当前账号还在用初始密码，知道初始密码的人都能登进来。
        </p>
        <p className="text-[12px] text-muted-foreground leading-relaxed">
          建议改一个只有你知道的密码。
        </p>
        <Field label="新密码" required hint="至少 6 位">
          <Input
            type="password"
            value={pwd}
            onChange={(e) => setPwd(e.target.value)}
            aria-label="新密码"
          />
        </Field>
        <Field label="确认新密码" required>
          <Input
            type="password"
            value={pwd2}
            onChange={(e) => setPwd2(e.target.value)}
            aria-label="确认新密码"
          />
        </Field>
        {err ? (
          <p className="text-[11px] text-destructive" role="alert">{err}</p>
        ) : null}
      </div>
    </Modal>
  )
}
