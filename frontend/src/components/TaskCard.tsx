import Markdown from 'react-markdown'
import type { GithubRepository } from '../hooks/useRepositories'
import type { ConversationItem, RepositoryTask, TaskStatus } from '../hooks/useTasks'
import DeleteButton from './DeleteButton'
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

function ConversationEntry({ item, status, isLast }: { item: ConversationItem; status: TaskStatus; isLast: boolean }) {
  if (item.author !== 'Autopilot') {
    return <div className="task-text">{item.content}</div>
  }
  if (status === 'Error' && isLast) {
    return <pre className="task-error">{item.content}</pre>
  }
  if (item.type === 'Reasoning' && status === 'Completed') {
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
  onMerge: (taskId: string) => void
  onDelete: (taskId: string) => void
  onFollowUp: (taskId: string, message: string) => Promise<boolean>
}

function TaskCard({ task, repository, onStart, onMerge, onDelete, onFollowUp }: TaskCardProps) {
  const conversation = mergeReasoning(task.conversation.slice(1), task.status)

  return (
    <li className="task-card">
      <div className="task-head">
        <div>
          {task.title && <div className="task-title">{task.title}</div>}
          <div className="task-text">{task.task}</div>
        </div>
        <div className="task-actions">
          <span className={`status-label status-${task.status}`}>{statusLabels[task.status]}</span>
          {(task.status === 'NotStarted' || task.status === 'Error') && (
            <button className="btn-primary" onClick={() => onStart(task.id)}>
              {task.status === 'Error' ? 'Retry' : 'Start'}
            </button>
          )}
          {task.status === 'Completed' && task.branchName && (
            <DeleteButton label="Merge" className="btn-primary" onConfirm={() => onMerge(task.id)} />
          )}
          {(task.status === 'NotStarted' || task.status === 'Completed' || task.status === 'Error') && (
            <DeleteButton onConfirm={() => onDelete(task.id)} />
          )}
        </div>
      </div>
      {conversation.map((item, index) => (
        <ConversationEntry key={index} item={item} status={task.status} isLast={index === conversation.length - 1} />
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
      </div>
    </li>
  )
}

export default TaskCard
