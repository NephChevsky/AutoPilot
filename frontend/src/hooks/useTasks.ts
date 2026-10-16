import { useEffect, useState } from 'react'
import toast from 'react-hot-toast'
import api from '../api/axios'

export type TaskStatus = 'NotStarted' | 'PendingStart' | 'Processing' | 'WaitingForMerge' | 'Completed' | 'Error' | 'Cancelled'

export interface ConversationItem {
  author: 'Neph' | 'Autopilot'
  type: 'Message' | 'Reasoning'
  content: string
  creationDateTime: string
}

export interface RepositoryTask {
  id: string
  repositoryId: string
  creationDateTime: string
  completionDateTime: string | null
  title: string
  task: string
  status: TaskStatus
  response: string | null
  branchName: string | null
  cost: number | null
  conversation: ConversationItem[]
}

const POLL_INTERVAL_MS = 3000

export function useTasks(repositoryId: string | undefined) {
  const [tasks, setTasks] = useState<RepositoryTask[]>([])

  useEffect(() => {
    let cancelled = false

    api
      .get<RepositoryTask[]>('/task', { params: { repositoryId } })
      .then(({ data }) => {
        if (!cancelled) setTasks(data)
      })
      .catch(() => {
        if (!cancelled) toast.error('Failed to load tasks')
      })

    return () => {
      cancelled = true
    }
  }, [repositoryId])

  const hasActiveTask = tasks.some((t) => t.status === 'PendingStart' || t.status === 'Processing')

  useEffect(() => {
    if (!hasActiveTask) return
    let cancelled = false

    const interval = setInterval(() => {
      api
        .get<RepositoryTask[]>('/task', { params: { repositoryId } })
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
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      clearInterval(interval)
    }
  }, [repositoryId, hasActiveTask])

  const replaceTask = (updated: RepositoryTask) =>
    setTasks((current) => current.map((t) => (t.id === updated.id ? updated : t)))

  const createTask = async (title: string, text: string) => {
    try {
      const { data } = await api.post<RepositoryTask>('/task', { repositoryId, title, task: text })
      setTasks((current) => [data, ...current])
      return true
    } catch {
      toast.error('Failed to create task')
      return false
    }
  }

  const updateTask = async (taskId: string, title: string, text: string) => {
    try {
      const { data } = await api.put<RepositoryTask>(`/task/${taskId}`, { title, task: text })
      setTasks((current) => current.map((t) => (t.id === data.id ? { ...data, response: t.response } : t)))
      return true
    } catch {
      toast.error('Failed to update task')
      return false
    }
  }

  const startTask = async (taskId: string) => {
    try {
      const { data } = await api.post<RepositoryTask>(`/task/${taskId}/start`)
      replaceTask(data)
    } catch {
      toast.error('Failed to start task')
    }
  }

  const followUp = async (taskId: string, message: string) => {
    try {
      const { data } = await api.post<RepositoryTask>(`/task/${taskId}/follow-up`, { message })
      replaceTask(data)
      return true
    } catch {
      toast.error('Failed to send changes')
      return false
    }
  }

  const mergeTask = async (taskId: string) => {
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

  const cancelTask = async (taskId: string): Promise<boolean> => {
    try {
      const { data } = await api.post<RepositoryTask>(`/task/${taskId}/cancel`)
      setTasks((current) => current.map((t) => (t.id === data.id ? { ...data, response: t.response } : t)))
      return true
    } catch (e) {
      const message = (e as { response?: { data?: unknown } }).response?.data
      toast.error(typeof message === 'string' ? message : 'Failed to cancel task')
      return false
    }
  }

  return { tasks, createTask, updateTask, startTask, followUp, mergeTask, deleteTask, cancelTask }
}
