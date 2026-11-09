import React, { useEffect, useState } from 'react'
import { NavLink, useParams } from 'react-router-dom'
import { api, timeAgo, fmtTime } from '../api'
import Poster, { Grid } from '../components/Poster'

// 资料库：收藏 / 播放记录
export default function Library() {
  const { tab = 'favorites' } = useParams()
  const [items, setItems] = useState(null)

  const load = () => (tab === 'history' ? api.history() : api.favorites()).then((d) => setItems(d.items || []))
  useEffect(() => {
    setItems(null)
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab])

  const remove = async (it) => {
    await (tab === 'history' ? api.removeHistory(it.name) : api.removeFavorite(it.name))
    load()
  }

  return (
    <div>
      <div className="tabs">
        <NavLink to="/library/favorites">收藏</NavLink>
        <NavLink to="/library/history">播放记录</NavLink>
      </div>
      {items === null ? (
        <div className="loading">加载中…</div>
      ) : !items.length ? (
        <div className="empty">{tab === 'history' ? '还没有播放记录' : '还没有收藏，去详情页点「收藏」吧'}</div>
      ) : (
        <Grid>
          {items.map((it) => (
            <Poster
              key={it.key}
              item={it}
              onRemove={remove}
              progress={tab === 'history' && it.duration ? it.position / it.duration : 0}
              sub={
                tab === 'history'
                  ? `${it.episode || ''}${it.position ? ' · ' + fmtTime(it.position) : ''} · ${timeAgo(it.watchedAt)}`
                  : it.eps
                    ? `共 ${it.eps} 集`
                    : undefined
              }
            />
          ))}
        </Grid>
      )}
    </div>
  )
}
