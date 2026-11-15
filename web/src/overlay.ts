// The in-game counter, drawn in a transparent, always-on-top Tauri window (desktop/src/overlay.rs).
// It never touches the game: it is an ordinary desktop window, so it shows over Fortnite in windowed and borderless
// modes only. Unlocked: drag to move, wheel to resize, right-click for the tray menu. Locked: the shell makes the
// window ignore the mouse. Same colours as the OBS page (gold = personal, cyan = squad, pink = session).
//
// What it shows, besides the figures:
// - "Match in progress" while Fortnite records a replay, "Reading the replay" while the finished one settles;
// - every figure rolls to its new value like an odometer, with a "+N" when it grows;
// - when a match the player was seen playing gets counted: a result card (chests of the match, placement, the
//   treasures of its replay, popping in one by one), which leaves by itself.
//
// This page only reads (the open /api/overlay feed, plus /api/settings for the language). Every change (position,
// size, lock) goes through the shell, which owns the window. The DOM is built once and updated in place, so the
// animations survive the feed's refreshes; the window is resized from a ResizeObserver, and entrances and exits
// animate transforms only, so the window changes size once per change, not once per frame.
import { invoke } from '@tauri-apps/api/core';
import { getCurrentWindow } from '@tauri-apps/api/window';
import '@fontsource-variable/bricolage-grotesque';
import './overlay.css';

interface OverlaySettings {
  enabled: boolean; locked: boolean; opacity: number; scale: number; showPersonal: boolean; showSquad: boolean;
  showSession: boolean; showLastMatch: boolean; showMembers: boolean;
}
interface OverlayMember { name: string; chests: number; online: boolean }
interface TreasureTag { kind: string; item?: string | null; name?: string | null; prizeKey?: string | null; golden: boolean; team: boolean }
interface OverlayData {
  personal: number; squad?: number | null; squadScope: string; session: number; lastMatch?: number | null;
  lastMatchPartial: boolean; members: OverlayMember[]; settings: OverlaySettings;
  live?: boolean; reading?: boolean; lastMatchId?: string | null; lastChests?: number | null; lastPlacement?: number | null;
  lastTotalPlayers?: number | null; lastEliminations?: number | null; lastTreasures?: TreasureTag[] | null;
}

// ---- words ----------------------------------------------------------------------------------------------------

const WORDS = {
  en: {
    chests: 'Chests', squad: 'Squad', together: 'Squad together', session: 'Session', last: 'Last match',
    live: 'Match in progress', reading: 'Reading the replay', over: 'Match over', victory: 'Victory Royale',
    chestsWord: 'chests', chestWord: 'chest', elims: '{n} elim.', elim: '1 elim.', total: 'total {n}',
    loot: 'Treasures', noLoot: 'No treasure this time', team: 'team',
    unknown: 'Treasure (unknown type)', golden: 'Golden chest reward',
    'kind.outfit': 'Gold Outfit', 'kind.pirate-shop': 'Pirate Shop Access', 'kind.car': 'Vehicle & Decal',
    'kind.pickaxe': 'Pickaxe or Back Bling', 'kind.jam': 'Jam Track', 'kind.loading': 'Loading Screen',
    'gizmo.extractor': 'Portable extractor', 'gizmo.finder': 'Spirit locator', 'gizmo.locator': 'Lucky locator',
    'gizmo.fireworks': 'Golden fireworks', 'gizmo.accelerator': 'Extraction accelerator',
  },
  fr: {
    chests: 'Coffres', squad: 'Escouade', together: 'Escouade ensemble', session: 'Session', last: 'Dernière partie',
    live: 'Partie en cours', reading: 'Lecture du replay', over: 'Partie terminée', victory: 'Victoire royale',
    chestsWord: 'coffres', chestWord: 'coffre', elims: '{n} élim.', elim: '1 élim.', total: 'total {n}',
    loot: 'Trésors', noLoot: 'Pas de trésor cette fois', team: 'équipe',
    unknown: 'Trésor (type inconnu)', golden: 'Récompense de coffre doré',
    'kind.outfit': 'Tenue en or', 'kind.pirate-shop': 'Accès à la boutique pirate', 'kind.car': 'Véhicule et autocollant',
    'kind.pickaxe': 'Pioche ou sac à dos', 'kind.jam': 'Piste musicale', 'kind.loading': 'Écran de chargement',
    'gizmo.extractor': 'Extracteur portable', 'gizmo.finder': 'Localisateur d’esprit', 'gizmo.locator': 'Localisateur chanceux',
    'gizmo.fireworks': 'Feu d’artifice doré', 'gizmo.accelerator': 'Accélérateur d’extraction',
  },
} as const;
type Word = keyof typeof WORDS.en;

let language: 'en' | 'fr' = 'en';
const T = (k: Word | string, n?: number | string): string => {
  const w = (WORDS[language] as Record<string, string>)[k] ?? (WORDS.en as Record<string, string>)[k] ?? k;
  return n == null ? w : w.replace('{n}', String(n));
};
const N = (v: number) => v.toLocaleString(language === 'fr' ? 'fr-FR' : 'en-US');
const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls = '', text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function svg(paths: string, cls = 'ico'): SVGSVGElement {
  const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  s.setAttribute('viewBox', '0 0 24 24');
  s.setAttribute('class', cls);
  s.setAttribute('aria-hidden', 'true');
  s.innerHTML = paths;
  return s;
}
const ICON = {
  chest: '<path d="M4 10a4 4 0 0 1 4-4h8a4 4 0 0 1 4 4v1H4z"/><path d="M4 11h16v7a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z"/><path d="M11 10h2v4h-2z" class="lock"/>',
  squad: '<circle cx="9" cy="8" r="3.2"/><path d="M3 19c0-3.3 2.7-5.5 6-5.5s6 2.2 6 5.5z"/><circle cx="17" cy="9" r="2.6"/><path d="M15.8 13.6c3 .2 5.2 2.2 5.2 5.4h-4.2"/>',
  session: '<path d="M12 3c1 3.5 5 5.4 5 10a5 5 0 0 1-10 0c0-2.3 1.1-3.6 2.2-4.8.2 1.6.9 2.6 1.9 3C10.6 8.2 11.2 5.5 12 3z"/>',
  last: '<path d="M6 21V4h1v1.2C9.3 4 11 4.6 12.6 5.3c1.7.7 3.3 1.2 5.4.2v8.3c-2.1 1-3.7.5-5.4-.2C11 12.9 9.3 12.3 7 13.5V21z"/>',
  gem: '<path d="M7 4h10l4 5-9 11L3 9z"/><path d="M3 9h18M9 4l3 5 3-5M12 9v11" class="facet"/>',
  crown: '<path d="M3 8l4.5 3.5L12 5l4.5 6.5L21 8l-2 10H5z"/>',
};

// ---- rolling numbers ------------------------------------------------------------------------------------------

const rolling = new Set<Odometer>();
let rollFrame = 0;
function rollTick(now: number) {
  for (const o of [...rolling]) o.frame(now);
  rollFrame = rolling.size > 0 ? requestAnimationFrame(rollTick) : 0;
}

const digitCount = (v: number) => (v < 1 ? 1 : Math.floor(Math.log10(v)) + 1);

/** Where the strip of one digit stands: the units turn continuously, a higher digit only while those below it pass 9 → 0. */
function stripPosition(v: number, place: number): number {
  if (place === 0) return v % 10;
  const m = Math.pow(10, place);
  const digit = Math.floor(v / m) % 10;
  return digit + Math.max(0, (v % m) - (m - 1));
}

/** A number drawn as one strip of digits per place, which rolls from one value to the next. */
class Odometer {
  readonly el = el('span', 'odo');
  private target: number | null = null;
  private shown = 0;
  private from = 0;
  private start = 0;
  private duration = 0;
  private strips: HTMLElement[] = [];
  private slots: HTMLElement[] = [];
  private seps: (HTMLElement | null)[] = [];
  private readonly suffixEl = el('span', 'odo-fix');
  private sepChar = '';
  onDone?: () => void;

  get value() { return this.target; }

  set(v: number | null, suffix = '', animate = true, minDuration = 0) {
    this.suffixEl.textContent = suffix;
    if (v == null || v < 0 || !Number.isFinite(v)) {
      rolling.delete(this);
      this.target = null;
      this.strips = [];
      this.el.replaceChildren(el('span', 'odo-fix', v == null ? '–' : N(v)), this.suffixEl);
      return;
    }
    const sep = language === 'fr' ? ' ' : ',';
    if (this.target == null || !animate || reducedMotion() || (v === this.target && !rolling.has(this))) {
      rolling.delete(this);
      this.target = this.shown = v;
      this.build(digitCount(v), sep);
      this.paint(v);
      if (animate && this.onDone) this.onDone();
      return;
    }
    if (v === this.target) return;
    this.from = this.shown;
    this.target = v;
    this.start = performance.now();
    const delta = Math.abs(v - this.from);
    this.duration = Math.max(minDuration, Math.min(2400, 650 + 230 * Math.log2(1 + delta)));
    this.build(Math.max(digitCount(this.from), digitCount(v)), sep);
    rolling.add(this);
    if (!rollFrame) rollFrame = requestAnimationFrame(rollTick);
  }

  frame(now: number) {
    const t = Math.min(1, (now - this.start) / this.duration);
    const eased = 1 - Math.pow(1 - t, 4);
    const to = this.target ?? 0;
    this.shown = this.from + (to - this.from) * eased;
    if (t >= 1) {
      rolling.delete(this);
      this.shown = to;
      this.build(digitCount(to), this.sepChar);
      this.paint(to);
      this.el.classList.remove('rolling');
      this.onDone?.();
      return;
    }
    this.el.classList.add('rolling');
    this.paint(this.shown);
  }

  private build(places: number, sep: string) {
    if (this.strips.length === places && this.sepChar === sep) return;
    this.sepChar = sep;
    const kids: Node[] = [];
    const strips: HTMLElement[] = [], slots: HTMLElement[] = [], seps: (HTMLElement | null)[] = [];
    for (let p = places - 1; p >= 0; p--) {
      const slot = el('span', 'slot');
      const strip = el('span', 'strip');
      for (let d = 0; d <= 10; d++) strip.append(el('span', '', String(d % 10)));
      slot.append(strip);
      kids.push(slot);
      strips[p] = strip;
      slots[p] = slot;
      seps[p] = null;
      if (p > 0 && p % 3 === 0) {
        const s = el('span', 'sep', sep);
        kids.push(s);
        seps[p] = s;
      }
    }
    kids.push(this.suffixEl);
    this.strips = strips;
    this.slots = slots;
    this.seps = seps;
    this.el.replaceChildren(...kids);
  }

  private paint(v: number) {
    for (let p = 0; p < this.strips.length; p++) {
      this.strips[p].style.transform = `translateY(${(-stripPosition(v, p) * 100) / 11}%)`;
      // A place the number has not reached yet (rolling 98 → 104): a faint zero.
      const lead = p > 0 && v < Math.pow(10, p) - 1;
      this.slots[p].classList.toggle('lead', lead);
      this.seps[p]?.classList.toggle('lead', lead);
    }
  }
}

// ---- confetti -------------------------------------------------------------------------------------------------

interface Bit { x: number; y: number; vx: number; vy: number; r: number; spin: number; a: number; w: number; h: number; c: string; life: number }

/** A short burst of confetti on a canvas laid over an element; stops drawing once the last bit is gone. */
class Confetti {
  readonly canvas = el('canvas', 'confetti');
  private bits: Bit[] = [];
  private frame = 0;
  private last = 0;

  burst(x: number, y: number, count: number, colors: string[], power = 1) {
    if (reducedMotion()) return;
    const k = window.devicePixelRatio || 1;
    const box = this.canvas.getBoundingClientRect();
    if (this.canvas.width !== Math.round(box.width * k) || this.canvas.height !== Math.round(box.height * k)) {
      this.canvas.width = Math.round(box.width * k);
      this.canvas.height = Math.round(box.height * k);
    }
    for (let i = 0; i < count; i++) {
      const angle = -Math.PI / 2 + (Math.random() - 0.5) * Math.PI * 1.25;
      const speed = (180 + Math.random() * 320) * power;
      this.bits.push({
        x, y, vx: Math.cos(angle) * speed, vy: Math.sin(angle) * speed, r: Math.random() * Math.PI, spin: (Math.random() - 0.5) * 14,
        a: 1, w: 3 + Math.random() * 4, h: 5 + Math.random() * 6, c: colors[i % colors.length], life: 0.9 + Math.random() * 0.9,
      });
    }
    if (!this.frame) {
      this.last = performance.now();
      this.frame = requestAnimationFrame((t) => this.tick(t));
    }
  }

  private tick(now: number) {
    const dt = Math.min(0.05, (now - this.last) / 1000);
    this.last = now;
    const ctx = this.canvas.getContext('2d');
    const k = window.devicePixelRatio || 1;
    if (!ctx) return;
    ctx.setTransform(k, 0, 0, k, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    this.bits = this.bits.filter((b) => (b.life -= dt) > 0);
    for (const b of this.bits) {
      b.vy += 620 * dt;
      b.vx *= 1 - 1.6 * dt;
      b.vy *= 1 - 1.1 * dt;
      b.x += b.vx * dt;
      b.y += b.vy * dt;
      b.r += b.spin * dt;
      ctx.save();
      ctx.globalAlpha = Math.min(1, b.life * 2);
      ctx.translate(b.x, b.y);
      ctx.rotate(b.r);
      ctx.scale(1, Math.cos(b.r * 1.7));
      ctx.fillStyle = b.c;
      ctx.fillRect(-b.w / 2, -b.h / 2, b.w, b.h);
      ctx.restore();
    }
    this.frame = this.bits.length > 0 ? requestAnimationFrame((t) => this.tick(t)) : 0;
  }

  stop() {
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.bits = [];
  }
}

// ---- the page -------------------------------------------------------------------------------------------------

const root = document.getElementById('overlay')!;
let locked = false;

const hud = el('div', 'hud');
const status = el('div', 'status');
const statusDot = el('span', 'status-dot');
const statusText = el('span', 'status-text');
const statusBars = el('span', 'status-bars');
for (let i = 0; i < 4; i++) statusBars.append(el('i'));
status.append(statusDot, statusText, statusBars);
const stats = el('div', 'stats');
hud.append(status, stats);
const crew = el('div', 'crew');
root.append(hud, crew);

type CellKey = 'personal' | 'squad' | 'session' | 'last';
interface Cell { el: HTMLElement; label: HTMLElement; odo: Odometer }
const cells = new Map<CellKey, Cell>();
const CELL_ORDER: CellKey[] = ['personal', 'squad', 'session', 'last'];
const CELL_STYLE: Record<CellKey, { color: string; icon: string }> = {
  personal: { color: 'gold', icon: ICON.chest },
  squad: { color: 'cyan', icon: ICON.squad },
  session: { color: 'pink', icon: ICON.session },
  last: { color: 'text', icon: ICON.last },
};

function cell(key: CellKey): Cell {
  let c = cells.get(key);
  if (!c) {
    const box = el('div', `cell ${CELL_STYLE[key].color}`);
    const head = el('div', 'cell-head');
    const label = el('span', 'label');
    head.append(svg(CELL_STYLE[key].icon), label);
    const odo = new Odometer();
    odo.el.classList.add('value');
    box.append(head, odo.el);
    c = { el: box, label, odo };
    cells.set(key, c);
  }
  return c;
}

/** The cell glows and a "+N" floats up from it. */
function bump(target: HTMLElement, gain: number) {
  if (reducedMotion()) return;
  target.classList.remove('bump');
  void target.offsetWidth; // restart the animation
  target.classList.add('bump');
  const float = el('span', 'gain', `+${N(gain)}`);
  float.addEventListener('animationend', () => float.remove());
  target.append(float);
}

const memberRows = new Map<string, { el: HTMLElement; name: HTMLElement; odo: Odometer }>();

let previous: OverlayData | null = null;
let liveSeenAt = 0;
let mode: 'idle' | 'live' | 'reading' = 'idle';

function render(d: OverlayData) {
  const s = d.settings;
  const first = previous == null;
  locked = s.locked;
  document.documentElement.style.setProperty('--k', String(Math.min(250, Math.max(60, s.scale)) / 100));
  root.style.opacity = String(Math.min(100, Math.max(20, s.opacity)) / 100);
  root.classList.toggle('unlocked', !s.locked);
  document.documentElement.lang = language;

  // State of play.
  if (d.live) liveSeenAt = Date.now();
  const nextMode = d.live ? 'live' : d.reading && Date.now() - liveSeenAt < 30 * 60_000 ? 'reading' : 'idle';
  if (nextMode !== mode) {
    mode = nextMode;
    hud.classList.toggle('is-live', mode === 'live');
    hud.classList.toggle('is-reading', mode === 'reading');
    status.classList.toggle('shown', mode !== 'idle');
    if (mode === 'live') dismissResult();
  }
  statusText.textContent = mode === 'live' ? T('live') : mode === 'reading' ? T('reading') : '';

  // The figures.
  const wanted: [CellKey, string, number | null, string][] = [];
  if (s.showPersonal) wanted.push(['personal', T('chests'), d.personal, '']);
  if (s.showSquad && d.squad != null) wanted.push(['squad', d.squadScope === 'shared' ? T('together') : T('squad'), d.squad, '']);
  if (s.showSession) wanted.push(['session', T('session'), d.session, '']);
  // "+" = some teammates have not uploaded this match yet: the figure can still grow.
  if (s.showLastMatch) wanted.push(['last', T('last'), d.lastMatch ?? null, d.lastMatchPartial ? '+' : '']);
  const keep = new Set(wanted.map((w) => w[0]));
  for (const [key, c] of cells) if (!keep.has(key)) { c.el.remove(); cells.delete(key); }
  for (const [key, label, value, suffix] of wanted) {
    const c = cell(key);
    c.label.textContent = label;
    const before = c.odo.value;
    const isNew = !c.el.isConnected;
    c.odo.set(value, suffix, !first && !isNew);
    if (!first && !isNew && before != null && value != null && value > before && key !== 'last') bump(c.el, value - before);
  }
  const ordered = CELL_ORDER.filter((k) => cells.has(k)).map((k) => cells.get(k)!.el);
  if (ordered.some((e, i) => stats.children[i] !== e)) stats.replaceChildren(...ordered);
  hud.hidden = cells.size === 0 && mode === 'idle';

  // The squad, member by member.
  const members = s.showMembers ? d.members : [];
  const names = new Set(members.map((m) => m.name));
  for (const [name, row] of memberRows) if (!names.has(name)) { row.el.remove(); memberRows.delete(name); }
  const rows: HTMLElement[] = [];
  for (const m of members) {
    let row = memberRows.get(m.name);
    const isNew = !row;
    if (!row) {
      const part = el('span', 'member');
      const name = el('span', 'name', m.name);
      const odo = new Odometer();
      odo.el.classList.add('chests');
      part.append(el('span', 'presence'), name, odo.el);
      row = { el: part, name, odo };
      memberRows.set(m.name, row);
    }
    row.el.classList.toggle('offline', !m.online);
    const before = row.odo.value;
    row.odo.set(m.chests, '', !first && !isNew);
    if (!first && !isNew && before != null && m.chests > before) bump(row.el, m.chests - before);
    rows.push(row.el);
  }
  if (rows.length !== crew.children.length || rows.some((e, i) => crew.children[i] !== e)) crew.replaceChildren(...rows);
  crew.hidden = rows.length === 0;

  // A new match was counted while the player was seen playing: celebrate it.
  if (!first && d.lastMatchId && d.lastMatchId !== previous!.lastMatchId && Date.now() - liveSeenAt < 60 * 60_000) {
    liveSeenAt = 0; // one card per match
    showResult(d);
  }
  previous = d;
  reportSize();
}

// ---- the end-of-match card ------------------------------------------------------------------------------------

let result: { el: HTMLElement; confetti: Confetti; timers: number[] } | null = null;

function treasureName(tag: TreasureTag): { name: string; kind?: string } {
  if (tag.name) {
    if (tag.kind === 'pirate-shop') return { name: T('kind.pirate-shop') };
    return { name: tag.name, kind: T(`kind.${tag.kind}`) };
  }
  if (tag.kind === 'gizmo' && tag.item) return { name: T(`gizmo.${tag.item}`), kind: 'Gizmo' };
  if (tag.kind === 'golden') return { name: T('golden') };
  return { name: T('unknown') };
}

function lootRow(tag: TreasureTag): HTMLElement {
  const row = el('div', `loot${tag.golden ? ' golden' : ''}${tag.team ? ' team' : ''}`);
  const thumb = el('span', 'thumb');
  if (tag.prizeKey) {
    const img = el('img');
    img.src = `/treasure-img/${encodeURIComponent(tag.prizeKey)}`;
    img.alt = '';
    img.draggable = false;
    img.onerror = () => img.replaceWith(svg(ICON.gem, 'ico gem'));
    thumb.append(img);
  } else {
    thumb.append(svg(ICON.gem, 'ico gem'));
  }
  const { name, kind } = treasureName(tag);
  const text = el('span', 'loot-text');
  text.append(el('span', 'loot-name', name));
  if (kind || tag.team) {
    const sub = el('span', 'loot-kind', kind ?? '');
    if (tag.team) sub.append(el('span', 'badge', T('team')));
    text.append(sub);
  }
  row.append(thumb, text, el('span', 'stars', tag.golden ? '★★' : '★'));
  return row;
}

function showResult(d: OverlayData) {
  dismissResult(true);
  const chests = d.lastChests ?? 0;
  const victory = d.lastPlacement === 1;
  const treasures = (d.lastTreasures ?? []).slice().sort((a, b) => Number(b.golden) - Number(a.golden) || Number(a.team) - Number(b.team));
  const anyGolden = treasures.some((t) => t.golden);

  const card = el('div', `result${victory ? ' victory' : ''}${anyGolden ? ' jackpot' : ''}`);
  const confetti = new Confetti();
  const timers: number[] = [];
  result = { el: card, confetti, timers };

  const head = el('div', 'r-head');
  const title = el('span', 'r-title');
  if (victory) title.append(svg(ICON.crown, 'ico crown'));
  title.append(document.createTextNode(victory ? T('victory') : T('over')));
  head.append(title);
  if (d.lastPlacement != null) {
    const place = el('span', 'r-place', `#${d.lastPlacement}`);
    if (d.lastTotalPlayers) place.append(el('small', '', `/${d.lastTotalPlayers}`));
    head.append(place);
  }

  const gain = el('div', 'r-gain');
  const plus = el('span', 'r-plus', '+');
  const odo = new Odometer();
  odo.el.classList.add('r-value');
  odo.set(0, '', false);
  const unit = el('span', 'r-unit', chests === 1 ? T('chestWord') : T('chestsWord'));
  gain.append(plus, odo.el, unit);

  const facts: string[] = [];
  if (d.lastEliminations != null) facts.push(d.lastEliminations === 1 ? T('elim') : T('elims', d.lastEliminations));
  facts.push(T('total', N(d.personal)));
  const sub = el('div', 'r-sub', facts.join('  ·  '));

  const loot = el('div', 'r-loot');
  const lootHead = el('div', 'r-loot-head');
  lootHead.append(el('span', '', T('loot')), el('span', 'count', String(treasures.length)));
  loot.append(lootHead);
  const lootRows = treasures.map(lootRow);
  if (lootRows.length === 0) loot.append(el('div', 'r-none', T('noLoot')));
  else loot.append(...lootRows);

  const timer = el('div', 'r-timer');
  const linger = 14_000 + 1_400 * treasures.length;
  timer.style.animationDuration = `${linger}ms`;

  card.append(confetti.canvas, head, gain, sub, loot, timer);
  root.append(card);
  reportSize();

  const at = (ms: number, fn: () => void) => timers.push(window.setTimeout(fn, reducedMotion() ? 0 : ms));
  const burstFrom = (target: Element, count: number, colors: string[], power = 1) => {
    const c = card.getBoundingClientRect();
    const b = target.getBoundingClientRect();
    confetti.burst(b.left - c.left + b.width / 2, b.top - c.top + b.height / 2, count, colors, power);
  };

  // The card lands, the chests roll up, then the treasures come in one by one.
  at(380, () => {
    odo.onDone = () => {
      gain.classList.add('landed');
      if (chests > 0) burstFrom(odo.el, victory ? 70 : 34, victory ? ['#f3c96b', '#ffe7a8', '#fff6dc', '#f593c9'] : ['#f3c96b', '#ffe7a8', '#9d8cff'], victory ? 1.15 : 0.8);
      lootRows.forEach((row, i) => at(260 + i * 420, () => {
        row.classList.add('in');
        if (row.classList.contains('golden')) burstFrom(row.querySelector('.thumb')!, 46, ['#f3c96b', '#ffe7a8', '#fff3cf', '#ffffff'], 0.9);
      }));
      if (lootRows.length === 0) at(200, () => loot.classList.add('in'));
      odo.onDone = undefined;
    };
    odo.set(chests, '', true, 900);
  });
  timers.push(window.setTimeout(() => dismissResult(), linger));
}

function dismissResult(immediate = false) {
  const r = result;
  if (!r) return;
  result = null;
  r.timers.forEach(clearTimeout);
  const done = () => {
    r.confetti.stop();
    r.el.remove();
    reportSize();
  };
  if (immediate || reducedMotion()) return done();
  r.el.classList.add('leaving');
  r.el.addEventListener('animationend', (e) => { if (e.target === r.el) done(); });
  window.setTimeout(done, 900); // in case the animation never runs
}

// ---- window size ------------------------------------------------------------------------------------------------

let sizeQueued = false;
function reportSize() {
  if (sizeQueued) return;
  sizeQueued = true;
  requestAnimationFrame(() => {
    sizeQueued = false;
    const box = root.getBoundingClientRect();
    const hasContent = !hud.hidden || !crew.hidden || result != null;
    // The shell sizes the window to the content and shows it only when there is something to show.
    invoke('overlay_layout', { width: Math.ceil(box.width), height: Math.ceil(box.height), hasContent }).catch(() => { /* shell gone, or a browser */ });
  });
}
new ResizeObserver(reportSize).observe(root);

// ---- feed -------------------------------------------------------------------------------------------------------

async function refresh() {
  try {
    const token = (window as unknown as { __CC_TOKEN?: string }).__CC_TOKEN;
    if (token) {
      const settings = await fetch('/api/settings', { headers: { 'X-CC-Token': token } }).then((r) => r.json());
      language = settings.language === 'fr' ? 'fr' : 'en';
    } else {
      const asked = new URLSearchParams(location.search).get('lang');
      if (asked === 'fr' || asked === 'en') language = asked;
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
  if (e.button !== 0 || locked) return;
  try {
    void getCurrentWindow().startDragging().catch(() => { /* shell gone */ });
  } catch {
    // a browser (DevHost preview): no window to drag
  }
});
root.addEventListener('wheel', (e) => {
  if (!locked && e.deltaY !== 0) void invoke('overlay_scale', { step: e.deltaY < 0 ? 1 : -1 }).catch(() => { /* a browser */ });
}, { passive: true });
window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  void invoke('overlay_menu').catch(() => { /* a browser */ });
});
