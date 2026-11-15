import { useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { Calculator, Plus, X } from 'lucide-react';
import { api, useLive, type AppState, type HuntReport } from './api';
import { useLocale } from './i18n';
import { Count, useAction } from './parts';
import { Modal } from './fx';

/**
 * Chests opened before the app counted them: added (or removed) by hand, each addition undoable. An estimate from the
 * match count Fortnite shows can fill the number in. Local only: never part of a squad figure.
 */
export function ManualDialog({ open, onClose, state }: { open: boolean; onClose: () => void; state: AppState }) {
  const { t, n, n1, date } = useLocale();
  const { data: hunt, reload } = useLive<HuntReport>(open ? '/api/hunt' : null, ['matches', 'hunt']);
  const [value, setValue] = useState('');
  const [thisWeek, setThisWeek] = useState(false);
  const [played, setPlayed] = useState('');
  const [estimate, setEstimate] = useState<string>();
  const action = useAction();
  const entries = hunt?.manualEntries ?? [];
  const added = state.totals.manualChests;
  const week = hunt?.week;

  const parse = (s: string) => {
    const digits = s.replace(/[^\d-]/g, '');
    return /^-?\d+$/.test(digits) ? Number(digits) : NaN;
  };
  const add = () => action.run(async () => {
    const chests = parse(value);
    if (!Number.isFinite(chests) || chests === 0) throw new Error(t('manual.invalid'));
    await api.post('/api/manual', { chests, thisWeek });
    setValue('');
    setEstimate(undefined);
    reload();
  });
  const remove = (id: string) => action.run(async () => { await api.post('/api/manual/remove', { id }); reload(); });
  const compute = () => {
    const career = parse(played);
    const counted = state.totals.matches;
    if (!Number.isFinite(career)) return;
    if (counted < 10) { setEstimate(t('manual.estimateTooFew')); return; }
    if (career <= counted) { setEstimate(t('manual.estimateNone')); return; }
    const avg = state.totals.average;
    const chests = Math.round((career - counted) * avg);
    setValue(String(chests));
    setEstimate(t('manual.estimateResult', { played: n(career), counted: n(counted), avg: n1(avg), chests: n(chests) }));
  };

  return (
    <Modal open={open} onClose={onClose} title={t('manual.title')}>
      <p className="note">{t('manual.lead')}</p>
      <div className="manual-added">{t('manual.added', { n: '' })}<b className="t0"><Count value={added} from0={false} /></b></div>
      <form className="manual-row" onSubmit={(e) => { e.preventDefault(); add(); }}>
        <input className="input" inputMode="numeric" value={value} onChange={(e) => setValue(e.target.value)} placeholder="0" aria-label={t('manual.title')} autoFocus />
        <button className="btn primary" type="submit" disabled={action.busy}><Plus size={16} />{t('manual.add')}</button>
      </form>
      {week && (
        <label className="check-line">
          <input type="checkbox" className="tick" checked={thisWeek} onChange={(e) => setThisWeek(e.target.checked)} />
          <span><b>{t('manual.thisWeek')}</b> <span className="mute">{t('manual.thisWeekHint', { n: week.number })}</span></span>
        </label>
      )}
      {action.error && <motion.div className="error" initial={{ x: -6 }} animate={{ x: [6, -4, 2, 0] }}>{action.error}</motion.div>}
      <ul className="manual-list">
        <AnimatePresence initial={false}>
          {entries.map((e) => (
            <motion.li key={e.id} layout initial={{ opacity: 0, height: 0, x: -10 }} animate={{ opacity: 1, height: 'auto', x: 0 }} exit={{ opacity: 0, height: 0, x: 20 }}>
              <span className={`num ${e.chests >= 0 ? 't0' : 'danger'}`}>{e.chests > 0 ? '+' : ''}{n(e.chests)}</span>
              <span className="mute small">{date(e.atUtc)}{e.weekFromUtc && hunt ? ` · ${t('manual.week', { n: hunt.weeks.find((w) => w.from === e.weekFromUtc)?.number ?? '?' })}` : ''}</span>
              <button className="icon-btn" onClick={() => remove(e.id)} aria-label={t('manual.undo')} title={t('manual.undo')}><X size={15} /></button>
            </motion.li>
          ))}
        </AnimatePresence>
      </ul>
      <div className="manual-estimate">
        <h3>{t('manual.estimate')}</h3>
        <p className="mute small">{t('manual.estimateHint')}</p>
        <form className="manual-row" onSubmit={(e) => { e.preventDefault(); compute(); }}>
          <input className="input" inputMode="numeric" value={played} onChange={(e) => setPlayed(e.target.value)} placeholder={t('manual.matchesPlayed')} aria-label={t('manual.matchesPlayed')} />
          <button className="btn" type="submit"><Calculator size={16} />{t('manual.compute')}</button>
        </form>
        <AnimatePresence>
          {estimate && <motion.p className="note" initial={{ opacity: 0, y: -4 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }}>{estimate}</motion.p>}
        </AnimatePresence>
      </div>
    </Modal>
  );
}
