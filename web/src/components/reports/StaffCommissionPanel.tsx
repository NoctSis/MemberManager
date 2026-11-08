import { Fragment, useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronDown, ChevronRight, CheckSquare, History, CalendarDays, FileSpreadsheet, Search, X } from 'lucide-react'
import { toast } from 'sonner'
import { Badge, Button, Input, Modal, SectionCard, cx, numCls, tdCls, thCls } from '@/components/common/ui'
import { commission as apiCommission, system as apiSystem } from '@/lib/api'
import { useExportDir } from '@/lib/useExportDir'
import { fmtMoney } from '@/data/mock'

type StaffSum = Awaited<ReturnType<typeof apiCommission.staffSummary>>[number]
type CommRow = Awaited<ReturnType<typeof apiCommission.details>>[number]
type SettleBatch = Awaited<ReturnType<typeof apiCommission.settleHistory>>[number]

/** M11：徽标按订单 kind 显示——售卡提成行的 type 被 CHECK 冻结成「消费」，只看 type 会失真。 */
const badgeType = (r: CommRow) => (r.kind === '售卡' ? '售卡' : r.type)

/**
 * 员工提成对账（T6，店主 10-08 定）。
 *
 * 取代原来的「提成明细（按月）」逐笔表 —— 原来只有一张全月混排的流水表，
 * 要看某个人还得自己从一堆记录里挑；现在一行一人（业绩 + 未结 + 应结），
 * 点一行展开才是他自己的逐笔，结算入口也在同一行上。
 *
 * 结算口径（店主定）：
 *  1. **按员工结算**，不再全月一刀切（旧的 SettleAll 语义已废弃，前端不再调用，方法留作冒烟基线）；
 *  2. **结算前必须过对账单** —— 逐笔列出来核对，不是「确认吗？」就完事；
 *  3. 对账单登记**应结 / 实付 / 差异 / 原因**（照搬日结的 cash_diff + diff_reason 范式）；
 *  4. 每次结算写批次历史（落 op_logs，含被结的 commission id，可反查）。
 */
export function StaffCommissionPanel({ defaultMonth }: { defaultMonth?: string }) {
  const [month, setMonth] = useState<string>('')
  const [sums, setSums] = useState<StaffSum[]>([])
  const [rows, setRows] = useState<CommRow[]>([])
  const [loading, setLoading] = useState(false)
  const [expanded, setExpanded] = useState<number>(0)
  const [checked, setChecked] = useState<number[]>([])
  const [exporting, setExporting] = useState(false)
  const { dir: exportDir } = useExportDir()

  // 对账单弹窗
  const [target, setTarget] = useState<StaffSum | null>(null)
  const [actual, setActual] = useState<string>('')
  const [reason, setReason] = useState<string>('')
  const [settling, setSettling] = useState(false)

  // 结算记录弹窗
  const [historyOpen, setHistoryOpen] = useState(false)
  const [history, setHistory] = useState<SettleBatch[]>([])

  // B3（10-21）：按姓名过滤——人多时找一个人不用整表扫
  const [nameKw, setNameKw] = useState<string>('')

  // 默认月份：跟随营业日（拿不到就用今天）
  useEffect(() => {
    if (defaultMonth) {
      setMonth((m) => m || defaultMonth)
      return
    }
    apiSystem
      .status()
      .then((s) => {
        const d = s.BizDate || new Date().toISOString().slice(0, 10)
        setMonth((m) => m || d.slice(0, 7))
      })
      .catch(() => setMonth((m) => m || new Date().toISOString().slice(0, 7)))
  }, [defaultMonth])

  const load = useCallback(() => {
    if (!month) return
    setLoading(true)
    Promise.all([apiCommission.staffSummary(month), apiCommission.details(month)])
      .then(([s, d]) => {
        setSums(s)
        setRows(d)
        setChecked([])
      })
      .catch((e) => {
        toast.error(String(e))
        setSums([])
        setRows([])
      })
      .finally(() => setLoading(false))
  }, [month])

  useEffect(() => {
    load()
  }, [load])

  const loadHistory = useCallback(() => {
    apiCommission
      .settleHistory(50)
      .then(setHistory)
      .catch((e) => toast.error(String(e)))
  }, [])

  useEffect(() => {
    if (historyOpen) loadHistory()
  }, [historyOpen, loadHistory])

  // 对账单打开时把实付默认填成应结（差异 0）
  useEffect(() => {
    if (target) {
      setActual(target.unsettledAmount.toFixed(2))
      setReason('')
    }
  }, [target])

  const rowsOf = useCallback((staffId: number) => rows.filter((r) => r.staffId === staffId), [rows])

  // B3：姓名过滤后的展示集合（总额合计仍用全量 sums，不因过滤而变）
  const visibleSums = useMemo(() => {
    const kw = nameKw.trim()
    if (!kw) return sums
    return sums.filter((s) => s.name.includes(kw))
  }, [sums, nameKw])

  const totals = useMemo(() => {
    let recharge = 0
    let consume = 0
    let unsettled = 0
    let settled = 0
    let cnt = 0
    for (const s of sums) {
      recharge += s.rechargeAmount
      consume += s.consumeAmount
      unsettled += s.unsettledAmount
      settled += s.settledAmount
      cnt += s.unsettledCount
    }
    return { recharge, consume, unsettled, settled, cnt }
  }, [sums])

  const checkable = useMemo(() => sums.filter((s) => s.unsettledCount > 0).map((s) => s.staffId), [sums])
  const allChecked = checkable.length > 0 && checked.length === checkable.length
  const checkedAmount = useMemo(
    () => sums.filter((s) => checked.indexOf(s.staffId) >= 0).reduce((a, s) => a + s.unsettledAmount, 0),
    [sums, checked],
  )

  const toggleCheck = (id: number) => {
    setChecked((c) => (c.indexOf(id) >= 0 ? c.filter((x) => x !== id) : c.concat([id])))
  }
  const toggleAll = () => {
    setChecked(allChecked ? [] : checkable.slice())
  }

  const doSettle = (ids: number[], actualVal: number, reasonText: string) => {
    if (!month || ids.length === 0 || settling) return
    setSettling(true)
    apiCommission
      .settleByStaff(month, ids.join(','), actualVal, reasonText)
      .then((r) => {
        if (r.Count === 0) {
          toast.info('所选员工本月没有未结算的提成')
        } else if (r.Diff !== 0) {
          toast.success(
            `已结算 ${r.Count} 笔：应结 ¥${fmtMoney(r.Amount)}，实付 ¥${fmtMoney(r.Actual)}，差异 ¥${fmtMoney(r.Diff)}（已写日志）`,
          )
        } else {
          toast.success(`已结算 ${r.Count} 笔，共 ¥${fmtMoney(r.Amount)}（已写日志）`)
        }
        setTarget(null)
        setChecked([])
        setExpanded(0)
        load()
        if (historyOpen) loadHistory()
      })
      .catch((e) => toast.error('结算失败：' + String(e)))
      .finally(() => setSettling(false))
  }

  /** 导出本提成月 Excel（明细 + 排名两表），落默认导出位置，成功后打开 */
  const doExport = () => {
    if (!month || exporting) return
    setExporting(true)
    apiCommission
      .exportExcel(month, exportDir || undefined)
      .then((r) => apiSystem.openPath(r.Path))
      .catch((e) => toast.error('导出失败：' + String(e)))
      .finally(() => setExporting(false))
  }

  const fmtTime = (t: string) => (t || '').replace('T', ' ').slice(0, 16)

  const actualNum = Number(actual)
  const diffNum = target && isFinite(actualNum) ? Math.round((actualNum - target.unsettledAmount) * 100) / 100 : 0
  const diffNeedsReason = diffNum !== 0
  const canConfirm = !!target && target.unsettledCount > 0 && isFinite(actualNum) && (!diffNeedsReason || reason.trim().length > 0)

  return (
    <>
      <SectionCard
        title="员工提成对账"
        extra={
          <>
            <span className="text-[11px] text-muted-foreground tabular-nums">
              {loading
                ? '加载中…'
                : month
                  ? `${month} · ${sums.length} 位员工 · 待结 ${totals.cnt} 笔 ¥${fmtMoney(totals.unsettled)}`
                  : '请选择月份'}
            </span>
            <Button
              variant="secondary"
              size="sm"
              onClick={doExport}
              disabled={exporting || !month}
              title={`导出 ${month || ''} 提成表 Excel（每笔明细 + 员工汇总两张表）`}
            >
              <FileSpreadsheet size={13} /> {exporting ? '导出中…' : '导出提成'}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setHistoryOpen(true)}
              title="查看历次结算记录（谁、何时、结给谁、多少钱）"
            >
              <History size={13} /> 结算记录
            </Button>
          </>
        }
        bodyClassName="p-0"
      >
        <div className="px-3 py-2.5 flex items-center gap-2.5 flex-wrap border-b border-border">
          <span className="flex items-center gap-1.5 text-sm font-medium text-foreground">
            <CalendarDays size={14} className="text-primary" /> 月份
          </span>
          <input
            type="month"
            value={month}
            onChange={(e) => {
              setMonth(e.target.value)
              setExpanded(0)
            }}
            className="h-7 px-2 text-xs bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary tabular-nums"
            aria-label="选择对账月份"
          />
          <span className="w-px h-4 bg-border" />
          <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <Search size={13} />
            <input
              value={nameKw}
              onChange={(e) => setNameKw(e.target.value)}
              placeholder="按姓名筛选"
              className="h-7 w-28 px-2 text-xs bg-card text-foreground rounded-[var(--radius-sm)] border border-input focus:outline-none focus:border-primary"
              aria-label="按姓名筛选员工"
            />
            {nameKw ? (
              <button onClick={() => setNameKw('')} className="text-muted-foreground hover:text-foreground" aria-label="清空姓名筛选">
                <X size={12} />
              </button>
            ) : null}
          </span>
          <Button
            variant="primary"
            size="sm"
            onClick={() => doSettle(checked.slice(), -1, '')}
            disabled={checked.length === 0 || settling}
            title={
              checked.length === 0
                ? '勾选表格左侧的方框选择员工后，可一次结算多人'
                : `结算选中的 ${checked.length} 位员工，共 ¥${fmtMoney(checkedAmount)}`
            }
          >
            <CheckSquare size={13} /> {settling ? '结算中…' : '批量结算'}
          </Button>
          {checked.length > 0 ? (
            <span className="text-[11px] text-muted-foreground tabular-nums">
              已选 {checked.length} 人 · 应结 ¥{fmtMoney(checkedAmount)}
            </span>
          ) : null}
        </div>

        <table className="w-full border-collapse">
          <thead>
            <tr>
              <th className={cx(thCls, 'w-8')}>
                <input
                  type="checkbox"
                  checked={allChecked}
                  onChange={toggleAll}
                  disabled={checkable.length === 0}
                  aria-label="全选可结算员工"
                />
              </th>
              <th className={thCls}>员工</th>
              <th className={cx(thCls, 'text-right')}>充值业绩</th>
              <th className={cx(thCls, 'text-right')}>消费业绩</th>
              <th className={cx(thCls, 'text-right')}>合计业绩</th>
              <th className={cx(thCls, 'text-right')}>未结笔数</th>
              <th className={cx(thCls, 'text-right')}>应结提成</th>
              <th className={cx(thCls, 'text-right')}>已结提成</th>
              <th className={thCls}>状态</th>
              <th className={thCls}>操作</th>
            </tr>
          </thead>
          <tbody>
            {sums.length === 0 ? (
              <tr>
                <td className={tdCls} colSpan={10}>
                  <span className="text-xs text-muted-foreground">
                    {loading ? '加载中…' : `${month || '该月'} 暂无提成记录（发生消费或充值后自动计提）`}
                  </span>
                </td>
              </tr>
            ) : visibleSums.length === 0 ? (
              <tr>
                <td className={tdCls} colSpan={10}>
                  <span className="text-xs text-muted-foreground">没有匹配「{nameKw.trim()}」的员工</span>
                </td>
              </tr>
            ) : (
              visibleSums.map((s) => {
                const open = expanded === s.staffId
                const detail = open ? rowsOf(s.staffId) : []
                const canCheck = s.unsettledCount > 0
                return (
                  <Fragment key={s.staffId}>
                    <tr className="even:bg-muted/40 hover:bg-accent/60 transition-colors">
                      <td className={cx(tdCls, 'w-8')}>
                        <input
                          type="checkbox"
                          checked={checked.indexOf(s.staffId) >= 0}
                          onChange={() => toggleCheck(s.staffId)}
                          disabled={!canCheck}
                          aria-label={`选择 ${s.name}`}
                        />
                      </td>
                      <td className={cx(tdCls, 'font-medium')}>
                        <button
                          type="button"
                          onClick={() => setExpanded(open ? 0 : s.staffId)}
                          className="flex items-center gap-1 text-left hover:text-primary"
                          title={`${open ? '收起' : '展开'} ${s.name} 的逐笔明细`}
                        >
                          {open ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
                          {s.name}
                        </button>
                      </td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(s.rechargeAmount)}</td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(s.consumeAmount)}</td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(s.rechargeAmount + s.consumeAmount)}</td>
                      <td className={cx(tdCls, numCls, s.unsettledCount > 0 ? 'text-foreground' : 'text-muted-foreground')}>
                        {s.unsettledCount}
                      </td>
                      <td className={cx(tdCls, numCls, s.unsettledAmount > 0 ? 'text-primary font-semibold' : 'text-muted-foreground')}>
                        ¥{fmtMoney(s.unsettledAmount)}
                      </td>
                      <td className={cx(tdCls, numCls, 'text-muted-foreground')}>¥{fmtMoney(s.settledAmount)}</td>
                      <td className={tdCls}>
                        {s.unsettledCount > 0 ? <Badge tone="primary">待结算</Badge> : <Badge tone="muted">已结清</Badge>}
                      </td>
                      <td className={tdCls}>
                        <Button
                          variant="secondary"
                          size="sm"
                          onClick={() => setTarget(s)}
                          disabled={s.unsettledCount === 0}
                          title={
                            s.unsettledCount === 0
                              ? `${s.name} 本月没有未结算的提成`
                              : `核对 ${s.name} 本月 ${s.unsettledCount} 笔共 ¥${fmtMoney(s.unsettledAmount)}，确认后结算`
                          }
                        >
                          对账并结算
                        </Button>
                      </td>
                    </tr>
                    {open ? (
                      <tr className="bg-muted/30">
                        <td className={tdCls} colSpan={10}>
                          {detail.length === 0 ? (
                            <span className="text-xs text-muted-foreground">该员工本月暂无提成记录</span>
                          ) : (
                            <table className="w-full border-collapse">
                              <thead>
                                <tr>
                                  <th className={cx(thCls, 'border-b-0')}>日期</th>
                                  <th className={cx(thCls, 'border-b-0')}>订单号</th>
                                  <th className={cx(thCls, 'border-b-0')}>类型</th>
                                  <th className={cx(thCls, 'border-b-0 text-right')}>金额（元）</th>
                                  <th className={cx(thCls, 'border-b-0 text-right')}>比例</th>
                                  <th className={cx(thCls, 'border-b-0 text-right')}>提成（元）</th>
                                  <th className={cx(thCls, 'border-b-0')}>结算</th>
                                </tr>
                              </thead>
                              <tbody>
                                {detail.map((r) => (
                                  <tr key={r.id}>
                                    <td className={cx(tdCls, numCls, 'text-muted-foreground')}>{r.date}</td>
                                    <td className={cx(tdCls, numCls)}>{r.orderNo || '—'}</td>
                                    <td className={tdCls}>
                                      {/* M11：售卡提成行的 type 被 CHECK 冻结成「消费」，徽标按订单 kind 显示才不失真 */}
                                      <Badge tone={badgeType(r) === '充值' ? 'primary' : 'muted'}>{badgeType(r)}</Badge>
                                    </td>
                                    <td className={cx(tdCls, numCls)}>¥{fmtMoney(r.amount)}</td>
                                    <td className={cx(tdCls, numCls, 'text-muted-foreground')}>
                                      {(r.rate * 100).toFixed(1)}%
                                    </td>
                                    <td className={cx(tdCls, numCls, 'text-primary font-semibold')}>
                                      ¥{fmtMoney(r.commission)}
                                    </td>
                                    <td className={tdCls}>
                                      {r.settled ? <Badge tone="muted">已结算</Badge> : <Badge tone="primary">未结算</Badge>}
                                    </td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          )}
                        </td>
                      </tr>
                    ) : null}
                  </Fragment>
                )
              })
            )}
            {sums.length > 0 && (
              <tr className="bg-muted/60">
                <td className={tdCls} colSpan={2}>
                  <span className="text-xs font-semibold text-muted-foreground">
                    合计　{sums.length} 人{totals.cnt > 0 ? <span className="ml-2 font-normal">（{totals.cnt} 笔待结算）</span> : null}
                  </span>
                </td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold')}>¥{fmtMoney(totals.recharge)}</td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold')}>¥{fmtMoney(totals.consume)}</td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold')}>¥{fmtMoney(totals.recharge + totals.consume)}</td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold')}>{totals.cnt}</td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold text-primary')}>¥{fmtMoney(totals.unsettled)}</td>
                <td className={cx(tdCls, numCls, 'text-xs font-semibold')}>¥{fmtMoney(totals.settled)}</td>
                <td className={tdCls} colSpan={2}></td>
              </tr>
            )}
          </tbody>
        </table>
      </SectionCard>

      {target ? (
        <Modal
          title={`对账：${target.name} · ${month}`}
          onClose={() => setTarget(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setTarget(null)}>
                取消
              </Button>
              <Button variant="danger" onClick={() => doSettle([target.staffId], actualNum, reason)} disabled={!canConfirm || settling}>
                <CheckSquare size={13} /> {settling ? '结算中…' : '确认无误，结算'}
              </Button>
            </>
          }
        >
          <p className="text-sm">
            该员工本月共 <b className="text-foreground tabular-nums">{target.unsettledCount}</b> 笔未结提成，
            应结 <b className="text-primary tabular-nums">¥{fmtMoney(target.unsettledAmount)}</b>。请逐笔核对后再结算。
          </p>

          <div className="mt-3 max-h-56 overflow-auto border border-border rounded-[var(--radius-sm)]">
            <table className="w-full border-collapse">
              <thead>
                <tr>
                  <th className={thCls}>日期</th>
                  <th className={thCls}>订单号</th>
                  <th className={thCls}>类型</th>
                  <th className={cx(thCls, 'text-right')}>金额</th>
                  <th className={cx(thCls, 'text-right')}>比例</th>
                  <th className={cx(thCls, 'text-right')}>提成</th>
                </tr>
              </thead>
              <tbody>
                {rowsOf(target.staffId)
                  .filter((r) => !r.settled)
                  .map((r) => (
                    <tr key={r.id} className="even:bg-muted/40">
                      <td className={cx(tdCls, numCls, 'text-muted-foreground')}>{r.date}</td>
                      <td className={cx(tdCls, numCls)}>{r.orderNo || '—'}</td>
                      <td className={tdCls}>
                        <Badge tone={badgeType(r) === '充值' ? 'primary' : 'muted'}>{badgeType(r)}</Badge>
                      </td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(r.amount)}</td>
                      <td className={cx(tdCls, numCls, 'text-muted-foreground')}>{(r.rate * 100).toFixed(1)}%</td>
                      <td className={cx(tdCls, numCls, 'text-primary font-semibold')}>¥{fmtMoney(r.commission)}</td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>

          <div className="mt-3 grid grid-cols-2 gap-3">
            <label className="flex flex-col gap-1">
              <span className="text-[11px] text-muted-foreground">实付金额（元）</span>
              <Input
                value={actual}
                onChange={(e) => setActual(e.target.value)}
                className="h-8 text-sm tabular-nums"
                inputMode="decimal"
                aria-label="实付金额"
              />
            </label>
            <div className="flex flex-col gap-1">
              <span className="text-[11px] text-muted-foreground">差异（实付 − 应结）</span>
              <div
                className={cx(
                  'h-8 flex items-center px-3 text-sm tabular-nums rounded-[var(--radius-sm)] border border-border',
                  diffNum === 0 ? 'text-muted-foreground' : 'text-destructive font-semibold',
                )}
              >
                ¥{isFinite(diffNum) ? fmtMoney(diffNum) : '—'}
              </div>
            </div>
          </div>

          {diffNeedsReason ? (
            <label className="flex flex-col gap-1 mt-3">
              <span className="text-[11px] text-destructive">差异原因（必填，会写进操作日志）</span>
              <Input
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder="例如：扣上月多发、抹零、离职结算等"
                className="h-8 text-sm"
                aria-label="差异原因"
              />
            </label>
          ) : null}

          <ul className="text-[11px] text-muted-foreground mt-3 space-y-1 list-disc pl-4">
            <li>只结算 <b className="text-foreground">{target.name}</b> 一个人，其他员工不受影响；</li>
            <li>标记后该员工未结提成清零，此操作<b className="text-foreground">不可逆</b>；</li>
          </ul>
        </Modal>
      ) : null}

      {historyOpen ? (
        <Modal title="结算记录" onClose={() => setHistoryOpen(false)}>
          {history.length === 0 ? (
            <p className="text-sm text-muted-foreground">还没有结算记录。</p>
          ) : (
            <div className="max-h-80 overflow-auto border border-border rounded-[var(--radius-sm)]">
              <table className="w-full border-collapse">
                <thead>
                  <tr>
                    <th className={thCls}>时间</th>
                    <th className={thCls}>操作人</th>
                    <th className={thCls}>月份</th>
                    <th className={thCls}>结算对象</th>
                    <th className={cx(thCls, 'text-right')}>笔数</th>
                    <th className={cx(thCls, 'text-right')}>应结</th>
                    <th className={cx(thCls, 'text-right')}>实付</th>
                    <th className={cx(thCls, 'text-right')}>差异</th>
                  </tr>
                </thead>
                <tbody>
                  {history.map((h) => (
                    <tr key={h.id} className="even:bg-muted/40">
                      <td className={cx(tdCls, numCls, 'text-muted-foreground')}>{fmtTime(h.time)}</td>
                      <td className={tdCls}>{h.operator}</td>
                      <td className={cx(tdCls, numCls)}>{h.period}</td>
                      <td className={cx(tdCls, 'text-xs')} title={h.staffNames}>
                        {h.staffNames || '—'}
                      </td>
                      <td className={cx(tdCls, numCls)}>{h.count}</td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(h.amount)}</td>
                      <td className={cx(tdCls, numCls)}>¥{fmtMoney(h.actual)}</td>
                      <td className={cx(tdCls, numCls, h.diff !== 0 ? 'text-destructive font-semibold' : 'text-muted-foreground')}>
                        ¥{fmtMoney(h.diff)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {history.some((h) => h.reason) ? (
            <div className="mt-3 text-[11px] text-muted-foreground">
              差异备注：
              {history
                .filter((h) => h.reason)
                .map((h) => ` ${fmtTime(h.time)} ${h.reason}`)
                .join('；')}
            </div>
          ) : null}
        </Modal>
      ) : null}
    </>
  )
}
