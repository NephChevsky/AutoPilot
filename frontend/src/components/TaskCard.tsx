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
  Completed: 'Completed',
  Error: 'Error',
}

// Collapses neighboring reasoning items into one block when reasoning is folded
function mergeReasoning(items: ConversationItem[], status: TaskStatus): ConversationItem[] {
  if (status !== 'Completed') return items
  return items.reduce<ConversationItem[]>((merged, item) => {
    const last = merged[merged.length - 1]
    if (last && last.type === 'Reasoning' && item.type === 'Reasoning' && last.author === item.author) {
      merged[merged.length - 1] = { ...last, content: `${last.content}\n\n${item.content}` }
    } else {
      merged.push(item)
    }
    return merged
  }, [])
}

function ConversationEntry({ item, status, isLast, isLastReasoning }: { item: ConversationItem; status: TaskStatus; isLast: boolean; isLastReasoning: boolean }) {
  if (item.author !== 'Autopilot') {
    return <div className="task-text">{item.content}</div>
  }
  if (status === 'Error' && isLast) {
    return <pre className="task-error">{item.content}</pre>
  }
  if (item.type === 'Reasoning' && (status === 'Completed' || !isLastReasoning)) {
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
  onFollowUp: (taskId: string, message: string) => Promise<boolean>
}

function TaskCard({ task, repository, onStart, onUpdate, onMerge, onDelete, onFollowUp }: TaskCardProps) {
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [saving, setSaving] = useState(false)
  const conversation = mergeReasoning(task.conversation.slice(1), task.status)
  const lastReasoningIndex = conversation.findLastIndex((item) => item.author === 'Autopilot' && item.type === 'Reasoning')
  const isEditing = editing && task.status === 'NotStarted'

  const startEditing = () => {
    setTitle(task.title)
    setText(task.task)
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
        <div>
          {task.title && <div className="task-title">{task.title}</div>}
          <div className="task-text">{task.task}</div>
        </div>
        <div className="task-actions">
          <span className={`status-label status-${task.status}`}>{statusLabels[task.status]}</span>
          {task.status === 'NotStarted' && (
            <button className="btn-secondary" onClick={startEditing}>
              Edit
            </button>
          )}
          {(task.status === 'NotStarted' || task.status === 'Error') && (
            <button className="btn-primary" onClick={() => onStart(task.id)}>
              {task.status === 'Error' ? 'Retry' : 'Start'}
            </button>
          )}
          {task.status === 'Completed' && task.branchName && (
            <ConfirmActionButton label="Merge" className="btn-primary" onConfirm={() => onMerge(task.id)} />
          )}
          {(task.status === 'NotStarted' || task.status === 'Completed' || task.status === 'Error') && (
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
          isLastReasoning={index === lastReasoningIndex}
        />
      ))}
      {task.status === 'Completed' && <FollowUpForm onSubmit={(message) => onFollowUp(task.id, message)} />}
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
