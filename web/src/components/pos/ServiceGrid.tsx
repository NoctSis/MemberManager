import { useState } from 'react'
import { Ticket } from 'lucide-react'
import { cx, Badge } from '@/components/common/ui'
import type { ServiceItem } from '@/data/mock'

/**
 * 项目选择（网格）：卡片固定最小宽 148px，一行能放几个就放几个。
 *  · 最窄时一行 3 个，放不下自动换到下一行；
 *  · 超过可视高度的部分纵向滚动（不出现水平滚动条）；
 *  · 窗口拉宽时 auto-fill 会自动多排一列，原先挤到下一行的项目回填到上一行，列宽同步放大。
 *
 * 防溢出/防错位的三个关键：
 *  1. 轨道用 minmax(min(148px,100%),1fr) —— 容器再窄也只会变成一列并压缩，不会撑破冒出横向滚动条；
 *  2. 名称 truncate（min-width:0 才会真的省略），图标/单价/价格整组 shrink-0 + whitespace-nowrap；
 *  3. 「支持次卡」徽标独占一行且高度固定，每张卡片结构完全一致 → 同行卡片的名称行、价格行天然对齐。
 */
const GRID_COLS = 'repeat(auto-fill, minmax(min(148px, 100%), 220px))'

export function ServiceGrid({ items, onPick }: { items: ServiceItem[]; onPick: (item: ServiceItem) => void }) {
  const [category, setCategory] = useState<string>('全部')
  const categories = ['全部', ...Array.from(new Set(items.map((s) => s.category)))]
  const list = category === '全部' ? items : items.filter((s) => s.category === category)

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex items-center gap-1.5 mb-3 flex-wrap shrink-0">
        {categories.map((c) => (
          <button
            key={c}
            onClick={() => setCategory(c)}
            className={cx(
              'h-8 px-3.5 text-[13px] rounded-[var(--radius-pill)] border transition-colors shrink-0 whitespace-nowrap',
              category === c
                ? 'bg-primary border-primary text-primary-foreground font-semibold shadow-sm'
                : 'bg-card border-input text-muted-foreground hover:text-foreground hover:border-primary/40',
            )}
          >
            {c}
          </button>
        ))}
      </div>

      <div
        className="flex-1 min-h-0 min-w-0 grid gap-1.5 content-start overflow-y-auto pr-1 pb-2"
        style={{ gridTemplateColumns: GRID_COLS }}
      >
        {list.map((s) => (
          <button
            key={s.id}
            onClick={() => onPick(s)}
            title={`${s.name}  ¥${s.price.toFixed(2)}/${s.unit}${s.isCardItem ? ' · 支持次卡' : ''}`}
            className="group flex flex-col gap-0.5 bg-card border border-border rounded-[var(--radius-sm)] px-2.5 py-1.5 text-left hover:border-primary hover:shadow-[var(--focus-ring)] active:bg-accent transition-all min-w-0"
          >
            <div className="flex items-center gap-1 min-w-0">
              <span className="text-[13px] font-semibold text-foreground truncate">{s.name}</span>
              {s.isCardItem ? (
                <span className="text-primary shrink-0" aria-hidden>
                  <Ticket size={12} />
                </span>
              ) : null}
            </div>
            <div className="flex items-baseline gap-1 min-w-0">
              <span className="text-[14px] font-bold text-primary tabular-nums whitespace-nowrap">¥{s.price.toFixed(2)}</span>
              <span className="text-[10px] text-muted-foreground whitespace-nowrap shrink-0">/{s.unit}</span>
            </div>
            <div className="h-4 flex items-center min-w-0 overflow-hidden">
              {s.isCardItem ? <Badge tone="primary" className="h-4 px-1.5 text-[10px]">支持次卡</Badge> : null}
            </div>
          </button>
        ))}
      </div>
    </div>
  )
}
