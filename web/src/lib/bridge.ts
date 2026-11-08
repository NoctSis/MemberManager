// JS 桥：{id, svc, method, args[]} → C# BridgeDispatcher → {id, ok, data|error}

let seq = 1
const pending = new Map<number, { res: (v: unknown) => void; rej: (e: string) => void }>()

type WebviewHost = {
  postMessage: (msg: string) => void
  addEventListener: (type: string, cb: (e: MessageEvent) => void) => void
}

function host(): WebviewHost | null {
  const w = window as unknown as { chrome?: { webview?: WebviewHost } }
  return w.chrome?.webview ?? null
}

// 消息泵（壳 PostWebMessageAsJson 回投的是 JSON 字符串）
if (typeof window !== 'undefined') {
  const h = host()
  if (h) {
    h.addEventListener('message', (e: MessageEvent) => {
      try {
        const raw = typeof e.data === 'string' ? JSON.parse(e.data) : e.data
        const p = pending.get(raw.id)
        if (!p) return
        pending.delete(raw.id)
        if (raw.ok) p.res(raw.data)
        else p.rej(String(raw.error ?? '未知错误'))
      } catch {
        /* 忽略无法解析的消息 */
      }
    })
  }
}

export function invoke<T>(svc: string, method: string, args: unknown[]): Promise<T> {
  const h = host()
  if (!h) return Promise.reject('需要在收银系统桌面程序内运行')
  const id = seq++
  return new Promise<T>((resolve, reject) => {
    pending.set(id, {
      res: (v) => resolve(v as T),
      rej: (e) => reject(e),
    })
    h.postMessage(JSON.stringify({ id, svc, method, args }))
  })
}
