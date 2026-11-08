import { useCallback, useEffect, useState } from 'react'
import { system as apiSystem, settings as apiSettings } from '@/lib/api'
import { toast } from 'sonner'

/**
 * 统一「默认导出位置」hook：4 个导出都落这里，改一次即成为新默认。
 * - dir：当前生效的默认导出目录（全局 export_dir，未设则后端兜底 data\exports）
 * - change()：弹系统文件夹选择框 → 选完立即写 settings.export_dir（持久化新默认）→ 刷新本地
 *   取消（选框里点了取消）则不动默认、不写库。
 */
export function useExportDir() {
  const [dir, setDir] = useState('')

  const refresh = useCallback(() => {
    apiSystem.exportDir().then(setDir).catch(() => {})
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const change = useCallback(() => {
    apiSystem
      .pickFolder()
      .then((picked) => {
        if (!picked) return // 取消 → 保持原默认
        return apiSettings
          .save({ export_dir: picked })
          .then(() => {
            setDir(picked)
            toast.success(`默认导出位置已改为 ${picked}`)
          })
      })
      .catch((e) => toast.error('更换导出位置失败：' + String(e)))
  }, [])

  return { dir, change, refresh }
}
