import { useEffect } from 'react'
import toast from 'react-hot-toast'
import { useRegisterSW } from 'virtual:pwa-register/react'

// Reloads the app automatically once a new build has been precached.
export function usePwaUpdate() {
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW({
    onRegisteredSW(_swUrl, registration) {
      if (!registration) return
      setInterval(() => {
        void registration.update()
      }, 60 * 60 * 1000)
    },
  })

  useEffect(() => {
    if (!needRefresh) return
    toast('New version found, updating...', { duration: 2000 })
    void updateServiceWorker(true)
  }, [needRefresh, updateServiceWorker])
}
