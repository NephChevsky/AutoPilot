import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuthStore } from '../store/authStore'

interface ProtectedRouteProps {
  children: ReactNode
  requiredRole?: string
}

export default function ProtectedRoute({ children, requiredRole }: ProtectedRouteProps) {
  const { user, loading } = useAuthStore()

  if (loading) return null

  if (!user) return <Navigate to="/login" replace />

  if (requiredRole && user.role !== requiredRole) return <Navigate to="/" replace />

  return children
}
