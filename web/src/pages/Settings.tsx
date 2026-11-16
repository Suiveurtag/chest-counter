import { useState } from 'react';
import { api, useLive, type Diagnostics } from '../api';
import { useLocale } from '../i18n';
import { Seg, Switch, useAction } from '../parts';
import { ImportOriginal } from './ImportOriginal';
import { OverlaySection } from './OverlaySection';
import type { PageProps } from '../App';

/** "Synced today, 2:43 PM" -> "synced today, 2:43 PM" (the time keeps its own case). */
const lowerFirst = (s: string) => s.charAt(0).toLowerCase() + s.slice(1);

export function Settings({ state, patch }: PageProps) {
  const { t, n, date, dateTime } = useLocale();
  const s = state.settings;
  const { data: diag } = useLive<Diagnostics>('/api/diagnostics', ['matches', 'tracking', 'sync']);
  const action = useAction();
  const [saved, setSaved] = useState<string>();
  const act = (name: string) => action.run(async () => {
    const result = await api.post<{ path?: string }>(`/api/actions/${name}`);
    setSaved(result.path && (name === 'backup' || name === 'export') ? result.path : undefined);
  });
  const counts = diag?.fileCounts ?? {};

  return (
    <div className="page" style={{ maxWidth: 860 }}>
      <div className="page-head"><h1>{t('nav.settings')}</h1></div>

      <section className="panel setting-list">
        <h2>{t('settings.replays')}</h2>
        <div className="field">
          <label>{t('settings.folder')}{s.demosFolderIsDefault && <span className="mute"> ({t('settings.folderDefault')})</span>}</label>
          <div className="code selectable">{s.demosFolder}</div>
        </div>
        <div className="actions">
          <button className="btn" onClick={() => act('pick-folder')}>{t('settings.choose')}</button>
          {!s.demosFolderIsDefault && <button className="btn quiet" onClick={() => act('reset-folder')}>{t('settings.useDefault')}</button>}
          <button className="btn quiet" onClick={() => act('open-replay-folder')}>{t('settings.open')}</button>
          <button className="btn quiet" onClick={() => act('scan')}>{t('settings.scan')}</button>
        </div>
        <p className="note">{t(`settings.recording.${state.recording.state}` as never, { date: date(state.recording.detail) })}</p>
        <p className="note mute">{t('settings.recordingHelp')}</p>
      </section>

      <OverlaySection state={state} patch={patch} />

      <section className="panel setting-list">
        <h2>{t('settings.startup')}</h2>
        <Switch checked={s.launchWithWindows} onChange={(v) => patch({ launchWithWindows: v })}>{t('settings.launchWindows')}</Switch>
        <Switch checked={s.launchWithFortnite} onChange={(v) => patch({ launchWithFortnite: v })} hint={t('settings.launchFortniteHint')}>{t('settings.launchFortnite')}</Switch>
        <Switch checked={s.startMinimized} onChange={(v) => patch({ startMinimized: v })}>{t('settings.startMinimized')}</Switch>
        <Switch checked={s.minimizeToTray} onChange={(v) => patch({ minimizeToTray: v })}>{t('settings.tray')}</Switch>
      </section>

      <section className="panel setting-list">
        <h2>{t('settings.display')}</h2>
        <div className="actions"><span className="soft" style={{ width: 110 }}>{t('settings.language')}</span>
          <Seg value={s.language} onChange={(v) => patch({ language: v })} options={[['en', 'English'], ['fr', 'Français']]} />
        </div>
        <div className="actions"><span className="soft" style={{ width: 110 }}>{t('settings.timeFormat')}</span>
          <Seg value={s.timeFormat} onChange={(v) => patch({ timeFormat: v })} options={[['auto', t('settings.auto')], ['24', '22:54'], ['12', '10:54 PM']]} />
        </div>
        {state.profiles.length > 1 && (
          <div className="actions"><span className="soft" style={{ width: 110 }}>{t('settings.profile')}</span>
            <select className="input" style={{ width: 'auto' }} value={s.profile ?? ''} onChange={(e) => patch({ profile: e.target.value || null })}>
              <option value="">{t('settings.profileAuto')}</option>
              {state.profiles.map((p) => <option key={p.key} value={p.key}>{p.name} ({n(p.chests)})</option>)}
            </select>
          </div>
        )}
      </section>

      <section className="panel setting-list">
        <h2>{t('settings.server')}</h2>
        {state.sync.state === 'disabled' ? <p className="note">{t('settings.serverNone')}</p> : (
          <>
            <div className="code selectable">{state.sync.server}</div>
            <p className="note">{state.sync.displayName}, {t(`squad.state.${state.sync.state}` as never)}{state.sync.lastSyncUtc ? `, ${lowerFirst(t('squad.lastSync', { time: dateTime(state.sync.lastSyncUtc) }))}` : ''}</p>
            {state.sync.lastError && state.sync.state === 'offline' && <p className="error">{state.sync.lastError}</p>}
            <div className="actions">
              <button className="btn" onClick={() => action.run(() => api.post('/api/squad/resync'))}>{t('settings.resync')}</button>
              <button className="btn danger" onClick={() => confirm(t('settings.disconnectConfirm')) && action.run(() => api.post('/api/squad/disconnect'))}>{t('settings.disconnect')}</button>
            </div>
          </>
        )}
      </section>

      <section className="panel setting-list">
        <h2>{t('settings.data')}</h2>
        <p className="note">{t('settings.dataHint')}</p>
        <div className="code selectable">{diag?.databasePath ?? state.dataDir}</div>
        <div className="actions">
          <button className="btn" onClick={() => act('backup')}>{t('settings.backup')}</button>
          <button className="btn" onClick={() => act('export')}>{t('settings.export')}</button>
          <button className="btn quiet" onClick={() => act('open-data-folder')}>{t('settings.openData')}</button>
        </div>
        {saved && <p className="note selectable">{t('settings.saved', { path: saved })}</p>}
        {action.error && <p className="error">{action.error}</p>}
        <div className="actions" style={{ marginTop: 6 }}>
          <button className="btn" onClick={() => act('reanalyze')}>{t('settings.reanalyze')}</button>
          <button className="btn quiet" onClick={() => act('retry-failed')}>{t('settings.retry')}</button>
        </div>
        <p className="note mute">{t('settings.reanalyzeHint')}</p>
      </section>

      <ImportOriginal />

      <section className="panel setting-list">
        <h2>{t('settings.diagnostics')}</h2>
        <p className="note">{t('settings.files', { counted: counts.counted ?? 0, duplicate: counts.duplicate ?? 0, notCounted: counts.not_counted ?? 0, incomplete: counts.incomplete ?? 0, error: counts.error ?? 0 })}</p>
        <h3>{t('settings.problems')}</h3>
        {(diag?.problems.length ?? 0) === 0 ? <p className="note mute">{t('settings.noProblems')}</p> : (
          <div className="rows">
            {diag!.problems.map((p) => (
              <div className="row" key={p.fileName} style={{ gridTemplateColumns: '1fr auto', fontSize: 13.5 }}>
                <div className="selectable"><div>{p.fileName}</div><div className="mute small">{p.detail}</div></div>
                <span className={`tag ${p.status === 'error' ? 'warn' : 'muted'}`}>{p.status}</span>
              </div>
            ))}
          </div>
        )}
        {(diag?.refusedUploads.length ?? 0) > 0 && (
          <div className="rows">
            {diag!.refusedUploads.map((u) => <div className="row small" key={u.matchId + u.playerId} style={{ gridTemplateColumns: '1fr auto' }}><span className="selectable">{u.matchId}</span><span className="tag warn">{u.lastError}</span></div>)}
          </div>
        )}
        <h3>{t('settings.limits')}</h3>
        <ul className="plain"><li>{t('settings.limit1')}</li><li>{t('settings.limit2')}</li><li>{t('settings.limit3')}</li><li>{t('settings.limit4')}</li></ul>
        <h3>{t('settings.log')}</h3>
        <div className="log">
          {diag?.log.map((l) => <div key={l.id} className={l.level}>{new Date(l.atUtc).toLocaleString()} [{l.source}] {l.message}</div>)}
        </div>
      </section>

      <section className="panel setting-list">
        <h2>{t('settings.about')}</h2>
        <p className="note">{t('settings.aboutText')}</p>
        <p className="note mute">{t('settings.version', { version: state.version, parser: state.parserVersion })}</p>
      </section>
    </div>
  );
}
