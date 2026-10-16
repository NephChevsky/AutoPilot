import { useNavigate } from 'react-router-dom'
import { useRepositories } from '../hooks/useRepositories'
import { useAuthStore } from '../store/authStore'
import './Sidebar.css'

function Sidebar() {
  const { repositories, loading, error, refreshing, refresh, setPinned } = useRepositories()
  const logout = useAuthStore((state) => state.logout)
  const navigate = useNavigate()

  const handleLogout = async () => {
    await logout()
    navigate('/login')
  }

  return (
    <aside className="sidebar">
      <div className="sidebar-title">AutoPilot</div>

      <nav className="sidebar-section">
        <div className="sidebar-section-header">
          <span className="sidebar-section-title">Repositories</span>
          <button
            className={`sidebar-refresh${refreshing ? ' spinning' : ''}`}
            onClick={refresh}
            disabled={refreshing}
            title="Refresh repositories"
            aria-label="Refresh repositories"
          >
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 12a9 9 0 1 1-2.64-6.36" />
              <path d="M21 3v6h-6" />
            </svg>
          </button>
        </div>
        {loading && <div className="sidebar-message">Loading...</div>}
        {error && <div className="sidebar-message">Failed to load repositories.</div>}
        {!loading && !error && repositories.length === 0 && (
          <div className="sidebar-message">No repositories.</div>
        )}
        <ul className="sidebar-list">
          {repositories.map((repo) => (
            <li key={repo.id} className={`sidebar-row${repo.pinned ? ' pinned' : ''}`}>
              <a
                className="sidebar-item"
                href={repo.htmlUrl}
                target="_blank"
                rel="noopener noreferrer"
                title={repo.name}
              >
                {repo.name}
              </a>
              <button
                className="sidebar-pin"
                onClick={() => setPinned(repo.id, !repo.pinned)}
                title={repo.pinned ? 'Unpin' : 'Pin'}
                aria-label={repo.pinned ? 'Unpin repository' : 'Pin repository'}
              >
                <svg viewBox="0 0 24 24" width="14" height="14" fill={repo.pinned ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M12 17v5" />
                  <path d="M9 3h6l-1 7 3 3v2H7v-2l3-3z" />
                </svg>
              </button>
            </li>
          ))}
        </ul>
      </nav>

      <button className="sidebar-logout" onClick={handleLogout}>
        Logout
      </button>
    </aside>
  )
}

export default Sidebar
