import { useState } from 'react'
import './CreateTaskDialog.css'

interface CreateTaskDialogProps {
  onCreate: (title: string, text: string) => void
  onClose: () => void
}

function CreateTaskDialog({ onCreate, onClose }: CreateTaskDialogProps) {
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Create Task"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === 'Escape' && onClose()}
      >
        <h2 className="dialog-title">Create Task</h2>
        <input
          className="dialog-input"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          placeholder="Title"
          autoFocus
        />
        <textarea
          className="dialog-textarea"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Describe the task..."
          rows={8}
        />
        <div className="dialog-actions">
          <button className="dialog-button" onClick={onClose}>
            Cancel
          </button>
          <button
            className="dialog-button primary"
            onClick={() => onCreate(title.trim(), text.trim())}
                        disabled={!title.trim() || !text.trim()}
          >
            Create
          </button>
        </div>
      </div>
    </div>
  )
}

export default CreateTaskDialog
