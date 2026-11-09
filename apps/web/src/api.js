// 后端接口封装
async function req(path, opts) {
  const r = await fetch(path, opts)
  if (!r.ok) {
    const e = await r.json().catch(() => ({}))
    throw new Error(e.error || 'HTTP ' + r.status)
  }
  return r.json()
}
const post = (path, body) =>
  req(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const q = encodeURIComponent

export const api = {
  home: () => req('/api/home'),
  categories: () => req('/api/categories'),
  category: (cat, page = 1) => req(`/api/category?cat=${q(cat)}&page=${page}`),
  detail: (site, id) => req(`/api/detail?site=${q(site)}&id=${q(id)}`),
  playAuto: (site, id, candidates) => post('/api/play/auto', { site, id, candidates }),
  favorites: () => req('/api/favorites'),
  addFavorite: (item) => post('/api/favorites', item),
  removeFavorite: (name) => req(`/api/favorites?name=${q(name)}`, { method: 'DELETE' }),
  history: () => req('/api/history'),
  putHistory: (item) => post('/api/history', item),
  removeHistory: (name) => req(`/api/history?name=${q(name)}`, { method: 'DELETE' }),
  status: () => req('/api/status'),
  refresh: () => post('/api/refresh', {}),
}

// 海报统一走后端缓存代理（绕过防盗链）
export const img = (url) => (url ? '/api/img?url=' + q(url) : '')

export const detailPath = (it) => `/detail/${q(it.site)}/${q(it.vodId)}`

// 搜索：SSE 流式，边搜边出
export function searchStream(key, onItems, onDone) {
  const ctrl = new AbortController()
  const seen = new Set()
  let finished = false
  const finish = () => {
    if (!finished) {
      finished = true
      onDone && onDone()
    }
  }
  fetch('/api/search/stream?q=' + q(key), { signal: ctrl.signal })
    .then(async (resp) => {
      if (!resp.ok) throw new Error('HTTP ' + resp.status)
      const reader = resp.body.getReader()
      const dec = new TextDecoder()
      let buf = ''
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        buf += dec.decode(value, { stream: true })
        let i
        while ((i = buf.indexOf('\n\n')) >= 0) {
          const line = buf.slice(0, i).trim()
          buf = buf.slice(i + 2)
          if (!line.startsWith('data:')) continue
          let evt
          try {
            evt = JSON.parse(line.slice(5))
          } catch {
            continue
          }
          if (evt.done) return finish()
          const fresh = (evt.items || []).filter((it) => {
            const k = it.name
            if (seen.has(k)) return false
            seen.add(k)
            return true
          })
          if (fresh.length) onItems(fresh)
        }
      }
      finish()
    })
    .catch(finish)
  return ctrl
}

export function timeAgo(ts) {
  if (!ts) return ''
  const s = Math.floor(Date.now() / 1000 - ts)
  if (s < 60) return '刚刚'
  if (s < 3600) return Math.floor(s / 60) + ' 分钟前'
  if (s < 86400) return Math.floor(s / 3600) + ' 小时前'
  return Math.floor(s / 86400) + ' 天前'
}

export function fmtTime(sec) {
  sec = Math.max(0, Math.floor(sec || 0))
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  const s = sec % 60
  const mm = String(m).padStart(h ? 2 : 1, '0')
  return (h ? h + ':' : '') + mm + ':' + String(s).padStart(2, '0')
}
