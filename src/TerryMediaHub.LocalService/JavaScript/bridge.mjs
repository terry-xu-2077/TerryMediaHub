import { createInterface } from 'node:readline';
import { mkdir, writeFile } from 'node:fs/promises';
import { createHash, createHmac } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { pdfa, pdfh, pd } from './drpy2/parser.mjs';

const cache = process.env.TERRY_JS_CACHE || join(dirname(fileURLToPath(import.meta.url)), 'cache');
const assets = new URL('./cat.mjs', import.meta.url).href;
const assetNames = readdirSync(fileURLToPath(new URL('./assets/', import.meta.url)));
const spiders = new Map();
await mkdir(cache, { recursive: true });

globalThis.SpiderDebug = { log: (...items) => console.error(...items) };
console.log = (...items) => console.error(...items);
globalThis.log = (...items) => console.error(...items);
globalThis.print = (...items) => console.error(...items);
globalThis.pdfa = pdfa;
globalThis.pdfh = pdfh;
globalThis.pd = pd;
const localStore = new Map();
globalThis.local = {
  get: (scope, key) => localStore.get(`${scope}|${key}`),
  set: (scope, key, value) => localStore.set(`${scope}|${key}`, value),
  delete: (scope, key) => localStore.delete(`${scope}|${key}`)
};
let activeSite = null;
function proxyBase(lang, site = activeSite) {
  if (!site || !process.env.TERRY_SERVICE_KEY) return '';
  const descriptor = JSON.stringify({ lang, site: site.site, api: site.api,
    ext: site.ext, type: site.type || 3 });
  const encoded = Buffer.from(descriptor + '\n').toString('base64url');
  const signature = createHmac('sha256', process.env.TERRY_SERVICE_KEY)
    .update(encoded).digest('hex').slice(0, 8);
  return `http://127.0.0.1:18766/site-proxy/${encoded}-${signature}`;
}
globalThis.getProxy = () => proxyBase('js');
globalThis.js2Proxy = (_local, _type, _site, url, headers) => {
  const base = proxyBase('js');
  if (!base) return '';
  const query = new URLSearchParams({ do: 'js', url: String(url ?? '') });
  if (headers) query.set('header', JSON.stringify(headers));
  return base + '?' + query;
};
globalThis.global = globalThis;
globalThis.window = globalThis;
globalThis.self = globalThis;
globalThis.req = async (url, options = {}) => {
  const method = (options.method || 'GET').toUpperCase();
  const headers = { ...options.headers };
  let body;
  if (method !== 'GET' && options.data !== undefined) {
    if (options.postType === 'form' && typeof options.data === 'object') {
      body = new URLSearchParams(options.data).toString();
      if (!Object.keys(headers).some(k => k.toLowerCase() === 'content-type'))
        headers['Content-Type'] = 'application/x-www-form-urlencoded';
    } else body = typeof options.data === 'string' ? options.data : JSON.stringify(options.data);
  }
  const response = await fetch(url, {
    method, headers, body, redirect: 'follow',
    signal: AbortSignal.timeout(Math.min(Math.max(options.timeout || 15000, 1000), 60000))
  });
  const buffer = Buffer.from(await response.arrayBuffer());
  return { ok: response.ok, status: response.status, url: response.url,
    headers: Object.fromEntries(response.headers),
    content: options.buffer === 2 ? buffer.toString('base64') : buffer.toString('utf8') };
};
const asyncReq = globalThis.req;
const nativeFetch = globalThis.fetch;
globalThis._http = (url, options = {}) => {
  const result = syncReq(url, options);
  if (typeof options.complete === 'function') options.complete(result);
  return result;
};

function syncReq(url, options = {}) {
  if (!/^https?:\/\//i.test(url)) throw new Error(`drpy2 不支持的请求地址: ${url}`);
  const timeout = Math.min(Math.max(Number(options.timeout) || 15000, 1000), 60000);
  const folder = mkdtempSync(join(tmpdir(), 'terry-drpy-'));
  const headersFile = join(folder, 'headers.txt');
  try {
    const args = ['--silent', '--show-error', '--location', '--max-redirs', '5',
      '--max-time', String(Math.ceil(timeout / 1000)),
      '--dump-header', headersFile];
    const method = (options.method || 'GET').toUpperCase();
    if (method !== 'GET') args.push('--request', method);
    for (const [name, value] of Object.entries(options.headers || {})) {
      if (value !== undefined && value !== null) args.push('--header', `${name}: ${value}`);
    }
    let data = options.body ?? options.data;
    if (data !== undefined && method !== 'GET') {
      if (typeof data === 'object') {
        data = new URLSearchParams(data).toString();
        if (!Object.keys(options.headers || {}).some(x => x.toLowerCase() === 'content-type'))
          args.push('--header', 'Content-Type: application/x-www-form-urlencoded');
      }
      args.push('--data-binary', String(data));
    }
    args.push(String(url));
    const proc = spawnSync('C:\\Windows\\System32\\curl.exe', args,
      { encoding: null, timeout: timeout + 2000, maxBuffer: 24 * 1024 * 1024, windowsHide: true });
    if (proc.error || proc.status !== 0) throw new Error(`drpy2 HTTP 请求失败: ${proc.error?.message || proc.stderr?.toString('utf8') || proc.status}`);
    const blocks = readFileSync(headersFile, 'utf8').trim().split(/\r?\n\r?\n/).filter(x => /^HTTP\//i.test(x));
    const headerLines = (blocks.at(-1) || '').split(/\r?\n/);
    const status = Number(headerLines[0]?.match(/\s(\d{3})\b/)?.[1] || 0);
    const responseHeaders = {};
    for (const line of headerLines.slice(1)) {
      const colon = line.indexOf(':');
      if (colon > 0) responseHeaders[line.slice(0, colon).trim().toLowerCase()] = line.slice(colon + 1).trim();
    }
    const charset = String(options.encoding || responseHeaders['content-type']?.match(/charset=([^;\s]+)/i)?.[1] || 'utf-8');
    let content;
    if (options.buffer === 2) content = proc.stdout.toString('base64');
    else {
      try { content = new TextDecoder(charset).decode(proc.stdout); }
      catch { content = proc.stdout.toString('utf8'); }
    }
    return { ok: status >= 200 && status < 300, status, url: String(url), headers: responseHeaders, content };
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

async function getSpider(request) {
  const identity = `${request.site}|${request.api}|${request.ext}`;
  if (spiders.has(identity)) return spiders.get(identity);
  const api = request.api;
  const drpy = /(?:^|\/)drpy2(?:\.min)?\.js(?:\?|$)/i.test(api);
  activeSite = request;
  globalThis.req = drpy ? syncReq : asyncReq;
  globalThis.fetch = drpy ? (url => syncReq(url).content) : nativeFetch;
  let module;
  if (drpy) {
    const filename = createHash('sha256').update(identity).digest('hex').slice(0, 20);
    module = await import(new URL(`./drpy2/drpy2.js?site=${filename}`, import.meta.url).href);
  } else {
    if (!/^https?:\/\//i.test(api)) throw new Error('JS 脚本地址不是 HTTP URL');
    const response = await nativeFetch(api, { signal: AbortSignal.timeout(15000) });
    if (!response.ok) throw new Error(`JS 脚本 HTTP ${response.status}`);
    let code = await response.text();
    if (/^\s*<(?:!doctype|html)/i.test(code)) throw new Error('JS 站点返回 HTML 而非脚本');
    code = code.replaceAll('assets://js/lib/cat.js', assets);
    for (const name of assetNames)
      code = code.replaceAll(`assets://js/lib/${name}`, new URL(`./assets/${name}`, import.meta.url).href);
    if (/assets:\/\//.test(code)) throw new Error('JS 站点需要尚未实现的 assets 模块');
    const filename = createHash('sha256').update(identity).digest('hex').slice(0, 20) + '.mjs';
    const local = join(cache, filename);
    await writeFile(local, code);
    module = await import(pathToFileURL(local).href);
  }
  const spider = module.__jsEvalReturn?.() ?? (typeof module.default === 'function' ? module.default() : module.default);
  if (!spider) throw new Error('JS 脚本没有导出 Spider');
  let ext = request.ext || '';
  if (typeof ext === 'string' && /^[\[{]/.test(ext.trim())) {
    try { ext = JSON.parse(ext); } catch { /* script handles string */ }
  }
  if (drpy) {
    if (/^https?:\/\//i.test(ext)) {
      const ruleResponse = syncReq(ext, { timeout: 15000 });
      if (!ruleResponse.ok) throw new Error(`drpy2 规则 HTTP ${ruleResponse.status}: ${ext}`);
      ext = ruleResponse.content;
    }
    spider.init?.(ext);
  } else await spider.init?.({ skey: request.site, stype: request.type || 3, ext });
  const entry = { spider, drpy };
  spiders.set(identity, entry);
  return entry;
}

async function call(request) {
  const { spider, drpy } = await getSpider(request);
  activeSite = request;
  globalThis.req = drpy ? syncReq : asyncReq;
  globalThis.fetch = drpy ? (url => syncReq(url).content) : nativeFetch;
  const args = request.args || {};
  let result;
  switch (request.method) {
    case 'home': result = await spider.home?.(true); break;
    case 'category': result = await spider.category?.(args.tid || '', String(args.pg || 1), true, args.extend || {}); break;
    case 'detail': result = await spider.detail?.(args.ids?.[0] || ''); break;
    case 'play': result = await spider.play?.(args.flag || '', args.id || '', args.vipFlags || []); break;
    case 'proxy': result = await spider.proxy?.(args.params || {}); break;
    default: throw new Error(`unknown method: ${request.method}`);
  }
  return typeof result === 'string' ? result : JSON.stringify(result || {});
}

for await (const line of createInterface({ input: process.stdin })) {
  try {
    const result = await call(JSON.parse(line.replace(/^\uFEFF/, '')));
    process.stdout.write(JSON.stringify({ ok: true, data: result }) + '\n');
  } catch (error) {
    process.stdout.write(JSON.stringify({ ok: false, error: String(error?.message || error) }) + '\n');
  }
}
