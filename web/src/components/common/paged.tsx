import { Button, cx } from './ui'

export interface PagedBarProps {
  /** 命中筛选条件的全量条数（来自后端 count，不是前端 reduce） */
  total: number
  /** 当前页码（1-based） */
  page: number
  /** 总页数 */
  pageCount: number
  /** 每页条数 */
  pageSize: number
  /** 跳到指定页（组件内部自动夹到 1..pageCount） */
  goToPage: (p: number) => void
  /** 提供则渲染第三段「每页 N」下拉；不传则第三段不出现 */
  onPageSizeChange?: (n: number) => void
  /** 每页可选档位 */
  pageSizes?: number[]
  /** 加载中禁用翻页 */
  loading?: boolean
}

/**
 * B2 页码条（三段靠拢居中，店主 10-12 定）：
 *   共 X 条 ｜ 上一页 1 2 3 … 26 下一页 ｜ 每页 N
 * - 不用 justify-between 顶两端（中间空一大片）；三段整体居中、小间距靠拢。
 * - 页码限宽 + 超量省略：最多 5 个页码格 + …，数据到 9999 条也不变长。
 * - 数据一页放得下（total ≤ pageSize）→ 只出一行「共 X 条」，不出翻页控件。
 */
export function PagedBar({
  total,
  page,
  pageCount,
  pageSize,
  goToPage,
  onPageSizeChange,
  pageSizes = [10, 20, 50],
  loading,
  className = '',
}: PagedBarProps & { className?: string }) {
  if (total <= 0) return null

  // 一页放得下：只出一行计数，不出翻页控件
  if (total <= pageSize) {
    return (
      <div className={cx('px-4 py-2.5 border-t border-border flex justify-center', className)}>
        <p className="text-[11px] text-muted-foreground">
          共 <b className="font-semibold text-foreground tabular-nums">{total}</b> 条
        </p>
      </div>
    )
  }

  const cell =
    'h-6 min-w-[24px] px-1.5 inline-flex items-center justify-center rounded-[var(--radius-sm)] text-[12px] tabular-nums transition-colors'
  const pages = buildPageList(page, pageCount)

  return (
    <div className={cx('px-4 py-2 border-t border-border flex items-center justify-center gap-3 select-none', className)}>
      {/* 段 1：共 X 条 */}
      <p className="text-[11px] text-muted-foreground whitespace-nowrap shrink-0">
        共 <b className="font-semibold text-foreground tabular-nums">{total}</b> 条
      </p>
      <span className="w-px h-4 bg-border shrink-0" />
      {/* 段 2：页码簇 */}
      <div className="flex items-center gap-1 min-w-0">
        <Button
          variant="secondary"
          size="sm"
          className="h-6 px-2 text-[11px]"
          disabled={page <= 1 || loading}
          onClick={() => goToPage(page - 1)}
        >
          上一页
        </Button>
        {pages.map((p, i) =>
          p === 'e' ? (
            <span key={'e' + i} className="text-[12px] text-muted-foreground w-4 text-center shrink-0 select-none">
              …
            </span>
          ) : (
            <button
              key={p}
              type="button"
              onClick={() => goToPage(p)}
              disabled={loading}
              aria-current={p === page ? 'page' : undefined}
              className={cx(
                cell,
                p === page
                  ? 'bg-primary text-primary-foreground border border-primary'
                  : 'bg-card text-foreground border border-input hover:bg-accent hover:text-accent-foreground',
              )}
            >
              {p}
            </button>
          ),
        )}
        <Button
          variant="secondary"
          size="sm"
          className="h-6 px-2 text-[11px]"
          disabled={page >= pageCount || loading}
          onClick={() => goToPage(page + 1)}
        >
          下一页
        </Button>
      </div>
      {/* 段 3：跳转到指定页 */}
      <div className="flex items-center gap-1.5 shrink-0">
        <span className="text-[11px] text-muted-foreground">跳至</span>
        <input
          type="number"
          min={1}
          max={pageCount}
          placeholder={String(page)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              const v = Number((e.target as HTMLInputElement).value)
              if (v >= 1 && v <= pageCount) goToPage(v)
            }
          }}
          onBlur={(e) => {
            const v = Number(e.target.value)
            if (v >= 1 && v <= pageCount) goToPage(v)
          }}
          className="h-6 w-10 px-1 text-[11px] text-center tabular-nums bg-card text-foreground border border-input rounded-[var(--radius-sm)] focus:outline-none focus:border-primary"
        />
        <span className="text-[11px] text-muted-foreground">页</span>
      </div>
    </div>
  )
}

/**
 * 生成页码序列：最多 5 个页码格 + …（店主 10-12 定「限宽 + 超量省略」）。
 * 恒定显示「第 1 页 + 末页 + 当前页 ±1」，中间空档用 … 代替：
 *   开头   → 1 2 3 … 26
 *   中间   → 1 … 12 13 14 … 26
 *   末尾   → 1 … 25 26
 * 数字总数恒 ≤ 5，数据到 9999 条也不变长。
 */
function buildPageList(page: number, pageCount: number): (number | 'e')[] {
  if (pageCount <= 5) return Array.from({ length: pageCount }, (_, i) => i + 1)
  const out: (number | 'e')[] = [1]
  if (page - 1 > 2) out.push('e')
  for (let i = Math.max(2, page - 1); i <= Math.min(pageCount - 1, page + 1); i++) out.push(i)
  if (page + 1 < pageCount - 1) out.push('e')
  out.push(pageCount)
  return out
}
