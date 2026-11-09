import React, { useRef } from 'react'
import { Link } from 'react-router-dom'

// 横向滚动的推荐行，左右箭头翻页。
export default function Rail({ title, more, children }) {
  const ref = useRef(null)
  const scroll = (d) => ref.current && ref.current.scrollBy({ left: d * ref.current.clientWidth * 0.85, behavior: 'smooth' })
  return (
    <section className="rail">
      <div className="rail-head">
        <h2>{title}</h2>
        {more ? (
          <Link to={more} className="rail-more">
            查看全部 ›
          </Link>
        ) : null}
      </div>
      <div className="rail-wrap">
        <button className="arrow l" onClick={() => scroll(-1)} aria-label="向左">
          ‹
        </button>
        <div className="rail-row" ref={ref}>
          {children}
        </div>
        <button className="arrow r" onClick={() => scroll(1)} aria-label="向右">
          ›
        </button>
      </div>
    </section>
  )
}
