import React, { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { searchStream } from '../api'
import Poster, { Grid, SkeletonGrid } from '../components/Poster'

export default function Search() {
  const [params] = useSearchParams()
  const kw = params.get('q') || ''
  const [items, setItems] = useState([])
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!kw) return
    setItems([])
    setBusy(true)
    const ctrl = searchStream(kw, (fresh) => setItems((x) => x.concat(fresh)), () => setBusy(false))
    return () => ctrl.abort()
  }, [kw])

  return (
    <div>
      <h1 className="page-title">
        搜索「{kw}」{busy ? <span className="hint-inline">搜索中…</span> : <span className="hint-inline">{items.length} 个结果</span>}
      </h1>
      {items.length ? (
        <Grid>
          {items.map((it) => (
            <Poster key={it.site + it.vodId} item={it} />
          ))}
        </Grid>
      ) : busy ? (
        <SkeletonGrid n={12} />
      ) : (
        <div className="empty">没有找到相关影视，换个关键词试试</div>
      )}
    </div>
  )
}
