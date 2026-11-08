import { PageFill } from '@/components/common/PageLayout'
import { useEffect, useState } from 'react'
import { Search, UserRound, Ticket, Pause, Play, Printer, CheckCircle2, ChevronDown, ChevronUp, UserCheck, UserPlus, Wallet } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Card, Badge, Input, Modal, cx } from '@/components/common/ui'
import { NewMemberDialog } from '@/components/members/MemberDialogs'
import { ServiceGrid } from '@/components/pos/ServiceGrid'
import { CartPanel } from '@/components/pos/CartPanel'
import type { CartLine } from '@/components/pos/CartPanel'
import { SellCardDialog } from '@/components/pos/SellCardDialog'
import { RechargeDialog } from '@/components/members/RechargeDialog'
import { canModule } from '@/lib/perm'
import { pos, system as apiSystem, settings as apiSettings, levels as apiLevels } from '@/lib/api'
import type { PosCard, PosStaffOption } from '@/lib/api'
import { getStaff, getLastOperatorId, setLastOperatorId } from '@/lib/session'
import { fmtMoney } from '@/data/mock'
import type { ServiceItem, Member, MemberLevel } from '@/data/mock'
import type { PosMember } from '@/lib/api'

interface HeldEntry {
  id: number
  orderNo: string
  memberName: string
  amount: number
  itemCount: number
  time: string
  staffName: string
}

export function PosPage() {
  const [items, setItems] = useState<ServiceItem[]>([])
  const [memberKw, setMemberKw] = useState<string>('')
  const [member, setMember] = useState<PosMember | null>(null)
  const [lines, setLines] = useState<CartLine[]>([])
  const [payBalance, setPayBalance] = useState<string>('')
  const [payCash, setPayCash] = useState<string>('')
  const [held, setHeld] = useState<HeldEntry[]>([])
  const [shift, setShift] = useState<{ Revenue: number; Count: number; BalanceRatio: number; RefundPending: number } | null>(null)
  // 本次操作员（店主可指定是谁经手的这一单；空=跟随登录账号）
  const [staffOptions, setStaffOptions] = useState<PosStaffOption[]>([])
  const [staffId, setStaffId] = useState<number | null>(null)
  // 会员可用次卡明细（展开看是哪几张卡，不用再翻会员档案）
  const [cards, setCards] = useState<PosCard[]>([])
  const [cardsOpen, setCardsOpen] = useState<boolean>(false)
  // 收银台现场建档（复用会员管理的同一套建档表单，含经办人/生日/标签；等级自动判定不选）
  const [newMemberOpen, setNewMemberOpen] = useState<boolean>(false)
  // 售卡（交易动作，主场在收银台）：卖给会员「多次套餐次卡」，之后核销只扣次数
  const [sellOpen, setSellOpen] = useState<boolean>(false)
  // 会员充值：收银台直接给当前选中的会员充值，不用跳去会员管理页
  const [rechargeOpen, setRechargeOpen] = useState<boolean>(false)
  const [levels, setLevels] = useState<MemberLevel[]>([])
  // 取单结账时记住原挂单 id，保证 H 单号重写而不是新开 D 单
  const [heldOrderId, setHeldOrderId] = useState<number | null>(null)
  const [paidModal, setPaidModal] = useState<boolean>(false)
  const [paidResult, setPaidResult] = useState<{ OrderNo: string; Subtotal: number; CardCovered: number; Receivable: number; BalanceUsed: number; BalanceBack: number; CashIn: number; Change: number; CardUsedTimes: number } | null>(null)
  // 本次结账的经手人（结账那一刻快照）——小票/成功弹窗用它，**不能用登录账号**（一店常共用一个店长账号，登录人 ≠ 经单人）
  const [paidOperator, setPaidOperator] = useState<string>('')
  // 小票打印：店名 / 尾部备注 / 是否启用，都从库读（正式接打印机走系统默认）
  const [shopName, setShopName] = useState<string>('')
  const [receiptNote, setReceiptNote] = useState<string>('')
  const [printEnabled, setPrintEnabled] = useState<boolean>(false)
  useEffect(() => {
    apiSystem.status().then((s) => setShopName(s.ShopName || '')).catch(() => {})
    apiSettings.getAll().then((d) => {
      setPrintEnabled(d.receipt_enabled === '1')
      if (d.receipt_note) setReceiptNote(d.receipt_note)
      if (d.shop_name) setShopName(d.shop_name)   // 店名兜底：从 settings 直取
    }).catch(() => {})
    // 等级制度：建档弹窗里实时预览「这次充多少会到几档」
    apiLevels.list().then(setLevels).catch(() => setLevels([]))
  }, [])

  // 一键直打默认打印机：走 WebView2 原生打印（system.Print → CoreWebView2.PrintAsync），
  // 不弹对话框（规避系统对话框被抢焦点后自动关闭）；没打印机时后端自动生成小票 PDF 并打开。
  const printReceipt = () => {
    apiSystem.print().catch((e) => toast.error('打印失败：' + String(e)))
  }

  /** 重拉挂单列表。挂单/取单/结账/放弃取单后都要叫一次，别只改本地数组——本地改了库没对上，
   *  侧栏角标和列表就会各说各话（10-05 店主报「挂单数字不消」就是这么来的）。 */
  const refreshHeld = () => {
    pos
      .held()
      .then(setHeld)
      .catch(() => setHeld([]))
  }
  /** 通知外壳重算挂单角标（App.tsx 监听 mm:held） */
  const pingHeld = () => window.dispatchEvent(new CustomEvent('mm:held'))

  const loadAux = () => {
    pos
      .items()
      .then(setItems)
      .catch((e) => toast.error(String(e)))
    refreshHeld()
    pos
      .snapshot()
      .then(setShift)
      .catch(() => setShift(null))
    pos
      .staffOptions()
      .then(setStaffOptions)
      .catch(() => setStaffOptions([]))
  }
  useEffect(loadAux, [])

  // 默认带上一次开单的人（跨页面/重启都记得），其次登录本人，最后列表第一位。
  // 不能每次都回落登录账号：一家店常共用一个店长账号，登录人 ≠ 经单人。
  useEffect(() => {
    if (staffOptions.length === 0) return
    if (staffId != null) return // 已手动选过就不再覆盖
    const last = getLastOperatorId()
    const hitLast = last > 0 ? staffOptions.find((o) => o.id === last) : undefined
    if (hitLast) {
      setStaffId(hitLast.id)
      return
    }
    const me = getStaff()
    const hit = me ? staffOptions.find((o) => o.id === me.id) : undefined
    setStaffId((hit ?? staffOptions[0]).id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [staffOptions])

  // 记住「本次开单是谁经手」：会员管理那边建档时把默认经办人带成这个人，
  // 而不是永远回落登录账号。只存界面偏好，不写库、不进操作日志。
  useEffect(() => {
    if (staffId != null) setLastOperatorId(staffId)
  }, [staffId])

  // 会员一换就刷新其可用次卡明细
  useEffect(() => {
    if (!member) {
      setCards([])
      setCardsOpen(false)
      return
    }
    pos
      .memberCards(member.id)
      .then(setCards)
      .catch(() => setCards([]))
  }, [member?.id])

  // 开单会员搜索（防抖 250ms）
  const [searchResults, setSearchResults] = useState<PosMember[]>([])
  // A1 键盘化：↓/↑ 在下拉里移动高亮、Enter 选中、Esc 清空；-1 = 未选中（Enter 时取第一项）
  const [hlIdx, setHlIdx] = useState(-1)
  useEffect(() => { setHlIdx(-1) }, [searchResults])
  useEffect(() => {
    const kw = memberKw.trim()
    if (!kw) {
      setSearchResults([])
      return
    }
    const t = window.setTimeout(() => {
      pos
        .searchMembers(kw)
        .then(setSearchResults)
        .catch(() => setSearchResults([]))
    }, 250)
    return () => window.clearTimeout(t)
  }, [memberKw])

  const pickMember = (m: PosMember) => {
    setMember(m)
    setMemberKw('')
    setHlIdx(-1)
  }

  /** A1 键盘化：输入框上的键盘操作——↓/↑ 移动高亮、Enter 选中（默认第一项）、Esc 清空关下拉 */
  const onSearchKey = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (searchResults.length === 0) return
      e.preventDefault()
      setHlIdx((i) => {
        const n = searchResults.length
        if (e.key === 'ArrowDown') return i < 0 ? 0 : (i + 1) % n
        return i < 0 ? n - 1 : (i - 1 + n) % n
      })
    } else if (e.key === 'Enter') {
      if (searchResults.length === 0) return
      e.preventDefault()
      pickMember(searchResults[hlIdx >= 0 && hlIdx < searchResults.length ? hlIdx : 0])
    } else if (e.key === 'Escape') {
      setMemberKw('')
      setHlIdx(-1)
    }
  }

  /** 某个项目在会员名下还剩多少次（同一项目可能有多张卡，累加） */
  const remainOf = (itemName: string): number =>
    cards.filter((c) => c.itemName && c.itemName === itemName).reduce((s, c) => s + c.remain, 0)

  // ---------- 收银台直接建档（店主 09-25：不用跳到会员页，现场办卡开单） ----------
  // 建档成功：把新会员挂到当前这一单上，接着选项目结账
  const onNewMemberCreated = (m: Member) => {
    setMember({
      id: m.id, cardNo: m.cardNo, name: m.name, phone: m.phone,
      balance: m.balance, points: m.points, cardRemain: m.cardRemain,
    } as PosMember)
    setNewMemberOpen(false)
  }

  // 售卡成功：若卖给的正是当前这单的会员，刷新 TA 的次卡余次与明细（顶部徽标/展开面板立刻更新）
  const onCardSold = () => {
    if (!member) return
    pos.getMember(member.id).then(setMember).catch(() => {})
    pos.memberCards(member.id).then(setCards).catch(() => setCards([]))
  }

  const pickItem = (item: ServiceItem) => {
    setLines((prev) => {
      const idx = prev.findIndex((l) => l.item.id === item.id && !l.byCard)
      if (idx >= 0) {
        const next = [...prev]
        next[idx] = { ...next[idx], qty: next[idx].qty + 1 }
        return next
      }
      // 店主定则：次卡项目 + 会员「这个项目」还有剩余 → 加行默认用次卡。
      // 按项目判断，不再看会员的总剩余次数（每张卡金额/次数都不同，不能混成一个数）。
      const byCard = !!(item.isCardItem && member && remainOf(item.name) > 0)
      return [...prev, { item, qty: 1, byCard }]
    })
  }

  const changeQty = (idx: number, delta: number) => {
    setLines((prev) => {
      const next = [...prev]
      const qty = next[idx].qty + delta
      if (qty <= 0) next.splice(idx, 1)
      else next[idx] = { ...next[idx], qty }
      return next
    })
  }

  const toggleCard = (idx: number) => {
    setLines((prev) => {
      const next = [...prev]
      next[idx] = { ...next[idx], byCard: !next[idx].byCard }
      return next
    })
  }

  const removeLine = (idx: number) => setLines((prev) => prev.filter((_, i) => i !== idx))

  const subtotal = lines.reduce((s, l) => s + l.item.price * l.qty, 0)
  const cardCovered = lines.filter((l) => l.byCard).reduce((s, l) => s + l.item.price * l.qty, 0)
  const needPay = Math.max(0, subtotal - cardCovered)

  // 店主定则（09-25）：
  //  会员 → 先用余额付，余额不够的部分自动落到现金（组合支付）；
  //  散客 → 余额框空，现金自动填全额。
  //  两个框都还能手改。
  useEffect(() => {
    if (member) {
      const useBalance = Math.min(needPay, member.balance)
      const rest = Math.max(0, needPay - useBalance)
      setPayBalance(useBalance.toFixed(2))
      setPayCash(rest > 0 ? rest.toFixed(2) : '')
    } else {
      setPayBalance('')
      setPayCash(needPay > 0 ? needPay.toFixed(2) : '')
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [member?.id, member?.balance, subtotal, cardCovered])

  const resetBill = () => {
    // 只在「挂单成功 / 结账完成」后调用。取单来的账单结账后库里那笔已变成正常单，
    // refreshHeld 只是重拉一次保证列表与角标对得上，不会把它放回挂单列表。
    // 「中途放弃」走 clearCart（作废），不走这里。
    const wasTaken = heldOrderId != null
    setLines([])
    setPayBalance('')
    setPayCash('')
    setMember(null)
    setMemberKw('')
    setHeldOrderId(null)
    if (wasTaken) {
      refreshHeld()
      pingHeld()
    }
  }

  /** 清空购物车但保留已选会员（按钮语义就这样）。
   *  取单来的账单：店主 11-05 定「清空 = 彻底作废这张挂单」，不再放回挂单列表。
   *  作废失败时必须把单放回列表，否则界面和库对不上（幽灵挂单）。 */
  const clearCart = () => {
    const voidId = heldOrderId
    setLines([])
    setPayBalance('')
    setPayCash('')
    setHeldOrderId(null)
    if (voidId != null) {
      pos
        .voidHeld(voidId)
        .then((orderNo) => {
          toast.success(`挂单 ${orderNo} 已作废`)
          refreshHeld()
          pingHeld()
        })
        .catch((e) => {
          toast.error('作废挂单失败：' + String(e))
          refreshHeld()
          pingHeld()
        })
    }
  }

  const holdOrder = () => {
    if (lines.length === 0) return
    pos
      .hold(
        lines.map((l) => ({ itemId: l.item.id, qty: l.qty, byCard: l.byCard })),
        member?.id ?? null,
        staffId,
      )
      .then((orderNo) => {
        toast.success(`已挂单（单号 ${orderNo}），可在右侧挂单列表取单`)
        resetBill()
        refreshHeld()
        pingHeld()
      })
      .catch((e) => toast.error(String(e)))
  }

  const restoreOrder = (h: HeldEntry) => {
    if (lines.length > 0) {
      toast.error('当前已有账单，请先结账、挂单或清空后再取单')
      return
    }
    pos
      .take(h.id)
      .then((r) => {
        setLines(r.lines)
        setHeldOrderId(r.orderId)
        // 恢复挂单时的经手人：否则结账会落到「当前选中/登录账号」，与挂单时选的人不一致（店主 10-04 反馈）
        if (r.staffId > 0) setStaffId(r.staffId)
        if (r.memberId != null) {
          // 按 ID 精确取回挂单时的会员（含最新余额/次卡余次）
          pos.getMember(r.memberId).then(setMember).catch(() => setMember(null))
        }
        setHeld((prev) => prev.filter((x) => x.id !== h.id))
        // 取单只是把内容读回账单，库里那笔还是「挂单」——角标这时候不该减，等结账才减。
        // 中途点「清空」= 作废这张挂单（见 clearCart），库里行留着软标记，列表和角标都会消掉。
        pingHeld()
        toast.success(`已取回挂单 ${h.orderNo}（经手人 ${r.staffName || '—'}；结账后保持原单号 ${h.orderNo}）`)
      })
      .catch((e) => toast.error(String(e)))
  }

  const checkout = () => {
    if (lines.length === 0) return
    pos
      .checkout(lines, member?.id ?? null, Number(payBalance) || 0, Number(payCash) || 0, staffId, heldOrderId)
      .then((r) => {
        setPaidResult(r)
        setPaidOperator(operatorName)   // 快照本次经手人：小票与成功弹窗都用它
        setPaidModal(true)
        loadAux()
        // 取单来的账单结账后，库里那笔挂单变成正常单，角标要减一
        pingHeld()
      })
      .catch((e) => toast.error(String(e)))
  }

  const finishOrder = () => {
    setPaidModal(false)
    setPaidResult(null)
    resetBill()
  }

  // 标题栏显示的经办人：跟随下方选择的经手人（店里共用账号时，登录人 ≠ 经单人）
  const operatorName = staffOptions.find((s) => s.id === staffId)?.name ?? getStaff()?.name ?? '—'

  return (
    <PageFill minWidth={1080}>
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            收银开单
          </h1>
        </div>
        {/* 常用动作集中放右上角：建档 / 充值 / 售卡，旁边显示本单经办人（跟着下方经手人变） */}
        <div className="flex items-center gap-2 shrink-0">
          <Button variant="secondary" size="sm" onClick={() => setNewMemberOpen(true)}>
            <UserPlus size={13} /> 新会员建档
          </Button>
          {/* 11-05 店主定：充值、售卡都是「收银开单」的日常动作，两个按钮一起门控。
              能进收银台就一定能充值、卖卡；按人关掉「储值与次卡」不影响这两个按钮。
              后端 storedvalue.Recharge/RechargeByStaff、cards.SellCard 已同步改绑「收银开单」。 */}
          {canModule('收银开单') ? (
            <>
              <Button
                variant="secondary"
                size="sm"
                onClick={() => {
                  if (!member) { toast.info('请先搜索并选中一位会员，再点「会员充值」'); return }
                  setRechargeOpen(true)
                }}
                title={member ? `给 ${member.name} 充值` : '先选中一位会员再充值'}
              >
                <Wallet size={13} /> 会员充值
              </Button>
              <Button variant="secondary" size="sm" onClick={() => setSellOpen(true)}>
                <Ticket size={13} /> 售卡
              </Button>
            </>
          ) : null}
          <span className="text-xs text-muted-foreground ml-1 whitespace-nowrap">
            经办人：<span className="font-medium text-foreground">{operatorName}</span>
          </span>
        </div>
      </div>

      <div className="flex gap-4 flex-1 min-h-0">
        {/* 左：会员 + 项目 */}
        <div className="page-body min-w-0 flex flex-col gap-3">
          {/* 会员选择 */}
          <Card className="p-3 shrink-0">
            {member ? (
              <div>
                <div className="flex items-center gap-3">
                  <div className="w-10 h-10 rounded-full bg-primary/10 text-primary flex items-center justify-center text-sm font-bold shrink-0">
                    {member.name.slice(0, 1)}
                  </div>
                  <div className="leading-tight flex-1 min-w-0">
                    <p className="text-sm font-semibold flex items-center gap-2">
                      {member.name}
                      {member.phone ? (
                        <span className="text-xs text-muted-foreground font-normal tabular-nums">{member.phone}</span>
                      ) : null}
                    </p>
                    <p className="text-xs text-muted-foreground mt-1 tabular-nums">
                      余额 <b className="text-primary">¥{fmtMoney(member.balance)}</b> · 积分 {member.points.toLocaleString('zh-CN')}
                    </p>
                  </div>
                  {member.cardRemain > 0 ? (
                    <button
                      onClick={() => setCardsOpen((v) => !v)}
                      aria-expanded={cardsOpen}
                      className={cx(
                        'inline-flex items-center gap-1 h-7 px-2.5 rounded-[var(--radius-pill)] border text-xs transition-colors shrink-0',
                        cardsOpen
                          ? 'border-primary bg-primary/10 text-primary'
                          : 'border-input text-muted-foreground hover:border-primary hover:text-primary',
                      )}
                    >
                      <Ticket size={12} />
                      {cards.length > 0
                        ? cards.map((c, i) => (
                            <span key={c.id}>
                              {i > 0 ? '、' : ''}
                              {c.itemName || c.cardName} 剩 <b className="tabular-nums">{c.remain}</b>
                            </span>
                          ))
                        : `可用次卡 ${member.cardRemain} 次`}
                      {cardsOpen ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
                    </button>
                  ) : null}
                  <Button variant="ghost" size="sm" onClick={() => { setMember(null); setMemberKw('') }}>
                    更换会员
                  </Button>
                </div>

                {/* 次卡明细：展开后直接看到是哪几张卡、各剩几次、何时到期（不用再翻会员档案） */}
                {cardsOpen ? (
                  <div className="mt-2.5 pt-2.5 border-t border-border/70">
                    {cards.length === 0 ? (
                      <p className="text-[11px] text-muted-foreground py-1.5">该会员暂无可用次卡（已用完 / 已过期）</p>
                    ) : (
                      <table className="w-full text-[11px] border-collapse">
                        <thead>
                          <tr className="text-muted-foreground">
                            <th className="text-left font-normal pb-1">项目</th>
                            <th className="text-left font-normal pb-1">卡名</th>
                            <th className="text-right font-normal pb-1 w-16">单次</th>
                            <th className="text-right font-normal pb-1 w-20">剩余/总次</th>
                            <th className="text-right font-normal pb-1 w-24">到期日</th>
                          </tr>
                        </thead>
                        <tbody>
                          {cards.map((c) => (
                            <tr key={c.id} className="border-t border-border/50">
                              <td className="py-1.5 font-medium">{c.itemName || '—'}</td>
                              <td className="py-1.5 text-muted-foreground">{c.cardName}</td>
                              <td className="py-1.5 text-right tabular-nums text-muted-foreground">
                                {c.unitPrice > 0 ? `¥${fmtMoney(c.unitPrice)}` : '—'}
                              </td>
                              <td className="py-1.5 text-right tabular-nums">
                                <b className="text-primary">{c.remain}</b>
                                <span className="text-muted-foreground"> / {c.total}</span>
                                {c.giftTimes > 0 ? (
                                  <span className="ml-1 text-[10px] text-primary">赠{c.giftTimes}</span>
                                ) : null}
                              </td>
                              <td className="py-1.5 text-right tabular-nums text-muted-foreground">{c.expireDate || '长期'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    )}
                    <p className="text-[10px] text-muted-foreground mt-1.5">
                      核销规则：先到期先用，自动从最接近到期的那张卡扣减；每张卡的单次价 = 实收金额 ÷ 卡上总次数
                      （赠送次数会一起摊薄单次价，所以 8 次付费 + 赠送 2 次的卡，单次是 ×30÷10 = 24 而不是 30）。
                    </p>
                  </div>
                ) : null}
              </div>
            ) : (
              <div className="flex items-center gap-3 flex-wrap">
                <div className="w-10 h-10 rounded-full bg-muted text-muted-foreground flex items-center justify-center shrink-0">
                  <UserRound size={18} />
                </div>
                {/* min-w-[190px]：窗口再窄也不把搜索框挤成只剩图标；放不下时整行换行而非逐字折断 */}
                <div className="relative flex-1 min-w-[190px] max-w-sm">
                  <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={memberKw}
                    onChange={(e) => setMemberKw(e.target.value)}
                    onKeyDown={onSearchKey}
                    placeholder="搜索会员：手机号 / 姓名 / 卡号"
                    className="pl-8"
                    aria-label="搜索会员"
                  />
                  {searchResults.length > 0 ? (
                    <div className="absolute top-10 left-0 right-0 z-30 bg-card border border-border rounded-[var(--radius-sm)] shadow-[var(--elev-raised)] overflow-hidden">
                      {searchResults.map((m, i) => (
                        <button
                          key={m.id}
                          onClick={() => pickMember(m)}
                          onMouseEnter={() => setHlIdx(i)}
                          className={cx(
                            'w-full flex items-center gap-2.5 px-3 h-10 text-left transition-colors border-b border-border/60 last:border-0',
                            i === hlIdx ? 'bg-accent' : 'hover:bg-accent',
                          )}
                        >
                          <span className="w-6 h-6 rounded-full bg-primary/10 text-primary flex items-center justify-center text-[11px] font-semibold">
                            {m.name.slice(0, 1)}
                          </span>
                          <span className="text-[13px] font-medium">{m.name}</span>
                          {m.phone ? <span className="text-xs text-muted-foreground tabular-nums">{m.phone}</span> : null}
                          <span className="ml-auto text-xs text-primary tabular-nums">¥{fmtMoney(m.balance)}</span>
                        </button>
                      ))}
                    </div>
                  ) : null}
                  {memberKw.trim() && searchResults.length === 0 ? (
                    <div className="absolute top-10 left-0 right-0 z-30 bg-card border border-border rounded-[var(--radius-sm)] shadow-[var(--elev-raised)] px-3 py-2.5 text-xs text-muted-foreground">
                      未找到「{memberKw.trim()}」相关的正常状态会员（冻结/挂失会员不可开单）
                    </div>
                  ) : null}
                </div>
                <span className="text-xs text-muted-foreground shrink-0 whitespace-nowrap">或按 <b className="text-foreground">散客</b> 直接开单</span>
              </div>
            )}
          </Card>

          {/* 项目选择 */}
          <Card className="p-3.5 flex-1 min-h-0 flex flex-col">
            <ServiceGrid items={items} onPick={pickItem} />
          </Card>
        </div>

        {/* 中：账单结算 */}
        <CartPanel
          lines={lines}
          member={member}
          cards={cards}
          payBalance={payBalance}
          payCash={payCash}
          staffId={staffId}
          staffOptions={staffOptions}
          onStaffChange={setStaffId}
          remainOf={remainOf}
          onQty={changeQty}
          onToggleCard={toggleCard}
          onRemove={removeLine}
          onPayBalanceChange={setPayBalance}
          onPayCashChange={setPayCash}
          onHold={holdOrder}
          onClear={clearCart}
          onCheckout={checkout}
        />

        {/* 右：挂单列表 */}
        <div className="w-48 min-w-[180px] shrink-0 flex flex-col gap-3">
          <Card className="p-3 flex-1 min-h-0 flex flex-col">
            <div className="flex items-center justify-between mb-2.5">
              <h3 className="text-sm font-semibold flex items-center gap-1.5" style={{ fontFamily: 'var(--font-display)' }}>
                <Pause size={13} className="text-muted-foreground" /> 挂单列表
              </h3>
              <Badge tone={held.length > 0 ? 'danger' : 'muted'}>{held.length} 笔</Badge>
            </div>
            <div className="flex-1 min-h-0 overflow-y-auto space-y-2">
              {held.length === 0 ? (
                <p className="text-xs text-muted-foreground text-center py-8 leading-relaxed">
                  暂无挂单
                  <br />
                  有客中断结账时点「挂单」
                </p>
              ) : (
                held.map((h) => (
                  // 整张卡片就是「取单」按钮（店主 11-05：之前就是点卡片取单，别再多一个按钮）
                  <div
                    key={h.id}
                    role="button"
                    tabIndex={0}
                    onClick={() => restoreOrder(h)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault()
                        restoreOrder(h)
                      }
                    }}
                    title="点击取单"
                    className="group border border-border rounded-[var(--radius-sm)] p-2.5 bg-background/60 cursor-pointer transition-colors hover:border-primary/50 hover:bg-accent focus-visible:outline-none focus-visible:shadow-[var(--focus-ring)]"
                  >
                    <p className="text-[13px] font-semibold flex items-center justify-between">
                      {h.memberName}
                      <span className="text-[10px] text-muted-foreground font-normal tabular-nums">{h.time}</span>
                    </p>
                    <p className="text-[11px] text-muted-foreground mt-0.5 tabular-nums">
                      {h.orderNo} · {h.itemCount} 项 · ¥{fmtMoney(h.amount)}
                    </p>
                    <p className="text-[10px] text-muted-foreground mt-0.5 flex items-center gap-1">
                      <UserCheck size={10} /> 挂单人 {h.staffName || '—'}
                    </p>
                    {/* 取单提示（店主 11-05：卡片可整块点，但「取单」这个字得看得见） */}
                    <p className="mt-1.5 h-6 flex items-center justify-center gap-1 rounded-[var(--radius-sm)] border border-dashed border-primary/40 text-[11px] text-primary bg-primary/5 group-hover:bg-primary/10">
                      <Play size={10} /> 取单
                    </p>
                  </div>
                ))
              )}
            </div>
          </Card>
          <Card className="p-3">
            <h3 className="text-sm font-semibold mb-2" style={{ fontFamily: 'var(--font-display)' }}>
              当班速览
            </h3>
            <dl className="space-y-1.5 text-xs">
              {[
                ['本班收银', '¥' + fmtMoney(shift?.Revenue ?? 0)],
                ['本班笔数', `${shift?.Count ?? 0} 笔`],
                ['余额支付占比', `${((shift?.BalanceRatio ?? 0) * 100).toFixed(1)}%`],
              ].map(([k, v]) => (
                <div key={k} className="flex justify-between">
                  <dt className="text-muted-foreground">{k}</dt>
                  <dd className="tabular-nums font-medium">{v}</dd>
                </div>
              ))}
            </dl>
          </Card>
        </div>
      </div>

      {/* 收银台现场建档 + 充值：与会员管理同一套表单（姓名/手机/生日/充值/标签/备注/经办人；等级自动判定） */}
      <NewMemberDialog
        open={newMemberOpen}
        onClose={() => setNewMemberOpen(false)}
        onCreated={onNewMemberCreated}
        staffOptions={staffOptions}
        defaultCreatorId={staffId ?? 0}
        submitLabel="建档并开单"
        levels={levels}
      />

      {/* 会员充值：选中会员后在收银台直接完成；成功后按 ID 重取会员，余额/积分即时更新 */}
      <RechargeDialog
        open={rechargeOpen}
        member={member}
        onClose={() => setRechargeOpen(false)}
        onDone={(r) => {
          toast.success(`已为 ${r.memberName} 充值 ¥${fmtMoney(r.amount)}${r.gift > 0 ? ` 送 ¥${fmtMoney(r.gift)}` : ''}，单号 ${r.orderNo}，经手人 ${r.operator}`)
          if (member) pos.getMember(member.id).then(setMember).catch(() => {})
        }}
        staffOptions={staffOptions}
        defaultStaffId={staffId ?? 0}
      />

      {/* 售卡（交易动作，主场在收银台）：下拉里是「可多次售卖的套餐次卡」= 已配置次卡套餐的项目 */}
      <SellCardDialog
        open={sellOpen}
        onClose={() => setSellOpen(false)}
        defaultStaffId={staffId ?? 0}
        onSold={onCardSold}
      />

      {/* 结账成功 */}
      <Modal
        open={paidModal}
        onClose={finishOrder}
        title="结账成功"
        width={420}
        footer={
          <Button variant="primary" onClick={printReceipt} disabled={!printEnabled} title={printEnabled ? '一键打印小票到默认打印机；无打印机时自动生成 PDF 并打开' : '先在 系统设置·小票打印 里开启'}>
            <Printer size={14} /> {printEnabled ? '打印小票' : '打印（未开启）'}
          </Button>
        }
      >
        <div className="text-center py-2">
          <div className={cx('w-14 h-14 mx-auto rounded-full bg-primary/10 text-primary flex items-center justify-center mb-3')}>
            <CheckCircle2 size={30} />
          </div>
          <p className="text-lg font-bold tabular-nums" style={{ fontFamily: 'var(--font-display)' }}>
            ¥{fmtMoney(paidResult?.Receivable ?? 0)}
          </p>
          <p className="text-xs text-muted-foreground mt-1.5">
            单号 {paidResult?.OrderNo ?? '—'} · 经办 {paidOperator || '—'} · {member ? member.name : '散客'} ·{' '}
            {(paidResult?.CardUsedTimes ?? 0) > 0 ? `次卡核销 ${paidResult?.CardUsedTimes} 次 · ` : ''}
            余额 ¥{fmtMoney(paidResult?.BalanceUsed ?? 0)} + 现金 ¥{fmtMoney(paidResult?.CashIn ?? 0)}
            {(paidResult?.Change ?? 0) > 0 ? ` · 找零 ¥${fmtMoney(paidResult?.Change ?? 0)}` : ''}
            {(paidResult?.BalanceBack ?? 0) > 0 ? ` · 余额多填 ¥${fmtMoney(paidResult?.BalanceBack ?? 0)} 未扣` : ''}
          </p>
          <p className="text-xs text-muted-foreground mt-3 leading-relaxed">
            余额扣减、次卡核销、提成与流水记录已在同一事务提交；
            <br />
            断电重启后可从流水完整恢复本单。
          </p>
        </div>
      </Modal>

      {/* 打印专用小票区：挂在组件根级（不被 Modal 的 overflow 裁剪），平时 hidden，
          @media print 时只显示这一块；打印由 system.Print → WebView2 原生打印触发 */}
      {paidModal ? (
        <div className="receipt-print hidden">
          <div className="mx-auto w-[300px] px-3 py-3 text-[12px] leading-relaxed text-black bg-white">
            <div className="text-center">
              <div className="text-[15px] font-bold">{shopName || '会员店'}</div>
              <div className="text-[11px] text-neutral-600">收 银 小 票</div>
            </div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="text-[11px] text-neutral-600 flex justify-between">
              <span>单号 {paidResult?.OrderNo ?? '—'}</span>
              <span>{new Date().toLocaleString('zh-CN')}</span>
            </div>
            <div className="text-[11px] text-neutral-600">
              会员：{member ? `${member.name}（${member.cardNo}）` : '散客'}
            </div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div>
              {lines.map((l, i) => (
                <div key={i} className="flex justify-between">
                  <span>
                    {l.item.name} × {l.qty}
                    {l.byCard ? '（次卡）' : ''}
                  </span>
                  <span className="tabular-nums">¥{fmtMoney(l.item.price * l.qty)}</span>
                </div>
              ))}
            </div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="flex justify-between"><span>小计</span><span className="tabular-nums">¥{fmtMoney(paidResult?.Subtotal ?? 0)}</span></div>
            {paidResult?.CardCovered > 0 ? (
              <div className="flex justify-between text-neutral-600"><span>次卡抵扣</span><span className="tabular-nums">-¥{fmtMoney(paidResult.CardCovered)}</span></div>
            ) : null}
            <div className="flex justify-between font-bold text-[14px]"><span>应收</span><span className="tabular-nums">¥{fmtMoney(paidResult?.Receivable ?? 0)}</span></div>
            {paidResult?.BalanceUsed > 0 ? (
              <div className="flex justify-between"><span>余额支付</span><span className="tabular-nums">¥{fmtMoney(paidResult.BalanceUsed)}</span></div>
            ) : null}
            {paidResult?.CashIn > 0 ? (
              <div className="flex justify-between"><span>现金</span><span className="tabular-nums">¥{fmtMoney(paidResult.CashIn)}</span></div>
            ) : null}
            {paidResult?.Change > 0 ? (
              <div className="flex justify-between"><span>找零</span><span className="tabular-nums">¥{fmtMoney(paidResult.Change)}</span></div>
            ) : null}
            {paidResult?.BalanceBack > 0 ? (
              <div className="flex justify-between text-neutral-600"><span>余额多填未扣</span><span className="tabular-nums">¥{fmtMoney(paidResult.BalanceBack)}</span></div>
            ) : null}
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="text-center text-[11px] text-neutral-600 whitespace-pre-wrap">
              {receiptNote ? receiptNote + '\n' : null}
              操作员 {paidOperator || operatorName || '—'}
            </div>
          </div>
        </div>
      ) : null}
    </PageFill>
  )
}
