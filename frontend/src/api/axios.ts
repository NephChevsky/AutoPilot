import axios, { type InternalAxiosRequestConfig } from 'axios'
import { getAccessToken, setAccessToken } from '../auth/tokenStore'

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL,
  withCredentials: true,
})

api.interceptors.request.use((config) => {
  const token = getAccessToken()
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

let isRefreshing = false
let refreshQueue: { resolve: (token: string) => void; reject: (error: unknown) => void }[] = []

function processQueue(error: unknown, token: string | null) {
  refreshQueue.forEach((p) => {
    if (error || !token) p.reject(error)
    else p.resolve(token)
  })
  refreshQueue = []
}

api.interceptors.response.use(
  (res) => res,
  async (error) => {
    const originalRequest = error.config as RetriableConfig | undefined

    if (
      originalRequest &&
      error.response?.status === 401 &&
      !originalRequest._retry &&
      !originalRequest.url?.includes('/auth/refresh') &&
      !originalRequest.url?.includes('/auth/login')
    ) {
      originalRequest._retry = true

      if (isRefreshing) {
        return new Promise<string>((resolve, reject) => {
          refreshQueue.push({ resolve, reject })
        }).then((token) => {
          originalRequest.headers.Authorization = `Bearer ${token}`
          return api(originalRequest)
        })
      }

      isRefreshing = true

      try {
        const { data } = await api.post<{ token: string }>('/auth/refresh')
        setAccessToken(data.token)
        processQueue(null, data.token)

        originalRequest.headers.Authorization = `Bearer ${data.token}`
        return api(originalRequest)
      } catch (err) {
        processQueue(err, null)
        setAccessToken(null)
        return Promise.reject(err)
      } finally {
        isRefreshing = false
      }
    }

    return Promise.reject(error)
  },
)

export default api
