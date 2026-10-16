import { useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useChats } from '../hooks/useChats'
import type { ChatMessage } from '../hooks/useChats'
import './ChatPage.css'

function ChatConversation({ id }: { id: string | undefined }) {
  const navigate = useNavigate()
  const getChat = useChats((state) => state.getChat)
  const sendMessage = useChats((state) => state.sendMessage)
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [loading, setLoading] = useState(id !== undefined)
  const [pendingMessage, setPendingMessage] = useState<string | null>(null)
  const [text, setText] = useState('')
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!id) return
    let cancelled = false

    getChat(id).then((chat) => {
      if (cancelled) return
      if (chat) setMessages(chat.messages)
      setLoading(false)
    })

    return () => {
      cancelled = true
    }
  }, [id, getChat])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, pendingMessage])

  const sending = pendingMessage !== null

  const send = async () => {
    const message = text.trim()
    if (!message || sending) return
    setPendingMessage(message)
    setText('')
    const chat = await sendMessage(id, message)
    if (!chat) {
      // Nothing was stored: give the text back so it can be resent
      setText(message)
      setPendingMessage(null)
      return
    }
    if (id) {
      setMessages(chat.messages)
      setPendingMessage(null)
    } else {
      navigate(`/chats/${chat.id}`, { replace: true })
    }
  }

  return (
    <div className="chat-page">
      <div className="chat-messages">
        {loading && <div className="sidebar-message">Loading...</div>}
        {!loading && messages.length === 0 && !sending && (
          <div className="chat-empty">Ask anything to start a conversation.</div>
        )}
        {messages.map((m) => (
          <div key={m.id} className={`chat-message ${m.role === 'User' ? 'user' : 'assistant'}`}>
            {m.content}
          </div>
        ))}
        {pendingMessage !== null && (
          <>
            <div className="chat-message user">{pendingMessage}</div>
            <div className="chat-message assistant chat-thinking">Thinking...</div>
          </>
        )}
        <div ref={bottomRef} />
      </div>
      <div className="chat-input-bar">
        <textarea
          className="follow-up-input"
          rows={3}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Ask a question... (Ctrl+Enter to send)"
          disabled={loading}
          autoFocus
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) send()
          }}
        />
        <button className="btn-primary" disabled={!text.trim() || sending || loading} onClick={send}>
          Send
        </button>
      </div>
    </div>
  )
}

function ChatPage() {
  const { id } = useParams()

  // Remount when switching chat so no state leaks from one conversation to the next
  return <ChatConversation key={id ?? 'new'} id={id} />
}

export default ChatPage
