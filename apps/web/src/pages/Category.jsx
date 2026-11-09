import React, { useCallback, useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api } from '../api'
import Poster, { Grid, SkeletonGrid } from '../components/Poster'

const LABELS = { series: '剧集', movie: '电影', variety: '综艺', anime: '动漫' }

// 分类页：网格 + 触底自动加载下一页（按名称跨页去重）
export default function Category() {
  const { cat } = useParams()
  const [items, setItems] = useState([])
  const [page, setPage] = useState(0)
  const [loading, setLoading] = useState(false)
  const [done, setDone] = useState(false)
  const seen = useRef(new Set())
  const sentinel = useRef(null)

  useEffect(() => {
    seen.current = new Set()
    setItems([])
    setPage(0)
    setDone(false)
    window.scrollTo(0, 0)
  }, [cat])

  const more = useCallback(async () => {
    if (loading || done) return
    setLoading(true)
    const next = page + 1
    try {
      const d = await api.category(cat, next)
      const fresh = (d.items || []).filter((i) => !seen.current.has(i.name) && seen.current.add(i.name))
      setItems((x) => x.concat(fresh))
      setPage(next)
      if (!d.items?.length) setDone(true)
    } catch {
      setDone(true)
    }
    setLoading(false)
  }, [cat, page, loading, done])

  useEffect(() => {
    const el = sentinel.current
    if (!el) return
    const io = new IntersectionObserver((e) => e[0].isIntersecting && more(), { rootMargin: '600px' })
    io.observe(el)
    return () => io.disconnect()
  }, [more])

  return (
    <div>
      <h1 className="page-title">{LABELS[cat] || cat}</h1>
      {items.length ? (
        <Grid>
          {items.map((it) => (
            <Poster key={it.site + it.vodId} item={it} />
          ))}
        </Grid>
      ) : !loading && done ? (
        <div className="empty">暂无内容，稍后再试</div>
      ) : (
        <SkeletonGrid />
      )}
      <div ref={sentinel} className="sentinel">
        {loading && items.length ? '加载中…' : done && items.length ? '— 到底了 —' : ''}
      </div>
    </div>
  )
}
