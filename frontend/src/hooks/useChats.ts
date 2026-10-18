import { create } from 'zustand'
import toast from 'react-hot-toast'
import api from '../api/axios'

export interface ChatSummary {
  id: string
  title: string
  creationDateTime: string
  updateDateTime: string
  cost: number
}

export interface ChatCosts {
  total: number
  month: number
}

export interface ChatMessage {
  id: string
  role: 'User' | 'Assistant'
  content: string
  cost: number | null
  creationDateTime: string
}

export interface Chat extends ChatSummary {
  messages: ChatMessage[]
}

const toSummary = ({ id, title, creationDateTime, updateDateTime, cost }: Chat): ChatSummary => ({
  id,
  title,
  creationDateTime,
  updateDateTime,
  cost,
})

// Newest first, like the API
const upsert = (list: ChatSummary[], chat: ChatSummary) =>
  [chat, ...list.filter((c) => c.id !== chat.id)].sort(
    (a, b) => new Date(b.updateDateTime).getTime() - new Date(a.updateDateTime).getTime(),
  )

const TITLE_POLL_INTERVAL_MS = 2000
const TITLE_POLL_MAX_ATTEMPTS = 10

// The API generates the title of a new chat after replying; poll until it replaces the placeholder
const titlePolls = new Map<string, ReturnType<typeof setTimeout>>()

const stopTitlePolls = () => {
  titlePolls.forEach((timer) => clearTimeout(timer))
  titlePolls.clear()
}

interface ChatState {
  chats: ChatSummary[]
  loading: boolean
  error: boolean
  loadChats: () => Promise<void>
  getChat: (id: string) => Promise<Chat | null>
  pollTitle: (id: string, placeholder: string) => void
  sendMessage: (chatId: string | undefined, message: string) => Promise<Chat | null>
  deleteChat: (id: string) => Promise<boolean>
  reset: () => void
}

export const useChats = create<ChatState>((set, get) => ({
  chats: [],
  loading: true,
  error: false,

  reset: () => {
    stopTitlePolls()
    set({ chats: [], loading: true, error: false })
  },

  loadChats: async () => {
    try {
      const { data } = await api.get<ChatSummary[]>('/chat')
      set({ chats: data, error: false })
    } catch {
      set({ chats: [], error: true })
    } finally {
      set({ loading: false })
    }
  },

  getChat: async (id) => {
    try {
      const { data } = await api.get<Chat>(`/chat/${id}`)
      return data
    } catch {
      toast.error('Failed to load chat')
      return null
    }
  },

  pollTitle: (id, placeholder) => {
    let attempts = 0
    const tick = async () => {
      titlePolls.delete(id)
      attempts++
      try {
        const { data } = await api.get<Chat>(`/chat/${id}`)
        if (data.title !== placeholder) {
          set((state) => ({ chats: state.chats.map((c) => (c.id === id ? { ...c, title: data.title } : c)) }))
          return
        }
      } catch {
        // Best effort: keep the placeholder title
      }
      if (attempts < TITLE_POLL_MAX_ATTEMPTS && get().chats.some((c) => c.id === id)) {
        titlePolls.set(id, setTimeout(tick, TITLE_POLL_INTERVAL_MS))
      }
    }
    titlePolls.set(id, setTimeout(tick, TITLE_POLL_INTERVAL_MS))
  },

  sendMessage: async (chatId, message) => {
    try {
      const { data } = chatId
        ? await api.post<Chat>(`/chat/${chatId}/messages`, { message })
        : await api.post<Chat>('/chat', { message })
      set((state) => ({ chats: upsert(state.chats, toSummary(data)) }))
      if (!chatId) {
        get().pollTitle(data.id, data.title)
      }
      return data
    } catch {
      toast.error('Failed to get an answer')
      return null
    }
  },

  deleteChat: async (id) => {
    try {
      await api.delete(`/chat/${id}`)
      clearTimeout(titlePolls.get(id))
      titlePolls.delete(id)
      set((state) => ({ chats: state.chats.filter((c) => c.id !== id) }))
      return true
    } catch {
      toast.error('Failed to delete chat')
      return false
    }
  },
}))
