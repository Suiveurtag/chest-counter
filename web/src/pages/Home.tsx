import { useState, type CSSProperties } from 'react';
import { AlertTriangle, ChevronRight, Crown, Pencil, Users } from 'lucide-react';
import { useLive, type Squad, type TimelinePoint } from '../api';
import { useLocale } from '../i18n';
import { Bars, Count, Legend, SplitBar, type Segment } from '../parts';
import { lastOutcomeLine, type PageProps } from '../App';
import { Odometer } from '../fx';
import { ActivityFeed } from '../ActivityFeed';
import { ManualDialog } from '../ManualDialog';
import { TreasureLabel } from '../hunt';

/** Members in a stable order, the local player first: a member keeps the same colour on every screen. */
export function memberSegments(squad: Squad, myUserId: string | undefined, value: (userId: string) => number | undefined, youLabel: string): Segment[] {
  const ordered = [...squad.members].sort((a, b) => (a.userId === myUserId ? -1 : b.userId === myUserId ? 1 : a.joinedUtc.localeCompare(b.joinedUtc)));
  return ordered.map((m, i) => {
    const v = value(m.userId);
    return { key: m.userId, label: m.userId === myUserId ? `${m.displayName} (${youLabel})` : m.displayName, value: v ?? 0, color: i % 4, missing: v === undefined };
  });
}

export function Home({ state, go }: PageProps) {
  const { t, n, n1, date, time, playlist } = useLocale();
  const { totals, squad, sync } = state;
  const [editing, setEditing] = useState(false);
  const from = new Date(Date.now() - 29 * 86400000);
  from.setHours(0, 0, 0, 0);
  const { data: timeline } = useLive<TimelinePoint[]>(`/api/timeline?from=${from.toISOString()}&to=${new Date(Date.now() + 86400000).toISOString()}`, ['matches', 'settings']);
  const outcome = lastOutcomeLine(state, t);
  const lastShared = squad?.sharedMatches.find((m) => m.status !== 'ambiguous');
  const personal = totals.chests + totals.manualChests;

  return (
    <div className="page">
      <section className="hero">
        <div className="hero-own">
          <div className="hero-label">
            <span className="label">{t('scope.personal')}</span>
            <button className="icon-btn pencil" onClick={() => setEditing(true)} aria-label={t('home.edit')} title={t('home.edit')}><Pencil size={16} /></button>
          </div>
          <div className="big"><Odometer value={personal} /></div>
          {totals.matches > 0 || totals.manualChests !== 0
            ? <div className="sub">
                {t('home.across', { matches: n(totals.matches) })}
                {totals.chestsDestroyed > 0 && <span className="mute">, {t('home.destroyed', { n: n(totals.chestsDestroyed) })}</span>}
                {totals.manualChests !== 0 && <span className="mute"> · {t('home.byHand', { n: n(totals.manualChests) })}</span>}
              </div>
            : <div className="empty"><strong>{t('home.noMatches')}</strong><span>{t('home.noMatchesHint')}</span></div>}
          {totals.possiblyIncompleteMatches > 0 && (
            <div className="sub small" style={{ marginTop: 8 }}>
              <span className="flag"><AlertTriangle size={14} /></span> {t('home.incomplete', { n: totals.possiblyIncompleteMatches })}
            </div>
          )}
        </div>

        {squad ? (
          <button className="panel hero-squad lift enter" style={{ textAlign: 'left', cursor: 'pointer', '--delay': '0.15s' } as CSSProperties} onClick={() => go('squad')}>
            <div>
              <div className="soft" style={{ fontWeight: 500 }}>{squad.name}</div>
              <div className="total"><Count value={squad.combinedMemberChests} /></div>
              <div className="soft small">{t('home.squadCombined')}</div>
            </div>
            <SplitBar segments={memberSegments(squad, sync.userId, (id) => squad.members.find((m) => m.userId === id)?.lifetimeChests, t('squad.you'))} />
            <Legend segments={memberSegments(squad, sync.userId, (id) => squad.members.find((m) => m.userId === id)?.lifetimeChests, t('squad.you'))} />
            {lastShared && (
              <div className="soft small">
                {t('home.lastShared')}: <b className="num" style={{ color: 'var(--text)' }}>{n(lastShared.confirmedChests)}</b>
                {lastShared.status === 'partial' && <> <span className="tag partial">{t('squad.partial')}</span></>}
              </div>
            )}
          </button>
        ) : (
          <div className="panel hero-squad enter" style={{ justifyContent: 'center', '--delay': '0.15s' } as CSSProperties}>
            <Users size={26} color="var(--cyan)" className="float" />
            <h2>{t('home.squadCta')}</h2>
            <p className="note">{t('home.squadCtaHint')}</p>
            <div><button className="btn" onClick={() => go('squad')}>{t('home.squadCtaButton')}</button></div>
          </div>
        )}
      </section>

      <section className="strip six">
        <div className="session">
          <div className="k">{t('scope.session')}</div>
          <div className="v"><Count value={totals.session.chests} /></div>
          <div className="x">{t('home.sessionSince', { time: time(totals.session.startedUtc) })}</div>
        </div>
        <div>
          <div className="k">{t('scope.last')}</div>
          <div className="v">{totals.lastMatch ? <Count value={totals.lastMatch.chests} /> : '–'}</div>
          <div className="x">{totals.lastMatch ? `${date(totals.lastMatch.when)}, ${playlist(totals.lastMatch.playlist)}` : ''}</div>
        </div>
        <div>
          <div className="k">{t('home.average')}</div>
          <div className="v">{totals.matches ? <Count value={totals.average} /> : '–'}</div>
          <div className="x">&nbsp;</div>
        </div>
        <div>
          <div className="k">{t('home.matches')}</div>
          <div className="v"><Count value={totals.matches} /></div>
          <div className="x">{outcome ?? ' '}</div>
        </div>
        <div>
          <div className="k">{t('home.best')}</div>
          <div className="v">{totals.best ? <Count value={totals.best.chests} /> : '–'}</div>
          <div className="x">{totals.best ? date(totals.best.when) : ''}</div>
        </div>
        <button className="treasure-cell" onClick={() => go('treasures')}>
          <div className="k">{t('home.treasures')} <ChevronRight size={14} /></div>
          <div className="v"><span className="star-spin">★</span> <Count value={totals.treasures} /></div>
          <div className="x">{t('home.treasuresSub', { matches: n(totals.matches), avg: n1(totals.average) })}</div>
        </button>
      </section>

      <section className="cols two feed-cols">
        <div className="panel feed-panel">
          <div className="panel-head"><h2>{t('home.feed')}</h2></div>
          <ActivityFeed state={state} />
        </div>
        <div className="side-col">
          <div className="panel">
            <div className="panel-head"><h2>{t('home.activity')}</h2></div>
            <Bars points={timeline ?? []} />
          </div>
          <div className="panel">
            <div className="panel-head">
              <h2>{t('home.recent')}</h2>
              <button className="btn quiet" onClick={() => go('matches')}>{t('nav.matches')} <ChevronRight size={15} /></button>
            </div>
            <div className="rows stagger">
              {state.recent.length === 0 && <div className="empty"><span>{t('home.noMatches')}</span></div>}
              {state.recent.slice(0, 6).map((m) => (
                <div className="row recent" key={m.matchId}>
                  <div>
                    <div>{date(m.when)}, {time(m.when)} {m.placement === 1 && <Crown size={13} className="crown" />}</div>
                    <div className="mute small">{playlist(m.playlist)}{m.placement ? `, #${m.placement}` : ''}</div>
                    {(m.treasures?.length ?? 0) > 0 && (
                      <div className="recent-treasures">{m.treasures!.filter((x) => !x.team).map((x, i) => <TreasureLabel key={i} tag={x} t={t} />)}</div>
                    )}
                  </div>
                  <div className="count">{n(m.chests)}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>
      <ManualDialog open={editing} onClose={() => setEditing(false)} state={state} />
    </div>
  );
}
