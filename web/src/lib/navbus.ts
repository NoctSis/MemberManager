/**
 * 极简跨页跳转总线。
 * 用法：来源页 requestNav({ page:'records', operator:'张三' })；
 *      App 监听 'mm:nav' 取目标页完成切页；目标页挂载时 takePendingArgs() 取走附带参数。
 * 不用全局状态库，避免为一次性需求引入依赖。
 */
export interface NavRequest {
  page: string
  /** 消费记录页：预填「操作员」筛选 */
  operator?: string
}

let pending: NavRequest | null = null

export function requestNav(req: NavRequest): void {
  pending = req
  window.dispatchEvent(new CustomEvent('mm:nav'))
}

/** App 调用：只看不取，切页后仍留给目标页消费。 */
export function peekPendingNav(): NavRequest | null {
  return pending
}

/** 目标页调用：page 匹配才取走并清空，避免被其它页面误消费。 */
export function takePendingArgs(page: string): NavRequest | null {
  if (pending && pending.page === page) {
    const r = pending
    pending = null
    return r
  }
  return null
}
