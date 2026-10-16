import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import api from '../api/axios'
import CreateTaskDialog from '../components/CreateTaskDialog'
import TaskCard from '../components/TaskCard'
import type { GithubRepository } from '../hooks/useRepositories'
import { useTasks } from '../hooks/useTasks'

function RepositoryDetails({ id }: { id: string | undefined }) {
  const [repository, setRepository] = useState<GithubRepository | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [creatingTask, setCreatingTask] = useState(false)
  const { tasks, createTask, updateTask, startTask, followUp, mergeTask, deleteTask, cancelTask } = useTasks(id)

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
    if (await createTask(title, text)) setCreatingTask(false)
  }

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
        <button className="btn-primary" onClick={() => setCreatingTask(true)}>
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 5v14" />
            <path d="M5 12h14" />
          </svg>
          Create Task
        </button>
      </div>

      <h2 className="section-title">Tasks</h2>
      {tasks.length === 0 ? (
        <div className="empty-state">No tasks yet. Create one to get started.</div>
      ) : (
        <ul className="task-list">
          {tasks.map((task) => (
            <TaskCard
              key={task.id}
              task={task}
              repository={repository}
              onUpdate={updateTask}
              onStart={startTask}
              onMerge={mergeTask}
              onDelete={deleteTask}
              onCancel={cancelTask}
              onFollowUp={followUp}
            />
          ))}
        </ul>
      )}
      {creatingTask && <CreateTaskDialog onClose={() => setCreatingTask(false)} onCreate={handleCreate} />}
    </div>
  )
}

function RepositoryPage() {
  const { id } = useParams()
  return <RepositoryDetails key={id} id={id} />
}

export default RepositoryPage
