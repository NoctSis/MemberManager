import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'

/**
 * 一页数据。
 * - rows：本批明细（按 offset/limit 截过）
 * - total：命中筛选条件的**全量**条数
 * - meta：随批返回的汇总（如单据查询的应收/现金/余额拆分），可选
 */
export interface PageSlice<T, M = undefined> {
  rows: T[]
  total: number
  meta?: M
}

/** 表体一行的高度（tdCls 的 h-10 = 40px），算「一页装几行」用 */
export const ROW_H = 40
/** 表头高度（thCls 的 h-9 = 36px），算可用高时要扣掉 */
const HEAD_H = 36

/**
 * 按容器实际高度算「一页该放几行」——店主 11-04：
 * 「到达当前窗口最大值的时候就不再生成记录，改为下一页」。
 *
 * 容器必须是 flex 布局里被 `flex-1 min-h-0` 撑开的那一块（高度由窗口决定、
 * 与内容无关），否则测出来的高度会随行数变化而抖动。
 *
 * 返回值夹在 [min, max]：太小翻页太碎，太大又起不到「填满窗口」的作用。
 * 窗口尺寸变化会自动重算（ResizeObserver + window.resize）。
 */
export function useAutoPageSize(
  ref: React.RefObject<HTMLElement | null>,
  opts: { min?: number; max?: number } = {},
): number {
  const min = opts.min ?? 5
  const max = opts.max ?? 100
  const [n, setN] = useState(min)

  useLayoutEffect(() => {
    const calc = () => {
      const el = ref.current
      if (!el) return
      const h = el.getBoundingClientRect().height
      if (h <= 0) return
      // 减去表头，剩下的除以行高就是能装下的行数
      const fit = Math.floor((h - HEAD_H) / ROW_H)
      setN(Math.max(min, Math.min(max, fit)))
    }
    calc()
    const ro = new ResizeObserver(calc)
    if (ref.current) ro.observe(ref.current)
    window.addEventListener('resize', calc)
    return () => {
      ro.disconnect()
      window.removeEventListener('resize', calc)
    }
  }, [ref, min, max])

  return n
}

export interface PagedNumberedState<T, M> {
  /** 当前页的明细（每页 pageSize 行，替换式而非追加） */
  rows: T[]
  /** 命中筛选条件的全量条数 */
  total: number
  /** fetchSlice 随批带回的汇总（如指标卡用的全量统计，不受翻页影响） */
  meta: M | undefined
  /** 当前页码（1-based） */
  page: number
  /** 每页条数 */
  pageSize: number
  /** 总页数（= ceil(total / pageSize)，至少 1） */
  pageCount: number
  /** 当前页加载中 */
  loading: boolean
  error: string | null
  /** 跳到指定页（自动夹到 1..pageCount） */
  goToPage: (p: number) => void
  /** 换每页条数：回到第 1 页并重取（手动指定后不再跟随窗口） */
  setPageSize: (n: number) => void
  /** 筛选条件变化后调用：回到第 1 页重取 */
  reload: () => void
}

/**
 * B2 页码分页（替换式 + 定位，非「加载更多」追加式）。
 *
 * 核心约定：
 * - **分页只截 rows，total 必须在筛选条件下全量算**（走后端 count 或 QueryResult.Total），
 *   绝不能从已取到的行里数 —— 否则「共 Y 条」的 Y 会随翻页缩水。
 * - 跳页 = 前端传 `offset =（页 − 1）× 每页`，**后端零改动**（LIMIT/OFFSET 已支持）。
 * - 换「每页 N」或改筛选条件都回到第 1 页。
 *
 * 自适应每页行数（店主 11-04）：
 * - 传 `autoSize`（由 `useAutoPageSize` 按容器高度算出）后，pageSize 会跟随窗口；
 *   用户在下拉里手动选过之后就不再跟随，避免「我刚选了 50 怎么又变回去」。
 * - **pageCount 变小时必须把 page 夹回来**：窗口拉高 → 每页行数变多 → 总页数变少，
 *   原先停在第 5 页但只剩 3 页时，offset 会超出 total，整页空掉 ——
 *   这就是店主说的「记录突然消失」。夹回去才不会。
 *
 * 防乱序：seq 序号，快速改筛选/跳页时旧请求晚到的结果不覆盖新结果。
 * 用法：`useEffect(() => { void p.reload() }, [filterA, filterB, p.reload])`
 */
export function usePagedNumbered<T, M = undefined>(
  fetchSlice: (limit: number, offset: number) => Promise<PageSlice<T, M>>,
  initialPageSize = 10,
  autoSize?: number,
): PagedNumberedState<T, M> {
  const fn = useRef({ fetchSlice })
  fn.current = { fetchSlice }

  const [pageSize, setPageSizeState] = useState(initialPageSize)
  const [page, setPage] = useState(1)
  const [rows, setRows] = useState<T[]>([])
  const [total, setTotal] = useState(0)
  const [meta, setMeta] = useState<M | undefined>(undefined)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const seq = useRef(0)
  /** 用户手动选过每页条数就不再跟随窗口 */
  const manual = useRef(false)

  // 跟随窗口高度自动调每页行数（只在用户没手动选过、且值真变了才动）
  useEffect(() => {
    if (manual.current) return
    if (autoSize && autoSize > 0 && autoSize !== pageSize) setPageSizeState(autoSize)
  }, [autoSize]) // eslint-disable-line react-hooks/exhaustive-deps

  // page / pageSize / tick 任一变化都重取当前页
  useEffect(() => {
    const { fetchSlice: fp } = fn.current
    const my = ++seq.current
    setLoading(true)
    setError(null)
    fp(pageSize, (page - 1) * pageSize)
      .then((s) => {
        if (my !== seq.current) return
        setRows(s.rows ?? [])
        setTotal(s.total)
        setMeta(s.meta)
      })
      .catch((e) => {
        if (my !== seq.current) return
        setError(String(e))
      })
      .finally(() => {
        if (my === seq.current) setLoading(false)
      })
  }, [page, pageSize, tick])

  const pageCount = Math.max(1, Math.ceil(total / Math.max(1, pageSize)))

  // 页数变少时把当前页夹回去 —— 否则 offset 超出 total，整页空掉（「记录突然消失」）。
  // 依赖里只有 pageCount：夹完 page 就不会再进这个分支，不会来回抖。
  useEffect(() => {
    if (page > pageCount) setPage(pageCount)
  }, [page, pageCount])

  const goToPage = useCallback(
    (p: number) => {
      const pc = Math.max(1, Math.ceil(total / Math.max(1, pageSize)))
      setPage(Math.min(Math.max(1, p), pc))
    },
    [total, pageSize],
  )

  const setPageSize = useCallback((n: number) => {
    manual.current = true
    setPageSizeState(n)
    setPage(1)
    setTick((t) => t + 1)
  }, [])

  const reload = useCallback(() => {
    setPage(1)
    setTick((t) => t + 1)
  }, [])

  return {
    rows,
    total,
    meta,
    page,
    pageSize,
    pageCount,
    loading,
    error,
    goToPage,
    setPageSize,
    reload,
  }
}
