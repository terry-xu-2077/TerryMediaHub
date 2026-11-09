// JS 爬虫宿主：与 Go 主服务通过 stdin/stdout 逐行 JSON RPC 通信（协议同 contracts/worker/protocol.md）。
// 加载 TVBox/CatVod 风格的 ES 模块脚本（import ... from 'assets://js/lib/cat.js'；export function __jsEvalReturn()）。
import { registerHooks } from 'node:module'
import { createInterface } from 'node:readline'
import { createHash } from 'node:crypto'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { mkdirSync, existsSync, readFileSync, writeFileSync } from 'node:fs'

const HERE = dirname(fileURLToPath(import.meta.url))
const CACHE = process.env.TERRY_JS_CACHE || join(HERE, 'cache')
mkdirSync(CACHE, { recursive: true })

// 协议通道独占 stdout；脚本里的 console.log 一律转到 stderr。
const out = (o) => process.stdout.write(JSON.stringify(o) + '\n')
console.log = console.info = console.warn = (...a) => process.stderr.write(a.join(' ') + '\n')

// 把 APP 内置模块路径 assets://js/lib/x.js 映射到本地 lib 目录。
registerHooks({
  resolve(spec, ctx, next) {
    if (spec.startsWith('assets://js/')) {
      return { url: pathToFileURL(join(HERE, spec.slice('assets://js/'.length))).href, shortCircuit: true }
    }
    return next(spec, ctx)
  },
})

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36'

// ---- 宿主提供给脚本的全局 API（对应 APP 原生注入）----
globalThis.SpiderDebug = { log: (...a) => process.stderr.write('[spider] ' + a.join(' ') + '\n') }
globalThis.js2Proxy = (...a) => a.join('|')
globalThis.req = async (url, o = {}) => {
  const method = (o.method || 'get').toUpperCase()
  const headers = { 'User-Agent': UA, ...(o.headers || {}) }
  let body
  if (o.data !== undefined && method !== 'GET') {
    if (typeof o.data === 'string') body = o.data
    else if (o.postType === 'form' || o.postType === 'form-data') {
      body = new URLSearchParams(o.data).toString()
      headers['Content-Type'] ??= 'application/x-www-form-urlencoded'
    } else {
      body = JSON.stringify(o.data)
      headers['Content-Type'] ??= 'application/json'
    }
  }
  const ctrl = new AbortController()
  const timer = setTimeout(() => ctrl.abort(), o.timeout || 15000)
  try {
    const r = await fetch(url, { method, headers, body, redirect: o.redirect === 0 ? 'manual' : 'follow', signal: ctrl.signal })
    const buf = Buffer.from(await r.arrayBuffer())
    const hd = {}
    r.headers.forEach((v, k) => (hd[k] = v))
    return { code: r.status, content: o.buffer === 2 ? buf.toString('base64') : buf.toString('utf8'), headers: hd }
  } finally {
    clearTimeout(timer)
  }
}

// ---- 脚本加载与实例缓存 ----
const spiders = new Map() // site key → {spider, inited}

async function loadScript(api) {
  const file = join(CACHE, createHash('sha1').update(api).digest('hex') + '.mjs')
  if (!existsSync(file)) {
    const r = await fetch(api, { headers: { 'User-Agent': UA }, signal: AbortSignal.timeout(30000) })
    if (!r.ok) throw new Error(`脚本下载失败 HTTP ${r.status}`)
    writeFileSync(file, await r.text())
  }
  const mod = await import(pathToFileURL(file).href)
  if (typeof mod.__jsEvalReturn === 'function') return mod.__jsEvalReturn()
  if (mod.default && typeof mod.default === 'object') return mod.default
  throw new Error('脚本没有导出 __jsEvalReturn / default')
}

function parseExt(ext) {
  if (typeof ext === 'string' && /^\s*[{[]/.test(ext)) {
    try { return JSON.parse(ext) } catch { /* 当字符串 */ }
  }
  return ext ?? ''
}

async function getSpider(req) {
  const key = req.site + '|' + req.api + '|' + (req.ext || '')
  let ent = spiders.get(key)
  if (!ent) {
    ent = { spider: await loadScript(req.api), inited: false }
    spiders.set(key, ent)
  }
  if (!ent.inited) {
    await ent.spider.init?.({ skey: req.site, stype: 3, ext: parseExt(req.ext) })
    ent.inited = true
  }
  return ent.spider
}

async function handle(req) {
  if (req.method === 'ping') return 'pong'
  const sp = await getSpider(req)
  const a = req.args || {}
  switch (req.method) {
    case 'home': return sp.home(true)
    case 'homeVideo': return (sp.homeVod || sp.homeVideo)?.call(sp)
    case 'category': return sp.category(a.tid, a.pg || '1', true, a.extend || {})
    case 'detail': return sp.detail(Array.isArray(a.ids) ? a.ids[0] : a.ids)
    case 'search': return sp.search(a.key, !!a.quick, a.pg || '1')
    case 'play': return sp.play(a.flag, a.id, a.vipFlags || [])
  }
  throw new Error('unknown method: ' + req.method)
}

out({ ready: true, ok: true, runtime: 'node ' + process.version })
const rl = createInterface({ input: process.stdin })
let chain = Promise.resolve() // 串行处理，保证响应与请求一一对应
rl.on('line', (line) => {
  line = line.trim()
  if (!line) return
  chain = chain.then(async () => {
    const t0 = Date.now()
    try {
      const data = await handle(JSON.parse(line))
      out({ ok: true, data: typeof data === 'string' ? data : JSON.stringify(data ?? {}), ms: Date.now() - t0 })
    } catch (e) {
      out({ ok: false, error: `${e?.name || 'Error'}: ${e?.message || e}`, trace: String(e?.stack || '').split('\n').slice(0, 6).join('\n'), ms: Date.now() - t0 })
    }
  })
})
rl.on('close', () => process.exit(0))
