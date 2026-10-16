import { Outlet } from 'react-router-dom'
import Sidebar from './Sidebar'

function Layout() {
  return (
    <>
      <Sidebar />
      <main style={{ marginLeft: 260 }}>
        <Outlet />
      </main>
    </>
  )
}

export default Layout
