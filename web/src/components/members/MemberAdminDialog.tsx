import { useEffect, useState } from 'react'
import { Award, Plus, Pencil, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Badge, Input, Field, Modal, cx, thCls, tdCls, numCls } from '@/components/common/ui'
import { levels as apiLevels } from '@/lib/api'
import { fmtMoney } from '@/data/mock'
import type { MemberLevel } from '@/data/mock'

/** 等级制度编辑草稿。门槛走字符串入表单、提交时转数字，避免边输边转丢掉小数点 */
interface LevelDraft {
  id: number
  name: string
  minRecharge: string
  enabled: boolean
}

/**
 * 会员设置弹窗（店主 09-26 定）：会员相关的「制度调整」集中在这里，入口从「设置」挪到「会员管理」页。
 *
 * 现在承载「会员等级制度」——等级完全自动判定、不在会员表单里手选；这里只改制度本身
 * （改名 / 改门槛 / 启停 / 删除），一改后端就在同一事务里重算全体会员的等级。
 * 入口按钮已做店长门控（收银员看不到）。
 */
export function MemberAdminDialog({
  open,
  onClose,
  onChanged,
}: {
  open: boolean
  onClose: () => void
  /** 制度变更后回调（外部刷新会员列表的等级徽标） */
  onChanged?: () => void
}) {
  const [levels, setLevels] = useState<MemberLevel[]>([])
  const [draft, setDraft] = useState<LevelDraft | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [del, setDel] = useState<MemberLevel | null>(null)

  const loadLevels = () => {
    apiLevels.list().then(setLevels).catch(() => setLevels([]))
  }
  useEffect(() => {
    if (open) loadLevels()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const openNew = () => {
    setDraft({ id: 0, name: '', minRecharge: '', enabled: true })
    setErrors({})
  }

  const openEdit = (l: MemberLevel) => {
    setDraft({ id: l.id, name: l.name, minRecharge: String(l.minRecharge), enabled: l.enabled })
    setErrors({})
  }

  const submit = () => {
    if (!draft) return
    const v = draft.minRecharge.trim()
    const num = Number(v)
    const next: Record<string, string> = {}
    if (!draft.name.trim()) next.name = '等级名称不能为空'
    else if (draft.name.trim().length > 20) next.name = '等级名称最多 20 字'
    if (v === '' || isNaN(num) || num < 0) next.minRecharge = '门槛须为不小于 0 的数字'
    else if (num > 99999999) next.minRecharge = '门槛金额过大'
    // 唯一性先在前端拦住，后端保存时还会再校一遍
    if (!next.minRecharge && levels.some((l) => l.id !== draft.id && l.minRecharge === num)) {
      next.minRecharge = '已有等级使用同样的门槛，门槛必须互不相同'
    }
    if (!next.name && levels.some((l) => l.id !== draft.id && l.name === draft.name.trim())) {
      next.name = '已有同名等级'
    }
    setErrors(next)
    if (Object.keys(next).length > 0) return
    setBusy(true)
    apiLevels
      .save(draft.id, draft.name.trim(), num, draft.enabled)
      .then((changed) => {
        toast.success(
          `已保存等级「${draft.name.trim()}」` +
            (changed > 0 ? `，已按新制度同步 ${changed} 位会员的等级` : `，没有会员的等级因此改变`),
        )
        setDraft(null)
        loadLevels()
        onChanged?.()
      })
      .catch((e) => setErrors({ name: String(e) }))
      .finally(() => setBusy(false))
  }

  const doDelete = () => {
    if (!del) return
    setBusy(true)
    apiLevels
      .remove(del.id)
      .then((changed) => {
        toast.success(
          `已删除等级「${del.name}」` +
            (changed > 0 ? `，已按剩余制度同步 ${changed} 位会员的等级` : `，没有会员因此改变等级`),
        )
        setDel(null)
        loadLevels()
        onChanged?.()
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setBusy(false))
  }

  if (!open) return null

  return (
    <>
      <Modal
        title="会员设置"
        width={760}
        onClose={onClose}
        footer={<Button variant="secondary" onClick={onClose}>关闭</Button>}
      >
        {/* 会员等级制度：等级自动判定，不在会员表单里手选 */}
        <div className="space-y-3">
          <div className="flex items-center justify-between gap-3">
            <div className="flex items-center gap-1.5 text-sm font-semibold">
              <Award size={14} className="text-primary" /> 会员等级制度
              <span className="ml-1 text-[11px] font-normal text-muted-foreground">
                共 {levels.length} 档 · 按累计实收充值自动判定，无需手工选择
              </span>
            </div>
            <Button variant="primary" size="sm" onClick={openNew}>
              <Plus size={13} /> 添加等级
            </Button>
          </div>

          <div className="border border-border rounded-[var(--radius-sm)] overflow-hidden">
            <table className="w-full border-collapse">
              <thead>
                <tr>
                  <th className={thCls}>等级</th>
                  <th className={cx(thCls, 'text-right')}>累计充值门槛（元）</th>
                  <th className={thCls}>状态</th>
                  <th className={cx(thCls, 'text-right')}>当前会员数</th>
                  <th className={cx(thCls, 'text-right')}>操作</th>
                </tr>
              </thead>
              <tbody>
                {levels.length === 0 ? (
                  <tr>
                    <td className={tdCls} colSpan={5}>
                      <div className="py-6 text-center text-sm text-muted-foreground">正在读取等级制度…</div>
                    </td>
                  </tr>
                ) : (
                  levels.map((l, i) => (
                    <tr key={l.id} className="even:bg-muted/40">
                      <td className={cx(tdCls, 'font-medium')}>
                        <span className="flex items-center gap-1.5">
                          <Badge tone={i === levels.length - 1 ? 'solid' : i === levels.length - 2 ? 'primary' : 'muted'}>
                            {l.name}
                          </Badge>
                          {i === 0 ? <span className="text-[10px] text-muted-foreground">入门级（兜底）</span> : null}
                        </span>
                      </td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(l.minRecharge)}</td>
                      <td className={tdCls}>
                        {l.enabled ? <Badge tone="primary">启用</Badge> : <Badge tone="danger">已停用</Badge>}
                      </td>
                      <td className={cx(tdCls, numCls)}>{l.memberCount}</td>
                      <td className={cx(tdCls, 'text-right')}>
                        <span className="inline-flex gap-1">
                          <Button variant="ghost" size="sm" onClick={() => openEdit(l)}>
                            <Pencil size={12} /> 编辑
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            disabled={levels.length <= 1}
                            title={levels.length <= 1 ? '至少要保留一个等级' : ''}
                            onClick={() => setDel(l)}
                          >
                            <Trash2 size={12} /> 删除
                          </Button>
                        </span>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <div className="rounded-[var(--radius-sm)] bg-muted/50 px-3 py-2.5 text-[11px] text-muted-foreground leading-relaxed">
            门槛按<b className="text-foreground">累计实收充值</b>计算（店家赠送的金额不计入），达标自动升档，会员表单里不再需要选等级。
            改名字 / 改门槛 / 启停 / 删除后，全体会员会在同一事务里<b className="text-foreground">按新制度同步重算</b>——改掉的等级不再挂在任何会员身上。
            门槛最低的一档是兜底档（所有不够任何门槛的会员都落在这里），门槛必须互不相同，至少保留一个等级。
          </div>
        </div>
      </Modal>

      {draft ? (
        <Modal
          title={draft.id === 0 ? '添加等级' : `编辑等级 · ${draft.name}`}
          onClose={() => setDraft(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDraft(null)}>取消</Button>
              <Button variant="primary" onClick={submit} disabled={busy}>
                {busy ? '保存中…' : '保存'}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <Field label="等级名称" required error={errors.name}>
              <Input
                value={draft.name}
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                placeholder="如 钻石卡"
                autoFocus
              />
            </Field>
            <Field
              label="累计充值门槛（元）"
              required
              error={errors.minRecharge}
              hint="会员累计实收充值达到这个金额即自动升为本档（赠送金额不计入）"
            >
              <Input
                value={draft.minRecharge}
                onChange={(e) => setDraft({ ...draft, minRecharge: e.target.value })}
                placeholder="如 10000"
                className="tabular-nums"
                inputMode="decimal"
              />
            </Field>
            <label className="flex items-center justify-between rounded-[var(--radius-sm)] border border-input px-3 h-10 cursor-pointer hover:border-primary/40 transition-colors">
              <span className="text-sm">启用（停用后不再用于判定，已有会员会落到其它档）</span>
              <button
                role="switch"
                aria-checked={draft.enabled}
                aria-label="等级启用状态"
                onClick={() => setDraft({ ...draft, enabled: !draft.enabled })}
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
            <p className="text-[11px] text-muted-foreground leading-relaxed">
              保存后立即生效：全体会员会在同一事务里按新制度重算等级，变动会写进操作日志（含影响了多少人）。
            </p>
          </div>
        </Modal>
      ) : null}

      {del ? (
        <Modal
          title={`删除等级 · ${del.name}`}
          onClose={() => setDel(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDel(null)}>取消</Button>
              <Button variant="danger" onClick={doDelete} disabled={busy}>
                <Trash2 size={13} /> {busy ? '删除中…' : '确认删除'}
              </Button>
            </>
          }
        >
          <p className="text-sm leading-relaxed">
            删除后「{del.name}」（累计充值门槛 ¥{fmtMoney(del.minRecharge)}）将立即从制度里移除，
            当前挂在这个等级下的 <b className="text-foreground">{del.memberCount}</b> 位会员会按剩余制度重新判定等级。
          </p>
          <p className="text-xs text-muted-foreground mt-2 leading-relaxed">
            删除的是等级规则本身，不影响任何会员的充值流水、余额与积分；该操作已写入操作日志。
          </p>
        </Modal>
      ) : null}
    </>
  )
}
