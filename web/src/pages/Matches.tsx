import { useEffect, useState } from 'react';
import { AlertTriangle, ChevronRight, X } from 'lucide-react';
import { api, useLive, type MatchDetail, type MatchSummary, type SessionTotals } from '../api';
import { useLocale } from '../i18n';
import { Seg, Switch } from '../parts';
import type { PageProps } from '../App';

type Range = '7' | '30' | '90' | 'all';

export function Matches(_: PageProps) {
  const { t, n, date, time, playlist, dateTime } = useLocale();
  const [range, setRange] = useState<Range>('30');
  const [session, setSession] = useState('');
  const [showOff, setShowOff] = useState(false);
  const [open, setOpen] = useState<string>();
  const { data: sessions } = useLive<SessionTotals[]>('/api/sessions');

  const query = new URLSearchParams({ limit: '500', counted: showOff ? 'all' : 'true' });
  if (session) query.set('session', session);
  else if (range !== 'all') {
    const from = new Date(Date.now() - (Number(range) - 1) * 86400000);
    from.setHours(0, 0, 0, 0);
    query.set('from', from.toISOString());
  }
  const { data: matches } = useLive<MatchSummary[]>(`/api/matches?${query}`, ['matches', 'settings']);
  const counted = (matches ?? []).filter((m) => m.counted);

  return (
    <div className="page">
      <div className="page-head">
        <h1>{t('nav.matches')}</h1>
        <div className="soft">{t('matches.total', { chests: n(counted.reduce((s, m) => s + m.chests, 0)), matches: n(counted.length) })}</div>
      </div>

      <div className="actions">
        <Seg value={session ? ('' as Range) : range} onChange={(v) => { setSession(''); setRange(v); }}
          options={[['7', t('matches.range.7')], ['30', t('matches.range.30')], ['90', t('matches.range.90')], ['all', t('matches.range.all')]]} />
        {(sessions?.length ?? 0) > 0 && (
          <select className="input" style={{ width: 'auto' }} value={session} onChange={(e) => setSession(e.target.value)} aria-label={t('matches.allSessions')}>
            <option value="">{t('matches.allSessions')}</option>
            {sessions!.map((s) => <option key={s.id} value={s.id}>{t('matches.session', { date: dateTime(s.startedUtc) })} ({n(s.chests)})</option>)}
          </select>
        )}
        <div style={{ marginLeft: 'auto' }}><Switch checked={showOff} onChange={setShowOff}>{t('matches.showNotCounted')}</Switch></div>
      </div>

      <div className="panel" style={{ paddingTop: 12 }}>
        {matches && matches.length === 0 && <div className="empty"><strong>{t('matches.empty')}</strong></div>}
        {matches && matches.length > 0 && (
          <div className="rows">
            <div className="row match head">
              <span>{t('matches.when')}</span><span>{t('matches.mode')}</span><span className="r">{t('matches.place')}</span>
              <span className="r">{t('matches.ammo')}</span><span className="r">{t('matches.other')}</span><span className="r">{t('matches.chests')}</span><span />
            </div>
            {matches.map((m) => (
              <button className={`row match${m.counted ? '' : ' off'}`} key={m.matchId} onClick={() => setOpen(m.matchId)}>
                <span>{date(m.when)}, {time(m.when)}</span>
                <span>
                  {playlist(m.playlist)}
                  {!m.counted && <> <span className="tag muted">{t(`matches.reason.${m.notCountedReason}` as never)}</span></>}
                  {m.possiblyIncomplete && <> <span className="flag" title={t('matches.flag.incomplete')}><AlertTriangle size={14} /></span></>}
                </span>
                <span className="r soft">{m.placement ? `#${m.placement}` : ''}</span>
                <span className="r soft">{n(m.ammoBoxes)}</span>
                <span className="r soft">{n(m.otherContainers)}</span>
                <span className="count">{n(m.chests)}</span>
                <ChevronRight size={16} color="var(--mute)" />
              </button>
            ))}
          </div>
        )}
      </div>
      {open && <MatchDrawer id={open} onClose={() => setOpen(undefined)} />}
    </div>
  );
}

function MatchDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { t, n, dateTime, playlist, duration } = useLocale();
  const [data, setData] = useState<MatchDetail>();
  useEffect(() => { api.get<MatchDetail>(`/api/matches/${encodeURIComponent(id)}`).then(setData).catch(() => onClose()); }, [id]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  if (!data) return null;
  const m = data.match;
  const d = data.detail;
  const length = Math.max(m.durationSeconds ?? 0, ...(d?.chests.map((c) => c.timeSeconds) ?? [0]), 1);
  return (
    <>
      <div className="scrim" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-modal="true">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
          <div>
            <div className="soft">{dateTime(m.when)}</div>
            <div className="mute small">{playlist(m.playlist)}{m.durationSeconds ? `, ${duration(m.durationSeconds)}` : ''}{m.placement ? `, #${m.placement}` : ''}</div>
          </div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')}><X size={18} /></button>
        </div>
        <div>
          <div className="big" style={m.counted ? undefined : { color: 'var(--mute)' }}>{n(m.chests)}</div>
          <div className="soft">{t('matches.chests').toLowerCase()}{m.chestsDestroyed > 0 && `, ${m.chestsDestroyed} ${t('matches.detail.destroyed')}`}</div>
          {!m.counted && <div style={{ marginTop: 8 }}><span className="tag muted">{t(`matches.reason.${m.notCountedReason}` as never)}</span></div>}
        </div>
        {m.possiblyIncomplete && <p className="note"><span className="flag"><AlertTriangle size={14} /></span> {t('matches.flag.incomplete')}</p>}
        {!m.identityReliable && <p className="note">{t('matches.flag.local')}</p>}

        {d && Object.keys(d.byKind).length > 0 && (
          <div>
            <h3 style={{ marginBottom: 10 }}>{t('matches.detail.kinds')}</h3>
            <div className="kinds">
              {Object.entries(d.byKind).sort((a, b) => b[1] - a[1]).map(([kind, count]) => (
                <div key={kind} style={{ display: 'contents' }}><span>{t(`kind.${kind}` as never)}</span><b>{n(count)}</b></div>
              ))}
            </div>
          </div>
        )}
        {d && d.chests.length > 0 && (
          <div>
            <h3 style={{ marginBottom: 10 }}>{t('matches.detail.timeline')}</h3>
            <div className="ticks">
              {d.chests.map((c, i) => <i key={i} className={c.destroyed ? 'destroyed' : ''} style={{ left: `${(c.timeSeconds / length) * 100}%` }} title={`${duration(Math.round(c.timeSeconds))} ${t(`kind.${c.kind}` as never)}`} />)}
            </div>
            <div className="axis"><span>0:00</span><span>{duration(length)}</span></div>
          </div>
        )}
        {d && d.roster.length > 0 && (
          <div>
            <h3>{t('matches.detail.team')}</h3>
            <p className="mute small" style={{ margin: '4px 0 10px' }}>{t('matches.detail.teamHint')}</p>
            <div className="kinds">
              {d.roster.map((r, i) => <div key={i} style={{ display: 'contents' }}><span>{r.name || (r.isBot ? 'Bot' : '?')}</span><b className="soft">{n(r.observedChests)}</b></div>)}
            </div>
          </div>
        )}
        {d && d.treasures.length > 0 && (
          <div>
            <h3 style={{ marginBottom: 10 }}>{t('matches.detail.treasures')}</h3>
            <div className="kinds">
              {d.treasures.map((x, i) => <div key={i} style={{ display: 'contents' }}><span className="selectable">{x.prizeId ?? x.rawItem}</span><b className="soft">{x.timeSeconds != null ? duration(Math.round(x.timeSeconds)) : ''}</b></div>)}
            </div>
          </div>
        )}
        <div className="mute small selectable" style={{ marginTop: 'auto' }}>
          {t('matches.detail.file')}: {m.replayFile}<br />{t('matches.detail.parser', { v: m.parserVersion })}{data.gameVersion ? `, ${data.gameVersion}` : ''}<br />{m.matchId}
        </div>
      </aside>
    </>
  );
}
