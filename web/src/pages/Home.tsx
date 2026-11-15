import { AlertTriangle, Users } from 'lucide-react';
import { useLive, type Squad, type TimelinePoint } from '../api';
import { useLocale } from '../i18n';
import { Bars, Count, Legend, SplitBar, type Segment } from '../parts';
import { lastOutcomeLine, type PageProps } from '../App';

/** Members in a stable order, the local player first: a member keeps the same colour on every screen. */
export function memberSegments(squad: Squad, myUserId: string | undefined, value: (userId: string) => number | undefined, youLabel: string): Segment[] {
  const ordered = [...squad.members].sort((a, b) => (a.userId === myUserId ? -1 : b.userId === myUserId ? 1 : a.joinedUtc.localeCompare(b.joinedUtc)));
  return ordered.map((m, i) => {
    const v = value(m.userId);
    return { key: m.userId, label: m.userId === myUserId ? `${m.displayName} (${youLabel})` : m.displayName, value: v ?? 0, color: i % 4, missing: v === undefined };
  });
}

export function Home({ state, go }: PageProps) {
  const { t, n, date, time, playlist } = useLocale();
  const { totals, squad, sync } = state;
  const from = new Date(Date.now() - 29 * 86400000);
  from.setHours(0, 0, 0, 0);
  const { data: timeline } = useLive<TimelinePoint[]>(`/api/timeline?from=${from.toISOString()}&to=${new Date(Date.now() + 86400000).toISOString()}`, ['matches', 'settings']);
  const outcome = lastOutcomeLine(state, t);
  const lastShared = squad?.sharedMatches.find((m) => m.status !== 'ambiguous');

  return (
    <div className="page">
      <section className="hero">
        <div className="hero-own">
          <div className="label">{t('scope.personal')}</div>
          <div className="big"><Count value={totals.chests} /></div>
          {totals.matches > 0
            ? <div className="sub">
                {t('home.across', { matches: n(totals.matches) })}
                {totals.chestsDestroyed > 0 && <span className="mute">, {t('home.destroyed', { n: n(totals.chestsDestroyed) })}</span>}
              </div>
            : <div className="empty"><strong>{t('home.noMatches')}</strong><span>{t('home.noMatchesHint')}</span></div>}
          {totals.possiblyIncompleteMatches > 0 && (
            <div className="sub small" style={{ marginTop: 8 }}>
              <span className="flag"><AlertTriangle size={14} /></span> {t('home.incomplete', { n: totals.possiblyIncompleteMatches })}
            </div>
          )}
        </div>

        {squad ? (
          <button className="panel hero-squad" style={{ textAlign: 'left', cursor: 'pointer' }} onClick={() => go('squad')}>
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
          <div className="panel hero-squad" style={{ justifyContent: 'center' }}>
            <Users size={26} color="var(--cyan)" />
            <h2>{t('home.squadCta')}</h2>
            <p className="note">{t('home.squadCtaHint')}</p>
            <div><button className="btn" onClick={() => go('squad')}>{t('home.squadCtaButton')}</button></div>
          </div>
        )}
      </section>

      <section className="strip">
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
          <div className="v">{totals.matches ? totals.average.toLocaleString(undefined, { maximumFractionDigits: 1 }) : '–'}</div>
          <div className="x">&nbsp;</div>
        </div>
        <div>
          <div className="k">{t('home.matches')}</div>
          <div className="v"><Count value={totals.matches} /></div>
          <div className="x">{outcome ?? ' '}</div>
        </div>
        <div>
          <div className="k">{t('home.best')}</div>
          <div className="v">{totals.best ? n(totals.best.chests) : '–'}</div>
          <div className="x">{totals.best ? date(totals.best.when) : ''}</div>
        </div>
      </section>

      <section className="cols two">
        <div className="panel">
          <div className="panel-head"><h2>{t('home.activity')}</h2></div>
          <Bars points={timeline ?? []} />
        </div>
        <div className="panel">
          <div className="panel-head">
            <h2>{t('home.recent')}</h2>
            <button className="btn quiet" onClick={() => go('matches')}>{t('nav.matches')}</button>
          </div>
          <div className="rows">
            {state.recent.length === 0 && <div className="empty"><span>{t('home.noMatches')}</span></div>}
            {state.recent.slice(0, 6).map((m) => (
              <div className="row recent" key={m.matchId}>
                <div>
                  <div>{date(m.when)}, {time(m.when)}</div>
                  <div className="mute small">{playlist(m.playlist)}{m.placement ? `, #${m.placement}` : ''}</div>
                </div>
                <div className="count">{n(m.chests)}</div>
              </div>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
