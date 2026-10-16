import { useEffect } from 'react'
import { useAuthStore } from './store/authStore'
import { Toaster } from 'react-hot-toast'
import App from './App'

function AppWrapper() {
  const initializeAuth = useAuthStore((state) => state.initializeAuth)

  useEffect(() => {
    void initializeAuth()
  }, [initializeAuth])

  return (
    <>
      <App />
      <Toaster position="bottom-center" />
    </>
  )
}

export default AppWrapper
