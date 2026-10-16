import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import api from '../api/axios'
import NewTaskCard from '../components/NewTaskCard'
import TaskCard from '../components/TaskCard'
import type { GithubRepository } from '../hooks/useRepositories'
import { useTasks } from '../hooks/useTasks'
import type { RepositoryTask, TaskStatus } from '../hooks/useTasks'

function RepositoryDetails({ id }: { id: string | undefined }) {
  const [repository, setRepository] = useState<GithubRepository | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [creatingTask, setCreatingTask] = useState(false)
  const [now] = useState(() => new Date())
  const [toggled, setToggled] = useState<Set<string>>(new Set())
  const { tasks, createTask, updateTask, startTask, followUp, mergeTask, completeTask, cancelTask } = useTasks(id)

  useEffect(() => {
    let cancelled = false

    api
      .get<GithubRepository>(`/github/repositories/${id}`)
      .then(({ data }) => {
        if (!cancelled) setRepository(data)
      })
      .catch(() => {
        if (!cancelled) setError(true)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [id])

  const handleCreate = async (title: string, text: string) => {
    const ok = await createTask(title, text)
    if (ok) setCreatingTask(false)
    return ok
  }

  const byCreation = (a: RepositoryTask, b: RepositoryTask) =>
    new Date(a.creationDateTime).getTime() - new Date(b.creationDateTime).getTime()
  const finishedTime = (t: RepositoryTask) => new Date(t.completionDateTime ?? t.creationDateTime).getTime()
  const withStatus = (...statuses: TaskStatus[]) => tasks.filter((t) => statuses.includes(t.status))

  const sections: { title: string; tasks: RepositoryTask[] }[] = [
    { title: 'Pending', tasks: withStatus('NotStarted', 'PendingStart').sort(byCreation) },
    { title: 'Processing', tasks: withStatus('Processing').sort(byCreation) },
    { title: 'Waiting for review', tasks: withStatus('WaitingForReview').sort(byCreation) },
    {
      title: 'Completed',
      tasks: withStatus('Completed', 'Error', 'Cancelled').sort((a, b) => finishedTime(b) - finishedTime(a)),
    },
  ]

  const isOpen = (title: string) => (title === 'Completed') === toggled.has(title)
  const toggleSection = (title: string) =>
    setToggled((prev) => {
      const next = new Set(prev)
      if (!next.delete(title)) next.add(title)
      return next
    })

  const totalCost = tasks.reduce((sum, t) => sum + (t.cost ?? 0), 0)
  const monthlyCost = tasks.reduce((sum, t) => {
    const d = new Date(t.completionDateTime ?? t.creationDateTime)
    const inMonth = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()
    return inMonth ? sum + (t.cost ?? 0) : sum
  }, 0)

  if (loading) return <div className="page"><p>Loading...</p></div>
  if (error || !repository) return <div className="page"><p>Repository not found.</p></div>

  return (
    <div className="page">
      <div className="page-header">
        <h1 className="page-title">{repository.name}</h1>
        <a
          className="page-link"
          href={repository.htmlUrl}
          target="_blank"
          rel="noopener noreferrer"
          title="Open on GitHub"
          aria-label="Open on GitHub"
        >
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
            <path d="M15 3h6v6" />
            <path d="M10 14 21 3" />
          </svg>
        </a>
        <span className="page-cost">
          Total: {totalCost.toFixed(2)} AIU · This month: {monthlyCost.toFixed(2)} AIU
        </span>
        <button className="btn-primary" onClick={() => setCreatingTask(true)}>
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 5v14" />
            <path d="M5 12h14" />
          </svg>
          Create Task
        </button>
      </div>

      {tasks.length === 0 && !creatingTask ? (
        <div className="empty-state">No tasks yet. Create one to get started.</div>
      ) : (
        sections.map(
          (section) => {
          if (!(section.tasks.length > 0 || (section.title === 'Pending' && creatingTask))) return null
          const open = isOpen(section.title)
          return (
              <section key={section.title}>
                <h2 className="section-title">
                  <button className="section-toggle" aria-expanded={open} onClick={() => toggleSection(section.title)}>
                    <span className="section-chevron">{open ? '▾' : '▸'}</span>
                    {section.title} ({section.tasks.length})
                  </button>
                </h2>
                {open && <ul className="task-list">
                  {section.title === 'Pending' && creatingTask && (
                    <NewTaskCard onCreate={handleCreate} onCancel={() => setCreatingTask(false)} />
                  )}
                  {section.tasks.map((task) => (
                    <TaskCard
                      key={task.id}
                      task={task}
                      repository={repository}
                      onUpdate={updateTask}
                      onStart={startTask}
                      onMerge={mergeTask}
                      onComplete={completeTask}
                      onCancel={cancelTask}
                      onFollowUp={followUp}
                    />
                  ))}
                </ul>}
              </section>
          )
        })
      )}
    </div>
  )
}

function RepositoryPage() {
  const { id } = useParams()
  return <RepositoryDetails key={id} id={id} />
}

export default RepositoryPage
