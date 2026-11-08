import { memo, useMemo } from 'react'
import { Button, StatusTag, Badge, DataTable, tdCls, trEvenCls, trSelectedCls, numCls, cx, LIST_MAX_H } from '@/components/common/ui'
import type { DataTableCol } from '@/components/common/ui'
import { fmtMoney } from '@/data/mock'
import type { Member, MemberLevel } from '@/data/mock'

// 本页 12 列比别的列表页宽。MemberRow 的 td 用 px-2 收紧一格（td = tdCls 的 px-3 变 px-2），
// DataTable 配 compact 让 thead 也跟着 px-2，两行对齐。列宽预算合计 ≈1010px，
// 在最小窗口 main 可用宽（约 1048px）内不横滚，最右「操作」列点得到。
const td = tdCls.replace('px-3', 'px-2')

/** 12 列宽度预算（px-2 紧凑，合计 ≈1010px） */
const MEMBER_COLS: DataTableCol[] = [
  { key: 'cardNo', title: '卡号', width: 92, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'name', title: '姓名 / 建档人', width: 124, cellClass: 'font-semibold' },
  { key: 'phone', title: '手机号', width: 104, cellClass: 'tabular-nums' },
  { key: 'level', title: '等级', width: 78 },
  { key: 'tags', title: '标签', width: 82 },
  { key: 'balance', title: '余额（元）', width: 90, headClass: 'text-right', cellClass: numCls },
  { key: 'points', title: '积分', width: 66, headClass: 'text-right', cellClass: numCls },
  { key: 'cardRemain', title: '次卡剩余', width: 96, headClass: 'text-right', cellClass: numCls },
  { key: 'status', title: '状态', width: 62 },
  { key: 'lastVisit', title: '最近到店', width: 70, cellClass: 'tabular-nums text-muted-foreground' },
  { key: 'action', title: '操作', width: 146, headClass: 'text-right', cellClass: 'text-right' },
]

/** 最近到店只留到「天」：掐掉时分秒，再取月-日（2026-09-26 14:32:10 -> 09-26）。
 *  店主 11-04：以前只切年份没切时间，栏里显示成 11-04 14:32:10 又长又挤到重叠。 */
function shortDate(s: string): string {
  if (!s) return ''
  const day = s.slice(0, 10)          // 先只要日期部分
  return day.length >= 10 ? day.slice(5) : day
}

/**
 * 等级徽标配色按「启用等级中的档位」推算，不再写死「钻石卡 / 金卡」这两个名字——
 * 店主改制度（改名、增删档位）后列表不用再改代码。
 * 最高两档 solid / primary，其余 muted。
 */
function levelTone(name: string, enabled: MemberLevel[]): string {
  const rank = enabled.findIndex((l) => l.name === name)
  if (rank < 0) return 'muted'
  if (enabled.length >= 2 && rank === enabled.length - 1) return 'solid'
  if (enabled.length >= 2 && rank === enabled.length - 2) return 'primary'
  return 'muted'
}

/** 悬停提示：这档的门槛 + 这位会员实际累计充了多少（赠送不计入） */
function levelTip(m: Member, enabled: MemberLevel[]): string {
  const base = `累计实收充值 ¥${fmtMoney(m.totalRecharge)}`
  if (!m.level) return `${base}（当前无可用等级档位）`
  const lv = enabled.find((l) => l.name === m.level)
  return lv ? `「${lv.name}」门槛 ¥${fmtMoney(lv.minRecharge)} · ${base}` : `${base}（该等级已从制度中移除）`
}

export function MembersTable({
  rows,
  selectedId,
  onSelect,
  onRecharge,
  onEdit,
  levels = [],
  fill = false,
}: {
  rows: Member[]
  selectedId: number | null
  onSelect: (m: Member) => void
  onRecharge: (m: Member) => void
  onEdit: (m: Member) => void
  /** 等级制度（全部）：只取启用档位决定徽标配色 */
  levels?: MemberLevel[]
  /** 填满父容器剩余高度（自适应页面，配合外层 flex 布局用） */
  fill?: boolean
}) {
  // 启用档位排序结果按 levels 引用缓存 —— 直接每次重算会让每一行都拿到新数组，
  // React.memo 就永远命中不了。levels 由页面 state 提供，引用稳定。
  const enabled = useMemo(
    () => levels.filter((l) => l.enabled).sort((a, b) => a.minRecharge - b.minRecharge || a.id - b.id),
    [levels],
  )
  return (
    <DataTable
      cols={MEMBER_COLS}
      rows={rows}
      rowKey={(m) => m.id}
      compact
      fill={fill}
      renderRow={(m) => (
        <MemberRow
          key={m.id}
          m={m}
          selected={selectedId === m.id}
          onSelect={onSelect}
          onRecharge={onRecharge}
          onEdit={onEdit}
          enabled={enabled}
        />
      )}
      empty={<div className="py-10 text-center text-sm text-muted-foreground">还没有会员，去「会员管理」建档第一位</div>}
    />
  )
}

/**
 * 单行记忆化：打字筛选时 rows 变的是「哪些行显示」，已经显示的行对象引用不变，
 * 因此 memo 命中、不重渲。前提是四个回调必须由父级 useCallback 提供稳定引用。
 */
const MemberRow = memo(function MemberRow({
  m,
  selected,
  onSelect,
  onRecharge,
  onEdit,
  enabled,
}: {
  m: Member
  selected: boolean
  onSelect: (m: Member) => void
  onRecharge: (m: Member) => void
  onEdit: (m: Member) => void
  enabled: MemberLevel[]
}) {
  return (
    <tr onClick={() => onSelect(m)} className={cx(trEvenCls, selected && trSelectedCls)}>
      <td className={cx(td, 'tabular-nums text-muted-foreground')}>{m.cardNo}</td>
      <td className={cx(td, 'font-semibold')}>
        <span className="flex items-center gap-1">
          <span className="w-6 h-6 rounded-full bg-primary/10 text-primary flex items-center justify-center text-[11px] font-semibold shrink-0">
            {m.name.slice(0, 1)}
          </span>
          <span className="leading-tight">
            <span className="block">{m.name}</span>
            <span
              title={m.createdByName ? `建档经办人：${m.createdByName}` : '建档经办人：历史档案'}
              className="block text-muted-foreground/70 font-normal text-xs"
            >
              {m.createdByName || '历史档案'}
            </span>
          </span>
        </span>
      </td>
      <td className={cx(td, 'tabular-nums')}>{m.phone}</td>
      <td className={td}>
        <span title={levelTip(m, enabled)} className="inline-flex">
          {/* 店主 10-15：一档都没启用时新会员等级落空白，显示「无」而不是幻影档名 */}
          <Badge tone={levelTone(m.level, enabled)}>{m.level || '无'}</Badge>
        </span>
      </td>
      <td className={td}>
        <span className="flex gap-1">
          {m.tags.length === 0 ? (
            <span className="text-muted-foreground/60 text-xs">—</span>
          ) : (
            m.tags.map((t) => (
              <Badge key={t} tone={t === '沉睡' ? 'danger' : 'muted'}>
                {t}
              </Badge>
            ))
          )}
        </span>
      </td>
      <td className={cx(td, numCls, m.status !== '正常' && 'text-muted-foreground')}>
        {m.status === '挂失' ? '——' : fmtMoney(m.balance)}
      </td>
      <td className={cx(td, numCls)}>{m.points.toLocaleString('zh-CN')}</td>
      <td className={cx(td, numCls)}>
        {m.cardRemain > 0 ? (
          <span title={`${m.cardRemain} 次（${m.cardName}）`}>
            <span className="block leading-tight">{m.cardRemain} 次</span>
            <span className="block text-muted-foreground font-normal text-xs leading-tight">{m.cardName}</span>
          </span>
        ) : (
          <span className="text-muted-foreground/60 font-normal">—</span>
        )}
      </td>
      <td className={td}>
        {m.deletedAt ? <Badge tone="muted">已归档</Badge> : <StatusTag status={m.status} />}
      </td>
      <td className={cx(td, 'tabular-nums text-muted-foreground')}>
        {m.lastVisit ? <span title={m.lastVisit}>{shortDate(m.lastVisit)}</span> : '—'}
      </td>
      <td className={cx(td, 'text-right')}>
        <span className="inline-flex gap-1" onClick={(e) => e.stopPropagation()}>
          <Button size="sm" variant="ghost" onClick={() => onSelect(m)}>
            详情
          </Button>
          {/* 已注销会员：只读。充值/编辑都不给点（后端也会拦），记录全留可查 */}
          {m.deletedAt ? (
            <span className="text-[11px] text-muted-foreground self-center">已归档，仅可查</span>
          ) : (
            <>
              <Button size="sm" variant="ghost" onClick={() => onEdit(m)}>
                编辑
              </Button>
              <Button
                size="sm"
                variant="secondary"
                disabled={m.status !== '正常'}
                onClick={() => onRecharge(m)}
              >
                充值
              </Button>
            </>
          )}
        </span>
      </td>
    </tr>
  )
})

/**
 * L2 会员列表分页（店主 10-18）：汇总条吃**后端全量统计**（Total/BalanceSum/Abnormal/Deleted），
 * 不再从当前页 rows 本地 reduce —— 否则会员多于 10 位时「共 N 位 / 余额合计」会随翻页缩水。
 */
export function MembersSummary({
  total,
  balanceSum,
  abnormal,
  deleted,
}: {
  total: number
  balanceSum: number
  abnormal: number
  deleted: number
}) {
  return (
    <div className="flex items-center gap-4 text-xs text-muted-foreground px-1">
      <span>
        共 <b className="text-foreground tabular-nums">{total}</b> 位会员
      </span>
      <span>
        余额合计 <b className="text-foreground tabular-nums">¥{fmtMoney(balanceSum)}</b>
      </span>
      {abnormal > 0 ? (
        <span>
          异常状态 <b className="text-destructive tabular-nums">{abnormal}</b> 位（冻结/挂失）
        </span>
      ) : null}
      {deleted > 0 ? (
        <span>
          已归档 <b className="text-muted-foreground tabular-nums">{deleted}</b> 位（余额已清理，记录仍可查）
        </span>
      ) : null}
    </div>
  )
}
