import { useEffect, useState } from 'react'
import { Outlet, useLocation } from 'react-router-dom'
import Sidebar from './Sidebar'

function Layout() {
  const [open, setOpen] = useState(false)
  const location = useLocation()

  useEffect(() => {
    setOpen(false)
  }, [location.pathname])

  return (
    <div className={`app-shell${open ? ' nav-open' : ''}`}>
      <header className="mobile-bar">
        <button
          className="mobile-menu-btn"
          onClick={() => setOpen((v) => !v)}
          aria-label={open ? 'Close menu' : 'Open menu'}
          aria-expanded={open}
        >
          <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M4 6h16M4 12h16M4 18h16" />
          </svg>
        </button>
        <span className="mobile-bar-title">AutoPilot</span>
      </header>
      <div className="sidebar-backdrop" onClick={() => setOpen(false)} />
      <Sidebar />
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  )
}

export default Layout
