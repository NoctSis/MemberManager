import { Trash2 } from 'lucide-react'
import { Button, Input, Select, Field, Modal, cx } from '@/components/common/ui'
import type { StaffAccount } from '@/data/mock'
import type { Draft } from './staffShared'

/**
 * 添加 / 编辑员工弹窗。
 * 校验规则与提交一律由页面的 submit() 负责，这里只渲染 + 把字段变更抛给页面。
 * 10-12：移除弹窗内的「个人比例」单值字段——逐项提成改在下方矩阵按项目覆盖，避免重复入口。
 */
export function StaffEditDialog({
  draft,
  errors,
  onClose,
  onSubmit,
  onChange,
}: {
  draft: Draft
  errors: Record<string, string>
  onClose: () => void
  onSubmit: () => void
  onChange: (next: Draft) => void
}) {
  return (
    <Modal
      title={draft.id === 0 ? '添加员工' : `编辑员工 · ${draft.name}`}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" onClick={onSubmit}>
            {draft.id === 0 ? '确认添加' : '保存'}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label="姓名" required error={errors.name}>
          <Input value={draft.name} onChange={(e) => onChange({ ...draft, name: e.target.value })} autoFocus />
        </Field>
        <Field label="角色">
          <Select value={draft.role} onChange={(e) => onChange({ ...draft, role: e.target.value as StaffAccount['role'] })}>
            <option>技师</option>
            <option>收银员</option>
          </Select>
        </Field>
        <Field label="手机号" error={errors.phone}>
          <Input
            value={draft.phone}
            onChange={(e) => onChange({ ...draft, phone: e.target.value })}
            placeholder="选填，方便联系"
            className="tabular-nums"
          />
        </Field>
        {/* A2 底薪（10-21）：默认不启用（留空=0）。按提成算钱的店完全不用管这格。 */}
        <Field label="底薪（元/月，选填）">
          <Input
            value={draft.baseSalaryText}
            onChange={(e) => onChange({ ...draft, baseSalaryText: e.target.value })}
            placeholder="留空 = 不启用（按提成算）"
            className="tabular-nums"
            inputMode="decimal"
          />
        </Field>
        <label className="flex items-center justify-between rounded-[var(--radius-sm)] border border-input px-3 h-10 cursor-pointer hover:border-primary/40 transition-colors">
          <span className="text-sm">启用（停用后收银台不再显示此人）</span>
          <button
            role="switch"
            aria-checked={draft.enabled}
            aria-label="启用状态"
            onClick={() => onChange({ ...draft, enabled: !draft.enabled })}
            className={cx(
              'w-9 h-5 rounded-[var(--radius-pill)] relative transition-colors',
              draft.enabled ? 'bg-primary' : 'bg-muted border border-input',
            )}
          >
            <span
              className={cx(
                'absolute top-0.5 w-4 h-4 rounded-full bg-card shadow transition-all',
                draft.enabled ? 'left-[18px]' : 'left-0.5',
              )}
            />
          </button>
        </label>
      </div>
    </Modal>
  )
}

/** 删除员工二次确认弹窗。 */
export function StaffDeleteDialog({
  target,
  onClose,
  onConfirm,
}: {
  target: StaffAccount
  onClose: () => void
  onConfirm: () => void
}) {
  return (
    <Modal
      title="移出员工名单"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" onClick={onConfirm}>
            <Trash2 size={13} /> 确认移出
          </Button>
        </>
      }
    >
      <p className="text-sm">
        把 <span className="font-semibold">{target.name}</span> 移出员工名单吗？
      </p>
      <p className="text-[11px] text-muted-foreground mt-2">
        移出后他不再出现在员工列表里，收银时也不能再被选为「本次操作」，不再计提成；
        他名下的历史单据、流水、提成<b className="text-foreground">全部保留</b>，账单上仍按姓名留痕，可到「消费记录」按操作员查到。
      </p>
      <p className="text-[11px] mt-2 rounded-md border border-border bg-muted/40 px-2.5 py-1.5">
        <b className="text-foreground">登录账号不受影响</b>——他仍可正常登录系统。
        如需禁止登录，请到「系统设置 · 登录账号」里停用。
      </p>
    </Modal>
  )
}