// Mediator for Chrome: the service worker.
//
// Connects to the Mediator desktop app on this PC (ws://127.0.0.1:<port>/chrome-bridge; the port and the pairing
// secret are in pairing.json, written into this folder by Mediator), proves both sides know the secret, then answers
// JSON-RPC requests: {id, method, params} -> {id, result} | {id, error: {message}}.
//
// Pages are read with serializePageForAssistant (a compact text view: [ref_N] role "name" @(x,y) for every control)
// instead of screenshots, so a model can act on a page for a few hundred tokens. Clicks and typing go through the
// Chrome DevTools Protocol (chrome.debugger) so sites receive real, trusted input events.
'use strict';

const VERSION = chrome.runtime.getManifest().version;
const BRIDGE_PATH = '/chrome-bridge';
const HEARTBEAT_MS = 20000;          // keeps the socket (and so this worker) alive; Chrome 116+
const MAX_BACKOFF_MS = 30000;
const DEBUGGER_IDLE_MS = 60000;      // detach (and hide Chrome's "is debugging" bar) after a minute without input
const LOAD_TIMEOUT_MS = 30000;
const CONSOLE_KEEP = 200;

let socket = null;
let status = { state: 'starting', detail: '' };
let backoff = 1000;
let reconnectTimer = null;
let heartbeatTimer = null;
let workTabId = null;                // the tab the last request used or opened

// ---------------------------------------------------------------------------------------------- connection

function setStatus(state, detail = '') {
  status = { state, detail, at: Date.now() };
  const badge = { connected: ['ON', '#1e9e4a'], error: ['!', '#c62828'] }[state] || ['', '#777777'];
  chrome.action.setBadgeText({ text: badge[0] }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ color: badge[1] }).catch(() => {});
  chrome.action.setTitle({ title: 'Mediator: ' + state + (detail ? ' - ' + detail : '') }).catch(() => {});
}

async function loadPairing() {
  let res;
  try { res = await fetch(chrome.runtime.getURL('pairing.json'), { cache: 'no-store' }); } catch { res = null; }
  if (!res || !res.ok) throw new Error('Not paired yet: in Mediator open Connectors > Chrome extension > Set up.');
  const p = await res.json();
  if (!p || typeof p.token !== 'string' || p.token.length < 32 || !Number.isInteger(p.port)) throw new Error('pairing.json is incomplete; press Set up in Mediator again.');
  return p;
}

function randomHex(bytes) {
  const a = crypto.getRandomValues(new Uint8Array(bytes));
  return Array.from(a, b => b.toString(16).padStart(2, '0')).join('');
}

async function hmacHex(secret, text) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey('raw', enc.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const sig = new Uint8Array(await crypto.subtle.sign('HMAC', key, enc.encode(text)));
  return Array.from(sig, b => b.toString(16).padStart(2, '0')).join('');
}

function sameText(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, backoff);
  backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
}

async function connect() {
  if (socket && (socket.readyState === WebSocket.CONNECTING || socket.readyState === WebSocket.OPEN)) return;
  clearTimeout(reconnectTimer);
  let pairing;
  try { pairing = await loadPairing(); } catch (e) { setStatus('unpaired', e.message); scheduleReconnect(); return; }

  const ws = new WebSocket(`ws://127.0.0.1:${pairing.port}${BRIDGE_PATH}`);
  socket = ws;
  setStatus('connecting');
  const clientNonce = randomHex(16);
  let serverNonce = null;
  let ready = false;

  ws.onopen = () => ws.send(JSON.stringify({ type: 'hello', nonce: clientNonce, version: VERSION }));
  ws.onmessage = async (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    if (!ready) {
      if (msg.type === 'challenge' && serverNonce === null) {
        serverNonce = String(msg.nonce || '');
        const expected = await hmacHex(pairing.token, `server|${clientNonce}|${serverNonce}`);
        if (!sameText(expected, msg.proof)) {
          setStatus('error', 'The app on this port is not your Mediator (pairing failed). Press Set up in Mediator.');
          ws.close(4001, 'pairing failed');
          return;
        }
        ws.send(JSON.stringify({ type: 'auth', proof: await hmacHex(pairing.token, `client|${serverNonce}|${clientNonce}`) }));
      } else if (msg.type === 'ready' && serverNonce !== null) {
        ready = true;
        backoff = 1000;
        setStatus('connected');
        startHeartbeat(ws);
      }
      return;
    }
    if (msg.type === 'pong' || msg.type === 'ping') { if (msg.type === 'ping') ws.send('{"type":"pong"}'); return; }
    if (msg.id !== undefined && typeof msg.method === 'string') handleRequest(ws, msg);
  };
  ws.onerror = () => {}; // onclose follows with the details
  ws.onclose = (ev) => {
    if (socket !== ws) return;
    socket = null;
    stopHeartbeat();
    if (status.state !== 'error' || ready) setStatus(ready ? 'disconnected' : 'waiting', ready ? 'Mediator closed the connection.' : 'Mediator is not running.');
    scheduleReconnect();
  };
}

function startHeartbeat(ws) {
  stopHeartbeat();
  heartbeatTimer = setInterval(() => { if (ws.readyState === WebSocket.OPEN) ws.send('{"type":"ping"}'); }, HEARTBEAT_MS);
}
function stopHeartbeat() { clearInterval(heartbeatTimer); heartbeatTimer = null; }

// Wakes the worker after Chrome stopped it while Mediator was closed (the minimum alarm period is 30 s).
chrome.alarms.create('mediator-reconnect', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(a => { if (a.name === 'mediator-reconnect') connect(); });
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// The popup asks for the status and can force a reconnect.
chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (sender.id !== chrome.runtime.id) return false;
  if (msg && msg.type === 'status') { reply({ ...status, version: VERSION }); return false; }
  if (msg && msg.type === 'reconnect') { backoff = 1000; if (socket) socket.close(); else connect(); reply({ ok: true }); return false; }
  return false;
});

connect();

// ---------------------------------------------------------------------------------------------- requests

const handlers = {
  get_tabs: getTabs,
  navigate,
  read_page: readPage,
  click,
  click_coordinates: click,
  type_text: typeText,
  press_key: pressKey,
  scroll,
  screenshot,
  read_console: readConsole,
  eval_js: evalJs,
  close_tab: closeTab,
  release,
};

async function handleRequest(ws, msg) {
  let reply;
  try {
    const fn = handlers[msg.method];
    if (!fn) throw new Error(`Unknown method ${msg.method}.`);
    reply = { id: msg.id, result: await fn(msg.params || {}) };
  } catch (e) {
    reply = { id: msg.id, error: { message: String((e && e.message) || e) } };
  }
  if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(reply));
}

const RESTRICTED = /^(chrome|chrome-extension|edge|about|devtools|view-source|chrome-search|chrome-untrusted):|^https:\/\/(chrome\.google\.com\/webstore|chromewebstore\.google\.com)/i;

async function resolveTab(params) {
  let tab = null;
  if (Number.isInteger(params.tabId)) tab = await chrome.tabs.get(params.tabId).catch(() => null);
  if (!tab && workTabId !== null) tab = await chrome.tabs.get(workTabId).catch(() => null);
  if (!tab) [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab) throw new Error('No tab to work in. Open one with navigate.');
  workTabId = tab.id;
  return tab;
}

function assertScriptable(tab) {
  if (RESTRICTED.test(tab.url || '')) throw new Error(`Chrome does not let extensions work on ${tab.url}. Navigate to a normal web page first.`);
}

function tabInfo(tab) { return { tabId: tab.id, url: tab.url, title: tab.title, active: tab.active, windowId: tab.windowId }; }

async function getTabs() {
  const tabs = await chrome.tabs.query({});
  return { workTabId, tabs: tabs.map(tabInfo) };
}

function waitForLoad(tabId, timeoutMs = LOAD_TIMEOUT_MS) {
  return new Promise((resolve) => {
    let done = false;
    const finish = () => { if (done) return; done = true; chrome.tabs.onUpdated.removeListener(onUpdated); clearTimeout(timer); resolve(); };
    const onUpdated = (id, info) => { if (id === tabId && info.status === 'complete') finish(); };
    const timer = setTimeout(finish, timeoutMs);
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.get(tabId).then(t => { if (t.status === 'complete') finish(); }).catch(finish);
  });
}

async function navigate(params) {
  let url = String(params.url || '').trim();
  if (!url) throw new Error('Give a url (or "back" / "forward" / "reload").');
  if (['back', 'forward', 'reload'].includes(url)) {
    const tab = await resolveTab(params);
    if (url === 'back') await chrome.tabs.goBack(tab.id);
    else if (url === 'forward') await chrome.tabs.goForward(tab.id);
    else await chrome.tabs.reload(tab.id);
    await sleep(300);
    await waitForLoad(tab.id);
    return tabInfo(await chrome.tabs.get(tab.id));
  }
  if (!/^[a-z][a-z0-9+.-]*:/i.test(url)) url = 'https://' + url;
  if (!/^https?:/i.test(url)) throw new Error('Only http and https pages can be opened.');
  let tab;
  if (params.newTab || (workTabId === null && !Number.isInteger(params.tabId))) {
    tab = await chrome.tabs.create({ url, active: true });
  } else {
    const current = await resolveTab(params);
    tab = await chrome.tabs.update(current.id, { url, active: true });
  }
  workTabId = tab.id;
  await sleep(200);
  await waitForLoad(tab.id);
  return tabInfo(await chrome.tabs.get(tab.id));
}

async function closeTab(params) {
  const tab = await resolveTab(params);
  await chrome.tabs.remove(tab.id);
  if (workTabId === tab.id) workTabId = null;
  return { closed: tab.id };
}

async function runInPage(tabId, func, args) {
  const [res] = await chrome.scripting.executeScript({ target: { tabId }, func, args: args || [] });
  if (!res) throw new Error('The page did not answer (it may still be loading).');
  return res.result;
}

async function readPage(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  const mode = ['full', 'interactive', 'viewport'].includes(params.mode) ? params.mode : 'full';
  const maxChars = Math.max(500, Math.min(Number(params.maxChars) || 12000, 60000));
  const offset = Math.max(0, Number(params.offset) || 0);
  let text = await runInPage(tab.id, serializePageForAssistant, [{ mode }]);
  if (typeof params.query === 'string' && params.query.trim()) {
    const q = params.query.trim().toLowerCase();
    const lines = text.split('\n');
    const keep = lines.filter((l, i) => i < 3 || l.toLowerCase().includes(q));
    text = keep.join('\n') + (keep.length <= 3 ? `\n(no line mentions "${params.query}")` : '');
  }
  const total = text.length;
  let slice = text.slice(offset, offset + maxChars);
  const more = offset + maxChars < total;
  if (more) slice += `\n... ${total - offset - maxChars} more characters: call read_page with offset=${offset + maxChars}`;
  return { tabId: tab.id, url: tab.url, title: tab.title, text: slice, total, truncated: more };
}

// ---------------------------------------------------------------------------------------------- input (CDP)

const attached = new Map();   // tabId -> { idle timer, console: [] }

async function ensureAttached(tabId) {
  let entry = attached.get(tabId);
  if (!entry) {
    try {
      await chrome.debugger.attach({ tabId }, '1.3');
    } catch (e) {
      const m = String(e && e.message || e);
      if (/already attached/i.test(m)) throw new Error('DevTools (or another debugger) is open on this tab. Close it and try again.');
      throw new Error('Could not control this tab: ' + m);
    }
    entry = { timer: null, console: [] };
    attached.set(tabId, entry);
    await cdp(tabId, 'Runtime.enable').catch(() => {});
    await cdp(tabId, 'Log.enable').catch(() => {});
  }
  clearTimeout(entry.timer);
  entry.timer = setTimeout(() => detach(tabId), DEBUGGER_IDLE_MS);
  return entry;
}

async function detach(tabId) {
  const entry = attached.get(tabId);
  if (!entry) return;
  clearTimeout(entry.timer);
  attached.delete(tabId);
  await chrome.debugger.detach({ tabId }).catch(() => {});
}

chrome.debugger.onDetach.addListener((src) => { const e = attached.get(src.tabId); if (e) { clearTimeout(e.timer); attached.delete(src.tabId); } });
chrome.tabs.onRemoved.addListener((tabId) => { attached.delete(tabId); if (workTabId === tabId) workTabId = null; });

chrome.debugger.onEvent.addListener((src, method, params) => {
  const entry = attached.get(src.tabId);
  if (!entry) return;
  let line = null;
  if (method === 'Runtime.consoleAPICalled') {
    line = { level: params.type, text: (params.args || []).map(a => a.value !== undefined ? String(a.value) : (a.description || a.type)).join(' ') };
  } else if (method === 'Runtime.exceptionThrown') {
    const d = params.exceptionDetails || {};
    line = { level: 'exception', text: (d.exception && d.exception.description) || d.text || 'exception' };
  } else if (method === 'Log.entryAdded') {
    line = { level: params.entry.level, text: params.entry.text + (params.entry.url ? ` (${params.entry.url})` : '') };
  }
  if (line) {
    line.text = line.text.slice(0, 2000);
    entry.console.push(line);
    if (entry.console.length > CONSOLE_KEEP) entry.console.shift();
  }
});

function cdp(tabId, method, params) { return chrome.debugger.sendCommand({ tabId }, method, params || {}); }
function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

async function settleAfterInput(tabId) {
  await sleep(250);
  const t = await chrome.tabs.get(tabId).catch(() => null);
  if (t && t.status === 'loading') await waitForLoad(tabId, 15000);
  return chrome.tabs.get(tabId).catch(() => null);
}

async function click(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  let x = Number(params.x), y = Number(params.y), what = `point (${x},${y})`, covered = null;
  if (params.ref) {
    const p = await runInPage(tab.id, mediatorRefPoint, [String(params.ref)]);
    if (p.error) throw new Error(p.error);
    ({ x, y } = p);
    what = p.desc;
    covered = p.covered;
  } else if (!Number.isFinite(x) || !Number.isFinite(y)) {
    throw new Error('Give a ref from read_page, or x and y (CSS pixels in the viewport).');
  }
  await ensureAttached(tab.id);
  const button = ['left', 'right', 'middle'].includes(params.button) ? params.button : 'left';
  const clickCount = params.double ? 2 : 1;
  await cdp(tab.id, 'Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
  for (let n = 1; n <= clickCount; n++) {
    await cdp(tab.id, 'Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button, clickCount: n });
    await cdp(tab.id, 'Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button, clickCount: n });
  }
  const after = await settleAfterInput(tab.id);
  return { clicked: what, x, y, coveredBy: covered, url: after && after.url, title: after && after.title };
}

async function typeText(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  const text = String(params.text ?? '');
  let target = 'the focused element';
  if (params.ref) {
    const r = await runInPage(tab.id, mediatorFocusRef, [String(params.ref), params.clear !== false, text]);
    if (r.error) throw new Error(r.error);
    target = r.desc;
    if (r.done) return { typed: false, selected: r.selected, target };
  }
  await ensureAttached(tab.id);
  if (text) await cdp(tab.id, 'Input.insertText', { text });
  if (params.submit) await sendKey(tab.id, 'Enter');
  const after = params.submit ? await settleAfterInput(tab.id) : tab;
  return { typed: true, chars: text.length, target, submitted: !!params.submit, url: after && after.url };
}

const KEYS = {
  enter: ['Enter', 'Enter', 13, '\r'], tab: ['Tab', 'Tab', 9], escape: ['Escape', 'Escape', 27], esc: ['Escape', 'Escape', 27],
  backspace: ['Backspace', 'Backspace', 8], delete: ['Delete', 'Delete', 46], space: [' ', 'Space', 32, ' '],
  arrowup: ['ArrowUp', 'ArrowUp', 38], arrowdown: ['ArrowDown', 'ArrowDown', 40], arrowleft: ['ArrowLeft', 'ArrowLeft', 37],
  arrowright: ['ArrowRight', 'ArrowRight', 39], up: ['ArrowUp', 'ArrowUp', 38], down: ['ArrowDown', 'ArrowDown', 40],
  left: ['ArrowLeft', 'ArrowLeft', 37], right: ['ArrowRight', 'ArrowRight', 39], pageup: ['PageUp', 'PageUp', 33],
  pagedown: ['PageDown', 'PageDown', 34], home: ['Home', 'Home', 36], end: ['End', 'End', 35],
};

async function sendKey(tabId, combo) {
  const parts = String(combo).split('+').map(s => s.trim()).filter(Boolean);
  const keyName = parts.pop() || '';
  let modifiers = 0;
  for (const m of parts.map(p => p.toLowerCase())) {
    if (m === 'alt') modifiers |= 1; else if (m === 'ctrl' || m === 'control') modifiers |= 2;
    else if (m === 'meta' || m === 'cmd') modifiers |= 4; else if (m === 'shift') modifiers |= 8;
    else throw new Error(`Unknown modifier ${m}.`);
  }
  let def = KEYS[keyName.toLowerCase()];
  if (!def && keyName.length === 1) {
    const up = keyName.toUpperCase();
    def = [keyName, /[a-z]/i.test(keyName) ? 'Key' + up : (/[0-9]/.test(keyName) ? 'Digit' + keyName : ''), up.charCodeAt(0), keyName];
  }
  if (!def) throw new Error(`Unknown key ${keyName}. Use Enter, Tab, Escape, Backspace, Delete, Space, arrows, PageUp/PageDown, Home/End or one character, with Ctrl+/Shift+/Alt+.`);
  const [key, code, vk, text] = def;
  const withText = text !== undefined && (modifiers & ~8) === 0;
  await cdp(tabId, 'Input.dispatchKeyEvent', { type: withText ? 'keyDown' : 'rawKeyDown', key, code, windowsVirtualKeyCode: vk, modifiers, text: withText ? text : undefined });
  await cdp(tabId, 'Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: vk, modifiers });
}

async function pressKey(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  await ensureAttached(tab.id);
  const keys = Array.isArray(params.keys) ? params.keys : String(params.keys || params.key || '').split(/\s+/).filter(Boolean);
  if (!keys.length) throw new Error('Give keys, e.g. "Enter" or "Ctrl+A".');
  for (const k of keys.slice(0, 50)) await sendKey(tab.id, k);
  const after = await settleAfterInput(tab.id);
  return { pressed: keys, url: after && after.url };
}

async function scroll(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  if (params.ref) {
    const p = await runInPage(tab.id, mediatorRefPoint, [String(params.ref)]);
    if (p.error) throw new Error(p.error);
    return { scrolledTo: p.desc };
  }
  const dir = String(params.direction || 'down').toLowerCase();
  const amount = Math.max(0.1, Math.min(Number(params.amount) || 0.8, 10));
  const view = await runInPage(tab.id, () => ({ w: innerWidth, h: innerHeight }));
  const x = Number.isFinite(Number(params.x)) ? Number(params.x) : Math.round(view.w / 2);
  const y = Number.isFinite(Number(params.y)) ? Number(params.y) : Math.round(view.h / 2);
  const dy = dir === 'up' ? -view.h * amount : dir === 'down' ? view.h * amount : 0;
  const dx = dir === 'left' ? -view.w * amount : dir === 'right' ? view.w * amount : 0;
  await ensureAttached(tab.id);
  await cdp(tab.id, 'Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX: dx, deltaY: dy });
  await sleep(250);
  const pos = await runInPage(tab.id, () => ({ scrollY: Math.round(scrollY), height: document.documentElement.scrollHeight, view: innerHeight }));
  return { direction: dir, ...pos };
}

async function screenshot(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  if (!tab.active) { await chrome.tabs.update(tab.id, { active: true }); await sleep(200); }
  await ensureAttached(tab.id);
  const m = await cdp(tab.id, 'Page.getLayoutMetrics');
  const v = m.cssVisualViewport || m.visualViewport;
  const dpr = (await runInPage(tab.id, () => devicePixelRatio)) || 1;
  // One image pixel per CSS pixel, so x/y read off the image can be passed straight to click.
  const shot = await cdp(tab.id, 'Page.captureScreenshot', {
    format: 'jpeg', quality: Math.max(20, Math.min(Number(params.quality) || 60, 90)),
    clip: { x: v.pageX, y: v.pageY, width: v.clientWidth, height: v.clientHeight, scale: 1 / dpr },
  });
  return { tabId: tab.id, mediaType: 'image/jpeg', data: shot.data, width: Math.round(v.clientWidth), height: Math.round(v.clientHeight) };
}

async function readConsole(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  const fresh = !attached.has(tab.id);
  const entry = await ensureAttached(tab.id);
  const lines = entry.console.slice();
  if (params.clear) entry.console.length = 0;
  return { tabId: tab.id, listeningSince: fresh ? 'now (call again after the page logs something)' : 'earlier', entries: lines };
}

async function evalJs(params) {
  const tab = await resolveTab(params);
  assertScriptable(tab);
  await ensureAttached(tab.id);
  const r = await cdp(tab.id, 'Runtime.evaluate', { expression: String(params.expression || ''), returnByValue: true, awaitPromise: true, userGesture: true });
  if (r.exceptionDetails) throw new Error('Script error: ' + ((r.exceptionDetails.exception && r.exceptionDetails.exception.description) || r.exceptionDetails.text));
  let value = r.result && 'value' in r.result ? r.result.value : (r.result && r.result.description);
  let text = typeof value === 'string' ? value : JSON.stringify(value);
  if (text === undefined) text = 'undefined';
  return { value: text.length > 20000 ? text.slice(0, 20000) + '... (truncated)' : text };
}

async function release() {
  const tabs = [...attached.keys()];
  await Promise.all(tabs.map(detach));
  return { detached: tabs.length };
}

// ---------------------------------------------------------------------------------------------- page functions
// These run inside the page (isolated world) through chrome.scripting.executeScript, so each must be self-contained.
// Refs live in globalThis.__mediator of the isolated world: stable across reads of the same document, invisible to
// the page's own scripts, and never written into the DOM.

function serializePageForAssistant(opts) {
  const mode = (opts && opts.mode) || 'full';
  const M = globalThis.__mediator || (globalThis.__mediator = { next: 1, byRef: new Map(), refOf: new WeakMap() });
  const vw = innerWidth, vh = innerHeight;
  const out = [];
  let line = '';
  let visited = 0;
  const emitted = new WeakSet();
  const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'HEAD', 'META', 'LINK', 'svg', 'SVG', 'CANVAS', 'PATH']);
  const ROLES = new Set(['button', 'link', 'checkbox', 'radio', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'option',
    'switch', 'combobox', 'textbox', 'searchbox', 'slider', 'spinbutton', 'treeitem']);
  const textOnly = mode === 'full';

  const clean = (s, n) => { s = String(s || '').replace(/\s+/g, ' ').trim(); return n && s.length > n ? s.slice(0, n - 3) + '...' : s; };
  const flush = () => { const t = clean(line); if (t) out.push(t); line = ''; };

  function refFor(el) {
    let r = M.refOf.get(el);
    if (!r) { r = 'ref_' + (M.next++); M.refOf.set(el, r); }
    M.byRef.set(r, new WeakRef(el));
    return r;
  }

  function hidden(el, cs) {
    if (cs.display === 'none' || cs.visibility === 'hidden' || cs.visibility === 'collapse') return true;
    if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
    if (el.tagName === 'INPUT' && el.type === 'hidden') return true;
    return false;
  }

  function role(el) {
    const r = (el.getAttribute('role') || '').toLowerCase();
    if (r) return r;
    const t = el.tagName;
    if (t === 'A') return 'link';
    if (t === 'BUTTON' || t === 'SUMMARY') return 'button';
    if (t === 'SELECT') return 'select';
    if (t === 'TEXTAREA') return 'textbox';
    if (t === 'INPUT') {
      const ty = (el.type || 'text').toLowerCase();
      if (['button', 'submit', 'reset', 'image'].includes(ty)) return 'button';
      if (ty === 'checkbox' || ty === 'radio' || ty === 'range' || ty === 'file') return ty;
      return 'textbox';
    }
    if (el.isContentEditable) return 'textbox';
    return 'clickable';
  }

  function interactive(el, cs) {
    const t = el.tagName;
    if (t === 'A' && el.hasAttribute('href')) return true;
    if (t === 'BUTTON' || t === 'SELECT' || t === 'TEXTAREA' || t === 'SUMMARY') return true;
    if (t === 'INPUT') return el.type !== 'hidden';
    if (ROLES.has((el.getAttribute('role') || '').toLowerCase())) return true;
    if (el.isContentEditable && !(el.parentElement && el.parentElement.isContentEditable)) return true;
    if (el.hasAttribute('onclick')) return true;
    // Custom controls (quiz answers are often divs): pointer cursor that its parent does not already have.
    if (cs.cursor === 'pointer' && el.parentElement && getComputedStyle(el.parentElement).cursor !== 'pointer' && clean(el.innerText).length > 0) return true;
    return false;
  }

  function nameOf(el) {
    const aria = el.getAttribute('aria-label');
    if (aria) return clean(aria, 120);
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const s = by.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(e => e.innerText).join(' ');
      if (clean(s)) return clean(s, 120);
    }
    if (el.labels && el.labels.length) {
      const s = Array.from(el.labels).map(l => l.innerText).join(' ');
      if (clean(s)) return clean(s, 160);
    }
    if (el.tagName === 'INPUT' && ['button', 'submit', 'reset'].includes(el.type)) return clean(el.value, 80);
    const own = el.tagName === 'SELECT' || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT' ? '' : clean(el.innerText, 160);
    if (own) return own;
    const img = el.querySelector && el.querySelector('img[alt]');
    return clean(el.getAttribute('title') || el.getAttribute('placeholder') || el.getAttribute('alt') || (img && img.alt) || el.getAttribute('name') || '', 80);
  }

  function pointOf(el) {
    let r = el.getBoundingClientRect();
    if ((r.width < 2 || r.height < 2) && el.labels && el.labels[0]) r = el.labels[0].getBoundingClientRect();
    return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), w: r.width, h: r.height };
  }

  function describe(el) {
    const p = pointOf(el);
    const onScreen = p.x >= 0 && p.y >= 0 && p.x <= vw && p.y <= vh;
    if (mode === 'viewport' && !onScreen) return null;
    // A label standing in for its hidden control: clicked as the label, described as the control.
    const ctl = el.tagName === 'LABEL' && el.control ? el.control : el;
    const rl = role(ctl);
    const bits = [`[${refFor(el)}] ${rl} "${nameOf(el)}"`];
    if (el.tagName === 'A') {
      const h = el.getAttribute('href') || '';
      if (h && !h.startsWith('javascript:') && h !== '#') bits.push('-> ' + clean(h.startsWith('http') ? h.replace(/^https?:\/\/(www\.)?/, '') : h, 80));
    }
    if (rl === 'checkbox' || rl === 'radio' || rl === 'switch' || rl === 'menuitemcheckbox' || rl === 'menuitemradio') {
      const checked = ctl.checked === true || ctl.getAttribute('aria-checked') === 'true';
      bits.push(checked ? '(checked)' : '(unchecked)');
      if (ctl.name) bits.push(`group=${clean(ctl.name, 40)}`);
    }
    if (el.getAttribute('aria-selected') === 'true' || el.getAttribute('aria-pressed') === 'true') bits.push('(selected)');
    if (el.getAttribute('aria-expanded')) bits.push(el.getAttribute('aria-expanded') === 'true' ? '(expanded)' : '(collapsed)');
    if (el.disabled || el.getAttribute('aria-disabled') === 'true') bits.push('(disabled)');
    if (el.tagName === 'SELECT') {
      const opts = Array.from(el.options).slice(0, 40).map(o => clean(o.text, 50) + (o.selected ? '*' : ''));
      bits.push(`options: ${opts.join(' | ')}${el.options.length > 40 ? ' | ...' : ''}`);
    } else if (rl === 'textbox' && el.tagName !== 'DIV') {
      const v = el.type === 'password' ? (el.value ? '****' : '') : clean(el.value, 80);
      bits.push(`value="${v}"`);
      if (el.required) bits.push('(required)');
    }
    bits.push(onScreen ? `@(${p.x},${p.y})` : '(offscreen)');
    return bits.join(' ');
  }

  function emitControl(el) {
    if (emitted.has(el)) return;
    emitted.add(el);
    const d = describe(el);
    if (d) { flush(); out.push(d); }
  }

  function walk(node, depth) {
    if (visited++ > 25000 || depth > 200) return;
    if (node.nodeType === 3) { if (textOnly) line += ' ' + node.nodeValue; return; }
    if (node.nodeType !== 1 && node.nodeType !== 11) return;
    if (node.nodeType === 11) { for (const c of node.childNodes) walk(c, depth + 1); return; }
    const el = node;
    if (SKIP.has(el.tagName)) return;
    const cs = getComputedStyle(el);
    if (hidden(el, cs)) return;
    const t = el.tagName;
    if (t === 'IFRAME' || t === 'FRAME') {
      flush();
      const src = el.getAttribute('src') || '';
      out.push(`[frame] ${clean(el.title || el.name || '', 60)} ${src.startsWith('http') ? src : (src ? new URL(src, location.href).href : '(inline)')} (open the URL with navigate to read it)`);
      return;
    }
    // A control hidden behind its styled label (common for quiz answers) is listed as the label, with the control's state.
    if (t === 'LABEL' && el.control) {
      if (emitted.has(el.control)) return;
      const ccs = getComputedStyle(el.control);
      emitControl(hidden(el.control, ccs) || ccs.opacity === '0' ? el : el.control);
      emitted.add(el.control);
      return;
    }
    if (interactive(el, cs)) { emitControl(el); return; }
    const heading = /^H[1-6]$/.test(t) ? Number(t[1]) : 0;
    const block = heading || ['block', 'flex', 'grid', 'list-item', 'table', 'table-row', 'flow-root'].includes(cs.display) || t === 'BR' || t === 'TD' || t === 'TH';
    if (block) flush();
    if (heading) line += '#'.repeat(heading) + ' ';
    if (t === 'LI' && textOnly) line += '- ';
    if (t === 'IMG' && textOnly && clean(el.alt)) line += ` [image: ${clean(el.alt, 100)}] `;
    if (heading && !textOnly) {
      // Headings are kept even in the compact modes: they say where the controls are.
      line += clean(el.innerText, 120);
    } else {
      if (el.shadowRoot) walk(el.shadowRoot, depth + 1);
      for (const c of el.childNodes) walk(c, depth + 1);
    }
    if (t === 'TD' || t === 'TH') line += ' |';
    if (block || heading) flush();
  }

  walk(document.body || document.documentElement, 0);
  flush();
  for (const [r, w] of M.byRef) { const e = w.deref(); if (!e || !e.isConnected) M.byRef.delete(r); }

  const head = [
    `Page: ${clean(document.title, 120)}`,
    `URL: ${location.href}`,
    `Viewport ${vw}x${vh}, scrolled ${Math.round(scrollY)} of ${document.documentElement.scrollHeight}px. Click with ref; @(x,y) are viewport CSS pixels.`,
  ];
  // Repeated identical text lines (menus, footers repeated by frameworks) cost tokens and say nothing new.
  const seen = new Set();
  const body = out.filter(l => (l.startsWith('[ref_') || l.length < 4 || !seen.has(l)) && (seen.add(l), true));
  return head.concat(body).join('\n');
}

function mediatorRefPoint(ref) {
  const M = globalThis.__mediator;
  const w = M && M.byRef.get(ref);
  const el = w && w.deref();
  if (!el || !el.isConnected) return { error: `${ref} is no longer on the page (it changed). Call read_page again.` };
  let target = el;
  let r = el.getBoundingClientRect();
  if ((r.width < 2 || r.height < 2 || getComputedStyle(el).opacity === '0') && el.labels && el.labels[0]) target = el.labels[0];
  target.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
  r = target.getBoundingClientRect();
  const x = Math.round(r.left + r.width / 2), y = Math.round(r.top + r.height / 2);
  const short = (e) => `${e.tagName.toLowerCase()} "${String(e.getAttribute('aria-label') || e.innerText || e.value || '').replace(/\s+/g, ' ').trim().slice(0, 60)}"`;
  let top = document.elementFromPoint(x, y);
  while (top && top.shadowRoot && top.shadowRoot.elementFromPoint) { const inner = top.shadowRoot.elementFromPoint(x, y); if (!inner || inner === top) break; top = inner; }
  const covered = top && !(target.contains(top) || top.contains(target) || el.contains(top)) ? short(top) : null;
  return { x, y, desc: `${ref} ${short(el)}`, covered };
}

function mediatorFocusRef(ref, clear, text) {
  const M = globalThis.__mediator;
  const w = M && M.byRef.get(ref);
  const el = w && w.deref();
  if (!el || !el.isConnected) return { error: `${ref} is no longer on the page (it changed). Call read_page again.` };
  const desc = `${ref} ${el.tagName.toLowerCase()}`;
  const ctl = el.tagName === 'LABEL' && el.control ? el.control : el;
  if (ctl.tagName === 'INPUT' && ['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'image'].includes(ctl.type)) {
    return { error: `${ref} is a ${ctl.type}: use click, not type_text.` };
  }
  if (el.tagName === 'SELECT') {
    const want = String(text).trim().toLowerCase();
    const opts = Array.from(el.options);
    const opt = opts.find(o => o.value.toLowerCase() === want || o.text.trim().toLowerCase() === want)
      || opts.find(o => o.text.trim().toLowerCase().includes(want));
    if (!opt) return { error: `No option "${text}" in ${ref}. Options: ${opts.map(o => o.text.trim()).slice(0, 30).join(' | ')}` };
    el.value = opt.value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return { done: true, selected: opt.text.trim(), desc };
  }
  if (el.tagName === 'INPUT' && ['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'image'].includes(el.type)) {
    return { error: `${ref} is a ${el.type}: use click, not type_text.` };
  }
  el.scrollIntoView({ block: 'center', behavior: 'instant' });
  el.focus();
  if (clear) {
    if (typeof el.select === 'function' && typeof el.value === 'string') el.select();
    else if (el.isContentEditable) { const s = getSelection(); s.removeAllRanges(); const rg = document.createRange(); rg.selectNodeContents(el); s.addRange(rg); }
  }
  if (document.activeElement !== el && !el.contains(document.activeElement)) return { error: `${ref} cannot take keyboard focus.` };
  return { done: false, desc };
}
