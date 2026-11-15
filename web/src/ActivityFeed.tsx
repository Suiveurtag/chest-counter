import { useMemo, useRef, useState, type CSSProperties } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import {
  AlertOctagon, ArrowRight, BadgeCheck, Ban, Copy, Crown, FileWarning, Gem, Layers, Loader2, Package, PenLine, Power, Radio,
  RefreshCw, ScanLine, Sparkles, Undo2, Users,
} from 'lucide-react';
import { useLive, type ActivityItem, type AppState, type TreasureTag } from './api';
import { useLocale } from './i18n';
import { Count } from './parts';
import { easeOut } from './fx';
import { ordinal, TreasureLabel } from './hunt';
import { trackingLine } from './App';

type Tone = 'gold' | 'violet' | 'pink' | 'cyan' | 'mute' | 'danger' | 'green';

const icons: Record<string, [typeof Package, Tone]> = {
  started: [Power, 'green'], recording: [Radio, 'pink'], analyzing: [ScanLine, 'pink'], match: [Package, 'gold'], importing: [Layers, 'violet'],
  import: [Layers, 'violet'], skipped: [Ban, 'mute'], duplicate: [Copy, 'mute'], incomplete: [FileWarning, 'danger'], error: [AlertOctagon, 'danger'],
  manual: [PenLine, 'gold'], manualRemoved: [Undo2, 'mute'], collected: [BadgeCheck, 'violet'], content: [RefreshCw, 'cyan'],
};

/**
 * What the counter did, as a timeline: one node per event, newest on top, grouped by day. A live node at the top
 * says what it is doing right now (a radar while a match is recorded). New events slide in and glow.
 */
export function ActivityFeed({ state }: { state: AppState }) {
  const { t, date } = useLocale();
  const [limit, setLimit] = useState(30);
  const { data } = useLive<ActivityItem[]>(`/api/activity?limit=${limit}`, ['activity', 'hunt']);
  const seenMax = useRef<number | null>(null);
  if (data && seenMax.current === null) seenMax.current = data[0]?.id ?? 0;
  const status = trackingLine(state, t);
  const scanning = state.tracking.state === 'scanning' || state.tracking.pending.some((p) => p.reason === 'settling');
  const recording = state.tracking.pending.some((p) => p.reason === 'recording');

  const groups = useMemo(() => {
    const out: { day: string; items: ActivityItem[] }[] = [];
    for (const item of data ?? []) {
      const day = date(item.atUtc);
      if (out.length === 0 || out[out.length - 1].day !== day) out.push({ day, items: [] });
      out[out.length - 1].items.push(item);
    }
    return out;
  }, [data, date]);

  let index = 0;
  return (
    <div className="feed">
      <div className={`feed-now ${status.tone}${recording ? ' recording' : ''}`}>
        <span className="feed-node now"><i className="radar" /><Radio size={15} /></span>
        <div>
          <div className="feed-now-k">{t('feed.now')}</div>
          <div className="feed-now-v">{status.text}</div>
          {state.tracking.state === 'scanning' && state.tracking.scanTotal > 0 && (
            <div className="feed-progress"><i style={{ width: `${(state.tracking.scanIndex / state.tracking.scanTotal) * 100}%` }} /></div>
          )}
        </div>
      </div>
      {data && data.length === 0 && <div className="empty"><span>{t('home.feedEmpty')}</span></div>}
      {groups.map((g) => (
        <div key={g.day} className="feed-day">
          <div className="feed-day-label">{g.day}</div>
          <ul className="feed-list">
            <AnimatePresence initial={false}>
              {g.items.map((item) => {
                const fresh = seenMax.current !== null && item.id > seenMax.current;
                const i = index++;
                const latest = item.id === data![0].id;
                return (
                  <motion.li key={item.id} layout="position" className={`feed-item k-${item.kind}${fresh ? ' fresh' : ''}`}
                    initial={{ opacity: 0, x: -18, scale: 0.97 }} animate={{ opacity: 1, x: 0, scale: 1 }}
                    transition={{ duration: 0.55, ease: easeOut, delay: fresh ? 0.05 : Math.min(i, 12) * 0.045 }}>
                    <Entry item={item} live={latest && ((item.kind === 'analyzing' && scanning) || (item.kind === 'recording' && recording) || (item.kind === 'importing' && scanning))} />
                  </motion.li>
                );
              })}
            </AnimatePresence>
          </ul>
        </div>
      ))}
      {data && data.length >= limit && (
        <div className="feed-more"><button className="btn quiet" onClick={() => setLimit(limit + 40)}>{t('feed.older')}</button></div>
      )}
    </div>
  );
}

function Entry({ item, live }: { item: ActivityItem; live: boolean }) {
  const { t, n, time, dateTime, playlist, lang, duration } = useLocale();
  const d = item.data;
  const [Icon, tone] = icons[item.kind] ?? [Sparkles, 'mute' as Tone];
  const victory = item.kind === 'match' && d.placement === 1;
  const node = (
    <span className={`feed-node t-${victory ? 'gold' : tone}${live ? ' is-live' : ''}`}>
      {live && item.kind !== 'recording' ? <Loader2 size={15} className="spin" /> : victory ? <Crown size={15} /> : <Icon size={15} />}
      {live && item.kind === 'recording' && <i className="radar" />}
    </span>
  );
  const at = <time className="feed-time">{time(item.atUtc)}</time>;
  const mb = (bytes: number) => (bytes / 1048576).toLocaleString(lang === 'fr' ? 'fr-FR' : 'en-US', { maximumFractionDigits: 1, minimumFractionDigits: 1 });

  let body: JSX.Element;
  switch (item.kind) {
    case 'match': {
      const treasures = (d.treasures ?? []) as { tag: TreasureTag; time?: number; source?: string; destroyed?: boolean }[];
      const squad = d.squad as { chests: number; members: { name: string; chests: number }[] } | undefined;
      body = (
        <div className={`feed-card match${victory ? ' victory' : ''}`}>
          <div className="feed-head">
            <b>{t('feed.match', { when: dateTime(d.when) })}</b>
            <span className="mute">{playlist(d.playlist)}</span>
            {victory ? <span className="tag vr"><Crown size={12} /> {t('matches.victory')}</span>
              : d.placement ? <span className="tag muted">{ordinal(d.placement, lang)}{d.totalPlayers ? ` / ${d.totalPlayers}` : ''}</span> : null}
            {d.replaced && <span className="tag muted">{t('feed.reanalyzed')}</span>}
            {at}
          </div>
          <div className="feed-figures">
            <span className="feed-plus">+<Count value={d.chests} /></span>
            <span className="soft">{t('feed.chests')}{d.destroyed > 0 && <span className="mute"> · {t('feed.destroyed', { n: d.destroyed })}</span>}</span>
            <ArrowRight size={16} className="feed-arrow" />
            <span className="feed-total"><span className="mute">{t('feed.total')}</span> <b><Count value={d.total} from0={false} /></b></span>
          </div>
          {treasures.length > 0 && (
            <div className="feed-treasures">
              {treasures.map((x, i) => (
                <motion.div key={i} className={`feed-treasure${x.tag.golden ? ' golden' : ''}`} initial={{ opacity: 0, y: 6, scale: 0.96 }} animate={{ opacity: 1, y: 0, scale: 1 }}
                  transition={{ delay: 0.35 + i * 0.12, type: 'spring', stiffness: 300, damping: 22 }}>
                  <Gem size={14} />
                  <span className="mute">{t('feed.treasure')}</span>
                  <TreasureLabel tag={x.tag} t={t} />
                  <span className="mute small">{x.time != null ? duration(Math.round(x.time)) : ''}{x.source ? ` · ${t(`src.${x.source}` as never)}${x.destroyed ? ' ' + t('hunt.destroyedSuffix') : ''}` : ''}</span>
                </motion.div>
              ))}
            </div>
          )}
          {d.teamTreasures > 0 && <div className="mute small">{t('feed.teamTreasures', { n: d.teamTreasures })}</div>}
          {squad && (
            <div className="feed-squad">
              <Users size={14} />
              <span>{t('feed.squad')}: <b>{n(squad.chests)}</b></span>
              <span className="feed-mates">
                {squad.members.map((m, i) => (
                  <span key={i} className="mate" style={{ '--i': i } as CSSProperties}><i className={`swatch c${(i + 1) % 4}`} />{m.name} <b>{m.chests}</b></span>
                ))}
              </span>
              <span className="mute small">{t('feed.squadHint')}</span>
            </div>
          )}
        </div>
      );
      break;
    }
    case 'import':
      body = (
        <div className="feed-card import">
          <div className="feed-head"><b>{t('feed.import', { n: n(d.files) })}</b>{at}</div>
          <div className="feed-stats">
            <span><b><Count value={d.matches} /></b> {t('feed.importMatches')}</span>
            <span><b className="t0"><Count value={d.chests} /></b> {t('feed.importChests')}</span>
            <span><b className="t3">★ <Count value={d.treasures} /></b> {t('feed.importTreasures')}</span>
            {d.replaced > 0 && <span><b><Count value={d.replaced} /></b> {t('feed.importReplaced')}</span>}
          </div>
          {d.skipped + d.errors > 0 && <div className="mute small">{t('feed.importSkipped', { n: d.skipped + d.errors })}</div>}
        </div>
      );
      break;
    case 'importing':
      body = <Line title={t('feed.importing', { n: n(d.files) })} at={at} />;
      break;
    case 'recording':
      body = <Line title={t('feed.recording')} sub={t('feed.recordingSub')} at={at} live={live} />;
      break;
    case 'analyzing':
      body = <Line title={`${t('feed.analyzing')} (${mb(d.bytes ?? 0)} MB)`} sub={live ? undefined : d.file} at={at} live={live} />;
      break;
    case 'skipped':
      body = <Line title={t('feed.skipped')} sub={[playlist(d.playlist), d.reason ? t(`matches.reason.${d.reason}` as never) : ''].filter(Boolean).join(' · ')} at={at} />;
      break;
    case 'duplicate': body = <Line title={t('feed.duplicate')} sub={d.file} at={at} />; break;
    case 'incomplete': body = <Line title={t('feed.incomplete')} sub={d.file} at={at} />; break;
    case 'error': body = <Line title={t('feed.error')} sub={d.file} at={at} />; break;
    case 'manual':
      body = <Line title={d.chests >= 0 ? t('feed.manual', { n: n(d.chests) }) : t('feed.manualNeg', { n: n(-d.chests) })}
        sub={`${d.thisWeek ? t('feed.thisWeek') + ' · ' : ''}${t('feed.total')} ${n(d.total)}`} at={at} />;
      break;
    case 'manualRemoved': body = <Line title={t('feed.manualRemoved', { n: n(d.chests) })} sub={`${t('feed.total')} ${n(d.total)}`} at={at} />; break;
    case 'collected': body = <Line title={t('feed.collected')} sub={`${d.name} (${t(`hunt.kind.${d.kind}` as never)})`} at={at} />; break;
    case 'content': body = <Line title={t('feed.content', { n: d.revision })} at={at} />; break;
    case 'started': body = <Line title={t('feed.started')} sub={t('feed.startedSub')} at={at} />; break;
    default: body = <Line title={item.kind} at={at} />;
  }
  return <>{node}{body}</>;
}

function Line({ title, sub, at, live }: { title: string; sub?: string; at: JSX.Element; live?: boolean }) {
  return (
    <div className={`feed-line${live ? ' is-live' : ''}`}>
      <div className="feed-head"><span>{title}{live && <span className="dots"><i /><i /><i /></span>}</span>{at}</div>
      {sub && <div className="mute small ellipsis">{sub}</div>}
      {live && <div className="feed-shimmer" />}
    </div>
  );
}
