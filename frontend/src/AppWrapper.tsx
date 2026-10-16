import { useEffect } from 'react'
import { useAuthStore } from './store/authStore'
import App from './App'

function AppWrapper() {
  const initializeAuth = useAuthStore((state) => state.initializeAuth)

  useEffect(() => {
    void initializeAuth()
  }, [initializeAuth])

  return <App />
}

export default AppWrapper
