import { useCallback, useEffect, useMemo, useState } from 'react'
import { PageScroll } from '@/components/common/PageLayout'
import { UserPlus } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/common/ui'
import { StaffRatesMatrix } from '@/components/staff/StaffRatesMatrix'
import { StaffTableCard } from '@/components/staff/StaffTableCard'
import { StaffEditDialog, StaffDeleteDialog } from '@/components/staff/StaffDialogs'
import { emptyDraft, baseSalaryOf, type Draft } from '@/components/staff/staffShared'
import { commission, perm as apiPerm } from '@/lib/api'
import { requestNav } from '@/lib/navbus'
import type { StaffAccount } from '@/data/mock'

// O4（10-11）：本页原 483 行，内联了「全局提成比例卡 / 员工名单表 / 两个弹窗」四块。
// 拆到 components/staff/ 后本页只留状态与业务调用（提交 / 停用 / 删除 / 加载）——
// 属纯搬家，行为、文案、接口、memo 引用稳定性均未变，详见整改计划表 §二十五。
export function StaffPage() {
  const [rows, setRows] = useState<StaffAccount[]>([])
  const [keyword, setKeyword] = useState('')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [delTarget, setDelTarget] = useState<StaffAccount | null>(null)
  // 全局提成兜底比例：只读加载，供矩阵「会员充值」列默认值与名单 tooltip 展示
  const [globalRates, setGlobalRates] = useState<{ recharge: number; consume: number } | null>(null)

  const load = useCallback(() => {
    apiPerm
      .staffList()
      // 员工管理 = 做项目/拿提成的人（收银员 + 技师）；纯「登录账号」（店长/收银员）在系统设置·账号密码里单独管
      .then((all) => setRows(all.filter((s) => s.role !== '店长')))
      .catch((e) => toast.error(String(e)))
  }, [])
  useEffect(load, [])

  // 加载全局提成兜底比例（0-1），供矩阵「会员充值」列默认值与名单 tooltip 展示
  const loadGlobalRates = useCallback(() => {
    commission
      .rates()
      .then(([recharge, consume]) => setGlobalRates({ recharge, consume }))
      .catch(() => setGlobalRates(null))
  }, [])
  useEffect(loadGlobalRates, [loadGlobalRates])

  // 矩阵里改了「会员充值」列默认值后刷新全局兜底（当前会话即时一致）
  const refreshGlobalRates = useCallback(() => {
    loadGlobalRates()
  }, [loadGlobalRates])

  // §三十二 32.7.5：拉每个员工的逐项覆盖数，给名单「提成比例」列做摘要徽标
  const [overrideCounts, setOverrideCounts] = useState<Record<number, number>>({})
  useEffect(() => {
    if (rows.length === 0) {
      setOverrideCounts({})
      return
    }
    Promise.all(rows.map((s) => commission.getStaffItemRates(s.id).catch(() => [] as { itemId: number; value: number }[])))
      .then((lists) => {
        const m: Record<number, number> = {}
        rows.forEach((s, i) => {
          m[s.id] = (lists[i] ?? []).filter((r) => r.itemId >= 0).length
        })
        setOverrideCounts(m)
      })
      .catch(() => {})
  }, [rows])

  // 交易记录：跳到消费记录页，并预填「操作员」筛选
  const viewRecords = useCallback((s: StaffAccount) => {
    requestNav({ page: 'records', operator: s.name })
  }, [])

  const openNew = () => {
    setDraft(emptyDraft())
    setErrors({})
  }

  const openEdit = useCallback((s: StaffAccount) => {
    setDraft({
      id: s.id, name: s.name, role: s.role, phone: s.phone ?? '', rate: s.rate ?? -1, enabled: s.enabled,
      baseSalaryText: s.baseSalary ? String(s.baseSalary) : '',
    })
    setErrors({})
  }, [])

  const submit = () => {
    if (!draft) return
    const next: Record<string, string> = {}
    if (!draft.name.trim()) next.name = '姓名不能为空'
    if (draft.phone.trim() && !/^1\d{10}$/.test(draft.phone.trim())) next.phone = '手机号必须为 11 位数字且以 1 开头'
    const bs = baseSalaryOf(draft.baseSalaryText)
    if (draft.baseSalaryText.trim() && bs <= 0) next.baseSalaryText = '底薪须为正数，或留空表示不启用'
    setErrors(next)
    if (Object.keys(next).length > 0) return

    apiPerm
      .saveStaff(draft.id, draft.name.trim(), draft.role, draft.phone.trim(), draft.rate, draft.enabled, bs)
      .then(() => {
        toast.success(draft.id === 0 ? `已添加员工「${draft.name.trim()}」` : `已保存「${draft.name.trim()}」的资料`)
        setDraft(null)
        load()
      })
      .catch((e) => setErrors({ name: String(e) }))
  }

  const toggle = useCallback((s: StaffAccount) => {
    apiPerm
      .toggleStaff(s.id)
      .then(() => {
        load()
      })
      .catch((e) => toast.error(String(e)))
  }, [load])

  const doDelete = () => {
    if (!delTarget) return
    apiPerm
      .deleteStaff(delTarget.id)
      .then(() => {
        setDelTarget(null)
        load()
      })
      .catch((e) => {
        setDelTarget(null)
        toast.error(String(e))
      })
  }

  const activeCount = useMemo(() => rows.filter((r) => r.enabled).length, [rows])
  // 过滤结果按 [名单, 关键词] 缓存：打字时只重算这一次，且 filteredRows 里每行的对象
  // 引用仍是 rows 里同一个 —— StaffRow 的 memo 因此能命中、不随行数一起放大重渲成本。
  const filteredRows = useMemo(() => {
    const kw = keyword.trim().toLowerCase()
    if (!kw) return rows
    return rows.filter(
      (s) =>
        s.name.toLowerCase().includes(kw)
        || (s.phone || '').toLowerCase().includes(kw)
        || s.role.toLowerCase().includes(kw),
    )
  }, [rows, keyword])

  return (
    <PageScroll minWidth={1080}>
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            员工管理
          </h1>
          <p className="text-xs text-muted-foreground mt-1">
            登记实际做项目的人——收银时选中其姓名，这笔业绩与提成就记在他名下
          </p>
        </div>
        <Button variant="primary" onClick={openNew}>
          <UserPlus size={15} /> 添加员工
        </Button>
      </div>

      {/* 38.6 层级：高频在上、低频在下 —— 员工名单在上（默认展开），提成设置在下（默认提成面板默认折叠） */}
      <StaffTableCard
        total={rows.length}
        activeCount={activeCount}
        keyword={keyword}
        filteredRows={filteredRows}
        globalRates={globalRates}
        overrideCounts={overrideCounts}
        onKeywordChange={setKeyword}
        onViewRecords={viewRecords}
        onEdit={openEdit}
        onToggle={toggle}
        onDelete={setDelTarget}
      />

      <StaffRatesMatrix staffRows={rows} globalRates={globalRates} onGlobalRefresh={refreshGlobalRates} />

      {draft ? (
        <StaffEditDialog
          draft={draft}
          errors={errors}
          onClose={() => setDraft(null)}
          onSubmit={submit}
          onChange={setDraft}
        />
      ) : null}

      {delTarget ? (
        <StaffDeleteDialog
          target={delTarget}
          onClose={() => setDelTarget(null)}
          onConfirm={doDelete}
        />
      ) : null}
    </PageScroll>
  )
}