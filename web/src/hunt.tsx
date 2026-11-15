import { useState } from 'react';
import { Gem } from 'lucide-react';
import type { Locale } from './i18n';
import type { TreasureTag } from './api';

export type Verdict = 'certain' | 'vlikely' | 'likely' | 'possible' | 'unlikely' | 'vunlikely';

/** A word for a chance. */
export function verdict(p: number): Verdict {
  const v = p * 100;
  return v >= 99.95 ? 'certain' : v >= 95 ? 'vlikely' : v >= 75 ? 'likely' : v >= 25 ? 'possible' : v >= 5 ? 'unlikely' : 'vunlikely';
}

/** A chance with enough decimals to mean something (0.0064%, 2.8%, 63%), never "100%". */
export function percent(p: number, lang: 'en' | 'fr'): string {
  const v = p * 100;
  const sep = lang === 'fr' ? ' %' : '%';
  const fmt = (x: number, d: number) => x.toLocaleString(lang === 'fr' ? 'fr-FR' : 'en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
  if (v >= 99.95) return `>${lang === 'fr' ? ' ' : ''}${fmt(99.9, 1)}${sep}`;
  const decimals = v >= 10 ? 1 : v <= 0 ? 0 : Math.min(8, Math.max(1, Math.ceil(-Math.log10(v)) + 1));
  return fmt(v, decimals) + sep;
}

/** "never had it: 1 in 5.2 × 10²⁷", from the log of a tiny chance. */
export function oneInHuge(logNever: number, n: (v: number) => string): string {
  const log10 = -logNever / Math.LN10;
  if (log10 < 9) return n(Math.round(Math.exp(-logNever)));
  const exp = Math.floor(log10);
  const sup = '⁰¹²³⁴⁵⁶⁷⁸⁹';
  return `${Math.pow(10, log10 - exp).toFixed(1)} × 10${[...String(exp)].map((d) => sup[Number(d)]).join('')}`;
}

/** How many on average ("≈ 4.3", "< 0.1"). */
export function expected(avg: number, lang: 'en' | 'fr') {
  const f = (x: number, d: number) => x.toLocaleString(lang === 'fr' ? 'fr-FR' : 'en-US', { maximumFractionDigits: d, minimumFractionDigits: d });
  return avg < 0.05 ? `< ${f(0.1, 1)}` : `≈ ${f(avg, avg >= 10 ? 0 : 1)}`;
}

/** "6d 21h 37m" (or "6 j 21 h 37 min"). */
export function countdown(ms: number, lang: 'en' | 'fr'): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const u = lang === 'fr' ? { d: ' j', h: ' h', m: ' min', s: ' s' } : { d: 'd', h: 'h', m: 'm', s: 's' };
  if (d > 0) return `${d}${u.d} ${h}${u.h} ${m}${u.m}`;
  if (h > 0) return `${h}${u.h} ${m}${u.m}`;
  return `${m}${u.m} ${String(sec).padStart(2, '0')}${u.s}`;
}

export function ordinal(p: number, lang: 'en' | 'fr') {
  if (lang === 'fr') return p === 1 ? '1er' : `${p}e`;
  const s = p % 100 >= 11 && p % 100 <= 13 ? 'th' : ({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[p % 10] ?? 'th';
  return `${p}${s}`;
}

/** Name of a treasure, in the chosen language. */
export function treasureName(tag: TreasureTag, t: Locale['t']): string {
  if (tag.name) {
    if (tag.kind === 'pirate-shop') return t('hunt.kind.pirate-shop');
    return `${tag.name} (${t(`hunt.kind.${tag.kind}` as never)})`;
  }
  if (tag.kind === 'gizmo' && tag.item) return t(`gizmo.${tag.item}` as never);
  if (tag.kind === 'golden') return t('treasure.golden', { id: tag.item ?? '?' });
  return t('treasure.unknown');
}

/** ★★ for a golden chest prize (named for sure), ★ for the rest. */
export function Stars({ golden }: { golden: boolean }) {
  return golden ? <span className="stars gold" aria-hidden="true">★★</span> : <span className="stars violet" aria-hidden="true">★</span>;
}

export function TreasureLabel({ tag, t }: { tag: TreasureTag; t: Locale['t'] }) {
  return <span className={`tlabel${tag.golden ? ' golden' : ''}${tag.team ? ' team' : ''}`}><Stars golden={tag.golden} /> <span>{treasureName(tag, t)}</span></span>;
}

/** A prize picture; a gem stands in when there is none (or it cannot be loaded). */
export function Thumb({ src, className, alt = '' }: { src?: string; className?: string; alt?: string }) {
  const [failed, setFailed] = useState(false);
  return (
    <span className={`thumb${className ? ' ' + className : ''}`}>
      {src && !failed ? <img src={src} alt={alt} loading="lazy" draggable={false} onError={() => setFailed(true)} /> : <Gem size={20} />}
    </span>
  );
}
