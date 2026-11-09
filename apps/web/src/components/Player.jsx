import React, { useEffect, useRef, useState } from 'react'
import Hls from 'hls.js'

// 全屏播放器：HLS/直链，支持从 startAt 续播，定时上报进度，播完自动下一集。
export default function Player({ title, url, startAt = 0, onProgress, onEnded, onClose, hasNext, onNext }) {
  const ref = useRef(null)
  const [err, setErr] = useState('')
  const last = useRef({ t: 0, d: 0 })

  useEffect(() => {
    const v = ref.current
    if (!v) return
    setErr('')
    const src = '/api/proxy?url=' + encodeURIComponent(url)
    let hls
    const seek = () => {
      if (startAt > 5) v.currentTime = startAt
      v.play().catch(() => {})
    }
    if (/\.m3u8/i.test(url) && Hls.isSupported()) {
      hls = new Hls({ enableWorker: true, backBufferLength: 30 })
      hls.loadSource(src)
      hls.attachMedia(v)
      hls.on(Hls.Events.MANIFEST_PARSED, seek)
      hls.on(Hls.Events.ERROR, (_, d) => d.fatal && setErr('播放出错：' + d.details))
    } else {
      v.src = src
      v.addEventListener('loadedmetadata', seek, { once: true })
    }
    // 每 10 秒上报一次进度
    const timer = setInterval(() => {
      if (v.currentTime > 0 && !v.paused) report()
    }, 10000)
    const report = () => {
      last.current = { t: v.currentTime, d: v.duration || 0 }
      onProgress && onProgress(v.currentTime, v.duration || 0)
    }
    return () => {
      clearInterval(timer)
      if (v.currentTime > 0) report()
      hls && hls.destroy()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url])

  useEffect(() => {
    const onKey = (e) => {
      const v = ref.current
      if (!v) return
      if (e.key === 'Escape') onClose()
      else if (e.key === ' ') (e.preventDefault(), v.paused ? v.play() : v.pause())
      else if (e.key === 'ArrowRight') v.currentTime += 10
      else if (e.key === 'ArrowLeft') v.currentTime -= 10
      else if (e.key === 'f') document.fullscreenElement ? document.exitFullscreen() : v.parentElement.requestFullscreen()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="player">
      <div className="player-top">
        <button className="pill" onClick={onClose}>
          ‹ 返回
        </button>
        <span className="pt">{title}</span>
        {hasNext ? (
          <button className="pill" onClick={onNext}>
            下一集 ›|
          </button>
        ) : null}
      </div>
      <video ref={ref} controls autoPlay playsInline onEnded={onEnded} />
      {err ? <div className="player-err">{err}</div> : null}
    </div>
  )
}
