import React from 'react'
import { Link } from 'react-router-dom'
import { img, detailPath } from '../api'

// 海报卡片（Infuse 风格：圆角、悬浮放大、底部标题）。progress 0~1 时显示进度条。
export default function Poster({ item, progress, sub, wide, onRemove }) {
  const name = item.name
  return (
    <Link to={detailPath(item)} className={'poster' + (wide ? ' wide' : '')}>
      <div className="poster-img">
        {item.pic ? (
          <img src={img(item.pic)} alt={name} loading="lazy" onError={(e) => (e.currentTarget.style.display = 'none')} />
        ) : null}
        <div className="poster-ph">{name.slice(0, 2)}</div>
        {item.remarks ? <span className="badge">{item.remarks}</span> : null}
        {item.stale ? <span className="badge warn left">源失效</span> : null}
        {progress > 0 ? (
          <div className="pbar">
            <i style={{ width: Math.min(100, progress * 100) + '%' }} />
          </div>
        ) : null}
        {onRemove ? (
          <button
            className="x"
            title="移除"
            onClick={(e) => {
              e.preventDefault()
              e.stopPropagation()
              onRemove(item)
            }}
          >
            ✕
          </button>
        ) : null}
      </div>
      <div className="poster-title">{name}</div>
      <div className="poster-sub">{sub ?? [item.year, item.typeName].filter(Boolean).join(' · ')}</div>
    </Link>
  )
}

export function Grid({ children }) {
  return <div className="grid">{children}</div>
}

export function SkeletonGrid({ n = 18 }) {
  return (
    <div className="grid">
      {Array.from({ length: n }).map((_, i) => (
        <div key={i} className="poster skel">
          <div className="poster-img" />
          <div className="poster-title">&nbsp;</div>
        </div>
      ))}
    </div>
  )
}
