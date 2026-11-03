// Ekko on the G2 (DR-38): tap to talk, tap to send. What you said goes to /hear, which turns it into text, then to /ask,
// which hands it to the Brain like a Telegram message. The lens shows what was heard while Ekko works, then the answer.
// Same conversation every time, so follow-ups and "yes" to a read-back work.

import { openLens, fit } from './lens.js';

const $ = id => document.getElementById(id);
const lens = await openLens($('screen'), $('controls'));
const MAX_SECONDS = 30;

// The page's key (Glasses_PageKey): typed once on the phone below, kept by the Even app. Never in a link.
let key = await lens.get('ekko.glassesKey');

// idle | listening | hearing | thinking | replied
const talk = { phase: 'idle', heard: '', reply: '', recorder: null, timer: null, turn: 0 };

function text() {
  switch (talk.phase) {
    case 'listening': return 'Listening…\n\nTap to send, double-tap to cancel.';
    case 'hearing': return 'Hearing you…';
    case 'thinking': return fit(`You: ${talk.heard}`, 900) + '\n\nEkko is thinking…';
    case 'replied': return fit(talk.heard ? `You: ${talk.heard}\n\nEkko: ${talk.reply}` : talk.reply, 960) + '\n\nTap to talk.';
    default: return 'Ekko\n\nTap to talk.';
  }
}

function set(phase) {
  talk.phase = phase;
  lens.setText('body', text());
}

function say(reply, heard = '') {
  talk.heard = heard;
  talk.reply = reply;
  set('replied');
}

async function start() {
  if (!key) return say('Set the glasses key on the phone first.');
  talk.recorder = await lens.record();
  if (!talk.recorder) return say(lens.kind === 'g2' ? "The microphone didn't open." : "This device's microphone didn't open (it needs permission, and HTTPS).");
  set('listening');
  clearTimeout(talk.timer);
  talk.timer = setTimeout(send, MAX_SECONDS * 1000);
}

async function cancel() {
  clearTimeout(talk.timer);
  const recorder = talk.recorder;
  talk.recorder = null;
  await recorder?.cancel();
  set('idle');
}

async function send() {
  clearTimeout(talk.timer);
  const recorder = talk.recorder;
  talk.recorder = null;
  if (!recorder) return;
  const audio = await recorder.stop();
  if (!audio || audio.size < 3200) return set('idle'); // a tap and nothing said
  const turn = ++talk.turn;
  set('hearing');
  const heard = await call('/hear', audio);
  if (turn !== talk.turn) return; // cancelled while hearing
  if (!heard.text) return say(heard.error || "I didn't catch anything.");
  await ask(heard.text, turn);
}

async function ask(said, turn = ++talk.turn) {
  talk.heard = said;
  set('thinking');
  const answer = await call('/ask', JSON.stringify({ text: said }), 'application/json');
  if (turn === talk.turn) say(answer.reply || answer.error || "Ekko didn't answer.", said);
}

/** The JSON answer, or { error } with a sentence for the lens. */
async function call(path, body, type) {
  try {
    const res = await fetch(path, { method: 'POST', headers: { 'X-Api-Key': key, ...(type ? { 'Content-Type': type } : {}) }, body });
    if (res.status === 401) return { error: 'The glasses key was refused. Set it again on the phone.' };
    return await res.json();
  } catch {
    return { error: "Ekko's glasses service isn't answering." };
  }
}

lens.onInput(({ kind }) => {
  if (kind === 'tap') {
    if (talk.phase === 'listening') return send();
    if (talk.phase === 'idle' || talk.phase === 'replied') return start();
    return; // hearing or thinking: busy
  }
  if (kind === 'double') {
    if (talk.phase === 'listening') return cancel();
    if (talk.phase === 'hearing') { talk.turn++; return set('idle'); } // nothing has reached Ekko yet
    if (talk.phase === 'thinking') return; // Ekko already has it; the answer still comes
    return lens.exit(); // the only page: its double tap raises the system's exit dialog
  }
  if (kind === 'exit' || kind === 'closed') cancel();
});

// ── the phone side: the key, typing instead of talking, and the lens as drawn ──────────────────────────
function status() {
  $('status').textContent = (key ? 'Ready.' : 'Set the glasses key below.')
    + (lens.kind === 'g2' ? '' : " Preview: no glasses, this device's mic.");
}

$('key').value = key ? '••••••••' : '';
$('settings').addEventListener('submit', async e => {
  e.preventDefault();
  const typed = $('key').value.trim();
  if (!typed || typed.startsWith('•')) return;
  key = typed;
  await lens.set('ekko.glassesKey', key);
  $('key').value = '••••••••';
  status();
});

$('typed').addEventListener('submit', e => {
  e.preventDefault();
  const said = $('said').value.trim();
  if (!said || talk.phase === 'listening' || talk.phase === 'hearing' || talk.phase === 'thinking') return;
  if (!key) return say('Set the glasses key on the phone first.');
  $('said').value = '';
  ask(said);
});

$('controls').hidden = lens.kind === 'g2';
status();
await lens.show({ texts: [{ name: 'body', x: 8, y: 4, w: 560, h: 280, content: text(), capture: true }] });
