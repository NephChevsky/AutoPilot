import { useEffect, useState } from 'react'
import toast from 'react-hot-toast'
import Markdown from 'react-markdown'
import { useParams } from 'react-router-dom'
import api from '../api/axios'
import CreateTaskDialog from '../components/CreateTaskDialog'
import type { GithubRepository } from '../hooks/useRepositories'

interface RepositoryTask {
  id: string
  repositoryId: string
  creationDateTime: string
  completionDateTime: string | null
  title: string
  task: string
  status: 'NotStarted' | 'PendingStart' | 'Processing' | 'Completed' | 'Error'
  response: string | null
  branchName: string | null
}

const statusLabels: Record<RepositoryTask['status'], string> = {
  NotStarted: 'Not started',
  PendingStart: 'Pending start',
  Processing: 'Processing',
  Completed: 'Completed',
  Error: 'Error',
}

function RepositoryDetails({ id }: { id: string | undefined }) {
  const [repository, setRepository] = useState<GithubRepository | null>(null)
  const [tasks, setTasks] = useState<RepositoryTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [creatingTask, setCreatingTask] = useState(false)

  useEffect(() => {
    let cancelled = false

    api
      .get<RepositoryTask[]>('/task', { params: { repositoryId: id } })
      .then(({ data }) => {
        if (!cancelled) setTasks(data)
      })
      .catch(() => {
        if (!cancelled) toast.error('Failed to load tasks')
      })

    return () => {
      cancelled = true
    }
  }, [id])

  const createTask = async (title: string, text: string) => {
    try {
      const { data } = await api.post<RepositoryTask>('/task', { repositoryId: id, title, task: text })
      setTasks((current) => [data, ...current])
      setCreatingTask(false)
    } catch {
      toast.error('Failed to create task')
    }
  }

  const startTask = async (taskId: string) => {
    try {
      const { data } = await api.post<RepositoryTask>(`/task/${taskId}/start`)
      setTasks((current) => current.map((t) => (t.id === data.id ? data : t)))
    } catch {
      toast.error('Failed to start task')
    }
  }

  const deleteTask = async (taskId: string) => {
    if (!window.confirm('Delete this task and its conversation?')) return
    try {
      await api.delete(`/task/${taskId}`)
      setTasks((current) => current.filter((t) => t.id !== taskId))
    } catch {
      toast.error('Failed to delete task')
    }
  }

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

  if (loading) return <p>Loading...</p>
  if (error || !repository) return <p>Repository not found.</p>

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <h1 style={{ margin: 0 }}>{repository.name}</h1>
        <a
          href={repository.htmlUrl}
          target="_blank"
          rel="noopener noreferrer"
          title="Open on GitHub"
          aria-label="Open on GitHub"
          style={{ display: 'flex', color: 'inherit' }}
        >
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
            <path d="M15 3h6v6" />
            <path d="M10 14 21 3" />
          </svg>
        </a>
        <button className="btn-primary" style={{ marginLeft: 12 }} onClick={() => setCreatingTask(true)}>
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 5v14" />
            <path d="M5 12h14" />
          </svg>
          Create Task
        </button>
      </div>

      <h2 style={{ marginTop: 24 }}>Tasks</h2>
      {tasks.length === 0 ? (
        <p>No tasks yet.</p>
      ) : (
        <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: 8 }}>
          {tasks.map((task) => (
            <li
              key={task.id}
              style={{ padding: 12, border: '1px solid #323849', borderRadius: 8 }}
            >
              <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 12 }}>
                <div>
                  {task.title && <div style={{ fontWeight: 600, marginBottom: 4 }}>{task.title}</div>}
                  <div style={{ whiteSpace: 'pre-wrap' }}>{task.task}</div>
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 8 }}>
                  <span className={`status-label status-${task.status}`}>
                    {statusLabels[task.status]}
                  </span>
                  {(task.status === 'NotStarted' || task.status === 'Error') && (
                    <button className="btn-primary" onClick={() => startTask(task.id)}>
                      {task.status === 'Error' ? 'Retry' : 'Start'}
                    </button>
                  )}
                  {(task.status === 'Completed' || task.status === 'Error') && (
                    <button className="btn-primary" onClick={() => deleteTask(task.id)}>
                      Delete
                    </button>
                  )}
                </div>
              </div>
              {task.response && task.status === 'Error' && (
                <pre
                  style={{
                    marginTop: 8,
                    padding: 8,
                    whiteSpace: 'pre-wrap',
                    overflowWrap: 'anywhere',
                    fontSize: 13,
                    background: 'rgba(0, 0, 0, 0.2)',
                    borderRadius: 6,
                    color: '#f87171',
                  }}
                >
                  {task.response}
                </pre>
              )}
              {task.response && task.status !== 'Error' && (
                <div className="markdown">
                  <Markdown>{task.response}</Markdown>
                </div>
              )}
              <div style={{ marginTop: 6, fontSize: 12, display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                <span style={{ opacity: 0.6 }}>{new Date(task.creationDateTime).toLocaleString()}</span>
                {task.branchName && (
                  <a
                    href={`${repository.htmlUrl}/tree/${task.branchName.split('/').map(encodeURIComponent).join('/')}`}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    Branch: {task.branchName}
                  </a>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      {creatingTask && (
        <CreateTaskDialog
          onClose={() => setCreatingTask(false)}
          onCreate={createTask}
        />
      )}
    </div>
  )
}

function RepositoryPage() {
  const { id } = useParams()
  return <RepositoryDetails key={id} id={id} />
}

export default RepositoryPage
