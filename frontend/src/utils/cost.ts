import type { RepositoryTask } from '../hooks/useTasks'

export function computeCosts(tasks: RepositoryTask[], now: Date) {
  let total = 0
  let monthly = 0
  for (const t of tasks) {
    const cost = t.cost ?? 0
    total += cost
    const d = new Date(t.completionDateTime ?? t.creationDateTime)
    if (d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()) monthly += cost
  }
  return { total, monthly }
}

export function formatCosts({ total, monthly }: { total: number; monthly: number }) {
  return `Total: ${total.toFixed(2)} AIU · This month: ${monthly.toFixed(2)} AIU`
}
