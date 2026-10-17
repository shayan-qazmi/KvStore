import { useState, useEffect } from 'react'

// Every request goes to /api/... and the Vite server forwards it to the C# API
// (see vite.config.js).
const API = '/api'

// Turns a status number into a friendly sentence.
// Status 0 means the request never reached the API at all.
function explain(status) {
  if (status === 0) return 'Cannot reach the API. Is it running on port 5080?'
  return `Unexpected answer (status ${status}).`
}

export default function App() {
  // ----- State: values React remembers. When one changes, the page is redrawn. -----
  const [key, setKey] = useState('')           // text in the "Key" box
  const [value, setValue] = useState('')       // text in the "Value" box
  const [start, setStart] = useState('a')      // range start
  const [end, setEnd] = useState('z')          // range end
  const [online, setOnline] = useState(null)   // null = checking, true = reachable, false = not
  const [result, setResult] = useState(null)   // message under the "One key" buttons
  const [rangeMessage, setRangeMessage] = useState('') // message under the range buttons
  const [rows, setRows] = useState([])         // rows of the range table
  const [log, setLog] = useState([])           // history of requests, newest first

  // Sends ONE HTTP request, adds it to the log, and returns the status and body text.
  async function call(method, path, body) {
    const startedAt = performance.now()
    let status = 0
    let text = ''

    try {
      // fetch = the browser's built-in way to send an HTTP request (like curl).
      const response = await fetch(API + path, {
        method,
        headers: body === undefined ? undefined : { 'Content-Type': 'text/plain; charset=utf-8' },
        body,
      })
      status = response.status
      text = await response.text()
    } catch (error) {
      // fetch only throws when the network itself fails (the API is not running).
      status = 0
      text = String(error)
    }

    const ms = Math.round(performance.now() - startedAt)
    const entry = { id: startedAt + Math.random(), method, path, status, ms }

    // Put the new entry first and keep only the latest 30.
    setLog((previous) => [entry, ...previous].slice(0, 30))

    return { status, text }
  }

  // Asks the API's home page (GET /) whether it is alive.
  async function checkConnection() {
    const { status } = await call('GET', '/')
    setOnline(status === 200)
  }

  // Runs once, when the page first appears (the empty [] means "only once").
  useEffect(() => {
    checkConnection()
  }, [])

  // PUT /kv/{key}: save the key with the value from the box.
  async function save() {
    // encodeURIComponent turns special characters into %XX so they are safe in a URL.
    const { status } = await call('PUT', `/kv/${encodeURIComponent(key)}`, value)
    if (status === 204) setResult({ ok: true, text: `Saved "${key}".` })
    else setResult({ ok: false, text: explain(status) })
  }

  // GET /kv/{key}: read the value of the key.
  async function get() {
    const { status, text } = await call('GET', `/kv/${encodeURIComponent(key)}`)
    if (status === 200) setResult({ ok: true, text: `Value of "${key}":`, value: text })
    else if (status === 404) setResult({ ok: false, text: `"${key}" does not exist (404).` })
    else setResult({ ok: false, text: explain(status) })
  }

  // DELETE /kv/{key}: remove the key.
  async function remove() {
    const { status } = await call('DELETE', `/kv/${encodeURIComponent(key)}`)
    if (status === 204) setResult({ ok: true, text: `Deleted "${key}".` })
    else if (status === 404) setResult({ ok: false, text: `"${key}" does not exist (404).` })
    else setResult({ ok: false, text: explain(status) })
  }

  // GET /kv?start=..&end=..: all keys between start and end, sorted.
  async function loadRange(from, to) {
    const query = `?start=${encodeURIComponent(from)}&end=${encodeURIComponent(to)}`
    const { status, text } = await call('GET', `/kv${query}`)

    if (status === 200) {
      setRows(JSON.parse(text)) // the API answers with JSON: [{ "key": "a", "value": "1" }, ...]
      setRangeMessage('')
    } else {
      setRows([])
      setRangeMessage(status === 400 ? 'Both start and end are required (400).' : explain(status))
    }
  }

  // ----- What the page looks like (JSX: HTML-like code inside JavaScript) -----
  return (
    <div className="app">
      <header>
        <h1>KvStore UI</h1>
        <span className={'badge ' + (online === null ? 'wait' : online ? 'up' : 'down')}>
          {online === null ? 'checking...' : online ? 'API online' : 'API offline'}
        </span>
        <button className="secondary" onClick={checkConnection}>Check again</button>
      </header>
      <p className="muted">
        Every button sends a real HTTP request to the C# API. Watch the request log at the bottom.
      </p>

      <section className="card">
        <h2>One key</h2>
        <label>
          Key
          <input value={key} onChange={(e) => setKey(e.target.value)} placeholder="name" />
        </label>
        <label>
          Value
          <textarea
            rows={3}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            placeholder="shayan"
          />
        </label>
        <div className="buttons">
          <button onClick={save} disabled={!key}>Save (PUT)</button>
          <button onClick={get} disabled={!key}>Get (GET)</button>
          <button className="danger" onClick={remove} disabled={!key}>Delete (DELETE)</button>
        </div>

        {result && (
          <div className={'result ' + (result.ok ? 'ok' : 'error')}>
            <div>{result.text}</div>
            {result.value !== undefined && <pre>{result.value}</pre>}
          </div>
        )}
      </section>

      <section className="card">
        <h2>Range</h2>
        <div className="row">
          <label>
            Start key
            <input value={start} onChange={(e) => setStart(e.target.value)} />
          </label>
          <label>
            End key
            <input value={end} onChange={(e) => setEnd(e.target.value)} />
          </label>
        </div>
        <div className="buttons">
          <button onClick={() => loadRange(start, end)}>Search range</button>
          {/* An empty start and a very large end key match every key. */}
          <button className="secondary" onClick={() => loadRange('', '\uffff')}>
            Show all keys
          </button>
        </div>

        {rangeMessage && <div className="result error">{rangeMessage}</div>}

        {rows.length > 0 && (
          <>
            <table>
              <thead>
                <tr><th>Key</th><th>Value</th></tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  // Clicking a row copies it into the form above.
                  <tr key={row.key} onClick={() => { setKey(row.key); setValue(row.value) }}>
                    <td>{row.key}</td>
                    <td>{row.value}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="muted">{rows.length} key(s). Click a row to copy it into the form above.</p>
          </>
        )}
      </section>

      <section className="card">
        <h2>Request log</h2>
        {log.length === 0 && <p className="muted">Nothing sent yet.</p>}
        <ul className="log">
          {log.map((entry) => (
            <li key={entry.id} className={entry.status >= 200 && entry.status < 300 ? 'good' : 'bad'}>
              <span className="method">{entry.method}</span>
              <span className="path">{entry.path}</span>
              <span className="status">{entry.status === 0 ? 'failed' : entry.status}</span>
              <span className="time">{entry.ms} ms</span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}