import { useState } from 'react'
import './CreateTaskDialog.css'

interface CreateTaskDialogProps {
  onCreate: (text: string) => void
  onClose: () => void
}

function CreateTaskDialog({ onCreate, onClose }: CreateTaskDialogProps) {
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
        <textarea
          className="dialog-textarea"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Describe the task..."
          rows={8}
          autoFocus
        />
        <div className="dialog-actions">
          <button className="dialog-button" onClick={onClose}>
            Cancel
          </button>
          <button
            className="dialog-button primary"
            onClick={() => onCreate(text.trim())}
            disabled={!text.trim()}
          >
            Create
          </button>
        </div>
      </div>
    </div>
  )
}

export default CreateTaskDialog
