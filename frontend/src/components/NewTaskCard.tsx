import { useState } from 'react'

interface NewTaskCardProps {
  onCreate: (title: string, text: string) => Promise<boolean>
  onCancel: () => void
}

function NewTaskCard({ onCreate, onCancel }: NewTaskCardProps) {
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [saving, setSaving] = useState(false)

  const save = async () => {
    if (!title.trim() || !text.trim() || saving) return
    setSaving(true)
    const ok = await onCreate(title.trim(), text.trim())
    // On success the parent unmounts this card
    if (!ok) setSaving(false)
  }

  return (
    <li className="task-card">
      <div className="task-edit">
        <input
          className="follow-up-input"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          placeholder="Title"
          autoFocus
        />
        <textarea
          className="follow-up-input"
          rows={4}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Describe the task..."
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) save()
          }}
        />
        <div className="task-edit-actions">
          <button className="btn-secondary" onClick={onCancel}>
            Cancel
          </button>
          <button className="btn-primary" disabled={!title.trim() || !text.trim() || saving} onClick={save}>
            Create
          </button>
        </div>
      </div>
    </li>
  )
}

export default NewTaskCard
