import { useState } from 'react'
import { useRepositories } from '../hooks/useRepositories'
import { useTasks } from '../hooks/useTasks'
import { computeCosts, formatCosts } from '../utils/cost'

function HomePage() {
  const { repositories, loading, error } = useRepositories()
  const { tasks } = useTasks(undefined)
  const [now] = useState(() => new Date())

  if (loading) return <div className="page"><p>Loading...</p></div>
  if (error) return <div className="page"><p>Failed to load repositories.</p></div>

  return (
    <div className="page">
      <div className="page-header">
        <h1 className="page-title">Costs</h1>
        <span className="page-cost">All repositories — {formatCosts(computeCosts(tasks, now))}</span>
      </div>
      <ul className="cost-list">
        {repositories.map((repo) => {
          const costs = computeCosts(
            tasks.filter((t) => t.repositoryId === repo.id),
            now,
          )
          if (costs.total === 0) return null
          return (
            <li key={repo.id} className="cost-item">
              <span className="cost-name">{repo.name}</span>
              <span className="page-cost">{formatCosts(costs)}</span>
            </li>
          )
        })}
      </ul>
    </div>
  )
}

export default HomePage
