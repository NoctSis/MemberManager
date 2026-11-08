import { useState } from 'react'
import { Minus, Plus, Trash2, Ticket, AlertCircle, Pause, Eraser, CheckCircle2, UserCheck, ChevronDown, ChevronUp } from 'lucide-react'
import { Button, Badge, cx, EmptyState } from '@/components/common/ui'
import { fmtMoney } from '@/data/mock'
import type { ServiceItem } from '@/data/mock'
import type { PosStaffOption, PosMember, PosCard } from '@/lib/api'

export interface CartLine {
  item: ServiceItem
  qty: number
  byCard: boolean // 本行是否用次卡抵扣
}

/**
 * 分配到某项目的可用次卡（先到期先用）。
 * 返回 [{card, use}] 列表——按到期日升序，把本行需要的 useTotal 次分摊到各张卡上。
 * 空 expireDate（长期有效）视为排到最后。
 */
function allocateCards(cards: PosCard[], itemName: string, useTotal: number): { card: PosCard; use: number }[] {
  const applicable = cards
    .filter((c) => c.itemName && c.itemName === itemName && c.remain > 0)
    .sort((a, b) => {
      const ae = (a.expireDate || '').trim()
      const be = (b.expireDate || '').trim()
      if (!ae && !be) return 0
      if (!ae) return 1 // 无到期日 = 长期有效，排最后
      if (!be) return -1
      return ae < be ? -1 : ae > be ? 1 : 0
    })
  const out: { card: PosCard; use: number }[] = []
  let need = useTotal
  for (const c of applicable) {
    if (need <= 0) break
    const take = Math.min(c.remain, need)
    out.push({ card: c, use: take })
    need -= take
  }
  return out
}

export function CartPanel({
  lines,
  member,
  cards,
  payBalance,
  payCash,
  staffId,
  staffOptions,
  onStaffChange,
  remainOf,
  onQty,
  onToggleCard,
  onRemove,
  onPayBalanceChange,
  onPayCashChange,
  onHold,
  onClear,
  onCheckout,
}: {
  lines: CartLine[]
  member: PosMember | null
  /** 会员可用次卡明细（展开看「哪张卡被扣几次」时要用） */
  cards: PosCard[]
  payBalance: string
  payCash: string
  staffId: number | null
  staffOptions: PosStaffOption[]
  onStaffChange: (id: number | null) => void
  /** 某项目在会员名下还剩几次（按项目判断，不看总次数） */
  remainOf: (itemName: string) => number
  onQty: (idx: number, delta: number) => void
  onToggleCard: (idx: number) => void
  onRemove: (idx: number) => void
  onPayBalanceChange: (v: string) => void
  onPayCashChange: (v: string) => void
  onHold: () => void
  onClear: () => void
  onCheckout: () => void
}) {
  const subtotal = lines.reduce((s, l) => s + l.item.price * l.qty, 0)
  const cardCovered = lines.filter((l) => l.byCard).reduce((s, l) => s + l.item.price * l.qty, 0)
  const needPay = subtotal - cardCovered
  const balanceUsed = Math.min(Number(payBalance) || 0, member?.balance ?? 0)
  const cashUsed = Number(payCash) || 0
  const received = balanceUsed + cashUsed
  const change = received - needPay

  // 展开哪一行看「这次核销从哪张卡扣」；同一时间只允许展开一行，避免面板太挤
  const [expandedRowIdx, setExpandedRowIdx] = useState<number | null>(null)
  const toggleRowExpand = (idx: number) => setExpandedRowIdx((prev) => (prev === idx ? null : idx))

  const errors: string[] = []
  if ((Number(payBalance) || 0) > (member?.balance ?? 0)) {
    errors.push(`余额不足：${member?.name ?? ''} 当前余额 ¥${fmtMoney(member?.balance ?? 0)}，本项最多支付 ¥${fmtMoney(member?.balance ?? 0)}`)
  }
  if (lines.length > 0 && received + 0.001 < needPay) {
    errors.push(`实收不足：还差 ¥${fmtMoney(needPay - received)}，请补全「余额 / 现金」金额`)
  }

  return (
    <div className="w-[312px] min-w-[280px] shrink-0 bg-card border border-border rounded-[var(--radius-md)] shadow-[var(--elev-ring)] flex flex-col overflow-hidden">
      {/* 账单行 */}
      <div className="flex items-center justify-between px-4 h-11 border-b border-border shrink-0">
        <h3 className="text-sm font-semibold" style={{ fontFamily: 'var(--font-display)' }}>
          当前账单 <span className="text-muted-foreground font-normal text-xs">（{lines.length} 项）</span>
        </h3>
        <Button variant="ghost" size="sm" onClick={onClear} disabled={lines.length === 0}>
          <Eraser size={12} /> 清空
        </Button>
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto px-2 py-2">
        {lines.length === 0 ? (
          <EmptyState title="账单为空" desc="" />
        ) : (
          lines.map((l, idx) => {
            const isExpanded = expandedRowIdx === idx
            // 次卡抵扣行：算出「本次从哪几张卡各扣几次」（先到期先用），展开时给店主看
            const alloc = l.byCard ? allocateCards(cards, l.item.name, l.qty) : []
            return (
              <div
                key={`${l.item.id}-${l.byCard}`}
                className={cx(
                  'rounded-[var(--radius-sm)] mb-1 border overflow-hidden',
                  l.byCard ? 'border-primary/40 bg-primary/5' : 'border-transparent hover:bg-accent/60',
                )}
              >
                <div className="flex items-center gap-2 px-2 py-2">
                  <div className="flex-1 min-w-0 leading-tight">
                    <p className="text-[13px] font-medium truncate flex items-center gap-1.5">
                      {l.item.name}
                      {l.byCard ? <Badge tone="primary">次卡 −{l.qty}</Badge> : null}
                    </p>
                    <p className="text-[11px] text-muted-foreground mt-0.5 tabular-nums">
                      ¥{l.item.price.toFixed(2)} / {l.item.unit}
                      {l.item.isCardItem && member && remainOf(l.item.name) > 0 ? (
                        <button
                          onClick={() => onToggleCard(idx)}
                          title={`该会员此项目还剩 ${remainOf(l.item.name)} 次`}
                          className={cx(
                            'ml-2 inline-flex items-center gap-0.5 px-1.5 py-0.5 rounded-[var(--radius-pill)] border text-[10px] transition-colors align-middle',
                            l.byCard
                              ? 'border-primary bg-primary text-primary-foreground'
                              : 'border-input text-muted-foreground hover:border-primary hover:text-primary',
                          )}
                        >
                          <Ticket size={9} /> {l.byCard ? '已用次卡' : `可用次卡 ${remainOf(l.item.name)}`}
                        </button>
                      ) : null}
                    </p>
                  </div>
                  <div className="flex items-center gap-0.5 shrink-0">
                    <button
                      onClick={() => onQty(idx, -1)}
                      aria-label={`减少${l.item.name}`}
                      className="w-6 h-6 rounded-[var(--radius-sm)] border border-input text-muted-foreground hover:text-foreground hover:border-primary/50 flex items-center justify-center transition-colors"
                    >
                      <Minus size={11} />
                    </button>
                    <span className="w-6 text-center text-[13px] font-semibold tabular-nums">{l.qty}</span>
                    <button
                      onClick={() => onQty(idx, 1)}
                      aria-label={`增加${l.item.name}`}
                      className="w-6 h-6 rounded-[var(--radius-sm)] border border-input text-muted-foreground hover:text-foreground hover:border-primary/50 flex items-center justify-center transition-colors"
                    >
                      <Plus size={11} />
                    </button>
                  </div>
                  <p className="w-16 text-right text-[13px] font-semibold tabular-nums shrink-0">
                    {l.byCard ? (
                      <span className="text-primary text-[11px]">抵扣</span>
                    ) : `¥${fmtMoney(l.item.price * l.qty)}`}
                  </p>
                  {l.byCard ? (
                    <button
                      onClick={() => toggleRowExpand(idx)}
                      aria-expanded={isExpanded}
                      aria-label={isExpanded ? '折叠次卡明细' : '展开次卡明细'}
                      title={isExpanded ? '折叠' : '展开：本次扣哪张卡'}
                      className="text-muted-foreground hover:text-primary transition-colors shrink-0 p-0.5"
                    >
                      {isExpanded ? <ChevronUp size={13} /> : <ChevronDown size={13} />}
                    </button>
                  ) : null}
                  <button
                    onClick={() => onRemove(idx)}
                    aria-label={`删除${l.item.name}`}
                    className="text-muted-foreground/50 hover:text-destructive transition-colors shrink-0 p-0.5"
                  >
                    <Trash2 size={13} />
                  </button>
                </div>

                {/* 次卡行展开：本次核销从哪张卡扣几次（先到期先用），并标注单次金额口径 */}
                {l.byCard && isExpanded ? (
                  <div className="border-t border-primary/30 bg-background/40 px-2.5 py-2 space-y-1.5">
                    <p className="text-[11px] text-muted-foreground flex items-center justify-between">
                      <span>本次核销 <b className="text-primary tabular-nums">{l.qty}</b> 次 · 按项目价抵扣 ¥{fmtMoney(l.item.price * l.qty)}</span>
                      <span className="tabular-nums">合计 ¥{fmtMoney(alloc.reduce((s, a) => s + a.use * a.card.unitPrice, 0))}（已预付）</span>
                    </p>
                    {alloc.length === 0 ? (
                      <p className="text-[11px] text-destructive">该会员此项目已无可用次卡</p>
                    ) : (
                      <ul className="space-y-1">
                        {alloc.map((a, i) => (
                          <li
                            key={a.card.id}
                            className="text-[11px] border border-border rounded-[var(--radius-sm)] px-2 py-1.5 bg-card"
                          >
                            <div className="flex items-center justify-between gap-1">
                              <span className="font-medium truncate">
                                <span className="text-muted-foreground mr-1">{i + 1}.</span>
                                {a.card.cardName}
                              </span>
                              <span className="text-primary font-semibold shrink-0 tabular-nums">扣 {a.use} 次</span>
                            </div>
                            <div className="flex items-center justify-between text-muted-foreground mt-0.5 tabular-nums">
                              <span>单次 ¥{fmtMoney(a.card.unitPrice)} × {a.use}</span>
                              <span>
                                剩 {a.card.remain - a.use}/{a.card.total}
                                {a.card.expireDate ? ` · 到期 ${a.card.expireDate}` : ' · 长期'}
                              </span>
                            </div>
                          </li>
                        ))}
                      </ul>
                    )}
                    <p className="text-[10px] text-muted-foreground leading-relaxed">
                      单次金额口径：结账按<b>项目价</b>计「次卡抵扣」（¥{fmtMoney(l.item.price * l.qty)}）；
                      卡次单价是会员购卡时的实付折算（已预付，结账不再扣）。
                    </p>
                  </div>
                ) : null}
              </div>
            )
          })
        )}
      </div>

      {/* 结算区 */}
      <div className="border-t border-border px-4 py-3 shrink-0 space-y-2.5 bg-card">
        {/* 经办人：这一单是谁经手的（开单/做项目/提成都记在 TA 名下） */}
        <label className="block">
          <span className="flex items-center gap-1 text-[11px] text-muted-foreground mb-1">
            <UserCheck size={11} className="text-primary" /> 经办人
          </span>
          <select
            value={staffId ?? ''}
            onChange={(e) => onStaffChange(e.target.value ? Number(e.target.value) : null)}
            aria-label="经办人"
            className="h-8 w-full px-2 text-[13px] rounded-[var(--radius-sm)] border border-input bg-background text-foreground focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow"
          >
            {staffOptions.length === 0 ? (
              <option value="">读取中…</option>
            ) : (
              staffOptions.map((s) => (
                <option key={s.id} value={s.id}>
                  {/* 姓名可以重复，登录账号唯一 —— 同名员工靠「·登录账号」区分（店主 11-05） */}
                  {s.loginName ? `${s.name}（${s.role}·${s.loginName}）` : `${s.name}（${s.role}）`}
                </option>
              ))
            )}
          </select>
        </label>

        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">小计</span>
          <span className="tabular-nums font-medium">¥{fmtMoney(subtotal)}</span>
        </div>
        {cardCovered > 0 ? (
          <div className="flex items-center justify-between text-sm">
            <span className="text-muted-foreground">次卡抵扣</span>
            <span className="tabular-nums font-medium text-primary">−¥{fmtMoney(cardCovered)}</span>
          </div>
        ) : null}
        <div className="flex items-center justify-between">
          <span className="text-sm font-semibold">应收</span>
          <span className="text-xl font-bold text-primary tabular-nums" style={{ fontFamily: 'var(--font-display)' }}>
            ¥{fmtMoney(needPay)}
          </span>
        </div>

        <div className="grid grid-cols-2 gap-2">
          <label className="block">
            <span className="block text-[11px] text-muted-foreground mb-1">
              余额支付（可用 ¥{fmtMoney(member?.balance ?? 0)}）
            </span>
            <input
              value={payBalance}
              onChange={(e) => onPayBalanceChange(e.target.value)}
              placeholder="0.00"
              inputMode="decimal"
              aria-label="余额支付金额"
              className={cx(
                'h-8 w-full px-2.5 text-sm tabular-nums rounded-[var(--radius-sm)] border bg-background text-foreground',
                'focus:outline-none focus:shadow-[var(--focus-ring)] transition-shadow',
                (Number(payBalance) || 0) > (member?.balance ?? 0) ? 'border-destructive' : 'border-input focus:border-primary',
              )}
            />
          </label>
          <label className="block">
            <span className="block text-[11px] text-muted-foreground mb-1">现金支付</span>
            <input
              value={payCash}
              onChange={(e) => onPayCashChange(e.target.value)}
              placeholder="0.00"
              inputMode="decimal"
              aria-label="现金支付金额"
              className="h-8 w-full px-2.5 text-sm tabular-nums rounded-[var(--radius-sm)] border border-input bg-background text-foreground focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow"
            />
          </label>
        </div>

        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">找零</span>
          <span className={cx('tabular-nums font-semibold', change > 0.001 ? 'text-primary' : 'text-muted-foreground')}>
            ¥{fmtMoney(Math.max(0, change))}
          </span>
        </div>

        {errors.length > 0 ? (
          <div className="rounded-[var(--radius-sm)] bg-destructive/8 border border-destructive/30 px-2.5 py-2 space-y-1" role="alert">
            {errors.map((e) => (
              <p key={e} className="text-[11px] text-destructive flex items-start gap-1.5">
                <AlertCircle size={13} className="shrink-0 mt-px" />
                {e}
              </p>
            ))}
          </div>
        ) : null}

        <div className="grid grid-cols-3 gap-2 pt-0.5">
          <Button variant="secondary" onClick={onHold} disabled={lines.length === 0} className="col-span-1">
            <Pause size={13} /> 挂单
          </Button>
          <Button
            variant="primary"
            className="col-span-2"
            disabled={lines.length === 0 || errors.length > 0}
            onClick={onCheckout}
          >
            <CheckCircle2 size={15} /> 结账（¥{fmtMathSafe(needPay)}）
          </Button>
        </div>
      </div>
    </div>
  )
}

// 结账按钮金额展示兜底，避免 NaN 出现在按钮上
function fmtMathSafe(n: number): string {
  return Number.isFinite(n) ? fmtMoney(n) : '0.00'
}
