import { useEffect, useState } from 'react'
import api from '../api/axios'

export type LedKind = 'white' | 'yellow' | 'green' | 'red'

interface StatusSummary {
  repositoryId: string
  statuses: (string | number)[]
}

const POLL_INTERVAL_MS = 4000

// Serialized enum may be a name or a number depending on JSON config.
const STATUS_TO_LED: Record<string, LedKind> = {
  NotStarted: 'white',
  '0': 'white',
  PendingStart: 'yellow',
  '1': 'yellow',
  Processing: 'yellow',
  '2': 'yellow',
  WaitingForReview: 'green',
  '3': 'green',
  Error: 'red',
  '5': 'red',
}

export function useRepositoryStatuses() {
  const [leds, setLeds] = useState<Record<string, LedKind[]>>({})

  useEffect(() => {
    let cancelled = false

    const load = () => {
      api
        .get<StatusSummary[]>('/task/status-summary')
        .then(({ data }) => {
          if (cancelled) return
          const next: Record<string, LedKind[]> = {}
          for (const item of data) {
            const kinds = new Set<LedKind>()
            for (const status of item.statuses) {
              const led = STATUS_TO_LED[String(status)]
              if (led) kinds.add(led)
            }
            next[item.repositoryId] = (['white', 'yellow', 'green', 'red'] as LedKind[]).filter((k) => kinds.has(k))
          }
          setLeds(next)
        })
        .catch(() => {
          // Silent: keep the last known state.
        })
    }

    load()
    const timer = setInterval(load, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  return leds
}
