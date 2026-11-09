import React, { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api, img, fmtTime } from '../api'
import Player from '../components/Player'

export default function Detail() {
  const { site, id } = useParams()
  const nav = useNavigate()
  const [d, setD] = useState(null)
  const [error, setError] = useState('')
  const [fav, setFav] = useState(false)
  const [hist, setHist] = useState(null) // 该片的播放记录
  const [playing, setPlaying] = useState(null) // {ep, url, startAt}
  const [busy, setBusy] = useState('')
  const lastSaved = useRef(0)

  useEffect(() => {
    let alive = true
    setD(null)
    setError('')
    api.detail(site, id).then((x) => alive && setD(x)).catch((e) => alive && setError(e.message))
    return () => {
      alive = false
    }
  }, [site, id])

  useEffect(() => {
    if (!d) return
    api.favorites().then((r) => setFav(r.items.some((f) => f.name === d.name)))
    api.history().then((r) => setHist(r.items.find((h) => h.name === d.name) || null))
  }, [d])

  const entry = useCallback(
    () => ({ site: d.site, vodId: d.vodId, name: d.name, pic: d.pic, year: d.year, remarks: d.remarks, eps: d.episodes.length }),
    [d],
  )

  const toggleFav = async () => {
    if (fav) await api.removeFavorite(d.name)
    else await api.addFavorite(entry())
    setFav(!fav)
  }

  const play = async (ep, startAt = 0) => {
    setBusy(ep.name)
    try {
      const cands = ep.urls?.length ? ep.urls : [{ flag: '', url: ep.url }]
      const p = await api.playAuto(d.site, d.vodId, cands)
      setPlaying({ ep, url: p.url, startAt })
      // 开播即写入一条记录，保证「继续观看」立刻出现
      api.putHistory({ ...entry(), episode: ep.name, position: startAt, duration: hist?.duration || 0 })
    } catch (e) {
      alert('播放失败：' + e.message)
    }
    setBusy('')
  }

  const onProgress = (pos, dur) => {
    if (Date.now() - lastSaved.current < 3000) return
    lastSaved.current = Date.now()
    api.putHistory({ ...entry(), episode: playing.ep.name, position: pos, duration: dur })
  }

  const closePlayer = () => {
    setPlaying(null)
    api.history().then((r) => setHist(r.items.find((h) => h.name === d.name) || null))
  }

  if (error)
    return (
      <div className="empty">
        <h3>详情加载失败</h3>
        <p>{error}</p>
        <button className="btn" onClick={() => nav(-1)}>
          返回
        </button>
      </div>
    )
  if (!d) return <div className="loading">加载中…</div>

  const eps = d.episodes || []
  const resumeEp = hist && eps.find((e) => e.name === hist.episode)
  const idx = playing ? eps.findIndex((e) => e.name === playing.ep.name) : -1
  const next = idx >= 0 ? eps[idx + 1] : null

  return (
    <div className="detail">
      <div className="detail-bg" style={{ backgroundImage: `url(${img(d.pic)})` }} />
      <button className="pill back" onClick={() => nav(-1)}>
        ‹ 返回
      </button>
      <div className="detail-main">
        <div className="detail-poster">
          {d.pic ? <img src={img(d.pic)} alt={d.name} /> : <div className="poster-ph">{d.name.slice(0, 2)}</div>}
        </div>
        <div className="detail-info">
          <h1>{d.name}</h1>
          <div className="meta">
            {[d.year, d.area, d.remarks, '来源 ' + d.siteName].filter(Boolean).map((m, i) => (
              <span key={i}>{m}</span>
            ))}
          </div>
          <div className="detail-actions">
            {resumeEp ? (
              <button className="btn primary" onClick={() => play(resumeEp, hist.position)}>
                ▶ 继续播放 {resumeEp.name}
                {hist.position > 5 ? ` · ${fmtTime(hist.position)}` : ''}
              </button>
            ) : eps.length ? (
              <button className="btn primary" onClick={() => play(eps[0])}>
                ▶ 播放
              </button>
            ) : null}
            <button className={'btn' + (fav ? ' on' : '')} onClick={toggleFav}>
              {fav ? '★ 已收藏' : '☆ 收藏'}
            </button>
          </div>
          {d.director ? <p className="people"><b>导演</b> {d.director}</p> : null}
          {d.actor ? <p className="people"><b>主演</b> {d.actor}</p> : null}
          {d.content ? <p className="plot">{d.content.replace(/<[^>]+>/g, '')}</p> : null}
        </div>
      </div>

      <section className="eps">
        <h2>选集 {eps.length ? `· ${eps.length}` : ''}</h2>
        {eps.length ? (
          <div className="ep-grid">
            {eps.map((e) => (
              <button
                key={e.name}
                className={'ep' + (hist?.episode === e.name ? ' watched' : '') + (busy === e.name ? ' busy' : '')}
                onClick={() => play(e, hist?.episode === e.name ? hist.position : 0)}
              >
                {e.name}
              </button>
            ))}
          </div>
        ) : (
          <div className="empty small">该资源暂无可用播放源</div>
        )}
      </section>

      {playing ? (
        <Player
          title={`${d.name} · ${playing.ep.name}`}
          url={playing.url}
          startAt={playing.startAt}
          onProgress={onProgress}
          onClose={closePlayer}
          hasNext={!!next}
          onNext={() => next && play(next)}
          onEnded={() => (next ? play(next) : closePlayer())}
        />
      ) : null}
    </div>
  )
}
