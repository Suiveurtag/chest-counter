import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AnimatePresence, motion } from 'motion/react';
import { ChevronDown, Info, X } from 'lucide-react';
import { useLocale } from './i18n';

/** The ease every entrance uses: fast start, long soft landing. */
export const easeOut = [0.16, 1, 0.3, 1] as const;

export const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/**
 * A number whose digits roll like a slot machine: from 0 when it first appears, then from the old value to the new
 * one. Columns are keyed from the right, so a new thousands digit slides in without disturbing the others.
 */
export function Odometer({ value, className }: { value: number; className?: string }) {
  const { n } = useLocale();
  const text = n(value);
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    let b = 0;
    const a = requestAnimationFrame(() => { b = requestAnimationFrame(() => setArmed(true)); });
    return () => { cancelAnimationFrame(a); cancelAnimationFrame(b); };
  }, []);
  const chars = [...text];
  return (
    <span className={`odo${className ? ' ' + className : ''}`} aria-label={text} role="text">
      {chars.map((ch, i) => {
        const fromRight = chars.length - i;
        if (!/\d/.test(ch)) return <span key={`s${fromRight}`} className="odo-sep" aria-hidden="true">{ch}</span>;
        const digit = armed ? Number(ch) : 0;
        return (
          <span key={`d${fromRight}`} className="odo-col" aria-hidden="true">
            <span className="odo-strip" style={{ transform: `translateY(${-digit * 10}%)`, transitionDelay: `${fromRight * 85}ms` }}>
              {DIGITS.map((d) => <span key={d}>{d}</span>)}
            </span>
          </span>
        );
      })}
    </span>
  );
}
const DIGITS = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

/** The current time, refreshed every <paramref name="ms"/> (countdowns). */
export function useNow(ms = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), ms);
    return () => window.clearInterval(id);
  }, [ms]);
  return now;
}

function readFlag(key: string, fallback: boolean) {
  try { const v = localStorage.getItem(key); return v === null ? fallback : v === '1'; } catch { return fallback; }
}
function writeFlag(key: string, value: boolean) {
  try { localStorage.setItem(key, value ? '1' : '0'); } catch { /* private mode */ }
}

/** A remembered on/off choice (folds, switches): per viewer, best effort. */
export function useStoredFlag(key: string, fallback: boolean): [boolean, (v: boolean) => void] {
  const [v, setV] = useState(() => readFlag(key, fallback));
  return [v, (x: boolean) => { setV(x); writeFlag(key, x); }];
}

/** A section whose title folds it; the body opens and closes with its real height. */
export function Fold({ id, title, info, right, open: openDefault = true, children }: {
  id: string; title: ReactNode; info?: string; right?: ReactNode; open?: boolean; children: ReactNode;
}) {
  const [open, setOpen] = useStoredFlag(`cc-fold-${id}`, openDefault);
  return (
    <section className={`fold${open ? ' open' : ''}`}>
      <div className="fold-head">
        <h2 className="fold-title">
          <button type="button" aria-expanded={open} onClick={() => setOpen(!open)}>
            <ChevronDown size={18} className="chev" />
            <span>{title}</span>
          </button>
          {info && <Tip text={info} />}
        </h2>
        {right}
      </div>
      <AnimatePresence initial={false}>
        {open && (
          <motion.div className="fold-body" key="body"
            initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }}
            transition={{ height: { duration: 0.45, ease: easeOut }, opacity: { duration: 0.25 } }}>
            <div className="fold-inner">{children}</div>
          </motion.div>
        )}
      </AnimatePresence>
    </section>
  );
}

/** A small "i" that explains, in a bubble that pops on hover or focus. */
export function Tip({ text }: { text: string }) {
  return (
    <span className="tip" tabIndex={0} role="note" aria-label={text}>
      <Info size={15} />
      <span className="tip-bubble">{text}</span>
    </span>
  );
}

/** A dialog that grows out of the page, over a blurred backdrop. Escape and the backdrop close it. */
export function Modal({ open, onClose, title, children, wide }: { open: boolean; onClose: () => void; title: string; children: ReactNode; wide?: boolean }) {
  const { t } = useLocale();
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);
  // Rendered at the end of <body>: the animated page wrapper would otherwise trap position: fixed.
  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div className="modal-layer" key="modal" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.22 }}>
          <div className="modal-scrim" onClick={onClose} />
          <motion.div className={`modal${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}
            initial={{ opacity: 0, scale: 0.92, y: 18, filter: 'blur(6px)' }} animate={{ opacity: 1, scale: 1, y: 0, filter: 'blur(0px)' }}
            exit={{ opacity: 0, scale: 0.96, y: 10, filter: 'blur(4px)' }} transition={{ type: 'spring', stiffness: 340, damping: 28 }}>
            <div className="modal-head">
              <h2>{title}</h2>
              <button className="icon-btn spin-hover" onClick={onClose} aria-label={t('common.close')}><X size={18} /></button>
            </div>
            {children}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  );
}

/** Long tables show 10 rows, then 20 more at a time; the choice is remembered per table. */
export function usePager(id: string, total: number, step = 10) {
  const key = `cc-rows-${id}`;
  const [shown, setShown] = useState(() => {
    try { const v = Number(localStorage.getItem(key)); return v >= step ? v : step; } catch { return step; }
  });
  const set = (v: number) => { setShown(v); try { localStorage.setItem(key, String(v)); } catch { /* private */ } };
  return { shown: Math.min(shown, total), more: () => set(shown + step * 2), all: () => set(1e9), less: () => set(step), canLess: shown > step, done: shown >= total, step };
}

export function Pager({ pager, total }: { pager: ReturnType<typeof usePager>; total: number }) {
  const { t } = useLocale();
  if (total <= pager.step) return null;
  return (
    <div className="pager">
      <span className="mute small">{t('pager.count', { shown: pager.shown, total })}</span>
      <div className="actions">
        {!pager.done && <button className="btn" onClick={pager.more}>{t('pager.more')}</button>}
        {!pager.done && <button className="btn" onClick={pager.all}>{t('pager.all')}</button>}
        {pager.canLess && <button className="btn quiet" onClick={pager.less}>{t('pager.less')}</button>}
      </div>
    </div>
  );
}

/**
 * Every button answers a press with a ripple from where it was pressed (one listener for the whole page, so every
 * action of the app has it, present and future).
 */
export function useRipples() {
  useEffect(() => {
    const onDown = (e: PointerEvent) => {
      const target = (e.target as HTMLElement | null)?.closest<HTMLElement>('.btn, .nav button, .seg button, button.row, .icon-btn, .prize, .chip-btn');
      if (!target || reducedMotion()) return;
      const r = target.getBoundingClientRect();
      const size = Math.max(r.width, r.height) * 2.2;
      const dot = document.createElement('span');
      dot.className = 'ripple';
      dot.style.width = dot.style.height = `${size}px`;
      dot.style.left = `${e.clientX - r.left - size / 2}px`;
      dot.style.top = `${e.clientY - r.top - size / 2}px`;
      if (getComputedStyle(target).position === 'static') target.style.position = 'relative';
      target.classList.add('rippling');
      target.appendChild(dot);
      dot.addEventListener('animationend', () => dot.remove());
    };
    document.addEventListener('pointerdown', onDown);
    return () => document.removeEventListener('pointerdown', onDown);
  }, []);
}

/** A burst of small stars from the middle of an element (a treasure ticked, a treasure obtained). */
export function Burst({ fire, count = 10 }: { fire: number; count?: number }) {
  const [bursts, setBursts] = useState<number[]>([]);
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    if (reducedMotion()) return;
    setBursts((b) => [...b, fire]);
    const id = window.setTimeout(() => setBursts((b) => b.filter((x) => x !== fire)), 900);
    return () => window.clearTimeout(id);
  }, [fire]);
  return (
    <>
      {bursts.map((b) => (
        <span key={b} className="burst" aria-hidden="true">
          {Array.from({ length: count }, (_, i) => (
            <i key={i} style={{ '--a': `${(360 / count) * i + (b % 30)}deg`, '--r': `${34 + (i % 3) * 12}px` } as CSSProperties} />
          ))}
        </span>
      ))}
    </>
  );
}
