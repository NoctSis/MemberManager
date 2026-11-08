import { useCallback, useEffect, useState, useRef } from 'react'
import { PageFill } from '@/components/common/PageLayout'
import { Search, UserPlus, Filter, Settings } from 'lucide-react'
import { toast } from 'sonner'
import { Button, Input, Select, Card, EmptyState, Modal, Field, cx } from '@/components/common/ui'
import { MembersTable, MembersSummary } from '@/components/members/MembersTable'
import { MemberDetailDialog, NewMemberDialog, EditMemberDialog } from '@/components/members/MemberDialogs'
import { MemberAdminDialog } from '@/components/members/MemberAdminDialog'
import { members as apiMembers, storedvalue, levels as apiLevels, pos as apiPos, settings as apiSettings, system as apiSystem } from '@/lib/api'
import type { PosStaffOption } from '@/lib/api'
import { usePagedNumbered, useAutoPageSize } from '@/lib/paged'
import { PagedBar } from '@/components/common/paged'
import { getLastOperatorId, setLastOperatorId, getStaff, isManager } from '@/lib/session'
import { fmtMoney } from '@/data/mock'
import type { Member, RechargePackage, MemberLevel } from '@/data/mock'

/** 会员列表页每页行数（= 表格区高度上限，与单据/积分页一致 10 行） */
const MEMBER_PAGE_SIZE = 10

export function MembersPage() {
  // L2（店主 10-18）：会员列表改后端分页，不再一次性拉全。
  // 明细按 limit/offset 取单页；汇总（Total/BalanceSum/Abnormal/Deleted）随批由后端全量算，
  // 不受翻页影响（铁律：总数绝不能从本页行数推）。
  const [keyword, setKeyword] = useState<string>('')
  const [statusFilter, setStatusFilter] = useState<string>('全部状态')
  const [tagFilter, setTagFilter] = useState<string>('全部标签')
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [detailMember, setDetailMember] = useState<Member | null>(null)
  const [newOpen, setNewOpen] = useState<boolean>(false)
  // 会员设置（店长专属）：等级制度等会员相关制度调整集中在这里
  const [adminOpen, setAdminOpen] = useState<boolean>(false)
  const [editMember, setEditMember] = useState<Member | null>(null)
  const [rechargeMember, setRechargeMember] = useState<Member | null>(null)
  const [rechargeAmount, setRechargeAmount] = useState<string>('0')
  const [selectedPkgId, setSelectedPkgId] = useState<string>('')
  const [rechargeMethod, setRechargeMethod] = useState<string>('现金')
  // 充值经手人：店里常共用一个账号登录，登录人≠经单人，提成/流水要记清是谁经手的（照售卡那套）
  const [rechargeStaffId, setRechargeStaffId] = useState<number>(0)
  const [rechargeBusy, setRechargeBusy] = useState<boolean>(false)
  // 充值小票打印：店名 / 是否启用，都从库读（与收银台同一套打印机制）
  const [shopName, setShopName] = useState<string>('')
  const [printEnabled, setPrintEnabled] = useState<boolean>(false)
  // 刚完成的这笔充值（渲染成小票区 + 「打印小票」按钮）；pendingPrint=true 表示成功后要自动打一张
  const [receipt, setReceipt] = useState<{ orderNo: string; amount: number; gift: number; memberName: string; memberCard: string; method: string; operator: string } | null>(null)
  const [pendingPrint, setPendingPrint] = useState<boolean>(false)
  const [packages, setPackages] = useState<RechargePackage[]>([])
  const [levels, setLevels] = useState<MemberLevel[]>([])
  // 建档经办人候选：显式拉一次并传给弹窗，避免弹窗里再请求、也保证「谁建的档」一定能选
  const [staffOptions, setStaffOptions] = useState<PosStaffOption[]>([])

  // L2（店主 10-18 前端批）：会员列表改后端分页，不再一次性拉全。
  // 明细按 limit/offset 取单页；汇总（Total/BalanceSum/Abnormal/Deleted）随批由后端**全量**算，
  // 不受翻页影响（铁律：总数绝不能从本页行数推）。筛选/翻页任一变化 → 回第 1 页重取。
  const [tags, setTags] = useState<string[]>([])
  // 表格区高度 → 一页装几行（店主 11-04：装满窗口就翻页）。挂在包住 MembersTable 的
  // 常驻 flex-1 层上（不在 MembersSummary/页码条那两块），高度与内容无关。
  const bodyRef = useRef<HTMLDivElement>(null)
  const autoSize = useAutoPageSize(bodyRef)
  const page = usePagedNumbered<Member, { total: number; balanceSum: number; abnormal: number; deleted: number }>(
    (limit, offset) =>
      apiMembers
        .searchPage(keyword, statusFilter, tagFilter, limit, offset)
        .then((r) => ({
          rows: r.rows,
          total: r.total,
          meta: { total: r.total, balanceSum: r.balanceSum, abnormal: r.abnormal, deleted: r.deleted },
        })),
    MEMBER_PAGE_SIZE,
    autoSize,
  )
  useEffect(() => {
    void page.reload()
  }, [page.reload, keyword, statusFilter, tagFilter])
  useEffect(() => {
    if (page.error) toast.error(page.error)
  }, [page.error])
  const rows = page.rows
  const sum = page.meta ?? { total: 0, balanceSum: 0, abnormal: 0, deleted: 0 }
  // 归档后需要重取（当前筛选/当前页可能已变）；与旧 onMemberChanged 的 load()+清缓存等价
  const reload = useCallback(() => { void page.reload() }, [page.reload])

  useEffect(() => {
    storedvalue.packages().then((ps) => setPackages(ps.filter((p) => p.enabled))).catch(() => setPackages([]))
    // 等级制度：表格徽标配色 + 建档/编辑弹窗的等级预览都要用它
    apiLevels.list().then(setLevels).catch(() => setLevels([]))
    apiPos.staffOptions().then(setStaffOptions).catch(() => setStaffOptions([]))
    // 小票打印：店名 / 是否启用（与收银台同一套，充值小票也要打）
    apiSystem.status().then((s) => setShopName(s.ShopName || '')).catch(() => {})
    apiSettings.getAll().then((d) => { setPrintEnabled(d.receipt_enabled === '1'); if (d.shop_name) setShopName(d.shop_name) }).catch(() => setPrintEnabled(false))
  }, [])

  // 标签下拉走后端去重（不再从已拉全的列表本地 flatMap）：列出全部会员出现过的标签
  useEffect(() => {
    apiMembers.tags().then(setTags).catch(() => setTags([]))
  }, [page.reload])

  // 充值小票：自动打印成功后，等小票区块渲染出来再触发一次原生打印（默认打印机直打；无实体机自动出 PDF）
  useEffect(() => {
    if (!pendingPrint) return
    setPendingPrint(false)
    apiSystem.print().catch((e) => toast.error('打印失败：' + String(e)))
  }, [pendingPrint])

  const openRecharge = useCallback((m: Member) => {
    setRechargeMember(m)
    // 默认金额 0、未选档位（店主 09-28 定：默认 0，可选择档位或手填）
    setRechargeAmount('0')
    setSelectedPkgId('')
    setRechargeStaffId(getLastOperatorId() || getStaff()?.id || 0)
    setRechargeBusy(false)
    setDetailMember(null)
  }, [])

  // 编辑档案：不关详情弹窗，保存后详情与列表同步刷新成最新档案
  const openEdit = useCallback((m: Member) => setEditMember(m), [])

  // 选中行：既记选中 id（高亮）又打开详情弹窗。抽成具名 handler，传给每行的引用才稳定。
  const handleSelect = useCallback((m: Member) => {
    setSelectedId(m.id)
    setDetailMember(m)
  }, [])

  // 充值金额：可选择储值方案档位（自动赠送），也可手动填写（不赠送）
  // —— 店主 09-28 定：默认 0，两种模式共存
  const selectedPkg = packages.find((p) => String(p.id) === selectedPkgId) ?? null
  const selectedGift = selectedPkg?.gift ?? 0

  const pickPkg = (id: string) => {
    setSelectedPkgId(id)
    if (id) {
      const pkg = packages.find((p) => String(p.id) === id)
      if (pkg) {
        setRechargeAmount(String(pkg.amount))
      }
    } else {
      setRechargeAmount('0')
    }
  }

  // 手动改金额时自动清除档位选择（金额不再与方案匹配，gift 归 0）
  const onAmountChange = (e: { target: { value: string } }) => {
    setRechargeAmount(e.target.value)
    setSelectedPkgId('')
  }

  const printReceipt = () => {
    apiSystem.print().catch((e) => toast.error('打印失败：' + String(e)))
  }

  const doRecharge = () => {
    const amount = Number(rechargeAmount)
    if (!rechargeMember) return
    if (!rechargeAmount || isNaN(amount) || amount <= 0) {
      toast.error('请输入大于 0 的充值金额')
      return
    }
    if (rechargeMember.status !== '正常') {
      toast.error('冻结/挂失会员不能充值')
      return
    }
    const gift = selectedGift
    const opName = staffOptions.find((s) => s.id === rechargeStaffId)?.name || getStaff()?.name || '—'
    setRechargeBusy(true)
    storedvalue
      .rechargeByStaff(rechargeMember.id, amount, gift, rechargeMethod, rechargeStaffId)
      .then((orderNo) => {
        toast.success(`已为 ${rechargeMember.name} 充值 ¥${fmtMoney(amount)}${gift > 0 ? ` 送 ¥${fmtMoney(gift)}` : ''}，单号 ${orderNo}，经手人 ${opName}，流水已入账`)
        // 记住这次的经手人：下次充值/开单/建档/售卡默认还是 TA
        if (rechargeStaffId > 0) setLastOperatorId(rechargeStaffId)
        // 渲染充值小票；打印开启则自动直打一张（无实体机自动出 PDF），否则留「打印小票」按钮
        setReceipt({ orderNo, amount, gift, memberName: rechargeMember.name, memberCard: rechargeMember.cardNo, method: rechargeMethod, operator: opName })
        setPendingPrint(printEnabled)
        setRechargeMember(null)
        reload()
      })
      .catch((e) => toast.error(String(e)))
      .finally(() => setRechargeBusy(false))
  }

  return (
    // 自适应布局（同 RecordsPage）：外层 flex 纵向撑满视口，标题/筛选/汇总 shrink-0，
    // 表格卡片 flex-1 min-h-0 + fill → 窗口多大表格都吃满，不留空白。
    <PageFill minWidth={1080}>
      <div className="flex items-end justify-between shrink-0">
        <div>
          <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            会员管理
          </h1>
        </div>
        <div className="flex gap-2">
          {/* 会员设置：等级制度等由店长调整（收银员看不到入口） */}
          {isManager() ? (
            <Button
              variant="secondary"
              onClick={() => setAdminOpen(true)}
              title="会员等级制度等设置"
            >
              <Settings size={14} /> 会员设置
            </Button>
          ) : null}
          <Button variant="primary" onClick={() => setNewOpen(true)}>
            <UserPlus size={15} /> 新建会员
          </Button>
        </div>
      </div>

      <Card className="p-3 shrink-0">
        <div className="flex items-center gap-2.5">
          <div className="relative w-72">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              placeholder="搜索：手机号 / 姓名 / 卡号"
              className="pl-8"
              aria-label="搜索会员"
            />
          </div>
          <Filter size={14} className="text-muted-foreground" />
          <Select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} aria-label="状态筛选">
            <option>全部状态</option>
            <option value="已注销">已归档</option>
            <option>正常</option>
            <option>冻结</option>
            <option>挂失</option>
          </Select>
          <Select value={tagFilter} onChange={(e) => setTagFilter(e.target.value)} aria-label="标签筛选">
            <option>全部标签</option>
            {tags.map((t) => <option key={t}>{t}</option>)}
          </Select>
          {(keyword || statusFilter !== '全部状态' || tagFilter !== '全部标签') && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setKeyword('')
                setStatusFilter('全部状态')
                setTagFilter('全部标签')
              }}
            >
              清空筛选
            </Button>
          )}
        </div>
      </Card>

      <Card className="page-body overflow-hidden flex flex-col">
        {rows.length > 0 ? (
          <>
            <div ref={bodyRef} className="flex-1 min-h-0 flex flex-col">
            <MembersTable
              fill
              rows={rows}
              selectedId={selectedId}
              onSelect={handleSelect}
              onRecharge={openRecharge}
              onEdit={openEdit}
              levels={levels}
            />
            </div>
            <div className="px-4 py-2.5 border-t border-border bg-card">
              <MembersSummary total={sum.total} balanceSum={sum.balanceSum} abnormal={sum.abnormal} deleted={sum.deleted} />
            </div>
            {page.total > 0 ? (
              <PagedBar
                className="shrink-0"
                total={page.total}
                page={page.page}
                pageCount={page.pageCount}
                pageSize={page.pageSize}
                goToPage={page.goToPage}
                onPageSizeChange={page.setPageSize}
                loading={page.loading}
              />
            ) : null}
          </>
        ) : page.loading ? (
          <div className="py-8 text-center text-xs text-muted-foreground">加载中…</div>
        ) : (
          <EmptyState
            title="未找到匹配的会员"
            desc={`没有符合「${keyword || statusFilter}」条件的会员`}
            action={
              <Button variant="primary" onClick={() => setNewOpen(true)}>
                <UserPlus size={15} /> 新建会员
              </Button>
            }
          />
        )}
      </Card>

      <MemberDetailDialog
        member={detailMember}
        onClose={() => {
          setDetailMember(null)
          setSelectedId(null)
        }}
        onRecharge={openRecharge}
        // S2：注销/核销后重取当前页（筛选/翻页状态保持不变）
        onMemberChanged={() => {
          reload()
        }}
        onStatusChange={(m, action) => {
          apiMembers
            .changeStatus(m.id, action)
            .then(() => {
              reload()
              setDetailMember(null)
            })
            .catch((e) => toast.error(String(e)))
        }}
        onEdit={openEdit}
      />

      <EditMemberDialog
        member={editMember}
        onClose={() => setEditMember(null)}
        onSaved={(m) => {
          setEditMember(null)
          reload()
          // 详情弹窗若开着同一位会员，就地换成最新档案，不用关了重开
          if (detailMember && detailMember.id === m.id) setDetailMember({ ...detailMember, ...m })
        }}
        levels={levels}
      />

      <NewMemberDialog
        open={newOpen}
        onClose={() => setNewOpen(false)}
        onCreated={(m) => {
          setNewOpen(false)
          toast.success(`会员「${m.name}」建档成功，卡号 ${m.cardNo}`)
          reload()
        }}
        staffOptions={staffOptions}
        defaultCreatorId={getLastOperatorId()}
        levels={levels}
      />

      {/* 会员设置（店长）：制度改了之后，列表的等级徽标与会员数据要跟着刷新 */}
      <MemberAdminDialog
        open={adminOpen}
        onClose={() => setAdminOpen(false)}
        onChanged={() => {
          apiLevels.list().then(setLevels).catch(() => setLevels([]))
          reload()
        }}
      />

      <Modal
        open={!!rechargeMember}
        onClose={() => setRechargeMember(null)}
        title={`会员充值 · ${rechargeMember?.name ?? ''}`}
        width={440}
        footer={
          <>
            <Button variant="secondary" onClick={() => setRechargeMember(null)}>
              取消
            </Button>
            <Button variant="primary" onClick={doRecharge}>
              确认充值
            </Button>
          </>
        }
      >
        <div className={cx('rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3.5 py-2.5 mb-4 text-xs text-foreground')}>
          当前余额 <b className="text-primary">¥{fmtMoney(rechargeMember?.balance ?? 0)}</b>
          {packages.length > 0 ? ' · 有 ' + packages.length + ' 档方案可选，也可直接手填金额' : ' · 无已配置方案，仅可手填金额'}
        </div>
        <Field label="充值方案">
          <Select value={selectedPkgId} onChange={(e) => pickPkg(e.target.value)}>
            <option value="">手动填写（无赠送）</option>
            {packages.map((p) => (
              <option key={p.id} value={String(p.id)}>
                {`充 ¥${fmtMoney(p.amount)} 送 ¥${fmtMoney(p.gift)}`}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="充值金额（元）" required>
          <Input
            value={rechargeAmount}
            onChange={onAmountChange}
            placeholder="0"
            className="tabular-nums"
            inputMode="decimal"
          />
        </Field>
        {selectedPkg ? (
          <div className="mt-2 rounded-[var(--radius-sm)] bg-primary/5 border border-primary/20 px-3 py-2 text-xs">
            按方案：充 <b className="text-primary">¥{fmtMoney(selectedPkg.amount)}</b> 送 <b className="text-primary">¥{fmtMoney(selectedPkg.gift)}</b>
            {selectedPkg.gift > 0
              ? <span className="ml-1 text-muted-foreground">（赠送比例 {((selectedPkg.gift / selectedPkg.amount) * 100).toFixed(1)}%）</span>
              : <span className="ml-1 text-muted-foreground">（该档位无赠送）</span>}
          </div>
        ) : (
          Number(rechargeAmount) > 0 ? (
            <div className="mt-2 rounded-[var(--radius-sm)] bg-muted/60 border border-input px-3 py-2 text-xs text-muted-foreground">
              手动金额：¥{fmtMoney(Number(rechargeAmount))}，本次不赠送（如需按方案赠送请下拉选择档位）
            </div>
          ) : null
        )}
        <Field label="收款方式">
          <Select value={rechargeMethod} onChange={(e) => setRechargeMethod(e.target.value)}>
            <option>现金</option>
            <option>微信</option>
            <option>支付宝</option>
          </Select>
        </Field>
        <p className="text-xs text-muted-foreground mt-3">
          金额与赠送金额同事务写入流水并更新余额，可在会员详情「余额对账」中逐笔核对。
        </p>
      </Modal>

      {/* 充值成功小票区：挂在组件根级（不被 Modal 的 overflow 裁剪），平时 hidden；
          @media print / 独立临时页打印时只显示这一块；打印由 system.Print → WebView2 原生打印触发，
          与收银台同一套机制（自适应纸宽、1:1 不裁）。pendingPrint 自动打印即抓此块，不再走整页兜底。 */}
      {receipt ? (
        <div className="receipt-print hidden">
          <div className="mx-auto w-[300px] px-3 py-3 text-[12px] leading-relaxed text-black bg-white">
            <div className="text-center">
              <div className="text-[15px] font-bold">{shopName || '会员店'}</div>
              <div className="text-[11px] text-neutral-600">充 值 收 据</div>
            </div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="text-[11px] text-neutral-600 flex justify-between">
              <span>单号 {receipt.orderNo}</span>
              <span>{new Date().toLocaleString('zh-CN')}</span>
            </div>
            <div className="text-[11px] text-neutral-600">
              会员：{receipt.memberName}（{receipt.memberCard}）
            </div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="flex justify-between"><span>充值金额</span><span className="tabular-nums">¥{fmtMoney(receipt.amount)}</span></div>
            {receipt.gift > 0 ? (
              <div className="flex justify-between"><span>赠送金额</span><span className="tabular-nums">¥{fmtMoney(receipt.gift)}</span></div>
            ) : null}
            <div className="flex justify-between"><span>收款方式</span><span>{receipt.method}</span></div>
            <div className="border-t border-dashed border-neutral-400 my-2" />
            <div className="text-center text-[11px] text-neutral-600 whitespace-pre-wrap">
              操作员 {receipt.operator || '—'}
            </div>
            <div className="text-center text-[11px] text-neutral-600 mt-1">谢谢惠顾</div>
          </div>
        </div>
      ) : null}
    </PageFill>
  )
}
