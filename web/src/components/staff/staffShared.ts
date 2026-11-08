import type { StaffAccount } from '@/data/mock'

/** 员工编辑弹窗的表单草稿。id=0 表示「添加」。 */
export interface Draft {
  id: number
  name: string
  role: StaffAccount['role']
  phone: string
  /** 旧的「个人比例」单值（-1 = 无）。10-12 起不在弹窗编辑，仅透传以免保存时误清通配槽。 */
  rate: number
  enabled: boolean
  /** A2 底薪文本框内容（空串 = 0 不启用）。保存时 parseFloat，NaN 落 0。 */
  baseSalaryText: string
}

export const emptyDraft = (): Draft => ({ id: 0, name: '', role: '技师', phone: '', rate: -1, enabled: true, baseSalaryText: '' })

/** 底薪文本 → 数值（空/非数 = 0）。-1 语义留给 api 层的「不动列」，这里不会产出 -1。 */
export const baseSalaryOf = (t: string): number => {
  const v = parseFloat(t.trim())
  return Number.isFinite(v) && v > 0 ? Math.round(v * 100) / 100 : 0
}

/** 0-1 小数 → 百分比字符串（去尾零）。null/undefined = 未加载。 */
export const pctOf = (d: number | null | undefined): string =>
  d == null ? '—' : `${Math.round(d * 1000) / 10}%`

/** 0-1 小数 → 0-100 输入框文本（去尾零，给回填用）。 */
export const toPctText = (d: number | null | undefined): string =>
  d == null ? '' : String(Math.round(d * 1000) / 10)