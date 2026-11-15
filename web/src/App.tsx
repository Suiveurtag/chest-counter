import { useEffect, useMemo, useState } from 'react';
import { BarChart3, Home as HomeIcon, List, Package, Settings as SettingsIcon, Users } from 'lucide-react';
import { api, useLive, type AppState } from './api';
import { LocaleContext, makeLocale, useLocale, type Key } from './i18n';
import { Home } from './pages/Home';
import { SquadPage } from './pages/Squad';
import { Matches } from './pages/Matches';
import { Insights } from './pages/Insights';
import { Settings } from './pages/Settings';
import { Onboarding } from './pages/Onboarding';

export type Page = 'home' | 'squad' | 'matches' | 'insights' | 'settings';
export interface PageProps { state: AppState; go: (page: Page) => void; patch: (settings: Record<string, unknown>) => Promise<void> }

const pages: [Page, Key, typeof HomeIcon][] = [
  ['home', 'nav.home', HomeIcon], ['squad', 'nav.squad', Users], ['matches', 'nav.matches', List],
  ['insights', 'nav.insights', BarChart3], ['settings', 'nav.settings', SettingsIcon],
];

/** One sentence on what the tracker is doing: the "latest replay-processing status". */
export function trackingLine(state: AppState, t: ReturnType<typeof useLocale>['t']): { text: string; tone: 'on' | 'busy' | 'off' } {
  const tr = state.tracking;
  if (!tr.folderFound || tr.state === 'paused') return { text: t('status.paused'), tone: 'off' };
  if (tr.state === 'scanning') return { text: t('status.scanning', { index: Math.min(tr.scanIndex + 1, tr.scanTotal), total: tr.scanTotal }), tone: 'busy' };
  if (tr.pending.some((p) => p.reason === 'settling')) return { text: t('status.settling'), tone: 'busy' };
  if (tr.pending.some((p) => p.reason === 'recording')) return { text: t('status.recording'), tone: 'busy' };
  return { text: t('status.watching'), tone: 'on' };
}

export function lastOutcomeLine(state: AppState, t: ReturnType<typeof useLocale>['t']): string | undefined {
  const o = state.tracking.lastOutcome;
  if (!o) return undefined;
  switch (o.status) {
    case 'counted': return t('status.lastCounted', { chests: o.match?.chests ?? 0 });
    case 'duplicate': return t('status.lastDuplicate');
    case 'not_counted': return t('status.lastNotCounted');
    case 'incomplete': return t('status.lastIncomplete');
    case 'error': return t('status.lastError');
    default: return undefined;
  }
}

export function App() {
  const { data: state, reload } = useLive<AppState>('/api/state', ['tracking', 'matches', 'sync', 'settings']);
  const [page, setPage] = useState<Page>('home');
  const locale = useMemo(() => makeLocale(state?.settings.language ?? 'en', state?.settings.timeFormat ?? 'auto'), [state?.settings.language, state?.settings.timeFormat]);
  useEffect(() => { document.documentElement.lang = locale.lang; }, [locale.lang]);

  if (!state) return null;
  const patch = async (settings: Record<string, unknown>) => { await api.post('/api/settings', settings); reload(); };
  const props: PageProps = { state, go: setPage, patch };

  if (!state.settings.onboardingDone) {
    return <LocaleContext.Provider value={locale}><Onboarding {...props} /></LocaleContext.Provider>;
  }

  const status = trackingLine(state, locale.t);
  const squadActivity = state.sync.state === 'online' && (state.squad?.members.filter((m) => m.online).length ?? 0) > 1;
  return (
    <LocaleContext.Provider value={locale}>
      <div className="shell">
        <aside className="rail">
          <div className="brand"><Package size={24} strokeWidth={2.2} /><div>Chest Counter<span>Squad</span></div></div>
          <nav className="nav">
            {pages.map(([id, key, Icon]) => (
              <button key={id} aria-current={page === id ? 'page' : undefined} onClick={() => setPage(id)}>
                <Icon size={18} /><span>{locale.t(key)}</span>
                {id === 'squad' && squadActivity && <i className="dot" title={locale.t('squad.online')} />}
              </button>
            ))}
          </nav>
          <div className="rail-foot">
            {state.profile && <div className="who">{state.profile.name}</div>}
            <div className="live"><i className={`pulse ${status.tone}`} /><span>{status.text}</span></div>
          </div>
        </aside>
        <main className="main">
          {page === 'home' && <Home {...props} />}
          {page === 'squad' && <SquadPage {...props} />}
          {page === 'matches' && <Matches {...props} />}
          {page === 'insights' && <Insights {...props} />}
          {page === 'settings' && <Settings {...props} />}
        </main>
      </div>
    </LocaleContext.Provider>
  );
}
