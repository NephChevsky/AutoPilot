import { useState } from 'react'

function FollowUpForm({ onSubmit }: { onSubmit: (message: string) => Promise<boolean> }) {
  const [message, setMessage] = useState('')
  const [sending, setSending] = useState(false)

  const submit = async () => {
    if (!message.trim() || sending) return
    setSending(true)
    const ok = await onSubmit(message.trim())
    setSending(false)
    if (ok) setMessage('')
  }

  return (
    <div className="follow-up">
      <textarea
        className="follow-up-input"
        rows={3}
        placeholder="Propose changes..."
        value={message}
        onChange={(e) => setMessage(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.nativeEvent.isComposing && (!e.shiftKey || e.ctrlKey || e.metaKey)) {
            e.preventDefault()
            submit()
          }
        }}
      />
      <button className="btn-primary" disabled={!message.trim() || sending} onClick={submit}>
        Send
      </button>
    </div>
  )
}

export default FollowUpForm
