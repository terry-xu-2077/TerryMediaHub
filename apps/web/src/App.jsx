import React, { useEffect, useRef, useState } from 'react'
import { Routes, Route, NavLink, Navigate, useNavigate, useSearchParams, useLocation } from 'react-router-dom'
import Home from './pages/Home'
import Category from './pages/Category'
import Search from './pages/Search'
import Detail from './pages/Detail'
import Library from './pages/Library'
import Settings from './pages/Settings'

const NAV = [
  ['/', '首页', true],
  ['/category/series', '剧集'],
  ['/category/variety', '综艺'],
  ['/category/movie', '电影'],
  ['/category/anime', '动漫'],
  ['/library/favorites', '收藏'],
  ['/library/history', '播放记录'],
]

export default function App() {
  const nav = useNavigate()
  const loc = useLocation()
  const [params] = useSearchParams()
  const [kw, setKw] = useState('')
  const input = useRef(null)

  useEffect(() => {
    setKw(loc.pathname === '/search' ? params.get('q') || '' : '')
  }, [loc.pathname, params])

  // 按 / 聚焦搜索框
  useEffect(() => {
    const f = (e) => {
      if (e.key === '/' && document.activeElement?.tagName !== 'INPUT') (e.preventDefault(), input.current?.focus())
    }
    window.addEventListener('keydown', f)
    return () => window.removeEventListener('keydown', f)
  }, [])

  const submit = (e) => {
    e.preventDefault()
    const k = kw.trim()
    if (k) nav('/search?q=' + encodeURIComponent(k))
  }

  return (
    <>
      <header className="topbar">
        <NavLink to="/" className="logo">
          <i>▶</i> TerryMediaHub
        </NavLink>
        <nav>
          {NAV.map(([to, label, end]) => (
            <NavLink key={to} to={to} end={end} className={({ isActive }) => (isActive ? 'on' : '')}>
              {label}
            </NavLink>
          ))}
        </nav>
        <form className="search" onSubmit={submit}>
          <span>⌕</span>
          <input ref={input} value={kw} onChange={(e) => setKw(e.target.value)} placeholder="搜索电影、剧集、综艺、动漫  ( / )" />
        </form>
        <NavLink to="/settings" className={({ isActive }) => 'gear' + (isActive ? ' on' : '')} title="订阅源设置">
          ⚙
        </NavLink>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/category/:cat" element={<Category />} />
          <Route path="/library/:tab" element={<Library />} />
          <Route path="/search" element={<Search />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="/detail/:site/:id" element={<Detail />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </>
  )
}
