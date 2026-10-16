import { useEffect, useState } from 'react'
import toast from 'react-hot-toast'
import Markdown from 'react-markdown'
import { useParams } from 'react-router-dom'
import api from '../api/axios'
import CreateTaskDialog from '../components/CreateTaskDialog'
import type { GithubRepository } from '../hooks/useRepositories'

const encodeBranch = (name: string) => name.split('/').map(encodeURIComponent).join('/')

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
  conversation: ConversationItem[]
}

interface ConversationItem {
  author: 'Neph' | 'Autopilot'
  type: 'Message' | 'Reasoning'
  content: string
  creationDateTime: string
}

// Collapses neighboring reasoning items into one block when reasoning is folded
function mergeReasoning(items: ConversationItem[], status: RepositoryTask['status']): ConversationItem[] {
  if (status !== 'Completed') return items
  return items.reduce<ConversationItem[]>((merged, item) => {
    const last = merged[merged.length - 1]
    if (last && last.type === 'Reasoning' && item.type === 'Reasoning' && last.author === item.author) {
      merged[merged.length - 1] = { ...last, content: `${last.content}\n\n${item.content}` }
    } else {
      merged.push(item)
    }
    return merged
  }, [])
}

const statusLabels: Record<RepositoryTask['status'], string> = {
  NotStarted: 'Not started',
  PendingStart: 'Pending start',
  Processing: 'Processing',
  Completed: 'Completed',
  Error: 'Error',
}

const CONFIRM_ARM_DELAY_MS = 600
const CONFIRM_TIMEOUT_MS = 4000

function DeleteButton({ onConfirm }: { onConfirm: () => void }) {
  const [state, setState] = useState<'idle' | 'arming' | 'armed'>('idle')

  useEffect(() => {
    if (state === 'arming') {
      const timer = setTimeout(() => setState('armed'), CONFIRM_ARM_DELAY_MS)
      return () => clearTimeout(timer)
    }
    if (state === 'armed') {
      const timer = setTimeout(() => setState('idle'), CONFIRM_TIMEOUT_MS)
      return () => clearTimeout(timer)
    }
  }, [state])

  return (
    <button
      className={`btn-secondary danger delete-btn${state === 'idle' ? '' : ' confirming'}${state === 'arming' ? ' arming' : ''}`}
      onClick={() => (state === 'idle' ? setState('arming') : state === 'armed' && onConfirm())}
      onBlur={() => setState('idle')}
    >
      <span className="delete-btn-label" key={state === 'idle' ? 'delete' : 'confirm'}>
        {state === 'idle' ? 'Delete' : 'Confirm?'}
      </span>
    </button>
  )
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

  const hasActiveTask = tasks.some((t) => t.status === 'PendingStart' || t.status === 'Processing')

  useEffect(() => {
    if (!hasActiveTask) return
    let cancelled = false

    const interval = setInterval(() => {
      api
        .get<RepositoryTask[]>('/task', { params: { repositoryId: id } })
        .then(({ data }) => {
          if (cancelled) return
          setTasks((current) =>
            data.map((t) => {
              const existing = current.find((c) => c.id === t.id)
              return existing && t.response === undefined ? { ...t, response: existing.response } : t
            }),
          )
        })
        .catch(() => {})
    }, 3000)

    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [id, hasActiveTask])

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

  const mergeTask = async (taskId: string) => {
    if (!window.confirm('Merge this task branch into the default branch?')) return
    try {
      const { data } = await api.post<RepositoryTask>(`/task/${taskId}/merge`)
      setTasks((current) => current.map((t) => (t.id === data.id ? { ...data, response: t.response } : t)))
      toast.success('Branch merged')
    } catch (e) {
      const message = (e as { response?: { data?: unknown } }).response?.data
      toast.error(typeof message === 'string' ? message : 'Failed to merge branch')
    }
  }

  const deleteTask = async (taskId: string) => {
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
            <li key={task.id} className="task-card">
              <div className="task-head">
                <div>
                  {task.title && <div className="task-title">{task.title}</div>}
                  <div className="task-text">{task.task}</div>
                </div>
                <div className="task-actions">
                  <span className={`status-label status-${task.status}`}>
                    {statusLabels[task.status]}
                  </span>
                  {(task.status === 'NotStarted' || task.status === 'Error') && (
                    <button className="btn-primary" onClick={() => startTask(task.id)}>
                      {task.status === 'Error' ? 'Retry' : 'Start'}
                    </button>
                  )}
                  {task.status === 'Completed' && task.branchName && (
                    <button className="btn-primary" onClick={() => mergeTask(task.id)}>
                      Merge
                    </button>
                  )}
                  {(task.status === 'NotStarted' || task.status === 'Completed' || task.status === 'Error') && (
                    <DeleteButton onConfirm={() => deleteTask(task.id)} />
                  )}
                </div>
              </div>
              {mergeReasoning(task.conversation.slice(1), task.status).map((item, index, items) =>
                item.author === 'Autopilot' && task.status === 'Error' && index === items.length - 1 ? (
                  <pre key={index} className="task-error">{item.content}</pre>
                ) : item.author === 'Autopilot' && item.type === 'Reasoning' && task.status === 'Completed' ? (
                  <details key={index} className="reasoning-fold">
                    <summary>Show reasoning</summary>
                    <div className="markdown">
                      <Markdown>{item.content}</Markdown>
                    </div>
                  </details>
                ) : item.author === 'Autopilot' ? (
                  <div key={index} className="markdown">
                    <Markdown>{item.content}</Markdown>
                  </div>
                ) : (
                  <div key={index} className="task-text">{item.content}</div>
                ),
              )}
              <div className="task-meta">
                <span>{new Date(task.creationDateTime).toLocaleString()}</span>
                {task.branchName && (
                  <a
                    href={`${repository.htmlUrl}/compare/${encodeBranch(repository.defaultBranch || 'master')}...${encodeBranch(task.branchName)}`}
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
