import { useEffect, useState } from 'react'

const CONFIRM_ARM_DELAY_MS = 600
const CONFIRM_TIMEOUT_MS = 4000

interface ConfirmActionButtonProps {
  onConfirm: () => void
  label?: string
  className?: string
  variant?: 'danger' | 'success'
}

function ConfirmActionButton({
  onConfirm,
  label = 'Delete',
  className = 'btn-secondary danger',
  variant = 'danger',
}: ConfirmActionButtonProps) {
  const [state, setState] = useState<'idle' | 'arming' | 'armed'>('idle')

  useEffect(() => {
    if (state === 'arming') {
      const timer = setTimeout(() => setState('armed'), CONFIRM_ARM_DELAY_MS)
      return () => clearTimeout(timer)
    }
    if (state === 'armed') {
      const timer = setTimeout(() => setState('idle'), CONFIRM_TIMEOUT_MS)
      return () => clearTimeout(timer)
    }
  }, [state])

  return (
    <button
      className={`${className} confirm-btn ${variant}${state === 'idle' ? '' : ' confirming'}${state === 'arming' ? ' arming' : ''}`}
      onClick={() => (state === 'idle' ? setState('arming') : state === 'armed' && onConfirm())}
      onBlur={() => setState('idle')}
    >
      <span className="confirm-btn-label" key={state === 'idle' ? 'delete' : 'confirm'}>
        {state === 'idle' ? label : 'Confirm?'}
      </span>
    </button>
  )
}

export default ConfirmActionButton
