import { forwardRef, type ReactNode } from 'react'

/**
 * 页面布局组件库——统一全站两种页面模式的容器与区域。
 *
 * 设计原则：
 *   1. 零 JS 开销：所有布局走 index.css 的 @layer components，组件只做 JSX 分组
 *   2. API 最小化：页面只声明"有什么"，不写 flex/shrink-0/gap 等布局细节
 *   3. 与 DataTable 无缝：PageBody 自动撑满，DataTable fill 自动吃满
 *
 * 用法 A——长列表页（表格撑满、页码贴底）：
 *   <PageFill minWidth={1030}>
 *     <PageHeader title="消费记录">
 *       <Button>新增</Button>
 *     </PageHeader>
 *     <PageToolbar>筛选控件...</PageToolbar>
 *     <PageBody ref={bodyRef}>
 *       <DataTable fill rows={rows} ... />
 *     </PageBody>
 *     <PageFooter><PagedBar ... /></PageFooter>
 *   </PageFill>
 *
 * 用法 B——内容滚动页（内容多时页内滚动）：
 *   <PageScroll minWidth={1080}>
 *     <PageHeader title="系统设置" />
 *     ...任意内容区块...
 *   </PageScroll>
 */

// ============================================================
// 外层容器
// ============================================================

/** A 模式：长列表页。flex 纵向 + gap 1rem + h-full，PageBody 自动撑满。 */
export function PageFill({ minWidth = 1080, children }: { minWidth?: 1030 | 1080; children: ReactNode }) {
  return (
    <div className={`page-fill ${minWidth === 1030 ? 'min-w-[1030px]' : 'min-w-[1080px]'}`}>
      {children}
    </div>
  )
}

/** B 模式：内容滚动页。overflow-y-auto + h-full + 区块间距 1rem。 */
export function PageScroll({ minWidth = 1080, children }: { minWidth?: 1030 | 1080; children: ReactNode }) {
  return (
    <div className={`page-scroll ${minWidth === 1030 ? 'min-w-[1030px]' : 'min-w-[1080px]'}`}>
      {children}
    </div>
  )
}

// ============================================================
// 区域插槽
// ============================================================

/** 标题区：左侧标题 + 右侧操作按钮。 */
export function PageHeader({ title, subtitle, children }: { title: string; subtitle?: string; children?: ReactNode }) {
  return (
    <div className="flex items-end justify-between shrink-0">
      <div>
        <h1 className="text-lg font-bold text-foreground" style={{ fontFamily: 'var(--font-display)' }}>
          {title}
        </h1>
        {subtitle ? <p className="text-xs text-muted-foreground mt-1">{subtitle}</p> : null}
      </div>
      {children ? <div className="flex items-center gap-2">{children}</div> : null}
    </div>
  )
}

/** 工具栏/筛选栏（固定高度，不压缩）。 */
export function PageToolbar({ children }: { children: ReactNode }) {
  return <div className="shrink-0">{children}</div>
}

/** 指标卡网格（固定高度，自动换行）。 */
export function PageMetrics({ children }: { children: ReactNode }) {
  return (
    <div className="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(150px,1fr))] shrink-0">
      {children}
    </div>
  )
}

/**
 * 主内容区（表格等）——page-fill 里唯一 flex:1 的区域，自动撑满剩余高度。
 * 传 ref 给 useAutoPageSize 测量「一页装几行」。
 */
export const PageBody = forwardRef<HTMLDivElement, { children: ReactNode; className?: string }>(
  function PageBody({ children, className }, ref) {
    return (
      <div ref={ref} className={`page-body bg-card rounded-[var(--radius-md)] shadow-[var(--elev-ring)] ${className ?? ''}`}>
        {children}
      </div>
    )
  },
)

/** 底部固定区（页码条等，贴死底边）。 */
export function PageFooter({ children }: { children: ReactNode }) {
  return <div className="shrink-0">{children}</div>
}
