// Mediator for Chrome: the side panel chat.
//
// Messages go to the service worker over a port named "panel"; it forwards them to the Mediator app, which runs each
// one as a Browser job (Claude Code or Antigravity on the user's subscription) in the tab next to this panel and
// streams back panel_event messages: start (route), step (a tool), text (the model's words), done (answer), error.
// The chat is kept in chrome.storage.session per window, so closing and reopening the panel keeps it.
'use strict';

const LABELS = { connected: 'Connected to Mediator', connecting: 'Connecting…', waiting: 'Mediator is not running',
  disconnected: 'Disconnected', unpaired: 'Not set up', error: 'Problem', starting: 'Starting…' };

const log = document.getElementById('log');
const input = document.getElementById('input');
const send = document.getElementById('send');
const dot = document.getElementById('dot');
const statusBar = document.getElementById('status');
const statusText = document.getElementById('statusText');

let windowId = null;
let chat = { chatId: newId(), items: [] };   // items: {role:'user'|'bot', text, meta, steps:[], narration:[], error, running}
let connected = false;
let port = null;

function newId() { return 'c' + crypto.randomUUID().replace(/-/g, '').slice(0, 20); }
function storageKey() { return 'chat-' + windowId; }
function running() { return chat.items.some(i => i.running); }

async function save() {
  try { await chrome.storage.session.set({ [storageKey()]: chat }); } catch { /* storage full or unavailable: the chat still works */ }
}

// ------------------------------------------------------------------------------------------------ rendering

function escapeHtml(s) { return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

// A small, safe Markdown subset: escape everything, then **bold**, `code`, and line breaks (pre-wrap keeps them).
function formatAnswer(text) {
  return escapeHtml(text)
    .replace(/`([^`\n]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
    .replace(/^#{1,4}\s+(.+)$/gm, '<strong>$1</strong>');
}

function render() {
  log.replaceChildren();
  if (chat.items.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.innerHTML = '<strong>Ask about this page</strong>Mediator works in the tab next to this panel, using your Claude or Gemini subscription in the Mediator app.';
    log.append(empty);
  }
  for (const item of chat.items) {
    const el = document.createElement('div');
    if (item.role === 'user') {
      el.className = 'msg user';
      el.textContent = item.text;
    } else {
      el.className = 'msg bot' + (item.error ? ' error' : '');
      if (item.meta) { const m = document.createElement('div'); m.className = 'meta'; m.textContent = item.meta; el.append(m); }
      if (item.steps && item.steps.length) {
        const s = document.createElement('div');
        s.className = 'steps' + (item.running ? ' working' : '');
        s.textContent = '⚙ ' + item.steps.slice(-8).join(' · ') + (item.steps.length > 8 ? ` (${item.steps.length} steps)` : '');
        el.append(s);
      } else if (item.running) {
        const s = document.createElement('div'); s.className = 'steps working'; s.textContent = 'Working'; el.append(s);
      }
      if (item.running && item.narration && item.narration.length) {
        const n = document.createElement('div'); n.className = 'narration'; n.textContent = item.narration[item.narration.length - 1]; el.append(n);
      }
      if (item.text) { const body = document.createElement('div'); body.innerHTML = formatAnswer(item.text); el.append(body); }
    }
    log.append(el);
  }
  log.scrollTop = log.scrollHeight;
  updateComposer();
}

function updateComposer() {
  const busy = running();
  send.textContent = busy ? 'Stop' : 'Send';
  send.classList.toggle('stop', busy);
  send.disabled = !busy && (!connected || input.value.trim().length === 0);
}

function showStatus(s) {
  connected = s.state === 'connected';
  dot.className = 'dot ' + s.state;
  dot.title = LABELS[s.state] || s.state;
  statusBar.classList.toggle('show', !connected);
  statusText.textContent = (LABELS[s.state] || s.state) + (s.detail ? ': ' + s.detail : '');
  document.getElementById('version').textContent = 'Mediator for Chrome ' + (s.version || '');
  updateComposer();
}

// ------------------------------------------------------------------------------------------------ messages

function lastBot() { for (let i = chat.items.length - 1; i >= 0; i--) if (chat.items[i].role === 'bot') return chat.items[i]; return null; }

function onPanelEvent(ev) {
  if (ev.chatId !== chat.chatId) return;
  const bot = lastBot();
  if (!bot || !bot.running) return;
  switch (ev.kind) {
    case 'start': bot.meta = ev.text; break;
    case 'step': bot.steps.push(ev.text); break;
    case 'text': bot.narration.push(ev.text); break;
    case 'done': bot.running = false; bot.text = ev.text; break;
    case 'error': bot.running = false; bot.error = true; bot.text = ev.text; break;
  }
  render();
  save();
}

async function currentTab() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, windowId });
    return tab ? { id: tab.id, url: tab.url || '', title: tab.title || '' } : null;
  } catch { return null; }
}

async function submit(text) {
  text = text.trim();
  if (!text || running() || !connected) return;
  chat.items.push({ role: 'user', text });
  chat.items.push({ role: 'bot', text: '', meta: '', steps: [], narration: [], running: true });
  input.value = '';
  autosize();
  render();
  save();
  port.postMessage({ type: 'chat', chatId: chat.chatId, text, tab: await currentTab() });
}

function stop() { port.postMessage({ type: 'stop', chatId: chat.chatId }); }

function newChat() {
  if (running()) stop();
  port.postMessage({ type: 'reset', chatId: chat.chatId });
  chat = { chatId: newId(), items: [] };
  render();
  save();
  input.focus();
}

function connectPort() {
  port = chrome.runtime.connect({ name: 'panel' });
  port.onMessage.addListener(msg => {
    if (msg.type === 'status') showStatus(msg);
    else if (msg.type === 'panel_event') onPanelEvent(msg);
    else if (msg.type === 'replay_missing' && msg.chatId === chat.chatId) {
      const bot = lastBot();
      if (bot && bot.running) { bot.running = false; bot.error = true; bot.text = 'This answer was lost while the panel was closed. Ask again.'; render(); save(); }
    }
  });
  // The worker can be stopped by Chrome; reconnect and ask for the status again.
  port.onDisconnect.addListener(() => setTimeout(connectPort, 500));
}

// ------------------------------------------------------------------------------------------------ wiring

function autosize() { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 160) + 'px'; }

document.getElementById('form').addEventListener('submit', e => { e.preventDefault(); if (running()) stop(); else submit(input.value); });
input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); if (!running()) submit(input.value); } });
input.addEventListener('input', () => { autosize(); updateComposer(); });
document.getElementById('newchat').addEventListener('click', newChat);
document.getElementById('reconnect').addEventListener('click', () => port.postMessage({ type: 'reconnect' }));
for (const chip of document.querySelectorAll('.chip')) chip.addEventListener('click', () => submit(chip.dataset.text));

(async () => {
  try { windowId = (await chrome.windows.getCurrent()).id; } catch { windowId = 0; }
  try {
    const saved = (await chrome.storage.session.get(storageKey()))[storageKey()];
    if (saved && saved.chatId && Array.isArray(saved.items)) chat = saved;
  } catch { /* nothing saved */ }
  connectPort();
  // A message still running when the panel closed: the worker replays what happened since it was sent.
  const bot = lastBot();
  if (bot && bot.running) { bot.steps = []; bot.narration = []; port.postMessage({ type: 'replay', chatId: chat.chatId }); }
  render();
  input.focus();
})();
