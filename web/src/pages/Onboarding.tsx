import { useEffect, useState } from 'react';
import { CircleAlert, CircleCheck } from 'lucide-react';
import logo from '../logo.png';
import { api, OVERLAY_ITEMS, type OverlayItem } from '../api';
import { useLocale } from '../i18n';
import { Count, Seg, Switch } from '../parts';
import { ImportOriginal } from './ImportOriginal';
import type { PageProps } from '../App';

/** Where the overlay starts: the top of the screen, left / centre / right. The shell anchors the window on that side. */
type Corner = 'left' | 'center' | 'right';
function cornerPosition(corner: Corner): { x: number; y: number; align: Corner } {
  const k = window.devicePixelRatio || 1;
  const left = Math.round(((screen as unknown as { availLeft?: number }).availLeft ?? 0) * k);
  const top = Math.round(((screen as unknown as { availTop?: number }).availTop ?? 0) * k);
  const width = Math.round(screen.width * k);
  const margin = Math.round(24 * k);
  const x = corner === 'left' ? left + margin : corner === 'right' ? left + width - margin : left + Math.round(width / 2);
  return { x, y: top + margin, align: corner };
}

/** First launch: find the replays, import them, bring the old history, pick the overlay and startup options. */
export function Onboarding({ state, patch, go }: PageProps) {
  const { t, n, date } = useLocale();
  const [step, setStep] = useState<1 | 2 | 3 | 4 | 5 | 6>(1);
  const o = state.settings.overlay;
  const s = state.settings;
  const overlay = (change: Record<string, unknown>) => patch({ overlay: { ...o, ...change } });
  const items = o.items ?? [];
  const toggleItem = (key: OverlayItem, on: boolean) => overlay({ items: on ? [...items, key] : items.filter((k) => k !== key) });
  const corner: Corner | undefined = o.x == null ? undefined : o.align ?? 'left';
  const tr = state.tracking;
  const imp = state.import;
  const scanning = tr.state === 'scanning';
  const processed = imp.counted + imp.duplicates + imp.notCounted + imp.incomplete + imp.errors;
  const remaining = Math.max(0, imp.files - processed - imp.waiting);
  // The import runs in the background whatever this screen shows: it is done when nothing is left to read.
  const importDone = !scanning && remaining === 0 && tr.lastScanUtc != null;

  useEffect(() => { if (step === 2 && importDone) setStep(3); }, [step, importDone]);
  const next = () => setStep((v) => Math.min(6, v + 1) as typeof step);

  const finish = async (then?: 'squad') => { await patch({ onboardingDone: true }); if (then) go('squad'); };
  const pick = () => api.post('/api/actions/pick-folder');
  const recording = t(`settings.recording.${state.recording.state}` as never, { date: date(state.recording.detail) });
  const recordingOk = state.recording.state === 'likely_on';

  return (
    <div className="onb shell" style={{ display: 'grid', gridTemplateColumns: '1fr' }}>
      <div className="onb-card">
        <div className="brand" style={{ padding: 0 }}><img src={logo} alt="" /><div>Chest Counter<span>Squad</span></div>
          <div style={{ marginLeft: 'auto' }}><Seg value={state.settings.language} onChange={(v) => patch({ language: v })} options={[['en', 'English'], ['fr', 'Français']]} /></div>
        </div>
        <div className="steps">
          {[t('onb.step1'), t('onb.step2'), t('onb.stepHistory'), t('onb.stepOverlay'), t('onb.stepStartup'), t('onb.step3')].map((label, i) => (
            <span key={label} className={step === i + 1 ? 'now' : step > i + 1 ? 'done' : ''}>{label}</span>
          ))}
        </div>

        {step === 1 && (
          <>
            <h1>{t('onb.title')}</h1>
            <div className="panel setting-list">
              <div className="check">
                {tr.folderFound ? <CircleCheck className="ok" size={20} /> : <CircleAlert className="bad" size={20} />}
                <div>
                  <div>{tr.folderFound ? t('onb.found', { n: n(imp.files) }) : t('onb.notFound')}</div>
                  <div className="code selectable" style={{ marginTop: 8 }}>{state.settings.demosFolder}</div>
                  {!tr.folderFound && <p className="note" style={{ marginTop: 8 }}>{t('onb.notFoundHint')}</p>}
                </div>
              </div>
              <div className="check">
                {recordingOk ? <CircleCheck className="ok" size={20} /> : <CircleAlert className="bad" size={20} />}
                <div>
                  <div>{recording}</div>
                  {!recordingOk && <p className="note mute" style={{ marginTop: 6 }}>{t('settings.recordingHelp')}</p>}
                </div>
              </div>
            </div>
            <div className="actions">
              {imp.files > 0
                ? <button className="btn primary" onClick={() => { api.post('/api/actions/scan'); setStep(importDone ? 3 : 2); }}>{t('onb.start')}</button>
                : <button className="btn primary" onClick={() => setStep(3)}>{t('onb.skipEmpty')}</button>}
              <button className="btn" onClick={pick}>{t('settings.choose')}</button>
            </div>
          </>
        )}

        {step === 3 && (
          <>
            <h1>{t('onb.historyTitle')}</h1>
            <ImportOriginal />
            <div className="actions">
              <button className="btn primary" onClick={next}>{t('onb.next')}</button>
            </div>
          </>
        )}

        {step === 4 && (
          <>
            <h1>{t('onb.overlayTitle')}</h1>
            <div className="panel setting-list">
              <Switch checked={o.enabled} onChange={(v) => overlay({ enabled: v })}>{t('settings.overlayOn')}</Switch>
              <h3>{t('settings.look')}</h3>
              <div className="ov-themes" role="radiogroup" aria-label={t('settings.look')}>
                {(['aurora', 'rift'] as const).map((theme) => (
                  <button key={theme} role="radio" aria-checked={o.theme === theme} className={`ov-theme${o.theme === theme ? ' on' : ''}`} onClick={() => overlay({ theme })}>
                    <span className={`ov-swatch ${theme}`} aria-hidden="true"><i className="a" /><i className="b" /><i className="c" /></span>
                    <span className="ov-theme-text">
                      <strong>{t(`settings.theme.${theme}` as never)}</strong>
                      <small>{t(`settings.theme.${theme}Hint` as never)}</small>
                    </span>
                  </button>
                ))}
              </div>
              <div className="actions"><span className="soft" style={{ width: 110 }}>{t('onb.where')}</span>
                <Seg value={corner ?? 'left'} onChange={(v) => overlay(cornerPosition(v))}
                  options={[['left', t('onb.whereLeft')], ['center', t('onb.whereCenter')], ['right', t('onb.whereRight')]]} />
              </div>
              <p className="note mute">{t('onb.whereHint')}</p>
              <h3>{t('settings.counters')}</h3>
              <div className="ov-items">
                {OVERLAY_ITEMS.map((key) => (
                  <div key={key} className={`ov-item${items.includes(key) ? ' on' : ''}`}>
                    <Switch checked={items.includes(key)} onChange={(v) => toggleItem(key, v)}>{t(`settings.item.${key}` as never)}</Switch>
                  </div>
                ))}
              </div>
              <h3>{t('onb.shortcuts')}</h3>
              <p className="note">{t('settings.hotkeys', { toggle: o.toggleHotkey, lock: o.lockHotkey })}</p>
              <Switch checked={o.locked} onChange={(v) => overlay({ locked: v })}>{t('settings.overlayLocked')}</Switch>
            </div>
            <p className="note mute">{t('onb.overlayLater')}</p>
            <div className="actions">
              <button className="btn primary" onClick={next}>{t('onb.next')}</button>
            </div>
          </>
        )}

        {step === 5 && (
          <>
            <h1>{t('onb.startupTitle')}</h1>
            <div className="panel setting-list">
              <Switch checked={s.launchWithWindows} onChange={(v) => patch({ launchWithWindows: v })}>{t('settings.launchWindows')}</Switch>
              <Switch checked={s.launchWithFortnite} onChange={(v) => patch({ launchWithFortnite: v })} hint={t('settings.launchFortniteHint')}>{t('settings.launchFortnite')}</Switch>
              <Switch checked={s.startMinimized} onChange={(v) => patch({ startMinimized: v })}>{t('settings.startMinimized')}</Switch>
              <Switch checked={s.minimizeToTray} onChange={(v) => patch({ minimizeToTray: v })}>{t('settings.tray')}</Switch>
            </div>
            <div className="actions">
              <button className="btn primary" onClick={next}>{t('onb.next')}</button>
            </div>
          </>
        )}

        {(step === 2 || step === 6) && (
          <>
            <h1>{step === 6 ? t('onb.importDone') : t('onb.importing', { index: n(Math.min(processed + 1, imp.files)), total: n(imp.files) })}</h1>
            {step === 2 && (
              <div>
                <div className="progress"><i style={{ width: `${imp.files ? (processed / imp.files) * 100 : 0}%` }} /></div>
                <div className="mute small" style={{ marginTop: 8, minHeight: 20 }}>{tr.currentFile}</div>
              </div>
            )}
            <div className="tally">
              <div><div className="v gold"><Count value={state.totals.chests} /></div><div className="soft small">{t('onb.chests')}</div></div>
              <div><div className="v"><Count value={imp.counted} /></div><div className="soft small">{t('onb.counted')}</div></div>
              <div><div className="v"><Count value={imp.duplicates + imp.notCounted + imp.incomplete} /></div><div className="soft small">{t('onb.skipped')}</div></div>
              <div><div className="v"><Count value={imp.errors} /></div><div className="soft small">{t('onb.unreadable')}</div></div>
            </div>
            {imp.duplicates + imp.notCounted + imp.incomplete > 0 && <p className="note mute">{t('onb.skippedHint')}</p>}
            {step === 2 ? <p className="note">{t('onb.leave')}</p> : (
              <>
                <p className="note">{t('onb.readyLead')}</p>
                <div className="actions">
                  <button className="btn primary" onClick={() => finish()}>{t('onb.finish')}</button>
                  <button className="btn" onClick={() => finish('squad')}>{t('onb.squad')}</button>
                </div>
              </>
            )}
          </>
        )}
      </div>
    </div>
  );
}
