import { useEffect, useState } from 'react'
import toast from 'react-hot-toast'
import api from '../api/axios'

export interface GithubRepository {
  id: string
  githubId: number
  name: string
  cloneUrl: string
  htmlUrl: string
  defaultBranch: string
  pinned: boolean
}

export function useRepositories() {
  const [repositories, setRepositories] = useState<GithubRepository[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [refreshing, setRefreshing] = useState(false)

  const setPinned = async (id: string, pinned: boolean) => {
    try {
      await api.put(`/github/repositories/${id}/pinned`, { pinned })
      setRepositories((current) =>
        current
          .map((r) => (r.id === id ? { ...r, pinned } : r))
          .sort((a, b) => Number(b.pinned) - Number(a.pinned) || a.name.localeCompare(b.name)),
      )
    } catch {
      toast.error('Failed to update pin')
    }
  }

  const refresh = async () => {
    setRefreshing(true)
    try {
      const { data } = await api.post<GithubRepository[]>('/github/repositories/refresh')
      setRepositories(data)
      setError(false)
    } catch {
      toast.error('Failed to refresh repositories')
    } finally {
      setRefreshing(false)
    }
  }

  useEffect(() => {
    let cancelled = false

    api
      .get<GithubRepository[]>('/github/repositories')
      .then(({ data }) => {
        if (!cancelled) setRepositories(data)
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
  }, [])

  return { repositories, loading, error, refreshing, refresh, setPinned }
}
