import { useEffect, useState } from 'react'

const CONFIRM_ARM_DELAY_MS = 600
const CONFIRM_TIMEOUT_MS = 4000

interface DeleteButtonProps {
  onConfirm: () => void
  label?: string
  className?: string
}

function DeleteButton({ onConfirm, label = 'Delete', className = 'btn-secondary danger' }: DeleteButtonProps) {
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
      className={`${className} delete-btn${state === 'idle' ? '' : ' confirming'}${state === 'arming' ? ' arming' : ''}`}
      onClick={() => (state === 'idle' ? setState('arming') : state === 'armed' && onConfirm())}
      onBlur={() => setState('idle')}
    >
      <span className="delete-btn-label" key={state === 'idle' ? 'delete' : 'confirm'}>
        {state === 'idle' ? label : 'Confirm?'}
      </span>
    </button>
  )
}

export default DeleteButton
