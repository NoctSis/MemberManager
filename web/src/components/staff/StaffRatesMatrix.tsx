import { useEffect, useMemo, useState } from 'react'
import { Percent, ChevronRight, ChevronDown } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Card, Input, Select, cx, thCls, tdCls } from '@/components/common/ui'
import { projects as apiProjects, commission } from '@/lib/api'
import type { ProjectRow } from '@/lib/api'
import type { StaffAccount } from '@/data/mock'

type Col = { id: number; name: string; mode: 'percent' | 'count'; defVal: number; baseDef: number }

/** 保留 1 位小数（百分比 0–100 / 按次元值都是这种量级）。 */
const round1 = (x: number) => Math.round(x * 10) / 10

/** 次卡提成结算方式（M11）：卡的钱只在「售卡 / 核销」选一处计，两段各半 = 卖时一半、每次核销一半。 */
const CARD_MODES: { value: string; label: string }[] = [
  { value: 'sale', label: '售卡时全算' },
  { value: 'use', label: '核销时全算' },
  { value: 'split', label: '两段各半' },
]
const cardModeLabel = (m: string) => CARD_MODES.find((x) => x.value === m)?.label ?? '两段各半'

/**
 * 「员工逐项提成」矩阵：行=员工、列=充值+各项目。
 * 配色/层级定则（38.6）：
 * - 同一区域只用一种底：表头统一灰底（thCls），列名不再夹默认值输入框；
 * - 「默认提成」抽到上方**独立可折叠面板**（低频在下）；
 * - 输入框无填充 + 描边（默认灰描边、改过主色描边）；
 * - 「已单独设」用**主色描边 + 覆盖小字**，不再用绿底大色块。
 */
export function StaffRatesMatrix({
  staffRows,
  globalRates,
  onGlobalRefresh,
}: {
  staffRows: StaffAccount[]
  globalRates: { recharge: number; consume: number } | null
  /** 保存了充值列默认值后回调，让父级刷新 globalRates（当前会话充值列默认值即时一致） */
  onGlobalRefresh?: () => void
}) {
  const [projItems, setProjItems] = useState<ProjectRow[]>([])
  const [vals, setVals] = useState<Record<number, Record<number, string>>>({})
  const [defEdits, setDefEdits] = useState<Record<number, string>>({})
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  // 默认提成面板：低频、默认折叠
  const [defOpen, setDefOpen] = useState(false)
  // 次卡提成结算方式（M11）：全局设置，改即时保存，未结算提成同步重计
  const [cardMode, setCardMode] = useState('use')
  const [cardModeSaving, setCardModeSaving] = useState(false)

  useEffect(() => {
    commission
      .cardMode()
      .then((m) => setCardMode(m))
      .catch(() => {})
  }, [])

  const changeCardMode = (mode: string) => {
    const prev = cardMode
    setCardMode(mode)
    setCardModeSaving(true)
    commission
      .saveCardMode(mode)
      .then(() => toast.success(`次卡提成结算已改为「${cardModeLabel(mode)}」，未结算提成已按新方式重计`))
      .catch((e) => {
        setCardMode(prev)
        toast.error(String(e))
      })
      .finally(() => setCardModeSaving(false))
  }

  // 列：充值(item_id=0) + 各在售项目。baseDef = 库里当前默认值（百分比 0-100 / 按次元），
  // defVal = 正在改的默认值（defEdits 非空用它预览）否则 baseDef；未覆盖的员工格子都显示 defVal。
  // 充值列默认值 = 全局 recharge_commission_rate；项目列 = 项目自身。
  const cols = useMemo<Col[]>(() => {
    const rechargeDef = globalRates ? round1(globalRates.recharge * 100) : 0
    const reEdit = defEdits[0]
    const reEdited = reEdit != null && reEdit.trim() !== '' ? Number(reEdit) : null
    const list: Col[] = [
      { id: 0, name: '会员充值', mode: 'percent', defVal: reEdited != null && Number.isFinite(reEdited) ? reEdited : rechargeDef, baseDef: rechargeDef },
    ]
    for (const p of projItems.filter((x) => x.enabled)) {
      const isCount = (p.commissionMode ?? 'percent') === 'count'
      const baseDef = isCount ? (p.commissionPerCount ?? 0) : round1((p.commissionRate ?? 0) * 100)
      const editTxt = defEdits[p.id]
      const edited = editTxt != null && editTxt.trim() !== '' ? Number(editTxt) : null
      const defVal = edited != null && Number.isFinite(edited) ? edited : baseDef
      list.push({ id: p.id, name: p.name, mode: isCount ? 'count' : 'percent', defVal, baseDef })
    }
    return list
  }, [projItems, globalRates, defEdits])

  useEffect(() => {
    let alive = true
    Promise.all([
      apiProjects.list(),
      ...staffRows.map((s) => commission.getStaffItemRates(s.id).catch(() => [] as { staffId: number; itemId: number; value: number; updatedAt: string }[])),
    ])
      .then((res) => {
        if (!alive) return
        const items = res[0] as ProjectRow[]
        const rateLists = res.slice(1) as { itemId: number; value: number }[][]
        setProjItems(items)
        const enabled = items.filter((x) => x.enabled)
        const next: Record<number, Record<number, string>> = {}
        staffRows.forEach((s, idx) => {
          const ov = rateLists[idx] ?? []
          // 只存「真实个人覆盖」；没覆盖的格子留空，交给 cols 的 defVal（默认值）兜底，
          // 这样改项目默认值时，未覆盖的员工格子会自动跟随新默认（应用到每个员工）。
          const map: Record<number, string> = {}
          const rechargeOv = ov.find((r) => r.itemId === 0)
          if (rechargeOv && rechargeOv.value > 0) map[0] = String(round1(rechargeOv.value * 100))
          for (const p of enabled) {
            const o = ov.find((r) => r.itemId === p.id)
            if (!o || o.value <= 0) continue
            const isCount = (p.commissionMode ?? 'percent') === 'count'
            map[p.id] = isCount ? String(o.value) : String(round1(o.value * 100))
          }
          next[s.id] = map
        })
        setVals(next)
        setLoading(false)
      })
      .catch(() => setLoading(false))
    return () => {
      alive = false
    }
  }, [staffRows, globalRates, reloadKey])

  const setCell = (staffId: number, itemId: number, v: string) =>
    setVals((prev) => ({ ...prev, [staffId]: { ...(prev[staffId] ?? {}), [itemId]: v } }))

  const saveAll = () => {
    setSaving(true)
    const done = async () => {
      // ① 先把改过的列头默认值写库——充值列走全局 recharge_commission_rate（saveRates），
      //    项目列走 service_items（saveItem）。一改即套用，自动带到所有没单独覆盖的员工。
      const reEditTxt = defEdits[0]
      if (reEditTxt != null && reEditTxt.trim() !== '') {
        const re = Number(reEditTxt)
        if (!Number.isFinite(re) || re < 0 || re > 100) {
          toast.error('会员充值提成须为 0 – 100 之间的数字')
          setSaving(false)
          return
        }
        await commission.saveRates(re / 100, globalRates?.consume ?? 0.03)
      }
      const byId = new Map(projItems.map((p) => [p.id, p]))
      const defJobs: Promise<void>[] = []
      for (const c of cols) {
        const txt = defEdits[c.id]
        if (c.id === 0 || txt == null || txt.trim() === '') continue
        const p = byId.get(c.id)
        if (!p) continue
        const n = Number(txt)
        if (!Number.isFinite(n) || n < 0) {
          toast.error(`「${c.name}」的默认值填的不对`)
          setSaving(false)
          return
        }
        const rate = c.mode === 'count' ? p.commissionRate ?? 0 : n / 100
        const perCount = c.mode === 'count' ? n : 0
        defJobs.push(
          apiProjects.save(p.id, p.name, p.category, p.price, p.unit, p.isCardItem, p.enabled, p.cardTimes, rate, c.mode, perCount),
        )
      }
      await Promise.all(defJobs)
      // ② 存各员工的个人覆盖（未覆盖的格子写 0，继续跟默认走）
      const jobs: Promise<void>[] = []
      for (const s of staffRows) {
        const map = vals[s.id] ?? {}
        const rows: { itemId: number; value: number }[] = []
        for (const c of cols) {
          const text = (map[c.id] ?? String(c.defVal)).trim()
          const n = text === '' ? 0 : Number(text)
          if (!Number.isFinite(n) || n < 0) {
            toast.error(`${s.name} 的「${c.name}」填的不对`)
            setSaving(false)
            return
          }
          const isDefault = Math.abs(n - c.defVal) < 1e-9
          rows.push({ itemId: c.id, value: isDefault ? 0 : c.mode === 'count' ? n : n / 100 })
        }
        jobs.push(commission.saveStaffItemRates(s.id, rows))
      }
      await Promise.all(jobs)
      // ③ 清编辑态 + 重新拉取项目/覆盖，未覆盖格子刷新成新默认值
      if (reEditTxt != null && reEditTxt.trim() !== '') onGlobalRefresh?.()
      setDefEdits({})
      setReloadKey((k) => k + 1)
      toast.success('已保存默认提成与员工逐项提成')
    }
    done()
      .catch((e) => toast.error(String(e)))
      .finally(() => setSaving(false))
  }

  if (loading) {
    return (
      <Card>
        <div className="flex items-center gap-1.5 px-4 h-11 border-b border-border">
          <Percent size={14} className="text-primary" />
          <h3 className="text-sm font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>员工逐项提成</h3>
        </div>
        <p className="text-sm text-muted-foreground p-4">加载中…</p>
      </Card>
    )
  }

  // 判断某列默认值是否被改过（用于默认面板的「改过主色描边」）
  const defEdited = (c: Col) => {
    const t = defEdits[c.id]
    return t != null && t.trim() !== '' && t.trim() !== String(c.baseDef)
  }

  return (
    <div className="space-y-4">
      {/* —— 默认提成独立面板（低频、可折叠）：同一区域一种底，输入无填充+描边，改过主色描边 —— */}
      <Card>
        <button
          type="button"
          onClick={() => setDefOpen((o) => !o)}
          className="w-full flex items-center justify-between px-4 h-11 text-left cursor-pointer select-none"
          aria-expanded={defOpen}
        >
          <span className="flex items-center gap-1.5 text-sm font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            <Percent size={14} className="text-primary" />
            默认提成
            <span className="text-[11px] font-normal text-muted-foreground">改这里套用到全员；没单独设的员工都跟它走</span>
          </span>
          {defOpen ? <ChevronDown size={16} className="text-muted-foreground" /> : <ChevronRight size={16} className="text-muted-foreground" />}
        </button>
        {defOpen ? (
          <div className="px-4 py-3 border-t border-border flex flex-wrap gap-x-5 gap-y-2.5">
            {cols.map((c) => (
              <label key={c.id} className="flex items-center gap-1.5">
                <span className="text-xs text-foreground">{c.name}</span>
                <span className="flex items-center gap-1">
                  <Input
                    value={defEdits[c.id] ?? String(c.baseDef)}
                    onChange={(e) => setDefEdits((prev) => ({ ...prev, [c.id]: e.target.value }))}
                    inputMode="decimal"
                    aria-label={`${c.name} 默认提成`}
                    className={cx(
                      'h-7 w-16 px-1.5 text-xs tabular-nums bg-transparent',
                      defEdited(c) && 'border-primary',
                    )}
                  />
                  <span className="text-[10px] text-muted-foreground">{c.mode === 'count' ? '元/次' : '%'}</span>
                </span>
              </label>
            ))}
            {/* 次卡提成结算方式（M11）：全局、即时保存；与上面的默认比例同一面板（都是全局提成设置） */}
            <div className="w-full flex items-center gap-2 pt-2.5 mt-1 border-t border-border">
              <span className="text-xs text-foreground">次卡提成结算</span>
              <Select
                value={cardMode}
                onChange={(e) => changeCardMode(e.target.value)}
                disabled={cardModeSaving}
                aria-label="次卡提成结算方式"
                className="h-7 w-28 text-xs bg-transparent"
                title={
                  '核销时全算：客户到店消费一次，就按这一次服务的「单次项目价」给经手技师记一次提成；'
                  + '整张卡的提成累计不超过这张卡实收对应的提成，超出的部分当找零不计（写入操作日志）。'
                  + '售卡时全算：卖卡时一次算清，之后核销不再计。两段各半：卖卡记一半、每次核销记一半。'
                }
              >
                {CARD_MODES.map((m) => (
                  <option key={m.value} value={m.value}>
                    {m.label}
                  </option>
                ))}
              </Select>
              <span className="text-[11px] text-muted-foreground">未结算的提成会按新方式重算，已结算的不动</span>
            </div>
          </div>
        ) : null}
      </Card>

      {/* —— 逐项矩阵：表头统一灰底只放列名；输入无填充+描边；覆盖=主色描边+小字，不用绿底 —— */}
      <Card>
        <div className="flex items-center justify-between px-4 h-11 border-b border-border">
          <div className="flex items-center gap-1.5">
            <h3 className="text-sm font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>员工逐项提成</h3>
            <span className="text-[11px] font-normal text-muted-foreground">改某格 = 单独设这一项</span>
          </div>
          <Button variant="secondary" size="sm" onClick={saveAll} disabled={saving}>
            {saving ? '保存中…' : '保存全部'}
          </Button>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full border-collapse min-w-[760px]">
            <thead>
              <tr>
                <th className={cx(thCls, 'border-r-2 border-r-border')}>员工</th>
                {cols.map((c) => (
                  <th key={c.id} className={cx(thCls, 'text-right')}>
                    <div className="flex flex-col items-end gap-0.5">
                      <span className="text-[11px] font-semibold">{c.name}</span>
                      <span className="text-[10px] text-muted-foreground">{c.mode === 'count' ? '元/次' : '%'}</span>
                    </div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {staffRows.map((s) => (
                <tr key={s.id} className="even:bg-muted/40">
                  <td className={cx(tdCls, 'font-medium whitespace-nowrap')}>
                    {s.name}
                    <span className="ml-1.5 text-[11px] text-muted-foreground">{s.role}</span>
                  </td>
                  {cols.map((c) => {
                    const display = vals[s.id]?.[c.id] ?? String(c.defVal)
                    const isCustom = Math.abs((display === '' ? 0 : Number(display)) - c.defVal) > 1e-9
                    const edited = vals[s.id]?.[c.id] != null
                    return (
                      <td key={c.id} className={cx(tdCls, 'text-right')}>
                        <span className="inline-flex items-center gap-1">
                          <Input
                            className={cx(
                              'h-7 w-20 px-1.5 text-sm tabular-nums ml-auto bg-transparent',
                              isCustom && 'border-primary font-medium',
                              !isCustom && !edited && 'text-muted-foreground/60 font-normal',
                            )}
                            value={display}
                            onChange={(e) => setCell(s.id, c.id, e.target.value)}
                            inputMode="decimal"
                            aria-label={`${s.name} ${c.name} 提成`}
                          />
                          {isCustom ? (
                            <span className="text-[10px] font-medium text-primary">覆盖</span>
                          ) : null}
                        </span>
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="px-4 py-2.5 border-t border-border text-[11px] text-muted-foreground">
          主色描边 = 已单独设；改回默认值即恢复沿用默认
        </p>
      </Card>
    </div>
  )
}
