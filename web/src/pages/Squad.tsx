import { useEffect, useRef, useState } from 'react';
import { Check, Copy, Crown, Link2, LogOut, Pencil, Plus, UserMinus, WifiOff } from 'lucide-react';
import { api, type Squad, type SquadFind } from '../api';
import { useLocale } from '../i18n';
import { Count, Legend, SplitBar, useAction, type Segment } from '../parts';
import { Stars, Thumb, TreasureLabel } from '../hunt';
import { memberSegments } from './Home';
import type { PageProps } from '../App';

export function SquadPage(props: PageProps) {
  const { state } = props;
  return state.squad ? <SquadView {...props} squad={state.squad} /> : <SquadSetup {...props} />;
}

/** Not in a squad yet: join with a pasted invite (one field), or create one on a server. */
function SquadSetup({ state }: PageProps) {
  const { t } = useLocale();
  const connected = state.sync.state !== 'disabled';
  const [name, setName] = useState(state.sync.displayName ?? state.profile?.name ?? '');
  const [invite, setInvite] = useState('');
  const [server, setServer] = useState(state.sync.server ?? '');
  const [key, setKey] = useState('');
  const [squadName, setSquadName] = useState('');
  const join = useAction();
  const create = useAction();

  const nameField = !connected && (
    <div className="field">
      <label htmlFor="display-name">{t('squad.displayName')}</label>
      <input id="display-name" className="input" value={name} maxLength={32} onChange={(e) => setName(e.target.value)} />
    </div>
  );

  return (
    <div className="page">
      <div className="page-head"><h1>{t('squad.setupTitle')}</h1></div>
      <p className="note">{t('squad.setupLead')}</p>
      {nameField}
      <div className="cols even">
        <form className="panel form" onSubmit={(e) => { e.preventDefault(); join.run(() => api.post('/api/squad/join', { code: invite, displayName: name, registrationKey: key })); }}>
          <div><h2>{t('squad.joinTitle')}</h2><p className="note">{t('squad.joinHint')}</p></div>
          <input className="input" aria-label={t('squad.joinTitle')} placeholder={t('squad.invitePlaceholder')} value={invite} onChange={(e) => setInvite(e.target.value)} />
          {join.error && <div className="error">{join.error}</div>}
          <div><button className="btn primary" disabled={join.busy || !invite.trim() || (!connected && !name.trim())}>{t('squad.join')}</button></div>
        </form>

        <form className="panel form" onSubmit={(e) => {
          e.preventDefault();
          create.run(async () => {
            if (!connected) await api.post('/api/squad/connect', { server, displayName: name, registrationKey: key });
            await api.post('/api/squad/create', { name: squadName });
          });
        }}>
          <div><h2>{t('squad.createTitle')}</h2><p className="note">{t('squad.createHint')}</p></div>
          {!connected && (
            <div className="field">
              <label htmlFor="server">{t('squad.server')}</label>
              <input id="server" className="input" placeholder="https://squad.example.com" value={server} onChange={(e) => setServer(e.target.value)} />
            </div>
          )}
          <div className="field">
            <label htmlFor="squad-name">{t('squad.squadName')}</label>
            <input id="squad-name" className="input" maxLength={40} value={squadName} onChange={(e) => setSquadName(e.target.value)} />
          </div>
          {create.error && <div className="error">{create.error}</div>}
          <div><button className="btn" disabled={create.busy || !squadName.trim() || (!connected && (!server.trim() || !name.trim()))}>{t('squad.create')}</button></div>
        </form>
      </div>
      {!connected && (
        <div className="field" style={{ maxWidth: 420 }}>
          <label htmlFor="reg-key">{t('squad.registrationKey')}</label>
          <input id="reg-key" className="input" value={key} onChange={(e) => setKey(e.target.value)} />
        </div>
      )}
    </div>
  );
}

/** The squad's latest play session, in the session colour: live while the last match ended less than 2 h ago. */
function SessionBand({ squad, me }: { squad: Squad; me?: string }) {
  const { t, n, time, ago } = useLocale();
  const s = squad.session!;
  const segments: Segment[] = memberSegments(squad, me, (id) => s.members.find((m) => m.userId === id)?.chests, t('squad.you'))
    .filter((seg) => seg.value > 0);
  return (
    <section className={`session-band${s.active ? ' live' : ''}`} title={t('squad.session.hint')}>
      <div className="sb-head">
        <span className="sb-title">{s.active && <i className="sb-dot" aria-hidden="true" />}{t(s.active ? 'squad.session.live' : 'squad.session.over')}</span>
        <span className="mute small">{s.active ? t('squad.session.since', { time: time(s.startUtc) }) : t('squad.session.ended', { time: ago(s.endUtc) })}</span>
      </div>
      <div className="sb-body">
        <div className="sb-total"><Count value={s.chests} /></div>
        <div className="sb-facts">
          <span>{t('squad.session.matches', { n: n(s.matches) })}</span>
          {s.sharedMatches > 0 && <span className="t1">{t('squad.session.together', { n: n(s.sharedChests) })}</span>}
          {s.treasures > 0 && <span className="t3"><Stars golden={false} /> {t('squad.session.treasures', { n: n(s.treasures) })}</span>}
        </div>
        {segments.length > 0 && (
          <div className="sb-split"><SplitBar segments={segments} thin /><Legend segments={segments} /></div>
        )}
      </div>
    </section>
  );
}

/** The members' latest treasures, with their picture, who found it and whether it was a match played together. */
function FindsPanel({ finds, squad, colorOf }: { finds: SquadFind[]; squad: Squad; colorOf: (userId: string) => number }) {
  const { t, date } = useLocale();
  const [showAll, setShowAll] = useState(false);
  const shown = showAll ? finds : finds.slice(0, 8);
  return (
    <section className="panel">
      <div className="panel-head"><h2>{t('squad.finds')}</h2></div>
      {finds.length === 0
        ? <div className="empty"><strong>{t('squad.findsEmpty')}</strong><span>{t('squad.findsEmptyHint')}</span></div>
        : (
          <div className="finds">
            {shown.map((f, i) => {
              const who = squad.members.find((m) => m.userId === f.userId)?.displayName ?? '';
              return (
                <div className={`find${f.tag.golden ? ' golden' : ''}`} key={`${f.matchId}-${f.userId}-${i}`} style={{ animationDelay: `${Math.min(i, 8) * 0.04}s` }}>
                  <Thumb src={f.tag.prizeKey ? `/treasure-img/${encodeURIComponent(f.tag.prizeKey)}` : undefined} />
                  <div className="find-text">
                    <TreasureLabel tag={f.tag} t={t} />
                    <div className="small">
                      <span className={`t${colorOf(f.userId)}`}>{who}</span>
                      <span className="mute"> · {date(f.matchStartUtc)}</span>
                      {f.shared && <span className="tag together">{t('squad.findTogether')}</span>}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      {!showAll && finds.length > 8 && (
        <div className="actions" style={{ marginTop: 10 }}>
          <button className="btn quiet" onClick={() => setShowAll(true)}>{t('common.showAll', { n: finds.length })}</button>
        </div>
      )}
    </section>
  );
}

function SquadView({ state, squad }: PageProps & { squad: Squad }) {
  const { t, n, ago, date, time, playlist } = useLocale();
  const [showAll, setShowAll] = useState(false);
  const me = state.sync.userId;
  const iAmOwner = squad.members.find((m) => m.userId === me)?.role === 'owner';
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(squad.name);
  const [copied, setCopied] = useState<string>();
  const action = useAction();
  const order = memberSegments(squad, me, () => 0, t('squad.you'));
  const colorOf = (userId: string) => order.find((s) => s.key === userId)?.color ?? 0;

  // A member's card lights up once when their total changes while the page is open.
  const seen = useRef<Record<string, number>>({});
  const [arrived, setArrived] = useState<Record<string, number>>({});
  useEffect(() => {
    const now: Record<string, number> = {};
    for (const m of squad.members) {
      if (seen.current[m.userId] !== undefined && seen.current[m.userId] !== m.lifetimeChests) now[m.userId] = squad.revision;
      seen.current[m.userId] = m.lifetimeChests;
    }
    if (Object.keys(now).length) setArrived((a) => ({ ...a, ...now }));
  }, [squad]);

  const copy = (link: string) => {
    navigator.clipboard.writeText(link).then(() => { setCopied(link); setTimeout(() => setCopied(undefined), 1600); });
  };
  const lifetime = memberSegments(squad, me, (id) => squad.members.find((m) => m.userId === id)?.lifetimeChests, t('squad.you'));
  const shared = memberSegments(squad, me, (id) => squad.members.find((m) => m.userId === id)?.sharedChests, t('squad.you'));
  const free = Math.max(0, squad.maxMembers - squad.members.length);
  const unknownTreasures = squad.members.reduce((sum, m) => sum + (m.treasuresUnknownMatches ?? 0), 0);
  const liveSession = squad.session?.active ? squad.session : undefined;
  const sessionOf = (userId: string) => liveSession?.members.find((x) => x.userId === userId && x.chests > 0);

  return (
    <div className="page">
      <div className="page-head">
        {renaming ? (
          <form className="actions" onSubmit={(e) => { e.preventDefault(); action.run(() => api.post('/api/squad/rename', { name })).then((ok) => ok && setRenaming(false)); }}>
            <input className="input" style={{ width: 280 }} value={name} maxLength={40} autoFocus onChange={(e) => setName(e.target.value)} />
            <button className="btn primary" disabled={!name.trim()}>{t('squad.rename')}</button>
            <button type="button" className="btn quiet" onClick={() => setRenaming(false)}>{t('common.cancel')}</button>
          </form>
        ) : (
          <h1>{squad.name} {iAmOwner && <button className="icon-btn" style={{ display: 'inline-grid', verticalAlign: 'middle' }} title={t('squad.rename')} onClick={() => { setName(squad.name); setRenaming(true); }}><Pencil size={15} /></button>}</h1>
        )}
        <div className="soft small" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          {state.sync.state === 'offline' && <WifiOff size={15} color="var(--danger)" />}
          {t(`squad.state.${state.sync.state === 'disabled' ? 'offline' : state.sync.state}` as never)}
        </div>
      </div>
      {(state.sync.pending > 0 || state.sync.refused > 0 || action.error) && (
        <div className="small">
          {state.sync.pending > 0 && <div className="soft">{t('squad.pending', { n: state.sync.pending })}</div>}
          {state.sync.refused > 0 && <div className="error">{t('squad.refused', { n: state.sync.refused })}</div>}
          {action.error && <div className="error">{action.error}</div>}
        </div>
      )}

      {squad.session && <SessionBand squad={squad} me={me} />}

      <section className={`duo${squad.combinedTreasures !== undefined ? ' trio' : ''}`}>
        <div>
          <div className="k">{t('squad.combined')}</div>
          <div className="v"><Count value={squad.combinedMemberChests} /></div>
          <p>{t('squad.combinedHint')}</p>
        </div>
        <div>
          <div className="k">{t('squad.together')}</div>
          <div className="v"><Count value={squad.sharedChests} /></div>
          <p>{t('squad.togetherHint', { n: squad.sharedMatchCount })}</p>
        </div>
        {squad.combinedTreasures !== undefined && (
          <div className="treasure-col">
            <div className="k"><Stars golden={false} /> {t('squad.treasures')}</div>
            <div className="v"><Count value={squad.combinedTreasures} /></div>
            <p>{t('squad.treasuresHint', { n: squad.sharedTreasures ?? 0 })}</p>
            {unknownTreasures > 0 && <p className="mute small">{t('squad.treasuresUnknown', { n: unknownTreasures })}</p>}
          </div>
        )}
      </section>

      <section>
        <div className="members">
          {order.map((seg) => {
            const m = squad.members.find((x) => x.userId === seg.key)!;
            return (
              <div key={m.userId + (arrived[m.userId] ?? '')} className={`member t${seg.color}${arrived[m.userId] ? ' arrived' : ''}`}>
                <div className="name">
                  <i className={`presence${m.online ? ' on' : ''}`} title={m.online ? t('squad.online') : t('squad.offline')} />
                  {m.displayName}
                  {m.role === 'owner' && <Crown size={13} color="var(--mute)" aria-label={t('squad.owner')} />}
                  {m.userId === me && <span className="mute small">{t('squad.you')}</span>}
                  {iAmOwner && m.userId !== me && (
                    <button className="icon-btn" title={t('squad.remove')} onClick={() => confirm(`${t('squad.remove')}: ${m.displayName}?`) && action.run(() => api.post('/api/squad/remove', { userId: m.userId }))}>
                      <UserMinus size={15} />
                    </button>
                  )}
                </div>
                <div className="big"><Count value={m.lifetimeChests} /></div>
                <div className="meta">{t('squad.lifetime')}, {n(m.sharedChests)} {t('squad.withSquad')}</div>
                {m.lastMatch && (
                  <div className="meta">
                    {t('squad.lastMatch', { n: n(m.lastMatch.chests) })}
                    {m.bestMatch && m.bestMatch.chests > m.lastMatch.chests && <span className="mute"> · {t('squad.best', { n: n(m.bestMatch.chests) })}</span>}
                  </div>
                )}
                {(m.lifetimeTreasures !== undefined || sessionOf(m.userId)) && (
                  <div className="chips">
                    {m.lifetimeTreasures !== undefined && m.lifetimeTreasures > 0 && <span className="chip violet"><Stars golden={false} />{t('squad.memberTreasures', { n: n(m.lifetimeTreasures) })}</span>}
                    {sessionOf(m.userId) && <span className="chip pink">{t('squad.session.tonight', { n: n(sessionOf(m.userId)!.chests) })}</span>}
                  </div>
                )}
                <div className="foot">{m.lastSyncUtc ? t('squad.lastSync', { time: ago(m.lastSyncUtc) }) : t('squad.neverSynced')}</div>
              </div>
            );
          })}
          {Array.from({ length: free }, (_, i) => (
            <div className="member empty" key={i}><Plus size={18} />{t('squad.emptySlot')}</div>
          ))}
        </div>
      </section>

      <section className="cols even">
        <div className="panel">
          <div className="panel-head"><h2>{t('squad.combined')}</h2></div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}><SplitBar segments={lifetime} /><Legend segments={lifetime} /></div>
        </div>
        <div className="panel">
          <div className="panel-head"><h2>{t('squad.together')}</h2></div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}><SplitBar segments={shared} /><Legend segments={shared} /></div>
        </div>
      </section>

      {state.squadFinds && squad.combinedTreasures !== undefined && <FindsPanel finds={state.squadFinds} squad={squad} colorOf={colorOf} />}

      <section className="panel">
        <div className="panel-head"><h2>{t('squad.history')}</h2></div>
        {squad.sharedMatches.length === 0 && <div className="empty"><strong>{t('squad.historyEmpty')}</strong><span>{t('squad.historyEmptyHint')}</span></div>}
        <div className="rows">
          {(showAll ? squad.sharedMatches : squad.sharedMatches.slice(0, 12)).map((m, i) => {
            const segments = [
              ...m.contributions.map((c) => ({ key: c.userId, label: squad.members.find((x) => x.userId === c.userId)?.displayName ?? c.playerName, value: c.chests, color: colorOf(c.userId) })),
              ...m.missing.map((x) => ({ key: x.userId, label: x.name, value: 0, color: colorOf(x.userId), missing: true })),
            ].sort((a, b) => order.findIndex((o) => o.key === a.key) - order.findIndex((o) => o.key === b.key));
            const estimate = m.missing.reduce((sum, x) => sum + (x.observedEstimate ?? 0), 0);
            return (
              <div className="row shared" key={m.matchId + i}>
                <div>
                  <div>{date(m.startUtc)}</div>
                  <div className="mute small">{time(m.startUtc)}, {playlist(m.playlist)}</div>
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 7, minWidth: 0 }}>
                  <SplitBar segments={segments} thin />
                  <Legend segments={segments} />
                  {m.status === 'partial' && (
                    <div className="small soft">
                      <span className="tag partial">{t('squad.partial')}</span> {t('squad.partialHint', { names: m.missing.map((x) => x.name).join(', ') })}
                      {estimate > 0 && <span className="mute"> ({t('squad.seenByOthers', { n: estimate })})</span>}
                    </div>
                  )}
                  {m.status === 'ambiguous' && (
                    <div className="small soft">
                      <span className="tag warn">{t('squad.ambiguous')}</span> {t('squad.ambiguousHint')} <span className="mute">({m.warnings.map((w) => t(`squad.warn.${w}` as never)).join(', ')})</span>
                    </div>
                  )}
                </div>
                <div className="count" style={{ color: m.status === 'ambiguous' ? 'var(--mute)' : 'var(--cyan)' }}>
                  {n(m.confirmedChests)}{m.status === 'partial' && <span className="t2">+</span>}
                </div>
              </div>
            );
          })}
        </div>
        {!showAll && squad.sharedMatches.length > 12 && (
          <div className="actions" style={{ marginTop: 10 }}>
            <button className="btn quiet" onClick={() => setShowAll(true)}>{t('common.showAll', { n: squad.sharedMatches.length })}</button>
          </div>
        )}
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>{t('squad.invites')}</h2>
          <button className="btn" disabled={free === 0 || action.busy} onClick={() => action.run(() => api.post('/api/squad/invite'))}><Link2 size={16} />{t('squad.newInvite')}</button>
        </div>
        {free === 0 ? <p className="note">{t('squad.full')}</p> : squad.invites.length === 0 && <p className="note">{t('squad.noInvites')}</p>}
        <div className="rows">
          {squad.invites.map((invite) => (
            <div className="row" key={invite.code} style={{ gridTemplateColumns: '1fr auto auto' }}>
              <div style={{ minWidth: 0 }}>
                <div className="code selectable">{invite.link}</div>
                <div className="mute small" style={{ marginTop: 4 }}>{t('squad.inviteUses', { uses: invite.uses, max: invite.maxUses, date: date(invite.expiresUtc) })}</div>
              </div>
              <button className="btn" onClick={() => copy(invite.link)}>{copied === invite.link ? <Check size={16} /> : <Copy size={16} />}{copied === invite.link ? t('squad.copied') : t('squad.copy')}</button>
              <button className="btn quiet" onClick={() => action.run(() => api.post('/api/squad/revoke', { code: invite.code }))}>{t('squad.revoke')}</button>
            </div>
          ))}
        </div>
      </section>

      <div>
        <button className="btn danger" onClick={() => confirm(t('squad.leaveConfirm')) && action.run(() => api.post('/api/squad/leave'))}><LogOut size={16} />{t('squad.leave')}</button>
      </div>
    </div>
  );
}
