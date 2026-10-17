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

  const costEntries: { key: string; name: string; costs: { total: number; monthly: number } }[] = []
  if (chatCosts.total > 0) {
    costEntries.push({ key: 'chats', name: 'Chats', costs: { total: chatCosts.total, monthly: chatCosts.month } })
  }
  for (const repo of repositories) {
    const costs = computeCosts(
      tasks.filter((t) => t.repositoryId === repo.id),
      now,
    )
    if (costs.total > 0) costEntries.push({ key: repo.id, name: repo.name, costs })
  }
  costEntries.sort((a, b) => b.costs.total - a.costs.total)

  return (
    <div className="page">
      <div className="page-header">
        <h1 className="page-title">Costs</h1>
        <span className="page-cost">All — {formatCosts(allCosts)}</span>
      </div>
      <ul className="cost-list">
        {costEntries.map(({ key, name, costs }) => (
          <li key={key} className="cost-item">
            <span className="cost-name">{name}</span>
            <span className="page-cost">{formatCosts(costs)}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

export default HomePage
