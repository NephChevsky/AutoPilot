import { create } from 'zustand'
import { jwtDecode } from 'jwt-decode'
import toast from 'react-hot-toast'
import api, { setAuthExpiredHandler } from '../api/axios'
import { setAccessToken } from '../auth/tokenStore'

export interface AuthUser {
  role: string
  expireAt: number
}

interface AuthState {
  user: AuthUser | null
  loading: boolean
  initializeAuth: () => Promise<void>
  logout: () => Promise<void>
}

interface TokenClaims {
  role: string
  exp: number
}

let initializeAuthPromise: Promise<void> | null = null

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  loading: true,

  initializeAuth: () => {
    // Guard against concurrent callers (e.g. StrictMode double effects)
    // firing parallel /auth/refresh requests.
    if (initializeAuthPromise) return initializeAuthPromise

    initializeAuthPromise = (async () => {
      try {
        const { data } = await api.post<{ token: string }>('/auth/refresh')
        setAccessToken(data.token)

        const decoded = jwtDecode<TokenClaims>(data.token)
        set({ user: { role: decoded.role, expireAt: decoded.exp }, loading: false })
      } catch {
        setAccessToken(null)
        set({ user: null, loading: false })
      } finally {
        initializeAuthPromise = null
      }
    })()

    return initializeAuthPromise
  },

  logout: async () => {
    try {
      await api.post('/auth/logout')
    } finally {
      setAccessToken(null)
      set({ user: null })
    }
  },
}))

setAuthExpiredHandler(() => {
  if (!useAuthStore.getState().user) return
  useAuthStore.setState({ user: null })
  toast.error('Session expired, please log in again')
})
