import { useEffect, useState } from 'react'
import api from '../api/axios'
import { useRepositories } from '../hooks/useRepositories'
import type { ChatCosts } from '../hooks/useChats'
import { useTasks } from '../hooks/useTasks'
import { computeCosts, formatCosts } from '../utils/cost'

function HomePage() {
  const { repositories, loading, error } = useRepositories()
  const { tasks } = useTasks(undefined)
  const [now] = useState(() => new Date())
  const [chatCosts, setChatCosts] = useState<ChatCosts>({ total: 0, month: 0 })

  useEffect(() => {
    api.get<ChatCosts>('/chat/costs').then(({ data }) => setChatCosts(data)).catch(() => {})
  }, [])

  if (loading) return <div className="page"><p>Loading...</p></div>
  if (error) return <div className="page"><p>Failed to load repositories.</p></div>

  const taskCosts = computeCosts(tasks, now)
  const allCosts = { total: taskCosts.total + chatCosts.total, monthly: taskCosts.monthly + chatCosts.month }

  return (
    <div className="page">
      <div className="page-header">
        <h1 className="page-title">Costs</h1>
        <span className="page-cost">All — {formatCosts(allCosts)}</span>
      </div>
      <ul className="cost-list">
        {chatCosts.total > 0 && (
          <li className="cost-item">
            <span className="cost-name">Chats</span>
            <span className="page-cost">{formatCosts({ total: chatCosts.total, monthly: chatCosts.month })}</span>
          </li>
        )}
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
