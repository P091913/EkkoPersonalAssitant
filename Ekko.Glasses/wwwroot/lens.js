// The glasses behind one small interface, so app.js never touches the SDK directly (carried over from the old app's
// g2/lens.js, trimmed to one text page):
//   show(page)          a whole page: { texts: [{ name, x, y, w, h, content, capture }] }
//   setText(name, text) one text box's words, in place (no flicker)
//   onInput(fn)         { kind: tap | double | up | down | long | long_end | enter | exit | closed }
//   record()            starts the mic: { stop() -> audio Blob, cancel() }, or null when it didn't open
//   exit()
// Inside the Even app it drives the real lens through the Even Hub SDK and records the glasses' mic. In a normal
// browser it draws the same page into a 576x288 pretend lens, takes gestures from buttons or the keyboard, and records
// that device's own mic, so the whole path can be tried before the glasses arrive.

const W = 576, H = 288;
const encoder = new TextEncoder();

/** At most max UTF-8 bytes, cut at a character: the firmware rejects a text box over 999 bytes. */
export function fit(text, max) {
  text = String(text ?? '');
  if (encoder.encode(text).length <= max) return text;
  let out = '';
  for (const ch of text) {
    if (encoder.encode(out + ch + '…').length > max) break;
    out += ch;
  }
  return out + '…';
}

/** The pretend lens: green on black, the same page the glasses get. */
class Mirror {
  constructor(host) {
    this.host = host;
    this.page = null;
  }

  render(page) {
    this.page = page;
    if (!this.host) return;
    const root = document.createElement('div');
    root.className = 'lens';
    for (const t of page.texts || []) {
      const el = document.createElement('div');
      el.className = 'lens__text' + (t.capture ? ' is-capture' : '');
      el.dataset.name = t.name;
      Object.assign(el.style, { left: `${t.x / W * 100}%`, top: `${t.y / H * 100}%`, width: `${t.w / W * 100}%`, height: `${t.h / H * 100}%` });
      el.textContent = t.content;
      root.append(el);
    }
    this.host.replaceChildren(root);
  }

  setText(name, content) {
    const t = (this.page?.texts || []).find(x => x.name === name);
    if (t) t.content = content;
    const el = this.host?.querySelector(`.lens__text[data-name="${name}"]`);
    if (el) el.textContent = content;
  }
}

/** Every bridge call one at a time, each given up on after 4 s: a flaky BLE hop otherwise hangs the lens for ~30 s. */
function queue() {
  let chain = Promise.resolve();
  return fn => {
    const run = chain.then(() => Promise.race([fn(), new Promise((_, no) => setTimeout(() => no(new Error('bridge timeout')), 4000))]));
    chain = run.catch(() => undefined);
    return run;
  };
}

/** The glasses' mic (16 kHz, 16-bit, mono PCM) as one WAV file, the way Whisper takes it. */
function wav(chunks) {
  const bytes = chunks.reduce((n, c) => n + c.length, 0);
  const out = new Uint8Array(44 + bytes);
  const v = new DataView(out.buffer);
  const text = (at, s) => [...s].forEach((c, i) => v.setUint8(at + i, c.charCodeAt(0)));
  text(0, 'RIFF'); v.setUint32(4, 36 + bytes, true); text(8, 'WAVE');
  text(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, 16000, true); v.setUint32(28, 32000, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  text(36, 'data'); v.setUint32(40, bytes, true);
  let at = 44;
  for (const c of chunks) { out.set(c, at); at += c.length; }
  return new Blob([out], { type: 'audio/wav' });
}

class G2Lens {
  constructor(bridge, sdk, mirror) {
    this.kind = 'g2';
    this.bridge = bridge;
    this.sdk = sdk;
    this.mirror = mirror;
    this.q = queue();
    this.started = false;
    this.inputs = [];
    this.recording = null;
    this.lastKind = { kind: '', at: 0 };
    bridge.onEvenHubEvent(e => this.event(e));
  }

  /** The page's storage in the Even app, which keeps it across launches; the browser's otherwise. */
  async get(key) {
    try { return (await this.bridge.getLocalStorage(key)) || localStorage.getItem(key) || ''; } catch { return ''; }
  }

  async set(key, value) {
    try { localStorage.setItem(key, value); } catch { /* private mode */ }
    try { await this.bridge.setLocalStorage(key, value); } catch { /* older host */ }
  }

  show(page) {
    const S = this.sdk;
    const texts = (page.texts || []).map((t, i) => new S.TextContainerProperty({
      xPosition: t.x, yPosition: t.y, width: t.w, height: t.h, borderWidth: 0, paddingLength: 4,
      containerID: i + 1, containerName: t.name, content: fit(t.content, 990), isEventCapture: t.capture ? 1 : 0,
    }));
    this.ids = Object.fromEntries((page.texts || []).map((t, i) => [t.name, i + 1]));
    this.mirror.render(page);
    const payload = { containerTotalNum: texts.length, textObject: texts };
    const first = !this.started;
    this.started = true;
    return this.q(async () => first
      ? (await this.bridge.createStartUpPageContainer(new S.CreateStartUpPageContainer(payload))) === 0
      : this.bridge.rebuildPageContainer(new S.RebuildPageContainer(payload)))
      .catch(err => { console.warn('show failed:', err); return false; });
  }

  setText(name, content) {
    this.mirror.setText(name, content);
    const id = this.ids?.[name];
    if (!id) return Promise.resolve(false);
    content = fit(content, 990);
    return this.q(() => this.bridge.textContainerUpgrade(new this.sdk.TextContainerUpgrade({
      containerID: id, containerName: name, contentOffset: 0, contentLength: content.length, content,
    }))).catch(err => { console.warn('setText failed:', err); return false; });
  }

  onInput(fn) { this.inputs.push(fn); }

  async record() {
    this.recording = [];
    const open = await this.q(() => this.bridge.audioControl(true, this.sdk.AudioInputSource.Glasses)).catch(() => false);
    if (!open) {
      this.recording = null;
      return null;
    }
    const finish = async keep => {
      const chunks = this.recording || [];
      this.recording = null;
      await this.q(() => this.bridge.audioControl(false)).catch(() => false);
      return keep ? wav(chunks) : null;
    };
    return { stop: () => finish(true), cancel: () => finish(false) };
  }

  exit() { return this.q(() => this.bridge.shutDownPageContainer(1)).catch(() => false); }

  emit(input) {
    // The same gesture can arrive twice 50-100 ms apart.
    const now = Date.now();
    if (input.kind === this.lastKind.kind && now - this.lastKind.at < 250) return;
    this.lastKind = { kind: input.kind, at: now };
    this.inputs.forEach(fn => fn(input));
  }

  event(e) {
    if (e.audioEvent) {
      const pcm = e.audioEvent.audioPcm;
      if (pcm?.length && this.recording) this.recording.push(pcm.slice());
      return;
    }
    // A zero comes through as undefined: a tap's event type.
    const t = (e.textEvent ?? e.sysEvent)?.eventType ?? 0;
    if (e.textEvent) {
      this.emit({ kind: ['tap', 'up', 'down', 'double'][t] ?? 'tap' });
      return;
    }
    if (e.sysEvent) {
      const kind = { 0: 'tap', 3: 'double', 4: 'enter', 5: 'exit', 6: 'closed', 7: 'closed', 9: 'long', 10: 'long_end' }[t];
      if (kind) this.emit({ kind });
    }
  }
}

/** A browser with no glasses: the pretend lens is the display, buttons or the keyboard are the touchpad, and the mic is this device's. */
class PreviewLens {
  constructor(mirror, controls) {
    this.kind = 'preview';
    this.mirror = mirror;
    this.inputs = [];
    const press = kind => this.inputs.forEach(fn => fn({ kind }));
    controls?.querySelectorAll('[data-gesture]').forEach(b => b.addEventListener('click', () => press(b.dataset.gesture)));
    addEventListener('keydown', e => {
      const kind = { Enter: 'tap', Escape: 'double' }[e.key];
      if (kind && !(e.target instanceof HTMLInputElement)) {
        e.preventDefault();
        press(kind);
      }
    });
  }

  async get(key) { try { return localStorage.getItem(key) || ''; } catch { return ''; } }
  async set(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode */ } }

  show(page) {
    this.mirror.render(page);
    return Promise.resolve(true);
  }

  setText(name, content) {
    this.mirror.setText(name, fit(content, 990));
    return Promise.resolve(true);
  }

  onInput(fn) { this.inputs.push(fn); }

  /** The device's own mic, in whatever format its browser records (WebM on Chrome, MP4 on Safari): speaches reads both. */
  async record() {
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch {
      return null; // refused, no mic, or not HTTPS
    }
    const recorder = new MediaRecorder(stream);
    const parts = [];
    recorder.ondataavailable = e => e.data.size && parts.push(e.data);
    recorder.start();
    const finish = keep => new Promise(resolve => {
      recorder.onstop = () => {
        stream.getTracks().forEach(t => t.stop());
        resolve(keep ? new Blob(parts, { type: recorder.mimeType || 'audio/webm' }) : null);
      };
      recorder.stop();
    });
    return { stop: () => finish(true), cancel: () => finish(false) };
  }

  exit() { return Promise.resolve(true); }
}

/** The G2 when this page runs inside the Even app, the preview otherwise. */
export async function openLens(screen, controls) {
  const mirror = new Mirror(screen);
  if (window.flutter_inappwebview) {
    try {
      // @evenrealities/even_hub_sdk 0.0.16 (MIT, see even_hub_sdk.LICENSE.txt): its dist/index.js, unchanged.
      const sdk = await import('./even_hub_sdk.js');
      const bridge = await Promise.race([sdk.waitForEvenAppBridge(), new Promise((_, no) => setTimeout(() => no(new Error('no bridge')), 6000))]);
      return new G2Lens(bridge, sdk, mirror);
    } catch (err) {
      console.warn('Even app bridge not ready, previewing instead:', err);
    }
  }
  return new PreviewLens(mirror, controls);
}
