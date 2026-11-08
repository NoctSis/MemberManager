import { memo } from 'react'
import { Users, ShieldCheck, Search, Trash2 } from 'lucide-react'
import {
  Button, SectionCard, Badge, Input, DataTable,
  tdCls, cx,
} from '@/components/common/ui'
import { LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { fmtMoney } from '@/data/mock'
import type { StaffAccount } from '@/data/mock'
import { pctOf } from './staffShared'

/** 列宽预算：最小窗口 main 可用宽约 1080px，7 列合计 950px，余量留给弹性伸缩。 */
const COLS: DataTableCol[] = [
  { key: 'name', title: '姓名', width: 110 },
  { key: 'role', title: '角色', width: 120 },
  { key: 'phone', title: '手机号', width: 130, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'rate', title: '提成', width: 130, headClass: 'text-right', cellClass: 'text-right' },
  { key: 'orders', title: '交易记录', width: 170, headClass: 'text-right', cellClass: 'text-right' },
  { key: 'status', title: '状态', width: 90 },
  { key: 'action', title: '操作', width: 200, headClass: 'text-right', cellClass: 'text-right' },
]

/**
 * 单行记忆化：搜索时 filteredRows 变的是「哪些行显示」，已显示行的对象引用不变，
 * 四个回调（含 onDelete = setDelTarget）都是稳定引用，所以 memo 命中、不重渲。
 * ⚠ 依赖这一点：页面侧传进来的 handler 必须都是 useCallback / setState 这类稳定引用。
 */
const StaffRow = memo(function StaffRow({
  s,
  onViewRecords,
  onEdit,
  onToggle,
  onDelete,
  globalPct,
  overrideCount,
}: {
  s: StaffAccount
  onViewRecords: (s: StaffAccount) => void
  onEdit: (s: StaffAccount) => void
  onToggle: (s: StaffAccount) => void
  onDelete: (s: StaffAccount) => void
  /** T2 tooltip：该员工沿用全局时，展示当前全局「充值 / 消费」值（字符串值 prop，memo 安全） */
  globalPct: string
  /** §三十二 32.7.5：该员工已单独覆盖的提成项数（0 = 全部沿用项目默认） */
  overrideCount: number
}) {
  return (
    <tr className="even:bg-muted/40">
      <td className={cx(tdCls, 'font-medium')}>
        {s.name}
        {/* 姓名可以重复，登录账号唯一 —— 同名员工在这里就能看出谁是谁（店主 11-05） */}
        {s.loginName && s.loginName !== s.name ? (
          <span className="block text-[10px] font-normal text-muted-foreground tabular-nums" title="登录账号（全库唯一，登录时输它）">
            {s.loginName}
          </span>
        ) : null}
      </td>
      <td className={tdCls}>
        <Badge tone={s.role === '店长' ? 'primary' : 'muted'}>
          {s.role === '店长' ? <ShieldCheck size={11} className="mr-1" /> : null}
          {s.role}
        </Badge>
      </td>
      <td className={cx(tdCls, 'tabular-nums text-muted-foreground')}>{s.phone || '—'}</td>
      <td className={cx(tdCls, 'text-right')}>
        {overrideCount > 0 ? (
          <Badge tone="primary">{overrideCount} 项覆盖</Badge>
        ) : (
          <span
            className="text-muted-foreground text-[11px]"
            title={
              globalPct
                ? `未单独设，按各项目默认计提（全局兜底：充值 ${globalPct.split(' / ')[0]} · 消费 ${globalPct.split(' / ')[1]}）`
                : '未单独设，沿用各项目默认提成'
            }
          >
            沿用项目默认
          </span>
        )}
      </td>
      <td className={cx(tdCls, 'text-right')}>
        {s.orders ? (
          <button
            type="button"
            onClick={() => onViewRecords(s)}
            title="到消费记录里看这个人经手的单据"
            className="tabular-nums text-primary hover:underline"
          >
            {s.orders} 单 · ¥{fmtMoney(s.amount ?? 0)}
          </button>
        ) : (
          <span className="tabular-nums text-muted-foreground">—</span>
        )}
      </td>
      <td className={tdCls}>
        {s.enabled ? <Badge tone="primary">启用</Badge> : <Badge tone="danger">已停用</Badge>}
      </td>
      <td className={cx(tdCls, 'text-right')}>
        <span className="inline-flex gap-1">
          <Button variant="ghost" size="sm" onClick={() => onEdit(s)}>
            编辑
          </Button>
          <Button variant="ghost" size="sm" onClick={() => onToggle(s)}>
            {s.enabled ? '停用' : '启用'}
          </Button>
          {/* 员工页只管「做项目的人」：删除只影响此处的登记，登录账号在「系统设置·账号密码」独立维护，
              两边靠同名账号关联但不互相牵连（店主 10-24 明确两者要分开）。 */}
          <Button variant="ghost" size="sm" onClick={() => onDelete(s)}>
            <Trash2 size={12} /> 删除
          </Button>
        </span>
      </td>
    </tr>
  )
})

/** 「员工名单」卡片：标题计数 + 搜索框 + 表格 + 底部说明。 */
export function StaffTableCard({
  total,
  activeCount,
  keyword,
  filteredRows,
  globalRates,
  overrideCounts,
  onKeywordChange,
  onViewRecords,
  onEdit,
  onToggle,
  onDelete,
}: {
  total: number
  activeCount: number
  keyword: string
  filteredRows: StaffAccount[]
  globalRates: { recharge: number; consume: number } | null
  overrideCounts: Record<number, number>
  onKeywordChange: (v: string) => void
  onViewRecords: (s: StaffAccount) => void
  onEdit: (s: StaffAccount) => void
  onToggle: (s: StaffAccount) => void
  onDelete: (s: StaffAccount) => void
}) {
  return (
    <SectionCard
      title={
        <span className="flex items-center gap-1.5">
          <Users size={14} className="text-primary" /> 员工名单
          <span className="ml-1 text-[11px] font-normal text-muted-foreground">
            共 {total} 人，启用 {activeCount} 人
          </span>
        </span>
      }
      extra={
        <span className="relative block">
          <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={keyword}
            onChange={(e) => onKeywordChange(e.target.value)}
            placeholder="搜索姓名 / 手机号 / 角色"
            aria-label="搜索员工"
            className="h-7 w-48 pl-7 text-xs"
          />
        </span>
      }
      bodyClassName="p-0"
    >
      <DataTable
        cols={COLS}
        rows={filteredRows}
        rowKey={(s) => s.id}
        maxHeight={LIST_MAX_H}
        renderRow={(s) => (
          <StaffRow
            key={s.id}
            s={s}
            onViewRecords={onViewRecords}
            onEdit={onEdit}
            onToggle={onToggle}
            onDelete={onDelete}
            globalPct={globalRates ? `${pctOf(globalRates.recharge)} / ${pctOf(globalRates.consume)}` : ''}
            overrideCount={overrideCounts[s.id] ?? 0}
          />
        )}
        empty={
          <div className="py-8 text-center text-sm text-muted-foreground">
            {keyword.trim() ? '没有匹配的员工，换个关键词试试' : '还没有员工，点右上角「添加员工」登记第一个人'}
          </div>
        }
      />
      <div className="px-4 py-2.5 border-t border-border text-[11px] text-muted-foreground truncate">
        做项目的人在此登记；登录账号去「系统设置 · 登录账号」
      </div>
    </SectionCard>
  )
}