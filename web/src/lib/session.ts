// 登录会话（单操作员）：桥接成功登录后登记，供各页面显示操作员

export interface StaffInfo {
  id: number
  name: string
  role: string
}

let current: StaffInfo | null = null

export function setStaff(s: StaffInfo | null): void {
  current = s
}

export function getStaff(): StaffInfo | null {
  return current
}

export const isManager = (): boolean => current?.role === '店长'

/**
 * 「记住密码」保存登录凭据用的 localStorage 键（所有账号通用，含店长 —— 店主 10-08 定）。
 * 放在这里是因为登录页写入、改密码弹窗也要同步更新，不能各写一份字符串。
 */
export const SAVED_LOGIN_KEY = 'mm_saved_login'

/** 系统初始密码：店长用它登录时才弹「建议改密码」提醒（见 PasswordTipDialog）。 */
export const DEFAULT_PWD = '123456'

/** 某账号已关掉过改密码提醒的标记键（按账号 id 存；换机器/清缓存会再提醒一次）。 */
export const pwdTipOffKey = (id: number): string => 'mm_pwd_tip_off_' + id

export const operatorName = (): string => current?.name ?? '系统'

// ---------- 上一次在收银台开单/操作的人 ----------
// 只记界面偏好，不入库：它不是业务数据，写 op_logs 纯属噪音。
// 会员管理「新建会员」靠它把默认经办人带成「上一次开单的人」，
// 而不是永远显示登录账号（一家店常共用一个店长账号登录，那没意义）。
// 缓存被清或值失效时返回 0，由调用方回落到登录本人。
const LAST_OPERATOR_KEY = 'mm:lastOperatorId'

export const getLastOperatorId = (): number => {
  const n = Number(localStorage.getItem(LAST_OPERATOR_KEY) ?? 0)
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0
}

export const setLastOperatorId = (id: number): void => {
  if (id > 0) localStorage.setItem(LAST_OPERATOR_KEY, String(Math.floor(id)))
}
