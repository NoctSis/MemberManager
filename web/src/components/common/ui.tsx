import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'
import { X, Inbox } from 'lucide-react'
import { createPortal } from 'react-dom'
import type { ReactNode, ButtonHTMLAttributes, InputHTMLAttributes, SelectHTMLAttributes } from 'react'
import { useRef } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'

export function cx(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

// ---------- 按钮（三态完整：默认 / hover / disabled，主按钮 focus 环） ----------
type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: 'sm' | 'md' | 'lg'
}

const variantCls: Record<string, string> = {
  primary:
    'bg-primary text-primary-foreground hover:bg-primary/90 active:bg-primary/95 disabled:bg-primary/40 disabled:text-primary-foreground/70 shadow-sm',
  secondary:
    'bg-card text-foreground border border-input hover:bg-accent hover:text-accent-foreground active:bg-muted disabled:opacity-50 disabled:cursor-not-allowed',
  ghost:
    'bg-transparent text-muted-foreground hover:bg-accent hover:text-accent-foreground active:bg-muted disabled:opacity-40 disabled:cursor-not-allowed',
  danger:
    'bg-destructive text-destructive-foreground hover:bg-destructive/90 active:bg-destructive/95 disabled:bg-destructive/40',
}

const sizeCls: Record<string, string> = {
  sm: 'h-7 px-2.5 text-xs rounded-[var(--radius-sm)] gap-1',
  md: 'h-9 px-3.5 text-sm rounded-[var(--radius-sm)] gap-1.5',
  lg: 'h-11 px-5 text-base rounded-[var(--radius-md)] gap-2',
}

export function Button({ variant = 'secondary', size = 'md', className, ...rest }: ButtonProps) {
  return (
    <button
      className={cx(
        'inline-flex items-center justify-center font-medium transition-colors select-none',
        'focus-visible:outline-none focus-visible:shadow-[var(--focus-ring)]',
        'disabled:cursor-not-allowed',
        variantCls[variant],
        sizeCls[size],
        className,
      )}
      {...rest}
    />
  )
}

// ---------- 卡片容器 ----------
export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return (
    <div className={cx('bg-card rounded-[var(--radius-md)] shadow-[var(--elev-ring)]', className)}>
      {children}
    </div>
  )
}

export function SectionCard({
  title,
  extra,
  className,
  bodyClassName,
  children,
}: {
  title: ReactNode
  extra?: ReactNode
  className?: string
  bodyClassName?: string
  children: ReactNode
}) {
  return (
    <Card className={cx('flex flex-col', className)}>
      <div className="flex items-center justify-between px-4 h-11 border-b border-border shrink-0">
        <h3 className="text-sm font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
          {title}
        </h3>
        {extra ? <div className="flex items-center gap-2">{extra}</div> : null}
      </div>
      {/* bodyClassName 带 p-* 时跳过默认 p-4 —— Tailwind 同特异性时看 CSS 顺序，
          p-0 不一定赢 p-4，所以干脆不同时出现（店主 11-04：页码条被 p-4 顶出可视区） */}
      <div className={cx('flex-1 min-h-0', /(^|\s)p-[0-9]/.test(bodyClassName || '') ? '' : 'p-4', bodyClassName)}>{children}</div>
    </Card>
  )
}

// ---------- 状态徽标 ----------
const toneCls: Record<string, string> = {
  primary: 'bg-primary/10 text-primary',
  muted: 'bg-muted text-muted-foreground',
  danger: 'bg-destructive/10 text-destructive',
  solid: 'bg-primary text-primary-foreground',
}

export function Badge({ tone = 'muted', className, children }: { tone?: string; className?: string; children: ReactNode }) {
  return (
    <span
      className={cx(
        'inline-flex items-center px-2 h-5 rounded-[var(--radius-pill)] text-xs font-medium whitespace-nowrap',
        toneCls[tone] ?? toneCls.muted,
        className,
      )}
    >
      {children}
    </span>
  )
}

export function StatusTag({ status }: { status: string }) {
  const tone = status === '正常' ? 'primary' : status === '挂失' ? 'danger' : 'muted'
  return <Badge tone={tone}>{status}</Badge>
}

// ---------- 表单控件 ----------
export function Input({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      className={cx(
        'h-9 w-full px-3 text-sm bg-card text-foreground rounded-[var(--radius-sm)]',
        'border border-input placeholder:text-muted-foreground/70',
        'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)]',
        'disabled:bg-muted disabled:text-muted-foreground disabled:cursor-not-allowed',
        'transition-shadow',
        className,
      )}
      {...rest}
    />
  )
}

export function Select({ className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      className={cx(
        'h-9 px-2.5 text-sm bg-card text-foreground rounded-[var(--radius-sm)] border border-input',
        'focus:outline-none focus:border-primary focus:shadow-[var(--focus-ring)] transition-shadow',
        className,
      )}
      {...rest}
    >
      {children}
    </select>
  )
}

export function Field({
  label,
  required,
  error,
  hint,
  children,
}: {
  label: string
  required?: boolean
  error?: string
  hint?: string
  children: ReactNode
}) {
  return (
    <label className="block">
      <span className="block text-sm text-foreground mb-1.5 font-medium">
        {label}
        {required ? <span className="text-destructive ml-0.5">*</span> : null}
      </span>
      {children}
      {hint && !error ? (
        <span className="block text-[11px] text-muted-foreground mt-1">{hint}</span>
      ) : null}
      {error ? (
        <span className="block text-xs text-destructive mt-1.5" role="alert">
          {error}
        </span>
      ) : null}
    </label>
  )
}

// ---------- 弹窗（居中模态， elevated 阴影） ----------
// 固定定位的遮罩若渲染在树内的可滚动祖先（AppShell <main class="overflow-y-auto">）里，
// WebView2 下 position:fixed 会退化到该祖先的视口，导致遮罩盖不全整屏（顶栏/底栏漏出）、
// 弹窗还只在内容区居中（顶部一大片空隙）。统一 createPortal 到 <body>，让 fixed inset-0 永远=整屏。
export function Modal({
  open = true,
  onClose,
  title,
  width = 560,
  footer,
  children,
}: {
  open?: boolean
  onClose: () => void
  title: string
  width?: number
  footer?: ReactNode
  children: ReactNode
}) {
  if (!open) return null
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-foreground/50 backdrop-blur-[2px]" onClick={onClose} aria-hidden />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="relative bg-card rounded-[var(--radius-md)] shadow-[var(--elev-raised)] flex flex-col max-h-[85vh] modal-in"
        style={{ width, maxWidth: 'calc(100vw - 48px)' }}
      >
        <div className="flex items-center justify-between px-5 h-12 border-b border-border shrink-0">
          <h3 className="text-base font-semibold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
            {title}
          </h3>
          <button
            onClick={onClose}
            aria-label="关闭"
            className="text-muted-foreground hover:text-foreground hover:bg-accent rounded-[var(--radius-sm)] p-1 transition-colors"
          >
            <X size={16} />
          </button>
        </div>
        <div className="px-5 py-4 overflow-y-auto min-h-0">{children}</div>
        {footer ? (
          <div className="flex items-center justify-end gap-2 px-5 h-14 border-t border-border shrink-0">
            {footer}
          </div>
        ) : null}
      </div>
    </div>,
    document.body,
  )
}

// ---------- 空状态 ----------
export function EmptyState({
  title,
  desc,
  action,
}: {
  title: string
  desc?: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-col items-center justify-center py-14 text-center">
      <div className="w-12 h-12 rounded-[var(--radius-md)] bg-muted flex items-center justify-center text-muted-foreground mb-3">
        <Inbox size={22} />
      </div>
      <p className="text-sm font-medium text-foreground">{title}</p>
      {desc ? <p className="text-xs text-muted-foreground mt-1 max-w-64">{desc}</p> : null}
      {action ? <div className="mt-4">{action}</div> : null}
    </div>
  )
}

// ---------- 加载骨架（异步操作超 1 秒必须出现的进度提示） ----------
export function Skeleton({ className }: { className?: string }) {
  return <div className={cx('animate-pulse rounded-[var(--radius-sm)] bg-muted', className)} />
}

export function PageSkeleton() {
  return (
    <div className="p-5 space-y-4" aria-label="加载中">
      <div className="grid grid-cols-4 gap-4">
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} className="h-24" />
        ))}
      </div>
      <div className="grid grid-cols-3 gap-4">
        <Skeleton className="h-64 col-span-2" />
        <Skeleton className="h-64" />
      </div>
      <Skeleton className="h-40" />
    </div>
  )
}

// ---------- 指标卡 ----------
export function MetricCard({
  label,
  value,
  delta,
  icon,
  prefix = '¥',
}: {
  label: string
  value: string
  /** up 表示方向（▲/▼ + 红绿）。neutral=true 时不加箭头、用中性灰——适用于「X 笔 / 客单价 / 累计 N 人」这类无涨跌含义的说明文字 */
  delta?: { text: string; up: boolean; neutral?: boolean }
  icon: ReactNode
  prefix?: string
}) {
  return (
    <Card className="p-4">
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <p className="text-xs text-muted-foreground">{label}</p>
          <p
            className="text-2xl font-bold text-foreground mt-1.5 tabular-nums min-w-0 break-words"
            style={{ fontFamily: 'var(--font-display)' }}
          >
            {prefix ? <span className="text-sm font-medium text-muted-foreground mr-0.5">{prefix}</span> : null}
            {value}
          </p>
          {delta ? (
            <p className={cx('text-xs mt-1.5 font-medium', delta.neutral ? 'text-muted-foreground' : delta.up ? 'text-primary' : 'text-destructive')}>
              {delta.neutral ? null : delta.up ? '▲ ' : '▼ '}
              {delta.text}
            </p>
          ) : null}
        </div>
        <div className="w-9 h-9 rounded-[var(--radius-sm)] bg-primary/10 text-primary flex items-center justify-center shrink-0">
          {icon}
        </div>
      </div>
    </Card>
  )
}

// ---------- 数据表通用样式类（固定行高、交替行、选中主题色、金额右对齐） ----------
export const thCls =
  'h-9 px-3 text-left text-xs font-semibold text-muted-foreground bg-muted border-b border-border whitespace-nowrap'
export const tdCls = 'h-10 px-3 text-sm text-foreground border-b border-border/60 whitespace-nowrap'
export const trEvenCls = 'even:bg-muted/40 hover:bg-accent/70 transition-colors cursor-pointer'
export const trSelectedCls = 'bg-primary/10 hover:bg-primary/10'
export const numCls = 'text-right tabular-nums font-medium'

/**
 * 长列表表格高度上限（全站共用）。
 * 店主 10-24 反馈：原先各处写死 436px，窗口一放大表格不跟着长，页面下方空一大块。
 * 现改为：436px 保底（最小窗口约 10 行），随窗口高度增长，上限 1200px 防无限拉长。
 * 100vh = 视口高；减 300px 是给「标题栏 + 筛选区 + 汇总指标卡 + 页码条 + 内边距」留的位置。
 * 单页有额外区块要占更高时，可自行传更大的字符串值覆盖。
 */
export const LIST_MAX_H = 'max(436px, min(1200px, calc(100vh - 300px)))'

// ---------- 列宽三分类工具 ----------
// 最小窗口 main 可用宽约 1080px（见 skill §1）。列宽按三类分配：
//   1) 固定窄列  → 传 width 数字（如 90），不参与伸缩
//   2) 弹性列    → 传 width + truncate，超出省略并挂 title 供 hover 看全文
//   3) 数值列    → 用 numCls（右对齐 + tabular-nums），按最大位数预留宽度
//
// 用法：
//   const cols: DataTableCol[] = [
//     { key: 'no',    width: 120, cellClass: 'font-medium' },
//     { key: 'item',  width: 220, truncate: true },     // 弹性列，超出省略
//     { key: 'amt',   width: 110, cellClass: numCls },  // 数值列
//   ]
//   <DataTable cols={cols} rows={rows} rowKey={r => r.id} maxHeight={520}
//     render={r => [r.no, r.item, <Money v={r.amt} />]} />
export interface DataTableCol {
  /** 列的唯一键，用于 <colgroup>/表头对齐 */
  key: string
  /** 表头文案 */
  title: ReactNode
  /** 固定像素宽；不传则按内容自适应。数值列请按最大位数预留 */
  width?: number
  /** 该列超出宽度时省略号截断（弹性列的推荐做法） */
  truncate?: boolean
  /** 表头类名覆盖 */
  headClass?: string
  /** 单元格类名（如 numCls 让金额右对齐） */
  cellClass?: string
}

/**
 * 统一数据表外壳：横向溢出滚动 + 表头吸顶 + 可选高度上限。
 *
 * - overflow-x-auto：列宽不足时横向滚动，绝不压缩列内容（最小窗口 1080px 的兜底）
 * - sticky top-0 z-10：配合 maxHeight 时表头不随内容滚走
 * - maxHeight：给列表页一个高度上限，超出内部纵向滚动 → 页面不再无限下拉
 */
export function DataTable<T>({
  cols,
  rows,
  render,
  renderRow,
  rowKey,
  rowOnClick,
  plain,
  footRow,
  compact,
  maxHeight,
  className,
  tableClassName,
  rowClassName,
  empty,
  headExtra,
  fill,
}: {
  cols: DataTableCol[]
  rows: T[]
  /** 每行返回与 cols 等长的单元格数组（与 renderRow 二选一） */
  render?: (row: T, index: number) => ReactNode[]
  /**
   * 整行渲染（与 render 二选一）。用于行组件已 memo 的场景：
   * 直接返回 <tr>…</tr>，避开每次重算 cols 数组、保住 memo 命中。
   */
  renderRow?: (row: T, index: number) => ReactNode
  rowKey: (row: T, index: number) => string | number
  /** 整行点击回调（如打开详情）；传了会给行加 cursor-pointer */
  rowOnClick?: (row: T, index: number) => void
  /** 内容区最大高度（px）；传了就内部纵向滚动、表头吸顶 */
  /** 高度上限：给列表页一个高度上限，超出内部纵向滚动 → 页面不再无限下拉。
   *  number = 像素；string = 任意 CSS 长度（如 'max(436px, calc(100vh - 300px))' 可随窗口自适应） */
  maxHeight?: number | string
  className?: string
  tableClassName?: string
  /** 行类名，可传函数按行返回（如高亮选中行）；renderRow 模式下由行组件自行负责 */
  rowClassName?: string | ((row: T, index: number) => string)
  /** 纯展示表（无整行点击）：true 时行只用 `even:bg-muted/40`，
   * 不加 cursor-pointer / hover 底色。适合营销小表这类"行不可点"的场景。
   */
  plain?: boolean
  /** 数据行之后的整行（如"合计"行）：渲染在 tbody 末尾、不参与行高亮/点击。 */
  footRow?: ReactNode
  /** 紧凑内边距（px-2 而非 px-3）：列很多（如 12 列会员表）时收紧一格放进视口。 */
  compact?: boolean
  /** 无数据时的占位（放在 tbody 外，居中显示） */
  empty?: ReactNode
  /** 表头右侧追加内容（如合计说明） */
  headExtra?: ReactNode
  /**
   * 填满父容器剩余空间（自适应页面高度）。
   * 用法：外层容器 `flex flex-col h-[calc(100vh-2.5rem)]`，本组件外层套 `flex-1 min-h-0`，
   * 再把 fill 打开 → 表格自动吃掉「标题/筛选/汇总/页码条」之外的所有高度，窗口多大都不留空白。
   * 与 maxHeight 互斥（fill 优先）；fill 打开时不需要再传 maxHeight。
   */
  fill?: boolean
}) {
  const hasRows = rows.length > 0
  const scrollRef = useRef<HTMLDivElement>(null)

  // TanStack Virtual：只渲染可见行，大数据量不卡。
  // 行高固定 40px（tdCls h-10），虚拟滚动最稳的场景。
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => 40,
    overscan: 8,
    enabled: rows.length > 30, // 行数少时不用虚拟（DOM 全量渲染反而省事）
  })

  const virtualItems = virtualizer.getVirtualItems()
  const virtualMode = rows.length > 30
  const visibleRows = virtualMode
    ? virtualItems.map((vi) => ({ idx: vi.index, row: rows[vi.index], vi }))
    : rows.map((row, i) => ({ idx: i, row, vi: null }))

  return (
    <div className={cx('relative', fill && 'flex flex-1 min-h-0 flex-col', className)}>
      <div
        ref={scrollRef}
        className={cx('overflow-x-hidden', fill && 'flex-1 min-h-0 overflow-y-auto')}
        style={!fill && maxHeight ? { maxHeight, overflowY: 'auto' } : undefined}
      >
        <table className={cx('w-full border-collapse table-fixed', tableClassName)}>
          <colgroup>
            {cols.map((c) => (
              <col key={c.key} style={c.width ? { width: c.width } : undefined} />
            ))}
          </colgroup>
          <thead className="sticky top-0 z-10">
            <tr>
              {cols.map((c) => (
                <th key={c.key} className={cx(thCls, c.headClass, compact && 'px-2')}>
                  {c.title}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {virtualMode ? (
              // 虚拟滚动：上占位 + 可见行 + 下占位
              <>
                {virtualItems.length > 0 && (
                  <tr aria-hidden style={{ height: virtualItems[0]?.start ?? 0 }}>
                    <td colSpan={cols.length} style={{ padding: 0, border: 0 }} />
                  </tr>
                )}
                {renderRow
                  ? virtualItems.map((vi) => renderRow(rows[vi.index], vi.index))
                  : virtualItems.map((vi) => {
                      const row = rows[vi.index]
                      const rc = typeof rowClassName === 'function' ? rowClassName(row, vi.index) : rowClassName
                      return (
                        <tr
                          key={rowKey(row, vi.index)}
                          onClick={rowOnClick ? () => rowOnClick(row, vi.index) : undefined}
                          className={cx(
                            plain ? 'even:bg-muted/40' : trEvenCls,
                            rowOnClick && 'cursor-pointer',
                            rc,
                          )}
                        >
                          {render!(row, vi.index).map((cell, j) => {
                            const c = cols[j]
                            return (
                              <td key={c?.key ?? j} className={cx(tdCls, c?.cellClass, compact && 'px-2')}>
                                {c?.truncate ? (
                                  <div className="truncate" title={typeof cell === 'string' ? cell : undefined}>
                                    {cell}
                                  </div>
                                ) : (
                                  cell
                                )}
                              </td>
                            )
                          })}
                        </tr>
                      )
                    })}
                {virtualItems.length > 0 && (
                  <tr
                    aria-hidden
                    style={{
                      height:
                        (virtualizer.getTotalSize() -
                          (virtualItems.length > 0 ? virtualItems[virtualItems.length - 1].end : 0)),
                    }}
                  >
                    <td colSpan={cols.length} style={{ padding: 0, border: 0 }} />
                  </tr>
                )}
              </>
            ) : (
              // 少量行：全量渲染（和之前完全一样）
              renderRow
                ? rows.map((row, i) => renderRow(row, i))
                : rows.map((row, i) => {
                    const rc = typeof rowClassName === 'function' ? rowClassName(row, i) : rowClassName
                    return (
                      <tr
                        key={rowKey(row, i)}
                        onClick={rowOnClick ? () => rowOnClick(row, i) : undefined}
                        className={cx(
                          plain ? 'even:bg-muted/40' : trEvenCls,
                          rowOnClick && 'cursor-pointer',
                          rc,
                        )}
                      >
                        {render!(row, i).map((cell, j) => {
                          const c = cols[j]
                          return (
                            <td key={c?.key ?? j} className={cx(tdCls, c?.cellClass, compact && 'px-2')}>
                              {c?.truncate ? (
                                <div className="truncate" title={typeof cell === 'string' ? cell : undefined}>
                                  {cell}
                                </div>
                              ) : (
                                cell
                              )}
                            </td>
                          )
                        })}
                      </tr>
                    )
                  })
            )}
            {footRow}
          </tbody>
        </table>
        {!hasRows && empty ? <div className="py-2">{empty}</div> : null}
      </div>
      {headExtra}
    </div>
  )
}
