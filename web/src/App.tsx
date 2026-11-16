import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { AnimatePresence, MotionConfig, motion } from 'motion/react';
import { BarChart3, Gem, Home as HomeIcon, List, Settings as SettingsIcon, Users } from 'lucide-react';
import logo from './logo.png';
import { api, useLive, type AppState } from './api';
import { LocaleContext, makeLocale, useLocale, type Key } from './i18n';
import { Home } from './pages/Home';
import { SquadPage } from './pages/Squad';
import { Matches } from './pages/Matches';
import { Insights } from './pages/Insights';
import { Settings } from './pages/Settings';
import { Onboarding } from './pages/Onboarding';
import { Treasures } from './pages/Treasures';
import { easeOut, useRipples } from './fx';

export type Page = 'home' | 'treasures' | 'squad' | 'matches' | 'insights' | 'settings';
export interface PageProps { state: AppState; go: (page: Page) => void; patch: (settings: Record<string, unknown>) => Promise<void> }

const pages: [Page, Key, typeof HomeIcon][] = [
  ['home', 'nav.home', HomeIcon], ['treasures', 'nav.treasures', Gem], ['squad', 'nav.squad', Users], ['matches', 'nav.matches', List],
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
  const { data: state, reload } = useLive<AppState>('/api/state', ['tracking', 'matches', 'sync', 'settings', 'hunt']);
  const [page, setPage] = useState<Page>('home');
  const main = useRef<HTMLElement>(null);
  const locale = useMemo(() => makeLocale(state?.settings.language ?? 'en', state?.settings.timeFormat ?? 'auto'), [state?.settings.language, state?.settings.timeFormat]);
  useEffect(() => { document.documentElement.lang = locale.lang; }, [locale.lang]);
  useRipples();

  const go = (p: Page) => {
    setPage(p);
    main.current?.scrollTo({ top: 0, behavior: 'smooth' });
  };

  // Single sliding indicator measured from the active button: moves in y/height only, so it never gets scale-distorted.
  const navRef = useRef<HTMLElement>(null);
  const [pill, setPill] = useState<{ y: number; h: number } | null>(null);
  const navReady = !!state?.settings.onboardingDone;
  useLayoutEffect(() => {
    const measure = () => {
      const btn = navRef.current?.querySelector<HTMLElement>('button[aria-current="page"]');
      if (btn) setPill((p) => (p && p.y === btn.offsetTop && p.h === btn.offsetHeight ? p : { y: btn.offsetTop, h: btn.offsetHeight }));
    };
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [page, navReady]);

  if (!state) {
    return <div className="boot"><img src={logo} alt="" /><i /></div>;
  }
  const patch = async (settings: Record<string, unknown>) => { await api.post('/api/settings', settings); reload(); };
  const props: PageProps = { state, go, patch };

  if (!state.settings.onboardingDone) {
    return <LocaleContext.Provider value={locale}><MotionConfig reducedMotion="user"><Onboarding {...props} /></MotionConfig></LocaleContext.Provider>;
  }

  const status = trackingLine(state, locale.t);
  const squadActivity = state.sync.state === 'online' && (state.squad?.members.filter((m) => m.online).length ?? 0) > 1;
  return (
    <LocaleContext.Provider value={locale}>
      <MotionConfig reducedMotion="user">
        <div className="shell">
          <motion.aside className="rail" initial={{ x: -30, opacity: 0 }} animate={{ x: 0, opacity: 1 }} transition={{ duration: 0.7, ease: easeOut }}>
            <div className="brand"><img src={logo} alt="" /><div>Chest Counter<span>Squad</span></div></div>
            <nav className="nav" ref={navRef}>
              {pill && <motion.span className="nav-pill" initial={false} animate={{ y: pill.y, height: pill.h }} transition={{ type: 'spring', stiffness: 380, damping: 32 }} />}
              {pages.map(([id, key, Icon], i) => (
                <motion.button key={id} data-ico={id} aria-current={page === id ? 'page' : undefined} onClick={() => go(id)}
                  initial={{ opacity: 0, x: -12 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: 0.15 + i * 0.05, duration: 0.5, ease: easeOut }}>
                  <Icon size={18} className="nav-icon" /><span>{locale.t(key)}</span>
                  {id === 'squad' && squadActivity && <i className="dot" title={locale.t('squad.online')} />}
                  {id === 'treasures' && state.totals.treasures > 0 && <i className="nav-count">{state.totals.treasures}</i>}
                </motion.button>
              ))}
            </nav>
            <div className="rail-foot">
              {state.profile && <div className="who">{state.profile.name}</div>}
              <div className="live"><i className={`pulse ${status.tone}`} /><AnimatePresence mode="wait">
                <motion.span key={status.text} initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -4 }} transition={{ duration: 0.25 }}>{status.text}</motion.span>
              </AnimatePresence></div>
            </div>
          </motion.aside>
          <main className="main" ref={main}>
            {/* No exit animation / mode="wait": a stalled exit (rapid clicks, hidden window) used to leave the next page unmounted = blank tab. */}
            <motion.div key={page} className="page-frame"
              initial={{ opacity: 0, y: 14 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.35, ease: easeOut }}>
              {page === 'home' && <Home {...props} />}
              {page === 'treasures' && <Treasures {...props} />}
              {page === 'squad' && <SquadPage {...props} />}
              {page === 'matches' && <Matches {...props} />}
              {page === 'insights' && <Insights {...props} />}
              {page === 'settings' && <Settings {...props} />}
            </motion.div>
          </main>
        </div>
      </MotionConfig>
    </LocaleContext.Provider>
  );
}
