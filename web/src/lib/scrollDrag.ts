// 拖拽滚动（09-29 店主定：屏蔽文字选中后，按住鼠标在页面上拖动即可翻页）
//
// 背景：浏览器原生只有滚轮/触摸板能滚动页面，鼠标按下拖动只会选中文字。
// 屏蔽选中之后这个手势就废了，这里把它接成滚动。
//
// 三条安全边界（顺序不能乱）：
//   1) 只在「本身还能继续滚」的容器内生效——按钮/链接/输入框/下拉/复选框/标签上按下
//      直接放弃，绝不干扰点击；
//   2) 4px 死区——位移不够就不算拖动，轻点不会被误当成翻页；
//   3) 单轴——越过死区那一刻按位移较大的方向定轴，只滚那一条轴。
//      先定轴再挑容器，是因为宽表格的滚动容器只有横向能滚（overflow-x-auto），
//      直接取「最近的滚动容器」会让纵向拖动落空：拖纵向时得继续往外找能纵滚的祖先。
//      这样宽表格横拖横滚、页面纵拖纵滚，斜拖不乱滚。
//
// 拖完还要吃掉一次 click：鼠标抬起后浏览器仍会在 mousedown/mouseup 的公共祖先上派发
// click，会把刚拖过的那一行当成点选（会员列表整行可点）。捕获阶段拦掉即可。
//
// 只接管鼠标（含手写笔）。触摸设备有原生惯性滚动，接管了反而破坏手感。
//
// 唯一的文字选中后门是 Ctrl：按住 Ctrl 放开选中，方便复制单号、手机号；
// 按住期间拖拽也不再翻页，把框选还给浏览器。配合 index.css 的 body.is-selecting。

const DEADZONE = 4
const CLICK_SUPPRESS_MS = 250

// 按下点落在这里就不接管——它们自己负责响应点击
const SKIP_SEL = 'button,a,input,select,textarea,label,[role="button"],[data-no-drag-scroll]'

type Scroller = { el: HTMLElement; canX: boolean; canY: boolean }

type Drag = {
  chain: Scroller[]
  sx: number
  sy: number
  el: HTMLElement | null
  axis: 'x' | 'y'
  active: boolean
  left: number
  top: number
}

let drag: Drag | null = null
let suppressClickUntil = 0
let inited = false
let ctrlDown = false

/** 从按下点到 body 之间所有还能继续滚的容器，最内层在前 */
function chainOf(target: EventTarget | null): Scroller[] {
  const chain: Scroller[] = []
  let n: HTMLElement | null = target instanceof HTMLElement ? target : null
  while (n && n !== document.body) {
    const s = getComputedStyle(n)
    const canX = (s.overflowX === 'auto' || s.overflowX === 'scroll') && n.scrollWidth > n.clientWidth + 1
    const canY = (s.overflowY === 'auto' || s.overflowY === 'scroll') && n.scrollHeight > n.clientHeight + 1
    if (canX || canY) chain.push({ el: n, canX, canY })
    n = n.parentElement
  }
  return chain
}

/** 越过死区时按方向定轴、按轴挑容器；挑不到就放弃，等价于原生无操作 */
function takeOver(e: PointerEvent): boolean {
  if (!drag) return false
  const dx = e.clientX - drag.sx
  const dy = e.clientY - drag.sy
  if (Math.abs(dx) < DEADZONE && Math.abs(dy) < DEADZONE) return false

  const axis: 'x' | 'y' = Math.abs(dx) >= Math.abs(dy) ? 'x' : 'y'
  const hit = drag.chain.find((c) => (axis === 'x' ? c.canX : c.canY))
  if (!hit) {
    drag = null
    return false
  }
  drag.axis = axis
  drag.el = hit.el
  // 从「这一刻」起算起点，避免把死区内那几像素一起滚过去
  drag.left = hit.el.scrollLeft
  drag.top = hit.el.scrollTop
  drag.sx = e.clientX
  drag.sy = e.clientY
  drag.active = true
  document.body.style.cursor = 'grabbing'
  return true
}

function onPointerDown(e: PointerEvent): void {
  if (e.pointerType !== 'mouse' || e.button !== 0 || drag || ctrlDown) return
  const t = e.target
  if (!(t instanceof Element)) return
  if (t.closest(SKIP_SEL)) return
  const chain = chainOf(t)
  if (chain.length === 0) return
  drag = { chain, sx: e.clientX, sy: e.clientY, el: null, axis: 'y', active: false, left: 0, top: 0 }
}

function onPointerMove(e: PointerEvent): void {
  if (!drag || e.pointerType !== 'mouse') return
  if (!drag.active && !takeOver(e)) return
  if (!drag.el) return
  if (drag.axis === 'x') drag.el.scrollLeft = drag.left - (e.clientX - drag.sx)
  else drag.el.scrollTop = drag.top - (e.clientY - drag.sy)
}

function endDrag(): void {
  if (!drag) return
  if (drag.active) suppressClickUntil = Date.now() + CLICK_SUPPRESS_MS
  drag = null
  document.body.style.cursor = ''
}

function onClickCapture(e: MouseEvent): void {
  if (Date.now() > suppressClickUntil) return
  e.stopPropagation()
  e.preventDefault()
  suppressClickUntil = 0
}

/** 按住 Ctrl：放开文字选中，让店主能复制单号/手机号 */
function setCtrlOn(on: boolean): void {
  if (ctrlDown === on) return
  ctrlDown = on
  document.body.classList.toggle('is-selecting', on)
}

/** Ctrl 松开前窗口就失焦了，状态得复位，否则文字会一直可选 */
function onBlur(): void {
  endDrag()
  setCtrlOn(false)
}

/** 幂等：重复调用只会挂一次监听。在 AppRoot 的 effect 里调一次即可。 */
export function initScrollDrag(): void {
  if (inited) return
  inited = true
  document.addEventListener('pointerdown', onPointerDown, true)
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', endDrag)
  window.addEventListener('pointercancel', endDrag)
  // 窗口失焦时鼠标可能已抬离页面，不收尾会卡住 cursor 和拖拽状态
  window.addEventListener('blur', onBlur)
  document.addEventListener('click', onClickCapture, true)
  document.addEventListener('keydown', (e) => { if (e.key === 'Control') setCtrlOn(true) })
  document.addEventListener('keyup', (e) => { if (e.key === 'Control') setCtrlOn(false) })
}
