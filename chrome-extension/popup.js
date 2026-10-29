'use strict';
const LABELS = { connected: 'Connected to Mediator', connecting: 'Connecting…', waiting: 'Waiting for Mediator', disconnected: 'Disconnected',
  unpaired: 'Not set up', error: 'Problem', starting: 'Starting…' };
function show(s) {
  document.getElementById('state').textContent = LABELS[s.state] || s.state;
  document.getElementById('dot').className = 'dot ' + s.state;
  document.getElementById('detail').textContent = s.detail || (s.state === 'connected' ? 'Mediator can use your tabs when you ask it to.' : '');
  document.getElementById('version').textContent = 'Version ' + (s.version || '');
}
function refresh() { chrome.runtime.sendMessage({ type: 'status' }).then(show).catch(() => {}); }
document.getElementById('reconnect').addEventListener('click', () => chrome.runtime.sendMessage({ type: 'reconnect' }).then(() => setTimeout(refresh, 800)));
refresh();
setInterval(refresh, 1500);
