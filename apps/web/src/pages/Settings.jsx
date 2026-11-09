import React, { useCallback, useEffect, useState } from 'react'
import { api, timeAgo } from '../api'

const KIND = { csp: 'JAR', cms: '苹果CMS', js: 'JS', py: 'Python', guard: '原生加固·不支持', other: '暂不支持' }

function kindChips(byKind) {
  return Object.entries(byKind || {})
    .filter(([, n]) => n > 0)
    .map(([k, n]) => (
      <span className="chip" key={k}>
        {KIND[k] || k} {n}
      </span>
    ))
}

// 设置：订阅源管理（添加 / 测试 / 启停 / 删除 / 重新加载）
export default function Settings() {
  const [list, setList] = useState(null)
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [test, setTest] = useState(null) // {ok, p | text}
  const [busy, setBusy] = useState('')

  const load = useCallback(() => api.sources().then((d) => setList(d.sources || [])).catch(() => {}), [])
  useEffect(() => {
    load()
  }, [load])
  // 有源在加载时轮询状态
  const loading = (list || []).some((s) => s.loading)
  useEffect(() => {
    if (!loading) return
    const t = setInterval(load, 2000)
    return () => clearInterval(t)
  }, [loading, load])

  const doTest = async () => {
    if (!url.trim()) return
    setBusy('test')
    setTest(null)
    try {
      setTest({ ok: true, p: await api.testSource(url.trim()) })
    } catch (e) {
      setTest({ ok: false, text: e.message })
    }
    setBusy('')
  }

  const doAdd = async () => {
    if (!url.trim()) return
    setBusy('add')
    try {
      await api.addSource(name.trim(), url.trim())
      setName('')
      setUrl('')
      setTest(null)
      await load()
      setTimeout(load, 500)
    } catch (e) {
      setTest({ ok: false, text: e.message })
    }
    setBusy('')
  }

  const remove = async (s) => {
    if (!window.confirm(`删除订阅「${s.name}」？该源的站点会从推荐和搜索中移除。`)) return
    await api.removeSource(s.id)
    load()
  }
  const toggle = async (s) => {
    await api.toggleSource(s.id, !s.enabled)
    setTimeout(load, 300)
  }
  const reload = async () => {
    await api.reloadSources()
    setTimeout(load, 300)
  }

  return (
    <div className="settings">
      <h1 className="page-title">订阅源设置</h1>
      <p className="lead">
        添加影视仓 / TVBox 配置地址即可接入更多片源，支持普通 JSON、图片内嵌配置和加密配置。
        添加后会在后台下载并加载，首页推荐稍后自动刷新。
      </p>

      <h2>添加订阅源</h2>
      <div className="src-form">
        <input placeholder="名称（可选）" value={name} onChange={(e) => setName(e.target.value)} />
        <input
          placeholder="订阅地址，如 http://example.com/tv"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && doAdd()}
        />
        <button className="btn" onClick={doTest} disabled={!url.trim() || busy === 'test'}>
          {busy === 'test' ? '测试中…' : '测试'}
        </button>
        <button className="btn primary" onClick={doAdd} disabled={!url.trim() || busy === 'add'}>
          添加
        </button>
      </div>
      {test ? (
        <div className={'test-result ' + (test.ok ? 'ok' : 'bad')}>
          {test.ok ? (
            <>
              ✓ 解码成功：共 {test.p.sites} 个站点，其中 <b>{test.p.usable}</b> 个当前可用
              <div className="chips">{kindChips(test.p.byKind)}</div>
            </>
          ) : (
            <>✗ {test.text}</>
          )}
        </div>
      ) : null}

      <h2>
        已添加（{list ? list.length : 0}）
        <button className="link" onClick={reload}>
          重新加载全部
        </button>
      </h2>
      {list === null ? (
        <div className="loading">加载中…</div>
      ) : !list.length ? (
        <div className="empty small">还没有订阅源，请在上方添加。</div>
      ) : (
        list.map((s) => (
          <div className={'src-card' + (s.enabled ? '' : ' off')} key={s.id}>
            <div>
              <div className="src-name">
                {s.name}
                {s.loading ? <span className="chip load">加载中…</span> : null}
                {!s.loading && s.loadedAt && !s.error ? <span className="chip good">已加载 · {timeAgo(s.loadedAt)}</span> : null}
                {!s.loading && s.fromCache ? <span className="chip bad">使用缓存</span> : null}
                {!s.loading && s.error && !s.fromCache && !s.sites ? <span className="chip bad">加载失败</span> : null}
              </div>
              <div className="src-url">{s.url}</div>
              {s.sites ? (
                <div className="chips">
                  <span className="chip good">
                    可用 {s.usable} / {s.sites}
                  </span>
                  {kindChips(s.byKind)}
                </div>
              ) : null}
            </div>
            <div className="src-actions">
              <div className={'switch' + (s.enabled ? ' on' : '')} onClick={() => toggle(s)} title={s.enabled ? '点击停用' : '点击启用'} />
              <button className="btn" onClick={() => remove(s)}>
                删除
              </button>
            </div>
            {s.error ? <div className="src-err">{s.error}</div> : null}
          </div>
        ))
      )}
    </div>
  )
}
