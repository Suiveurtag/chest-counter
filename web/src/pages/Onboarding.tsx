import { useEffect, useState } from 'react';
import { CircleAlert, CircleCheck } from 'lucide-react';
import logo from '../logo.png';
import { api } from '../api';
import { useLocale } from '../i18n';
import { Count, Seg } from '../parts';
import type { PageProps } from '../App';

/** First launch: find the replays, import them, done. Three steps, in that order. */
export function Onboarding({ state, patch, go }: PageProps) {
  const { t, n, date } = useLocale();
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const tr = state.tracking;
  const imp = state.import;
  const scanning = tr.state === 'scanning';
  const processed = imp.counted + imp.duplicates + imp.notCounted + imp.incomplete + imp.errors;
  const remaining = Math.max(0, imp.files - processed - imp.waiting);
  // The import runs in the background whatever this screen shows: it is done when nothing is left to read.
  const importDone = !scanning && remaining === 0 && tr.lastScanUtc != null;

  useEffect(() => { if (step === 2 && importDone) setStep(3); }, [step, importDone]);

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
          {[t('onb.step1'), t('onb.step2'), t('onb.step3')].map((label, i) => (
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

        {step >= 2 && (
          <>
            <h1>{step === 3 ? t('onb.importDone') : t('onb.importing', { index: n(Math.min(processed + 1, imp.files)), total: n(imp.files) })}</h1>
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
