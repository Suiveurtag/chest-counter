import { useEffect, useId, useMemo, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { animate, motion } from 'motion/react';
import { useLocale } from './i18n';
import type { TimelinePoint } from './api';
import { reducedMotion } from './fx';

/**
 * A figure that counts up to its value: from 0 the first time it appears (the entrance), then from its previous
 * value whenever it changes, with a short glow when it went up.
 */
export function Count({ value, className, from0 = true, format }: { value: number; className?: string; from0?: boolean; format?: (v: number) => string }) {
  const { n } = useLocale();
  const fmtRef = useRef(format ?? n);
  fmtRef.current = format ?? n;
  const ref = useRef<HTMLSpanElement>(null);
  const shown = useRef<number | null>(null);
  useEffect(() => {
    const fmt = fmtRef.current;
    const custom = format !== undefined;
    const from = shown.current ?? (from0 ? 0 : value);
    const first = shown.current === null;
    shown.current = value;
    const el = ref.current;
    if (!el) return;
    if (from === value || reducedMotion()) {
      el.textContent = fmt(value);
      return;
    }
    if (!first && value > from) {
      el.classList.remove('bump');
      void el.offsetWidth;
      el.classList.add('bump');
    }
    const controls = animate(from, value, {
      duration: first ? Math.min(1.6, 0.6 + Math.log10(Math.abs(value - from) + 1) * 0.3) : Math.min(1.2, 0.35 + Math.abs(value - from) / 120),
      ease: [0.16, 1, 0.3, 1],
      onUpdate: (v) => { el.textContent = custom ? fmtRef.current(v) : fmtRef.current(Number.isInteger(value) ? Math.round(v) : Math.round(v * 10) / 10); },
    });
    return () => { controls.stop(); el.textContent = fmtRef.current(value); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, n, from0]);
  // The text is written by the effect from then on: React renders the first value only.
  const [initial] = useState(() => fmtRef.current(from0 && !reducedMotion() ? 0 : value));
  return <span ref={ref} className={`count-num${className ? ' ' + className : ''}`}>{initial}</span>;
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
      {segments.map((s, i) => s.missing
        ? <i key={s.key} className="missing" title={s.label} style={{ '--i': i } as CSSProperties} />
        : <i key={s.key + (changed.has(s.key) ? s.value : '')} className={`c${s.color}${changed.has(s.key) ? ' flash' : ''}`}
            style={{ flexGrow: total === 0 ? 1 : Math.max(s.value, total * 0.012), flexBasis: 0, '--i': i } as CSSProperties} title={`${s.label}: ${s.value}`} />)}
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

/** Chests per day or week. Bars grow in one after the other; hovering one lifts it and reads it out in a bubble. */
export function Bars({ points, tall }: { points: TimelinePoint[]; tall?: boolean }) {
  const { n, t, lang } = useLocale();
  const [hover, setHover] = useState<number | null>(null);
  const max = Math.max(1, ...points.map((p) => p.chests));
  const best = points.reduce((b, p, i) => (p.chests > (points[b]?.chests ?? -1) ? i : b), 0);
  const fmt = useMemo(() => new Intl.DateTimeFormat(lang === 'fr' ? 'fr-FR' : undefined, { day: 'numeric', month: 'short' }), [lang]);
  const label = (date: string) => fmt.format(new Date(date + 'T12:00:00'));
  const shown = hover != null ? points[hover] : undefined;
  // A new range replays the growth.
  const key = points.length ? `${points[0].date}-${points.length}` : 'none';
  if (points.length === 0) return <div className="empty"><span>{t('insights.empty')}</span></div>;
  return (
    <div className="chart">
      <div className="readout">
        {shown
          ? <motion.span key={hover} initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.18 }}>
              <b>{n(shown.chests)}</b> {t('matches.chests').toLowerCase()}, {shown.matches} {t('common.matches')}, {label(shown.date)}
            </motion.span>
          : <span className="mute">{t('insights.busiestDay')}: <b>{n(points[best].chests)}</b> · {label(points[best].date)}</span>}
      </div>
      <div key={key} className={`bars${tall ? ' tall' : ''}${hover != null ? ' hovering' : ''}`} onMouseLeave={() => setHover(null)}>
        {points.map((p, i) => (
          <div key={p.date} className={`bar${p.chests === 0 ? ' zero' : ''}${hover === i ? ' sel' : ''}${i === best && p.chests > 0 ? ' top' : ''}`}
            style={{ '--h': `${(p.chests / max) * 100}%`, '--i': i } as CSSProperties} onMouseEnter={() => setHover(i)}>
            {hover === i && p.chests > 0 && <span className="bar-tip">{n(p.chests)}</span>}
          </div>
        ))}
      </div>
      <div className="axis"><span>{label(points[0].date)}</span><span>{label(points[points.length - 1].date)}</span></div>
    </div>
  );
}

/** Running total as an area: the line draws itself, the fill rises, and a crosshair follows the pointer. */
export function Area({ points }: { points: TimelinePoint[] }) {
  const { n, lang } = useLocale();
  const [hover, setHover] = useState<number | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  if (points.length < 2) return null;
  let sum = 0;
  const values = points.map((p) => (sum += p.chests));
  const w = 1000, h = 200;
  const x = (i: number) => (i / (values.length - 1)) * w;
  const y = (v: number) => h - (v / Math.max(1, sum)) * (h - 6);
  const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
  const fmt = new Intl.DateTimeFormat(lang === 'fr' ? 'fr-FR' : undefined, { day: 'numeric', month: 'short' });
  const onMove = (e: ReactPointerEvent) => {
    const r = svg.current!.getBoundingClientRect();
    setHover(Math.max(0, Math.min(values.length - 1, Math.round(((e.clientX - r.left) / r.width) * (values.length - 1)))));
  };
  return (
    <div className="chart">
      <div className="readout">
        {hover != null
          ? <><b>{n(values[hover])}</b> · {fmt.format(new Date(points[hover].date + 'T12:00:00'))}</>
          : <b><Count value={sum} /></b>}
      </div>
      <div className="area-wrap" onPointerLeave={() => setHover(null)}>
        <svg ref={svg} className="area" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ width: '100%', height: 180, display: 'block' }}
          aria-hidden="true" onPointerMove={onMove} key={`${points[0].date}-${points.length}`}>
          <defs>
            <linearGradient id="areaFill" x1="0" x2="0" y1="0" y2="1">
              <stop offset="0%" stopColor="var(--gold)" stopOpacity="0.32" />
              <stop offset="100%" stopColor="var(--gold)" stopOpacity="0" />
            </linearGradient>
          </defs>
          <path className="fill" d={`${line} L${w},${h} L0,${h} Z`} fill="url(#areaFill)" />
          <path className="stroke" d={line} pathLength={1} />
          {hover != null && <line className="cross" x1={x(hover)} x2={x(hover)} y1={0} y2={h} />}
        </svg>
        {hover != null && <span className="area-dot" style={{ left: `${(x(hover) / w) * 100}%`, top: `${(y(values[hover]) / h) * 180}px` }} />}
      </div>
    </div>
  );
}

/** A switch whose knob springs across. */
export function Switch({ checked, onChange, children, hint }: { checked: boolean; onChange: (v: boolean) => void; children: ReactNode; hint?: string }) {
  return (
    <label className="switch">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>{children}{hint && <small>{hint}</small>}</span>
    </label>
  );
}

/** A segmented control: the highlight slides to the chosen option. */
export function Seg<T extends string>({ value, options, onChange }: { value: T; options: [T, string][]; onChange: (v: T) => void }) {
  const id = useId();
  return (
    <div className="seg" role="group">
      {options.map(([v, label]) => (
        <button key={v} aria-pressed={value === v} onClick={() => onChange(v)}>
          {value === v && <motion.span layoutId={`seg-${id}`} className="seg-pill" transition={{ type: 'spring', stiffness: 420, damping: 34 }} />}
          <span className="seg-label">{label}</span>
        </button>
      ))}
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
