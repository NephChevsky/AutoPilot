import { create } from 'zustand'
import toast from 'react-hot-toast'
import api from '../api/axios'

export interface ChatSummary {
  id: string
  title: string
  creationDateTime: string
  updateDateTime: string
}

export interface ChatMessage {
  id: string
  role: 'User' | 'Assistant'
  content: string
  creationDateTime: string
}

export interface Chat extends ChatSummary {
  messages: ChatMessage[]
}

const toSummary = ({ id, title, creationDateTime, updateDateTime }: Chat): ChatSummary => ({
  id,
  title,
  creationDateTime,
  updateDateTime,
})

// Newest first, like the API
const upsert = (list: ChatSummary[], chat: ChatSummary) =>
  [chat, ...list.filter((c) => c.id !== chat.id)].sort(
    (a, b) => new Date(b.updateDateTime).getTime() - new Date(a.updateDateTime).getTime(),
  )

interface ChatState {
  chats: ChatSummary[]
  loading: boolean
  error: boolean
  loadChats: () => Promise<void>
  getChat: (id: string) => Promise<Chat | null>
  sendMessage: (chatId: string | undefined, message: string) => Promise<Chat | null>
  deleteChat: (id: string) => Promise<boolean>
}

export const useChats = create<ChatState>((set) => ({
  chats: [],
  loading: true,
  error: false,

  loadChats: async () => {
    try {
      const { data } = await api.get<ChatSummary[]>('/chat')
      set({ chats: data, error: false })
    } catch {
      set({ error: true })
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

  sendMessage: async (chatId, message) => {
    try {
      const { data } = chatId
        ? await api.post<Chat>(`/chat/${chatId}/messages`, { message })
        : await api.post<Chat>('/chat', { message })
      set((state) => ({ chats: upsert(state.chats, toSummary(data)) }))
      return data
    } catch {
      toast.error('Failed to get an answer')
      return null
    }
  },

  deleteChat: async (id) => {
    try {
      await api.delete(`/chat/${id}`)
      set((state) => ({ chats: state.chats.filter((c) => c.id !== id) }))
      return true
    } catch {
      toast.error('Failed to delete chat')
      return false
    }
  },
}))
