import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from '@tanstack/react-router'
import { AppProviders } from './app/providers'
import { router } from './app/router'
import './design/tokens.css'
import './design/base.css'

const container = document.getElementById('root')

if (!container) {
  throw new Error('The document has no #root to mount into.')
}

createRoot(container).render(
  <StrictMode>
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>
  </StrictMode>,
)
