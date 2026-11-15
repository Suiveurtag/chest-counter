import { useState } from 'react';
import { useLive, type TimelinePoint } from '../api';
import { useLocale } from '../i18n';
import { Area, Bars, Legend, Seg, SplitBar } from '../parts';
import { memberSegments } from './Home';
import type { PageProps } from '../App';

type Range = '30' | '90' | 'all';

export function Insights({ state }: PageProps) {
  const { t, n } = useLocale();
  const [bucket, setBucket] = useState<'day' | 'week'>('day');
  const [range, setRange] = useState<Range>('90');
  const query = new URLSearchParams({ bucket });
  if (range !== 'all') {
    const from = new Date(Date.now() - (Number(range) - 1) * 86400000);
    from.setHours(0, 0, 0, 0);
    query.set('from', from.toISOString());
    query.set('to', new Date(Date.now() + 86400000).toISOString());
  }
  const { data: points } = useLive<TimelinePoint[]>(`/api/timeline?${query}`, ['matches', 'settings']);
  const { data: days } = useLive<TimelinePoint[]>(`/api/timeline?${new URLSearchParams([...query].filter(([k]) => k !== 'bucket'))}`, ['matches', 'settings']);
  const { totals, squad, sync } = state;

  const played = (days ?? []).filter((d) => d.matches > 0);
  const busiest = played.reduce<TimelinePoint | undefined>((best, d) => (!best || d.chests > best.chests ? d : best), undefined);
  const inRange = played.reduce((s, d) => s + d.chests, 0);
  const fmt = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long' });

  const me = squad?.members.find((m) => m.userId === sync.userId);
  const others = squad ? squad.members.filter((m) => m.userId !== sync.userId) : [];
  // Chests per match, not lifetime totals: teammates who play more would otherwise always look better.
  const perMatch = (list: { lifetimeChests: number; lifetimeMatches: number }[]) => {
    const matches = list.reduce((s, m) => s + m.lifetimeMatches, 0);
    return matches ? list.reduce((s, m) => s + m.lifetimeChests, 0) / matches : undefined;
  };
  const avg = (v: number | undefined) => (v === undefined ? '–' : v.toLocaleString(undefined, { maximumFractionDigits: 1 }));
  const myAverage = me ? perMatch([me]) : undefined;
  const squadAverage = perMatch(others);
  const lifetime = squad ? memberSegments(squad, sync.userId, (id) => squad.members.find((m) => m.userId === id)?.lifetimeChests, t('squad.you')) : [];
  const shared = squad ? memberSegments(squad, sync.userId, (id) => squad.members.find((m) => m.userId === id)?.sharedChests, t('squad.you')) : [];

  return (
    <div className="page">
      <div className="page-head">
        <h1>{t('nav.insights')}</h1>
        <div className="actions">
          <Seg value={range} onChange={setRange} options={[['30', t('matches.range.30')], ['90', t('matches.range.90')], ['all', t('matches.range.all')]]} />
          <Seg value={bucket} onChange={setBucket} options={[['day', t('insights.daily')], ['week', t('insights.weekly')]]} />
        </div>
      </div>

      <section className="strip" style={{ gridTemplateColumns: 'repeat(4, 1fr)' }}>
        <div><div className="k">{t('insights.activeDays')}</div><div className="v">{n(played.length)}</div></div>
        <div><div className="k">{t('insights.perDay')}</div><div className="v">{played.length ? n(Math.round(inRange / played.length)) : '–'}</div></div>
        <div><div className="k">{t('insights.busiestDay')}</div><div className="v">{busiest ? n(busiest.chests) : '–'}</div><div className="x">{busiest ? fmt.format(new Date(busiest.date + 'T12:00:00')) : ''}</div></div>
        <div><div className="k">{t('home.average')}</div><div className="v">{totals.matches ? totals.average.toLocaleString(undefined, { maximumFractionDigits: 1 }) : '–'}</div></div>
      </section>

      <section className="panel">
        <div className="panel-head"><h2>{t('insights.overTime')}</h2></div>
        <Bars points={points ?? []} tall />
      </section>

      <section className="cols even">
        <div className="panel">
          <div className="panel-head"><h2>{t('insights.cumulative')}</h2></div>
          {(days?.length ?? 0) >= 2 ? <Area points={days!} /> : <div className="empty"><span>{t('insights.empty')}</span></div>}
        </div>
        <div className="panel">
          <div className="panel-head"><h2>{t('insights.youVsSquad')}</h2></div>
          {!squad || !me ? <div className="empty"><span>{t('insights.noSquad')}</span></div> : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
              <div className="kinds">
                <span className="soft">{t('home.average')}</span><b />
                <span>{me.displayName} ({t('squad.you')})</span><b className="t0">{avg(myAverage)}</b>
                {others.length > 0 && <><span className="soft">{others.map((o) => o.displayName).join(', ')}</span><b className="t1">{avg(squadAverage)}</b></>}
              </div>
              <div>
                <h3 style={{ marginBottom: 8 }}>{t('insights.contributions')}, {t('squad.combined').toLowerCase()}</h3>
                <SplitBar segments={lifetime} /><div style={{ height: 8 }} /><Legend segments={lifetime} />
              </div>
              <div>
                <h3 style={{ marginBottom: 8 }}>{t('insights.contributions')}, {t('squad.together').toLowerCase()}</h3>
                <SplitBar segments={shared} /><div style={{ height: 8 }} /><Legend segments={shared} />
              </div>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
