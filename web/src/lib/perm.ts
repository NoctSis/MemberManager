// 权限门控（前端）：店长恒全权限；其他账号按「按人权限」判定。
// 店主 2026-09-27 定：权限从「按角色」细化为「按人」——
//   门控用 perm.myPerms()（当前登录账号的生效权限，后端 staff_perms 优先、回落 role_perms）；
//   店主 11-05 删掉了「角色 × 模块」功能矩阵：技师不登录、没有任何权限，那份矩阵没有意义。
// 登录成功后拉一次并缓存；页面用 can(key) / canModule(name) 隐藏无权限入口。
// 后端模块名固定 10 项（见 WebServices.Ops.ModuleNames），此处只维护「页签 → 模块」映射。
import { isManager } from './session'
import { perm } from './api'

/**
 * 导航页签 key → 后端模块名。
 * null 表示只读总览页，恒可见（主工作台不写库、无编辑动作）。
 */
export const MODULE_OF: Record<string, string | null> = {
  dashboard: null,
  pos: '收银开单',
  projects: '储值与次卡',
  records: '交接班日结',
  members: '会员管理',
  'stored-value': '储值与次卡',
  // 店主 10-24：积分页独立成「积分商城」模块——员工能进商城兑换，
  // 但积分规则/礼品增删改等管理功能在页内按 isManager() 二次门控。
  points: '积分商城',
  staff: '员工提成',
  reports: '报表导出',
  permission: '权限设置',
  backup: '数据备份',
  settings: '系统设置',
}

// 店长专属页签：非店长一律不可见。
// 11-05：收银员默认仍拿到「储值与次卡」模块，但该模块名下还挂着
// 会员资产页（退款红冲、充值方案增删改）和项目列表页（改价、增删项目），这两页的写操作
// 都是店长管理动作，不能随模块一起放出去；后端对应接口也已改绑「系统设置」二次校验。
// 会员充值与售卡都是收银台的交易动作，归「收银开单」（后端三个接口同口径），
// 不随「储值与次卡」开关变化；收银员不需要进这两页管理页。
// 加在这里比在 MODULE_OF 里塞字符串常量更清楚：改「谁能进」不需要动后端模块清单。
const MANAGER_ONLY: Set<string> = new Set(['stored-value', 'projects'])

let mine: string[] | null = null

/**
 * 登录成功后拉一次当前账号的生效权限并缓存（函数名沿用旧的 loadMatrix）。
 * 角色矩阵已从界面删除，这里不再多发一次 MatrixData 请求；门控本来也只看按人权限。
 * 桥不可用/未登录时静默失败（店长不受影响，其余按无权限处理）。
 */
export function loadMatrix(): Promise<void> {
  return perm
    .myPerms()
    .then((p) => { mine = p })
    .catch(() => { mine = null })
}

/** 按人权限被修改后重新拉取（「系统设置 · 登录账号」的权限弹窗保存后调用） */
export function refreshMatrix(): Promise<void> {
  mine = null
  return loadMatrix()
}

/**
 * 某模块对当前登录账号是否授权。店长恒 true；未加载时按未授权处理（宁可少给）。
 * 判定走按人权限（后端 staff_perms 优先、回落该角色默认），不再看角色矩阵。
 */
export function canModule(module: string): boolean {
  if (isManager()) return true
  if (!module) return false
  if (!mine) return false
  return mine.indexOf(module) >= 0
}

/** 某页签对当前操作员是否可见（导航过滤与路由兜底共用） */
export function can(key: string): boolean {
  // 店长专属页签先判，别拿它去查模块清单（模块可能已经授权但页面仍不给进）
  if (MANAGER_ONLY.has(key) && !isManager()) return false
  const m = MODULE_OF[key] ?? null
  return m === null || canModule(m)
}

/** 快捷操作别名 → 真实页签（与 App 的 aliasMap 保持一致） */
const ALIAS_OF: Record<string, string> = {
  // 11-05：主工作台「会员充值」快捷入口改指收银台。会员资产页是店长专属，
  // 若仍指过去，收银员点快捷入口只会撞到无权访问页；收银台右上角就有充值按钮。
  recharge: 'pos',
  'new-member': 'members',
  daily: 'reports',
  export: 'reports',
}

/** 快捷入口/深链目标对当前操作员是否可见 */
export function canAlias(key: string): boolean {
  return can(ALIAS_OF[key] ?? key)
}
