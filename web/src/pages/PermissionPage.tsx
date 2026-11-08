import { useEffect, useState } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import { ShieldCheck, KeyRound, UserX, UserCheck } from 'lucide-react'
import { toast } from 'sonner'
import { Button, SectionCard, Badge, Modal, DataTable } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { perm as apiPerm } from '@/lib/api'
import type { StaffAccount } from '@/data/mock'

/** 员工账号 5 列预算 */
const ACCOUNT_COLS: DataTableCol[] = [
  { key: 'name', title: '姓名', width: 150, cellClass: 'font-medium' },
  { key: 'role', title: '角色', width: 130 },
  { key: 'phone', title: '手机号', width: 150, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'status', title: '状态', width: 100 },
  { key: 'action', title: '操作', width: 190, headClass: 'text-right', cellClass: 'text-right' },
]

/**
 * 登录账号管理（店主 11-05 定：删掉「角色权限矩阵」）。
 * 技师不登录系统、没有任何权限，给它维护一份角色 × 模块矩阵没有意义；
 * 真正生效的是「按人权限」——在「系统设置 · 登录账号」里逐个账号勾选，
 * 后端 staff_perms 优先、无记录才回落 role_perms 的默认值。
 */
export function PermissionPage() {
  const [staff, setStaff] = useState<StaffAccount[]>([])
  const [resetTarget, setResetTarget] = useState<StaffAccount | null>(null)

  const load = () => {
    apiPerm.staffList().then(setStaff).catch((e) => toast.error(String(e)))
  }
  useEffect(load, [])

  const toggleStaff = (s: StaffAccount) => {
    if (s.role === '店长' && s.enabled) {
      toast('至少保留一名店长账号，不可停用', { icon: '🔒' })
      return
    }
    apiPerm
      .toggleStaff(s.id)
      .then(() => { load() })
      .catch((e) => toast.error(String(e)))
  }

  const doReset = () => {
    if (!resetTarget) return
    apiPerm
      .resetPwd(resetTarget.id)
      .then(() => {
        toast.success(`${resetTarget.name} 的密码已重置为初始密码`)
        setResetTarget(null)
        load()
      })
      .catch((e) => toast.error(String(e)))
  }

  return (
    <PageFill minWidth={1080}>
      <div className="shrink-0">
        <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
          权限与日志
        </h1>
      </div>

      <div className="page-body flex flex-col">
        <SectionCard
          className="flex flex-col"
          title="员工账号"
          extra={
            <Badge tone="muted" className="h-6 text-[11px] font-normal">
              新建与改名请到「系统设置 · 登录账号」
            </Badge>
          }
          bodyClassName="p-0 flex-1 min-h-0 flex flex-col"
        >
          <DataTable
            cols={ACCOUNT_COLS}
            rows={staff}
            rowKey={(s) => s.id}
            plain
            fill
            render={(s) => [
              s.name,
              <Badge key="r" tone={s.role === '店长' ? 'primary' : 'muted'}>
                <ShieldCheck size={11} className="mr-1" />
                {s.role}
              </Badge>,
              s.phone || '—',
              s.enabled ? <Badge key="st" tone="primary">启用</Badge> : <Badge key="st" tone="danger">已停用</Badge>,
              <div key="a" className="inline-flex gap-1">
                <Button variant="ghost" size="sm" onClick={() => setResetTarget(s)}>
                  <KeyRound size={12} /> 重置密码
                </Button>
                <Button variant="ghost" size="sm" onClick={() => toggleStaff(s)}>
                  {s.enabled ? <><UserX size={12} /> 停用</> : <><UserCheck size={12} /> 启用</>}
                </Button>
              </div>,
            ]}
            empty={<div className="py-8 text-center text-xs text-muted-foreground">还没有登录账号，到「系统设置 · 登录账号」新建</div>}
          />
          <div className="px-4 py-2.5 border-t border-border text-[11px] text-muted-foreground truncate">
            按人权限在「系统设置 · 登录账号」逐个账号调整；技师只用于做项目和算提成，不提供登录。
          </div>
        </SectionCard>
      </div>

      {resetTarget ? (
        <Modal
          title="重置登录密码"
          onClose={() => setResetTarget(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setResetTarget(null)}>取消</Button>
              <Button variant="primary" onClick={doReset}>
                <KeyRound size={13} /> 确认重置
              </Button>
            </>
          }
        >
          <p className="text-sm">
            将把 <span className="font-semibold">{resetTarget.name}</span> 的登录密码重置为初始密码（123456）。
          </p>
          <p className="text-[11px] text-muted-foreground mt-2">重置操作写入日志（操作人、时间、对象）。</p>
        </Modal>
      ) : null}
    </PageFill>
  )
}
