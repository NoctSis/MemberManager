import { useCallback, useEffect, useMemo, useState, memo } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import { Plus, Search, Ticket, Package, Tags, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import {
  Button, SectionCard, Badge, Input, Select, Field, Modal, Card, cx, DataTable,
  tdCls, numCls,
} from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { projects, cards as apiCards, type ProjectRow } from '@/lib/api'
import { fmtMoney } from '@/data/mock'
import type { CardTemplate } from '@/data/mock'

// 项目列表（店主 09-22 新增需求）：服务/商品的增删改查，收银开单与次卡套餐共用此数据。
//
// 09-26 店主定：
//  · 次卡套餐直接挂在项目表单里（次数 + 总价 + 有效期），界面上不出现「模板」二字；
//    `card_templates` 仍作内部存储（member_cards.template_id 依赖它），勾选则 upsert、取消则停售（不删）。
//  · **售卡是交易动作，已挪到收银台**（`components/pos/SellCardDialog`）；本页只做维护、不再售卖。
//  · 分类可留空（默认就是空、空就是「无」），后端把空分类归入「未分类」。
const categories = ['美发', '美容', '商品']

/** 项目表 9 列预算（≈990px，最小窗口内不横滚）；项目名称/次卡套餐弹性截断。 */
const PROJECT_COLS: DataTableCol[] = [
  { key: 'name', title: '项目名称', width: 150, truncate: true, cellClass: 'font-medium' },
  { key: 'category', title: '分类', width: 90 },
  { key: 'price', title: '单次价', width: 90, headClass: 'text-right', cellClass: numCls },
  { key: 'unit', title: '计价单位', width: 80, cellClass: 'text-muted-foreground' },
  { key: 'commission', title: '提成', width: 110, headClass: 'text-right', cellClass: 'text-right tabular-nums' },
  { key: 'card', title: '次卡', width: 112 },
  { key: 'pkg', title: '次卡套餐', truncate: true },
  { key: 'status', title: '状态', width: 90 },
  { key: 'action', title: '操作', width: 150, headClass: 'text-right', cellClass: 'text-right' },
]

export function ProjectsPage() {
  const [items, setItems] = useState<ProjectRow[]>([])
  const [keyword, setKeyword] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('全部分类')
  const [editOpen, setEditOpen] = useState(false)
  const [editId, setEditId] = useState(0) // 0 = 新增
  const [name, setName] = useState('')
  const [category, setCategory] = useState('')
  const [price, setPrice] = useState('')
  const [unit, setUnit] = useState('次')
  const [isCardItem, setIsCardItem] = useState(false)
  const [cardTimes, setCardTimes] = useState('1') // A4：每做一次核销掉几次
  const [rateText, setRateText] = useState('5') // 项目提成率 %（0-100），0 = 不提
  const [mode, setMode] = useState<'percent' | 'count'>('percent') // 提成方式：百分比 / 按次
  const [perCountText, setPerCountText] = useState('') // 按次金额（元/次）
  const [errors, setErrors] = useState<Record<string, string>>({})

  // 次卡套餐（挂在项目上）：次数 / 总价 / 有效期。取消勾选 = 停售套餐。
  const [pkgEnabled, setPkgEnabled] = useState(false)
  const [pkgTimes, setPkgTimes] = useState('10')
  const [pkgPrice, setPkgPrice] = useState('')
  const [pkgMonths, setPkgMonths] = useState('12')
  // 编辑时记住原名：改项目名后套餐要跟着改 itemName，否则套餐会失配
  const [originName, setOriginName] = useState('')

  const [templates, setTemplates] = useState<CardTemplate[]>([])
  // 删除项目（软删除）：不再售卖，历史账单/流水/提成保留
  const [delItem, setDelItem] = useState<ProjectRow | null>(null)
  const [delBusy, setDelBusy] = useState(false)

  const load = useCallback(() => {
    projects.list().then(setItems).catch((e) => toast.error(String(e)))
  }, [])
  const loadTemplates = useCallback(() => {
    apiCards.templates().then(setTemplates).catch(() => setTemplates([]))
  }, [])
  useEffect(load, [])
  useEffect(loadTemplates, [])

  // 过滤只依赖 [项目列表, 关键词, 分类] —— 打字时只重算这一次，每行对象引用不变，
  // ProjectRow 的 memo 才能命中。
  const filtered = useMemo(() => {
    const k = keyword.trim()
    return items.filter((s) => {
      const hitKw = !k || s.name.includes(k)
      const hitCat = categoryFilter === '全部分类' || s.category === categoryFilter
      return hitKw && hitCat
    })
  }, [items, keyword, categoryFilter])

  // 某项目当前启用的次卡套餐（内部存储，按 itemName 归属项目）
  const pkgOf = useCallback(
    (itemName: string): CardTemplate | undefined => templates.find((t) => t.itemName === itemName && t.enabled),
    [templates],
  )

  const openNew = () => {
    setEditId(0); setName(''); setCategory(''); setPrice(''); setUnit('次')
    setIsCardItem(false); setCardTimes('1'); setRateText('5')
    setMode('percent'); setPerCountText('')
    setPkgEnabled(false); setPkgTimes('10'); setPkgPrice(''); setPkgMonths('12')
    setOriginName(''); setErrors({})
    setEditOpen(true)
  }

  const openEdit = useCallback((s: ProjectRow) => {
    setEditId(s.id); setName(s.name)
    // 老数据里分类可能是「未分类」这个占位值，回填时还原成空（空就是无）
    setCategory(s.category === '未分类' ? '' : s.category)
    setPrice(String(s.price))
    setUnit(s.unit); setIsCardItem(s.isCardItem)
    setCardTimes(String(s.cardTimes && s.cardTimes > 0 ? s.cardTimes : 1))
    // 提成率 0~1 回填成百分比数字（0.05 → "5"；老数据默认 0.05）
    setRateText(s.commissionRate != null ? String(Math.round(s.commissionRate * 1000) / 10) : '5')
    // 提成方式：默认百分比；count 模式回填按次金额
    setMode(s.commissionMode === 'count' ? 'count' : 'percent')
    setPerCountText(s.commissionPerCount != null && s.commissionPerCount > 0 ? String(s.commissionPerCount) : '')
    setOriginName(s.name)
    // 回填已配置的套餐（没有就给默认值，勾选再填）
    const pkg = pkgOf(s.name)
    setPkgEnabled(!!pkg)
    setPkgTimes(pkg ? String(pkg.times) : '10')
    setPkgPrice(pkg ? String(pkg.price) : '')
    setPkgMonths(pkg ? String(pkg.validMonths) : '12')
    setErrors({})
    setEditOpen(true)
  }, [pkgOf])

  const submit = () => {
    const errs: Record<string, string> = {}
    if (!name.trim()) errs.name = '项目名称不能为空'
    const p = Number(price)
    if (!price.trim() || !Number.isFinite(p) || p < 0) errs.price = '价格必须为不小于 0 的数字'
    const ct = Math.trunc(Number(cardTimes) || 0)
    if (isCardItem && (!Number.isFinite(ct) || ct < 1 || ct > 999)) errs.cardTimes = '核销次数必须是 1–999 的整数'
    // 提成率 %：0-100，留空按 0（不提）处理
    const ratePct = rateText.trim() === '' ? 0 : Number(rateText)
    if (!Number.isFinite(ratePct) || ratePct < 0 || ratePct > 100) errs.rateText = '提成比例须为 0 – 100 之间的数字'
    // 按次金额：仅 count 模式需要，须为 ≥0 的数字
    const perCount = mode === 'count' ? Number(perCountText) : 0
    if (mode === 'count' && (!perCountText.trim() || !Number.isFinite(perCount) || perCount < 0)) {
      errs.perCount = '按次金额须为不小于 0 的数字'
    }
    // 次卡套餐校验（勾了才校验）
    const pt = Math.trunc(Number(pkgTimes) || 0)
    const pp = Number(pkgPrice)
    if (pkgEnabled) {
      if (!Number.isFinite(pt) || pt < 1 || pt > 999) errs.pkgTimes = '套餐次数必须是 1–999 的整数'
      if (!pkgPrice.trim() || !Number.isFinite(pp) || pp <= 0) errs.pkgPrice = '请填写套餐总价（可直接填优惠后的实数）'
    }
    setErrors(errs)
    if (Object.keys(errs).length > 0) return

    const finalName = name.trim()
    // 分类可留空：空就交给后端归入「未分类」；提成率 % → 0~1 小数传后端
    projects
      .save(editId, finalName, category.trim(), p, unit.trim() || '次', isCardItem, true, isCardItem ? ct : 0, ratePct / 100, mode, perCount)
      .then(async () => {
        // 保存项目后同步次卡套餐（内部写一条 itemName=项目名的模板，界面上不暴露「模板」概念）。
        // 店主定：一个项目只留一个套餐 —— 勾选则 upsert 第一条（优先复用启用中的）并停用同项目其余套餐；
        // 取消勾选则把同项目全部套餐停售。一律停用而非删除，保护 member_cards 已售出的卡。
        const owner = editId > 0 ? originName : finalName
        const owned = templates.filter((t) => t.itemName === owner)
        const keep = owned.find((t) => t.enabled) ?? owned[0]
        const mths = Number(pkgMonths) || 12
        const tplName = `${finalName}${pt}次卡`
        if (pkgEnabled && isCardItem) {
          if (keep) await apiCards.saveTemplate(keep.id, tplName, finalName, pt, pp, mths, true)
          else await apiCards.saveTemplate(0, tplName, finalName, pt, pp, mths, true)
          for (const t of owned) {
            if (t.id !== keep?.id && t.enabled) await apiCards.saveTemplate(t.id, t.name, finalName, t.times, t.price, t.validMonths, false)
          }
        } else {
          for (const t of owned) {
            if (t.enabled) await apiCards.saveTemplate(t.id, t.name, finalName, t.times, t.price, t.validMonths, false)
          }
        }
      })
      .then(() => {
        toast.success(editId === 0 ? `项目「${finalName}」已创建` : `项目「${finalName}」已保存`)
        setEditOpen(false)
        load()
        loadTemplates()
      })
      .catch((e) => toast.error(String(e)))
  }

  const toggle = useCallback((s: ProjectRow) => {
    projects
      .setEnabled(s.id, !s.enabled)
      .then(() => { load() })
      .catch((e) => toast.error(String(e)))
  }, [load])

  // 删除项目：软删除（历史订单/流水/提成不删），同时该项目名下的次卡套餐停售
  const doDelete = () => {
    if (!delItem) return
    setDelBusy(true)
    projects
      .remove(delItem.id)
      .then(() => {
        setDelItem(null)
        load()
        loadTemplates()
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setDelBusy(false))
  }

  return (
    <PageFill minWidth={1080}>
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            项目列表
          </h1>
          <p className="text-xs text-muted-foreground mt-1">勾选「卖次卡套餐」的项目可在收银台售出多次</p>
        </div>
        <Button variant="primary" onClick={openNew}>
          <Plus size={14} /> 新增项目
        </Button>
      </div>

      <Card className="p-3 shrink-0">
        <div className="flex items-center gap-2.5">
          <div className="relative w-64">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              placeholder="搜索项目名称"
              className="pl-8"
              aria-label="搜索项目"
            />
          </div>
          <Select value={categoryFilter} onChange={(e) => setCategoryFilter(e.target.value)} aria-label="分类筛选">
            <option>全部分类</option>
            {categories.map((c) => <option key={c}>{c}</option>)}
          </Select>
        </div>
      </Card>

      <SectionCard className="page-body flex flex-col" title={`项目（${filtered.length} 项）`} bodyClassName="p-0 flex-1 min-h-0 flex flex-col">
        <DataTable
          cols={PROJECT_COLS}
          rows={filtered}
          rowKey={(s) => s.id}
          renderRow={(s) => (
            <ProjectRow
              key={s.id}
              s={s}
              pkg={pkgOf(s.name)}
              onEdit={openEdit}
              onToggle={toggle}
              onDelete={setDelItem}
            />
          )}
          fill
          empty={<div className="py-8 text-center text-xs text-muted-foreground">还没有项目，点「新增项目」建立第一个</div>}
        />
      </SectionCard>

      {editOpen ? (
        <Modal
          title={editId === 0 ? '新增项目' : '编辑项目'}
          onClose={() => setEditOpen(false)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setEditOpen(false)}>取消</Button>
              <Button variant="primary" onClick={submit}>保存</Button>
            </>
          }
        >
          <div className="grid grid-cols-2 gap-x-4 gap-y-3.5">
            <Field label="项目名称" required error={errors.name}>
              <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="如 洗剪吹" />
            </Field>
            <Field label="分类" hint="可直接输入，留空即「未分类」">
              <Input
                value={category}
                onChange={(e) => setCategory(e.target.value)}
                list="mm-project-categories"
                placeholder="留空 = 未分类"
                aria-label="项目分类"
              />
              <datalist id="mm-project-categories">
                {categories.map((c) => <option key={c} value={c} />)}
              </datalist>
            </Field>
            <Field label="单次价格（元）" required error={errors.price}>
              <Input value={price} onChange={(e) => setPrice(e.target.value)} placeholder="38" inputMode="decimal" className="tabular-nums" />
            </Field>
            <Field label="计价单位">
              <Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="次 / 件" />
            </Field>
            <Field label="提成方式" error={errors.perCount} hint="百分比按价格提成；按次给固定金额">
              <div className="flex items-center gap-4">
                <label className="flex items-center gap-1.5 text-sm cursor-pointer">
                  <input type="radio" checked={mode === 'percent'} onChange={() => setMode('percent')} className="accent-[hsl(var(--primary))]" />
                  按价格百分比
                </label>
                <label className="flex items-center gap-1.5 text-sm cursor-pointer">
                  <input type="radio" checked={mode === 'count'} onChange={() => setMode('count')} className="accent-[hsl(var(--primary))]" />
                  按次卡次数
                </label>
              </div>
              {mode === 'count' ? (
                <div className="mt-2 flex items-center gap-2">
                  <span className="text-sm text-muted-foreground">会员每核销一次提成</span>
                  <Input
                    value={perCountText}
                    onChange={(e) => setPerCountText(e.target.value)}
                    placeholder="如 10"
                    inputMode="decimal"
                    className="tabular-nums w-28"
                    aria-label="按次金额"
                  />
                  <span className="text-sm text-muted-foreground">元/次</span>
                </div>
              ) : (
                <div className="mt-2 flex items-center gap-2">
                  <Input
                    value={rateText}
                    onChange={(e) => setRateText(e.target.value)}
                    placeholder="5"
                    inputMode="decimal"
                    className="tabular-nums w-28"
                    aria-label="提成比例百分比"
                  />
                  <span className="text-sm text-muted-foreground">%（留空或 0 = 不提）</span>
                </div>
              )}
            </Field>
            <div className="col-span-2">
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={isCardItem} onChange={(e) => setIsCardItem(e.target.checked)} className="accent-[hsl(var(--primary))]" />
                <Package size={14} className="text-muted-foreground" /> 支持次卡抵扣
              </label>
            </div>
            {isCardItem ? (
              <Field label="每次核销次数" required error={errors.cardTimes} hint="做一次这个项目扣掉几次，默认 1">
                <Input value={cardTimes} onChange={(e) => setCardTimes(e.target.value)} inputMode="numeric" className="tabular-nums" />
              </Field>
            ) : null}

            {/* 次卡套餐：直接挂在项目上，不再单独做「卡模板」 */}
            {isCardItem ? (
              <div className="col-span-2 border border-border rounded-[var(--radius-sm)] p-3 space-y-3 bg-muted/30">
                <label className="flex items-center gap-2 text-sm font-medium">
                  <input
                    type="checkbox"
                    checked={pkgEnabled}
                    onChange={(e) => setPkgEnabled(e.target.checked)}
                    className="accent-[hsl(var(--primary))]"
                  />
                  <Ticket size={14} className="text-primary" />
                  这个项目同时卖次卡套餐（会员一次性买多次）
                </label>
                {pkgEnabled ? (
                  <>
                    <div className="grid grid-cols-3 gap-3">
                      <Field label="套餐次数" required error={errors.pkgTimes}>
                        <Input value={pkgTimes} onChange={(e) => setPkgTimes(e.target.value)} placeholder="10" inputMode="numeric" className="tabular-nums" />
                      </Field>
                      <Field label="套餐总价（元）" required error={errors.pkgPrice} hint="直接填，不用算单价">
                        <Input value={pkgPrice} onChange={(e) => setPkgPrice(e.target.value)} placeholder="300" inputMode="decimal" className="tabular-nums" />
                      </Field>
                      <Field label="有效期（月）" required>
                        <Input value={pkgMonths} onChange={(e) => setPkgMonths(e.target.value)} inputMode="numeric" className="tabular-nums" />
                      </Field>
                    </div>
                    {Number(pkgTimes) > 0 && Number(pkgPrice) > 0 ? (
                      <p className="text-xs text-muted-foreground bg-card rounded-[var(--radius-sm)] px-3 py-2">
                        折合单次 <b className="text-primary">¥{(Number(pkgPrice) / Number(pkgTimes)).toFixed(2)}</b>
                        ，项目原价 ¥{fmtMoney(Number(price) || 0)} —— 套餐比单次省 ¥{Math.max(0, (Number(price) || 0) - Number(pkgPrice) / Number(pkgTimes)).toFixed(2)}/次。
                      </p>
                    ) : null}
                    <p className="text-[11px] text-muted-foreground flex items-start gap-1.5">
                      <Tags size={12} className="shrink-0 mt-px" />
                      取消勾选 = 停售套餐；已售出的卡不受影响，仍可核销。
                    </p>
                  </>
                ) : null}
              </div>
            ) : null}
          </div>
        </Modal>
      ) : null}

      {delItem ? (
        <Modal
          title={`删除项目 · ${delItem.name}`}
          onClose={() => setDelItem(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDelItem(null)}>取消</Button>
              <Button variant="danger" onClick={doDelete} disabled={delBusy}>
                <Trash2 size={13} /> {delBusy ? '删除中…' : '确认删除'}
              </Button>
            </>
          }
        >
          <p className="text-sm leading-relaxed">
            删除后「{delItem.name}」会从收银台与项目列表消失，该项目名下的次卡套餐同时停售。
          </p>
          <p className="text-xs text-muted-foreground mt-2 leading-relaxed">
            已经产生的<b className="text-foreground">账单、流水与提成都不会删除</b>——历史单据仍按原项目名留痕，各项目互相独立；
            已售出的次卡也不受影响、仍可继续核销。若只是临时下架、以后还要卖，用「停用」即可。
          </p>
        </Modal>
      ) : null}
    </PageFill>
  )
}

/**
 * 单行记忆化：搜索时 filtered 变的是「哪些行显示」，已显示行的对象引用不变；
 * pkg 由 templates.find 取到，也是数组里的真实对象、引用稳定，所以 memo 命中、不重渲。
 */
const ProjectRow = memo(function ProjectRow({
  s,
  pkg,
  onEdit,
  onToggle,
  onDelete,
}: {
  s: ProjectRow
  pkg?: CardTemplate
  onEdit: (s: ProjectRow) => void
  onToggle: (s: ProjectRow) => void
  onDelete: (s: ProjectRow) => void
}) {
  return (
    <tr className="even:bg-muted/40">
      <td className={cx(tdCls, 'font-medium')}>
        <div className="truncate" title={s.name}>{s.name}</div>
      </td>
      <td className={tdCls}>
        {s.category && s.category !== '未分类'
          ? <Badge tone="muted">{s.category}</Badge>
          : <span className="text-muted-foreground/60 text-xs">未分类</span>}
      </td>
      <td className={cx(tdCls, numCls)}>¥{s.price.toFixed(2)}</td>
      <td className={cx(tdCls, 'text-muted-foreground')}>/ {s.unit}</td>
      <td className={cx(tdCls, 'text-right tabular-nums')}>
        {s.commissionMode === 'count' ? (
          <span className="text-foreground">按次 ¥{(s.commissionPerCount ?? 0).toFixed(2)}/次</span>
        ) : s.commissionRate && s.commissionRate > 0 ? (
          <span className="text-foreground">{Math.round(s.commissionRate * 1000) / 10}%</span>
        ) : (
          <span className="text-muted-foreground/60">不提</span>
        )}
      </td>
      <td className={tdCls}>
        {s.isCardItem ? (
          <Badge tone="primary"><Ticket size={10} className="mr-1" />扣 {s.cardTimes && s.cardTimes > 0 ? s.cardTimes : 1} 次</Badge>
        ) : (
          <span className="text-muted-foreground/60 text-xs">—</span>
        )}
      </td>
      <td className={tdCls}>
        {pkg ? (
          <div className="truncate" title={`${pkg.times}次 ¥${fmtMoney(pkg.price)}（折合 ¥${(pkg.price / pkg.times).toFixed(2)}/次 · ${pkg.validMonths}个月有效期）`}>
            <b className="text-primary">{pkg.times}次</b> ¥{fmtMoney(pkg.price)}
            <span className="text-muted-foreground"> · 折合¥{(pkg.price / pkg.times).toFixed(2)}/次</span>
          </div>
        ) : (
          <span className="text-muted-foreground/60 text-xs">未配置</span>
        )}
      </td>
      <td className={tdCls}>
        {s.enabled ? <Badge tone="primary">启用</Badge> : <Badge tone="muted">停用</Badge>}
      </td>
      <td className={cx(tdCls, 'text-right')}>
        <span className="inline-flex gap-1">
          <Button variant="ghost" size="sm" onClick={() => onEdit(s)}>编辑</Button>
          <Button
            variant="ghost"
            size="sm"
            className={cx(s.enabled && 'text-muted-foreground')}
            onClick={() => onToggle(s)}
          >
            {s.enabled ? '停用' : '启用'}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="text-destructive hover:text-destructive"
            onClick={() => onDelete(s)}
          >
            删除
          </Button>
        </span>
      </td>
    </tr>
  )
})
