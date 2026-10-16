import { useState } from 'react'
import Markdown from 'react-markdown'
import type { GithubRepository } from '../hooks/useRepositories'
import type { ConversationItem, RepositoryTask, TaskStatus } from '../hooks/useTasks'
import ConfirmActionButton from './ConfirmActionButton'
import FollowUpForm from './FollowUpForm'

const encodeBranch = (name: string) => name.split('/').map(encodeURIComponent).join('/')

const statusLabels: Record<TaskStatus, string> = {
  NotStarted: 'Not started',
  PendingStart: 'Pending start',
  Processing: 'Processing',
  WaitingForMerge: 'Waiting for merge',
  Completed: 'Completed',
  Error: 'Error',
  Cancelled: 'Cancelled',
}

interface DisplayItem extends ConversationItem {
  live?: boolean
}

// Merges consecutive reasoning items into one folded block; the trailing run of
// reasoning items stays expanded and unmerged while the task is not completed
function mergeReasoning(items: ConversationItem[], status: TaskStatus): DisplayItem[] {
  const isReasoning = (item: ConversationItem) => item.author === 'Autopilot' && item.type === 'Reasoning'
  let liveStart = items.length
  if (status !== 'Completed' && status !== 'WaitingForMerge') {
    while (liveStart > 0 && isReasoning(items[liveStart - 1])) liveStart--
  }
  return items.reduce<DisplayItem[]>((merged, item, index) => {
    if (index >= liveStart) {
      merged.push({ ...item, live: true })
      return merged
    }
    const last = merged[merged.length - 1]
    if (last && !last.live && isReasoning(last) && isReasoning(item)) {
      merged[merged.length - 1] = { ...last, content: `${last.content}\n\n${item.content}` }
    } else {
      merged.push(item)
    }
    return merged
  }, [])
}

function ConversationEntry({ item, status, isLast }: { item: DisplayItem; status: TaskStatus; isLast: boolean }) {
  if (item.author !== 'Autopilot') {
    return <div className="task-text">{item.content}</div>
  }
  if (status === 'Error' && isLast) {
    return <pre className="task-error">{item.content}</pre>
  }
  if (item.type === 'Reasoning' && !item.live) {
    return (
      <details className="reasoning-fold">
        <summary>Show reasoning</summary>
        <div className="markdown">
          <Markdown>{item.content}</Markdown>
        </div>
      </details>
    )
  }
  return (
    <div className="markdown">
      <Markdown>{item.content}</Markdown>
    </div>
  )
}

interface TaskCardProps {
  task: RepositoryTask
  repository: GithubRepository
  onStart: (taskId: string) => void
  onUpdate: (taskId: string, title: string, text: string) => Promise<boolean>
  onMerge: (taskId: string) => void
  onDelete: (taskId: string) => void
  onCancel: (taskId: string) => Promise<boolean>
  onFollowUp: (taskId: string, message: string) => Promise<boolean>
}

function TaskCard({ task, repository, onStart, onUpdate, onMerge, onDelete, onCancel, onFollowUp }: TaskCardProps) {
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [saving, setSaving] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  // Reset once the task leaves Processing (cancelled, finished or retried)
  if (cancelling && task.status !== 'Processing') setCancelling(false)
  const conversation = mergeReasoning(task.conversation, task.status)
  const isEditing = editing && task.status === 'NotStarted'

  const startEditing = () => {
    setTitle(task.title)
    setText(task.conversation[0]?.content ?? '')
    setEditing(true)
  }

  const save = async () => {
    if (!title.trim() || !text.trim() || saving) return
    setSaving(true)
    const ok = await onUpdate(task.id, title.trim(), text.trim())
    setSaving(false)
    if (ok) setEditing(false)
  }

  return (
    <li className="task-card">
      {isEditing ? (
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
            <button className="btn-secondary" onClick={() => setEditing(false)}>
              Cancel
            </button>
            <button className="btn-primary" disabled={!title.trim() || !text.trim() || saving} onClick={save}>
              Save
            </button>
          </div>
        </div>
      ) : (
      <div className="task-head">
        <div className="task-title">{task.title}</div>
        <div className="task-actions">
          <span className={`status-label status-${task.status}`}>{statusLabels[task.status]}</span>
          {task.status === 'NotStarted' && (
            <button className="btn-secondary" onClick={startEditing}>
              Edit
            </button>
          )}
          {(task.status === 'NotStarted' || task.status === 'Error' || task.status === 'Cancelled') && (
            <button className="btn-primary" onClick={() => onStart(task.id)}>
              {task.status === 'NotStarted' ? 'Start' : 'Retry'}
            </button>
          )}
          {task.status === 'Processing' && cancelling && (
            <button className="btn-secondary" disabled>
              Cancelling…
            </button>
          )}
          {(task.status === 'PendingStart' || (task.status === 'Processing' && !cancelling)) && (
            <ConfirmActionButton
              label="Cancel"
              onConfirm={async () => {
                const wasProcessing = task.status === 'Processing'
                const ok = await onCancel(task.id)
                if (ok && wasProcessing) setCancelling(true)
              }}
            />
          )}
          {task.status === 'WaitingForMerge' && task.branchName && (
            <ConfirmActionButton label="Merge" className="btn-primary" onConfirm={() => onMerge(task.id)} />
          )}
          {(task.status === 'NotStarted' || task.status === 'WaitingForMerge' || task.status === 'Completed' || task.status === 'Error' || task.status === 'Cancelled') && (
            <ConfirmActionButton onConfirm={() => onDelete(task.id)} />
          )}
        </div>
      </div>
      )}
      {conversation.map((item, index) => (
        <ConversationEntry
          key={index}
          item={item}
          status={task.status}
          isLast={index === conversation.length - 1}
        />
      ))}
      {(task.status === 'WaitingForMerge' || task.status === 'Completed') && <FollowUpForm onSubmit={(message) => onFollowUp(task.id, message)} />}
      <div className="task-meta">
        <span>{new Date(task.creationDateTime).toLocaleString()}</span>
        {task.branchName && (
          <a
            href={`${repository.htmlUrl}/compare/${encodeBranch(repository.defaultBranch || 'master')}...${encodeBranch(task.branchName)}`}
            target="_blank"
            rel="noopener noreferrer"
          >
            Branch: {task.branchName}
          </a>
        )}
        {task.cost != null && <span className="task-cost">Cost: {task.cost.toFixed(2)} AIU</span>}
      </div>
    </li>
  )
}

export default TaskCard
