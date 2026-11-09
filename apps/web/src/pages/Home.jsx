import React, { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, img, detailPath, timeAgo } from '../api'
import Poster from '../components/Poster'
import Rail from '../components/Rail'

export default function Home() {
  const [data, setData] = useState(null)
  const [idx, setIdx] = useState(0)
  const nav = useNavigate()

  useEffect(() => {
    let alive = true
    const load = () => api.home().then((d) => alive && setData(d)).catch(() => {})
    load()
    // 首次启动快照为空时，后台刷新完成前轮询
    const t = setInterval(() => alive && (!data || !data.rows.length) && load(), 4000)
    return () => {
      alive = false
      clearInterval(t)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const rows = data?.rows || []
  const hero = rows.flatMap((r) => r.items.slice(0, 3)).filter((i) => i.pic).slice(0, 6)

  useEffect(() => {
    if (hero.length < 2) return
    const t = setInterval(() => setIdx((i) => (i + 1) % hero.length), 7000)
    return () => clearInterval(t)
  }, [hero.length])

  if (!data) return <div className="loading">加载中…</div>
  if (!rows.length)
    return (
      <div className="empty">
        <div className="spinner" />
        <h3>正在准备首页推荐…</h3>
        <p>首次启动需要从片源拉取数据，通常 1 分钟内完成{data.status?.step ? `（${data.status.step}）` : ''}</p>
      </div>
    )

  const h = hero[idx % hero.length]
  const history = data.history || []
  const favorites = data.favorites || []

  return (
    <div>
      {h ? (
        <div className="hero" key={h.site + h.vodId}>
          <div className="hero-bg" style={{ backgroundImage: `url(${img(h.pic)})` }} />
          <div className="hero-shade" />
          <img className="hero-poster" src={img(h.pic)} alt="" />
          <div className="hero-body">
            <div className="hero-tag">{[h.typeName, h.year, h.area].filter(Boolean).join(' · ')}</div>
            <h1>{h.name}</h1>
            {h.remarks ? <div className="hero-remark">{h.remarks}</div> : null}
            <div className="hero-actions">
              <button className="btn primary" onClick={() => nav(detailPath(h))}>
                ▶ 立即观看
              </button>
            </div>
          </div>
          <div className="dots">
            {hero.map((_, i) => (
              <i key={i} className={i === idx % hero.length ? 'on' : ''} onClick={() => setIdx(i)} />
            ))}
          </div>
        </div>
      ) : null}

      {history.length ? (
        <Rail title="继续观看" more="/library/history">
          {history.map((x) => (
            <Poster
              key={x.key}
              item={x}
              wide
              progress={x.duration ? x.position / x.duration : 0}
              sub={x.episode ? `${x.episode}${x.remarks ? ' · ' + x.remarks : ''}` : x.remarks}
            />
          ))}
        </Rail>
      ) : null}

      {favorites.length ? (
        <Rail title="我的收藏" more="/library/favorites">
          {favorites.map((x) => (
            <Poster key={x.key} item={x} />
          ))}
        </Rail>
      ) : null}

      {rows.map((r) => (
        <Rail key={r.key} title={r.title} more={'/category/' + r.key}>
          {r.items.slice(0, 24).map((it) => (
            <Poster key={it.site + it.vodId} item={it} />
          ))}
        </Rail>
      ))}

      <div className="foot">
        推荐更新于 {timeAgo(rows[0]?.at)}
        {data.status?.running ? ` · 正在刷新（${data.status.step}）` : ''}
        <button className="link" onClick={() => api.refresh()}>
          立即刷新
        </button>
      </div>
    </div>
  )
}
