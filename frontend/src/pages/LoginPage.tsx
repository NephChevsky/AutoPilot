import { useEffect, useState, type SubmitEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import toast, { Toaster } from 'react-hot-toast'
import { isAxiosError } from 'axios'
import api from '../api/axios'
import { useAuthStore } from '../store/authStore'
import './LoginPage.css'

function LoginPage() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [loginLoading, setLoginLoading] = useState(false)

  const navigate = useNavigate()
  const user = useAuthStore((state) => state.user)
  const initializeAuth = useAuthStore((state) => state.initializeAuth)

  useEffect(() => {
    if (user) navigate('/')
  }, [user, navigate])

  const handleSubmit = async (e: SubmitEvent<HTMLFormElement>) => {
    e.preventDefault()

    if (!username || !password) {
      toast.error('Please enter both username and password.')
      return
    }

    setLoginLoading(true)

    try {
      await api.post('/auth/login', { username, password })
      await initializeAuth()
      navigate('/')
    } catch (err) {
      if (isAxiosError(err) && err.response?.status === 401) {
        toast.error('Invalid username or password.')
      } else {
        toast.error('Temporary error. Please try again.')
      }
    } finally {
      setLoginLoading(false)
    }
  }

  return (
    <div className="login-container">
      <form onSubmit={handleSubmit} className="login-form">
        <h2>Login</h2>
        <div className="login-input-group">
          <label htmlFor="username">Username</label>
          <input
            id="username"
            type="text"
            autoComplete="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            className="login-input"
            disabled={loginLoading}
          />
        </div>
        <div className="login-input-group">
          <label htmlFor="password">Password</label>
          <input
            id="password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="login-input"
            disabled={loginLoading}
          />
        </div>
        <button type="submit" className="login-button" disabled={loginLoading}>
          {loginLoading ? 'Signing in...' : 'Sign in'}
        </button>
      </form>
      <Toaster position="bottom-center" />
    </div>
  )
}

export default LoginPage
