// The in-game counter, drawn in a transparent, always-on-top Tauri window (desktop/src/overlay.rs).
// It never touches the game: it is an ordinary desktop window, so it shows over Fortnite in windowed and borderless
// modes only. Unlocked: drag to move, wheel to resize, right-click for the tray menu. Locked: the shell makes the
// window ignore the mouse. Same look as the OBS page (gold = personal, cyan = squad, pink = session).
//
// This page only reads (the open /api/overlay feed, plus /api/settings for the language). Every change (position,
// size, lock) goes through the shell, which owns the window.
import { invoke } from '@tauri-apps/api/core';
import { getCurrentWindow } from '@tauri-apps/api/window';
import './overlay.css';

interface OverlaySettings {
  enabled: boolean; locked: boolean; opacity: number; scale: number; showPersonal: boolean; showSquad: boolean;
  showSession: boolean; showLastMatch: boolean; showMembers: boolean;
}
interface OverlayMember { name: string; chests: number; online: boolean }
interface OverlayData {
  personal: number; squad?: number | null; squadScope: string; session: number; lastMatch?: number | null;
  lastMatchPartial: boolean; members: OverlayMember[]; settings: OverlaySettings;
}

const root = document.getElementById('overlay')!;
let language: 'en' | 'fr' = 'en';
let locked = false;
const T = (en: string, fr: string) => (language === 'fr' ? fr : en);
const N = (v?: number | null) => (v == null ? '–' : v.toLocaleString(language === 'fr' ? 'fr-FR' : 'en-US'));

function el(tag: string, cls: string, text?: string): HTMLElement {
  const e = document.createElement(tag);
  e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function render(d: OverlayData) {
  const s = d.settings;
  locked = s.locked;
  document.documentElement.style.setProperty('--k', String(Math.min(250, Math.max(60, s.scale)) / 100));
  root.style.opacity = String(Math.min(100, Math.max(20, s.opacity)) / 100);
  root.classList.toggle('unlocked', !s.locked);

  const cells: [string, string, string][] = [];
  if (s.showPersonal) cells.push([T('Chests', 'Coffres'), N(d.personal), 'gold']);
  if (s.showSquad && d.squad != null)
    cells.push([d.squadScope === 'shared' ? T('Squad together', 'Escouade ensemble') : T('Squad', 'Escouade'), N(d.squad), 'cyan']);
  if (s.showSession) cells.push([T('Session', 'Session'), N(d.session), 'pink']);
  // "+" = some teammates have not uploaded this match yet: the figure can still grow.
  if (s.showLastMatch) cells.push([T('Last match', 'Dernière partie'), N(d.lastMatch) + (d.lastMatchPartial ? '+' : ''), 'text']);
  const members = s.showMembers ? d.members : [];

  root.replaceChildren();
  if (cells.length > 0) {
    const row = el('div', 'card row');
    for (const [label, value, color] of cells) {
      const cell = el('div', 'cell');
      cell.append(el('div', 'label', label), el('div', `value ${color}`, value));
      row.append(cell);
    }
    root.append(row);
  }
  if (members.length > 0) {
    const row = el('div', 'card members');
    for (const m of members) {
      const part = el('span', 'member');
      part.append(el('span', m.online ? 'name' : 'name offline', m.name), el('span', 'chests', N(m.chests)));
      row.append(part);
    }
    root.append(row);
  }

  // The shell sizes the window to the content and shows it only when there is something to show.
  requestAnimationFrame(() => {
    const box = root.getBoundingClientRect();
    invoke('overlay_layout', { width: Math.ceil(box.width), height: Math.ceil(box.height), hasContent: root.childElementCount > 0 })
      .catch(() => { /* shell gone */ });
  });
}

async function refresh() {
  try {
    const token = (window as unknown as { __CC_TOKEN?: string }).__CC_TOKEN;
    if (token) {
      const settings = await fetch('/api/settings', { headers: { 'X-CC-Token': token } }).then((r) => r.json());
      language = settings.language === 'fr' ? 'fr' : 'en';
    }
    render(await fetch('/api/overlay').then((r) => r.json()));
  } catch {
    // the engine is restarting: the next event refreshes
  }
}

// Coalesce bursts (an import of many replays raises one event per file).
let queued = false;
function schedule() {
  if (queued) return;
  queued = true;
  setTimeout(() => { queued = false; void refresh(); }, 150);
}

new EventSource('/api/overlay/events').onmessage = schedule;
void refresh();

root.addEventListener('mousedown', (e) => {
  if (e.button === 0 && !locked) void getCurrentWindow().startDragging();
});
root.addEventListener('wheel', (e) => {
  if (!locked && e.deltaY !== 0) void invoke('overlay_scale', { step: e.deltaY < 0 ? 1 : -1 });
}, { passive: true });
window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  void invoke('overlay_menu');
});
