import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { animate } from 'motion';
import { useLocale } from './i18n';
import type { TimelinePoint } from './api';

const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/** A figure that counts from its previous value to the new one when it changes (never on first paint). */
export function Count({ value, className }: { value: number; className?: string }) {
  const { n } = useLocale();
  const ref = useRef<HTMLSpanElement>(null);
  const shown = useRef(value);
  useEffect(() => {
    const from = shown.current;
    shown.current = value;
    if (from === value || !ref.current || reducedMotion()) {
      if (ref.current) ref.current.textContent = n(value);
      return;
    }
    const controls = animate(from, value, {
      duration: Math.min(1.2, 0.35 + Math.abs(value - from) / 120),
      ease: [0.2, 0.8, 0.2, 1],
      onUpdate: (v) => { if (ref.current) ref.current.textContent = n(Math.round(v)); },
    });
    return () => controls.stop();
  }, [value, n]);
  return <span ref={ref} className={className}>{n(value)}</span>;
}

export interface Segment { key: string; label: string; value: number; color: number; missing?: boolean }

/** Who contributed what, to scale. A missing upload is a hatched segment: visibly absent, never a made-up value. */
export function SplitBar({ segments, thin }: { segments: Segment[]; thin?: boolean }) {
  const previous = useRef<Record<string, number>>({});
  const changed = new Set<string>();
  for (const s of segments) {
    if (previous.current[s.key] !== undefined && previous.current[s.key] !== s.value) changed.add(s.key);
  }
  useEffect(() => { previous.current = Object.fromEntries(segments.map((s) => [s.key, s.value])); });
  const total = segments.reduce((sum, s) => sum + (s.missing ? 0 : s.value), 0);
  return (
    <div className={`split${thin ? ' thin' : ''}`} role="img"
      aria-label={segments.map((s) => `${s.label}: ${s.missing ? '?' : s.value}`).join(', ')}>
      {segments.map((s) => s.missing
        ? <i key={s.key} className="missing" title={s.label} />
        : <i key={s.key + (changed.has(s.key) ? s.value : '')} className={`c${s.color}${changed.has(s.key) ? ' flash' : ''}`}
            style={{ flexGrow: total === 0 ? 1 : Math.max(s.value, total * 0.012), flexBasis: 0 }} title={`${s.label}: ${s.value}`} />)}
    </div>
  );
}

export function Legend({ segments }: { segments: Segment[] }) {
  const { n } = useLocale();
  return (
    <div className="legend">
      {segments.map((s) => (
        <span key={s.key}><i className={`swatch c${s.color}`} />{s.label}<b>{s.missing ? '?' : n(s.value)}</b></span>
      ))}
    </div>
  );
}

/** Chests per day or week. Hovering a bar reads it out above the chart. */
export function Bars({ points, tall }: { points: TimelinePoint[]; tall?: boolean }) {
  const { n, t } = useLocale();
  const [hover, setHover] = useState<number | null>(null);
  const max = Math.max(1, ...points.map((p) => p.chests));
  const fmt = useMemo(() => new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' }), []);
  const label = (date: string) => fmt.format(new Date(date + 'T12:00:00'));
  const shown = hover != null ? points[hover] : undefined;
  if (points.length === 0) return <div className="empty"><span>{t('insights.empty')}</span></div>;
  return (
    <div className="chart">
      <div className="readout">
        {shown && <><b>{n(shown.chests)}</b> {t('matches.chests').toLowerCase()}, {shown.matches} {t('common.matches')}, {label(shown.date)}</>}
      </div>
      <div className={`bars${tall ? ' tall' : ''}`} onMouseLeave={() => setHover(null)}>
        {points.map((p, i) => (
          <div key={p.date} className={`${p.chests === 0 ? 'zero' : ''}${hover === i ? ' sel' : ''}`}
            style={{ height: `${(p.chests / max) * 100}%` }} onMouseEnter={() => setHover(i)} />
        ))}
      </div>
      <div className="axis"><span>{label(points[0].date)}</span><span>{label(points[points.length - 1].date)}</span></div>
    </div>
  );
}

/** Running total as an area. */
export function Area({ points }: { points: TimelinePoint[] }) {
  const { n } = useLocale();
  if (points.length < 2) return null;
  let sum = 0;
  const values = points.map((p) => (sum += p.chests));
  const w = 1000, h = 200;
  const x = (i: number) => (i / (values.length - 1)) * w;
  const y = (v: number) => h - (v / Math.max(1, sum)) * (h - 6);
  const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
  return (
    <div className="chart">
      <div className="readout"><b>{n(sum)}</b></div>
      <svg className="area" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ width: '100%', height: 180, display: 'block' }} aria-hidden="true">
        <path className="fill" d={`${line} L${w},${h} L0,${h} Z`} />
        <path className="stroke" d={line} />
      </svg>
    </div>
  );
}

export function Switch({ checked, onChange, children, hint }: { checked: boolean; onChange: (v: boolean) => void; children: ReactNode; hint?: string }) {
  return (
    <label className="switch">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>{children}{hint && <small>{hint}</small>}</span>
    </label>
  );
}

export function Seg<T extends string>({ value, options, onChange }: { value: T; options: [T, string][]; onChange: (v: T) => void }) {
  return (
    <div className="seg" role="group">
      {options.map(([v, label]) => <button key={v} aria-pressed={value === v} onClick={() => onChange(v)}>{label}</button>)}
    </div>
  );
}

/** Runs an async action; shows its error next to the button that started it. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const run = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try { await work(); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; }
    finally { setBusy(false); }
  };
  return { busy, error, run, clear: () => setError(undefined) };
}
