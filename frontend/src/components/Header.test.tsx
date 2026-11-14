import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { Header } from './Header'

describe('shared data hub header', () => {
  it('shows backend connectivity without product-specific source counts', () => {
    const html = renderToStaticMarkup(<Header connection="connected" hasApiError={false} mockMode
      lastResponseMs={null} disconnectedSinceMs={null} />)
    expect(html).toContain('DATA HUB')
    expect(html).toContain('BACKEND CONNECTED')
    expect(html).not.toContain('PRICE SOURCES')
    expect(html).not.toContain('LIVE 3/3')
  })

  it('still reports backend disconnection', () => {
    const html = renderToStaticMarkup(<Header connection="disconnected" hasApiError={false} mockMode={null}
      lastResponseMs={null} disconnectedSinceMs={null} />)
    expect(html).toContain('BACKEND DISCONNECTED')
  })
})
