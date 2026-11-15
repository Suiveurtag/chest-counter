import { useState, type CSSProperties } from 'react';
import { motion } from 'motion/react';
import { Check, Flag, Gem, Hourglass, Lock, Star, Timer } from 'lucide-react';
import { api, useLive, type ChanceView, type HuntPrizeView, type HuntReport, type TreasureView } from '../api';
import { useLocale } from '../i18n';
import { Count, Switch } from '../parts';
import { Burst, Fold, Pager, easeOut, useNow, usePager, useStoredFlag } from '../fx';
import { countdown, expected, oneInHuge, percent, Thumb, TreasureLabel, verdict } from '../hunt';
import type { PageProps } from '../App';

export function Treasures(_: PageProps) {
  const { t, n, lang } = useLocale();
  const { data: hunt } = useLive<HuntReport>('/api/hunt', ['matches', 'hunt', 'settings']);
  const now = useNow(1000);
  if (!hunt) return <div className="page"><div className="skeleton" style={{ height: 220 }} /><div className="skeleton" style={{ height: 420 }} /></div>;

  const week = hunt.week;
  const weekLeft = week ? new Date(week.to).getTime() - now : 0;
  const every = hunt.treasures > 0 ? Math.round(hunt.seasonChests / hunt.treasures) : 0;

  return (
    <div className="page hunt">
      <div className="page-head">
        <div>
          <h1 className="shine-title">{t('hunt.title')}</h1>
          {week && <div className="soft">{t('hunt.week', { n: week.number, title: week.title })}</div>}
        </div>
        {week && weekLeft > 0 && (
          <div className="leaves">
            <span className="mute small">{t('hunt.leavesIn')}</span>
            <b className="num">{countdown(weekLeft, lang)}</b>
          </div>
        )}
      </div>

      <section className="tiles">
        <div className="tile violet">
          <div className="k"><Star size={15} /> {t('hunt.treasuresFound')}</div>
          <div className="v">★ <Count value={hunt.treasures} /></div>
          <div className="x">{every > 0 ? t('hunt.every', { n: n(every) }) : ' '}{hunt.teamTreasures > 0 && <> · {t('hunt.teamPlus', { n: hunt.teamTreasures })}</>}</div>
        </div>
        <div className="tile gold">
          <div className="k"><Gem size={15} /> {t('hunt.seasonChests')}</div>
          <div className="v"><Count value={hunt.seasonChests} /></div>
          <div className="x">{t('hunt.since', { date: new Date(hunt.huntStart).toLocaleDateString(lang === 'fr' ? 'fr-FR' : undefined, { day: 'numeric', month: 'long' }) })}{hunt.manual !== 0 && <> · {t('hunt.byHandShort', { n: n(hunt.manual) })}</>}</div>
        </div>
        <div className="tile pink">
          <div className="k"><Timer size={15} /> {t('hunt.weekChests')}</div>
          <div className="v"><Count value={hunt.weekChests} /></div>
          <div className="x">{week ? t('hunt.week', { n: week.number, title: week.title }) : ' '}</div>
        </div>
        <div className="tile ring-tile">
          <Ring value={hunt.collectionGot} total={hunt.collectionTotal} />
          <div>
            <div className="k">{t('hunt.collection')}</div>
            <div className="v"><Count value={hunt.collectionGot} /><span className="mute">/{hunt.collectionTotal}</span></div>
          </div>
        </div>
      </section>

      {!hunt.over && <HourBanner hunt={hunt} now={now} />}
      {hunt.over && <p className="note">{t('hunt.over')}</p>}
      {!hunt.over && <Chances hunt={hunt} now={now} />}
      {!hunt.over && <Collection hunt={hunt} />}

      <section className="cols even">
        <ByKind hunt={hunt} />
        <Sources hunt={hunt} />
      </section>

      <Finds hunt={hunt} />
    </div>
  );
}

/** Progress ring that sweeps to its value. */
function Ring({ value, total }: { value: number; total: number }) {
  const r = 26, c = 2 * Math.PI * r;
  return (
    <svg className="ring" viewBox="0 0 64 64" width="64" height="64" aria-hidden="true">
      <circle cx="32" cy="32" r={r} className="ring-bg" />
      <motion.circle cx="32" cy="32" r={r} className="ring-fg" strokeDasharray={c}
        initial={{ strokeDashoffset: c }} animate={{ strokeDashoffset: c * (1 - (total ? value / total : 0)) }} transition={{ duration: 1.4, ease: easeOut, delay: 0.3 }} />
    </svg>
  );
}

function HourBanner({ hunt, now }: { hunt: HuntReport; now: number }) {
  const { t, lang } = useLocale();
  const live = hunt.hours.find((h) => now >= new Date(h.start).getTime() && now < new Date(h.end).getTime());
  const next = hunt.hours.find((h) => new Date(h.start).getTime() > now);
  const hour = live ?? next;
  if (!hour) return null;
  const fmt = new Intl.DateTimeFormat(lang === 'fr' ? 'fr-FR' : undefined, { weekday: 'long', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });
  const timeFmt = new Intl.DateTimeFormat(lang === 'fr' ? 'fr-FR' : undefined, { hour: 'numeric', minute: '2-digit' });
  return (
    <motion.div className={`hour-banner${live ? ' live' : ''}`} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: easeOut }}>
      <Hourglass size={20} className={live ? 'spin-slow' : ''} />
      <div>
        <div className="hb-title">
          {live
            ? <><b>{t('hunt.hourLive')}</b> · {t('hunt.hourUntil', { time: timeFmt.format(new Date(live.end)) })} · <span className="num">{t('hunt.hourEndsIn', { d: countdown(new Date(live.end).getTime() - now, lang) })}</span></>
            : <><b>{t('hunt.hourNext')}</b>: {fmt.format(new Date(hour.start))} · <span className="num">{t('hunt.hourIn', { d: countdown(new Date(hour.start).getTime() - now, lang) })}</span> <span className="mute">({hour.confirmed ? t('hunt.hourConfirmed') : t('hunt.hourExpected')})</span></>}
        </div>
        <div className="hb-boosts">
          {Object.entries(hour.boosts).map(([kind, x], i) => (
            <motion.span key={kind} className="boost" initial={{ opacity: 0, scale: 0.8 }} animate={{ opacity: 1, scale: 1 }} transition={{ delay: 0.3 + i * 0.08, type: 'spring', stiffness: 400, damping: 20 }}>
              {t(`hunt.kind.${kind}` as never)} <b>×{x.toLocaleString(lang === 'fr' ? 'fr-FR' : 'en-US')}</b>
            </motion.span>
          ))}
        </div>
      </div>
    </motion.div>
  );
}

function Pct({ c }: { c: ChanceView }) {
  const { n, lang } = useLocale();
  const title = c.p * 100 >= 99.95 ? `${lang === 'fr' ? 'ne pas l’avoir eu : 1 sur' : 'never had it: 1 in'} ${oneInHuge(c.logNever, n)}` : undefined;
  return <span title={title}><Count value={c.p} format={(v) => percent(v, lang)} /></span>;
}

function Chip({ p }: { p: number }) {
  const { t } = useLocale();
  const v = verdict(p);
  return <span className={`vchip ${v}`}>{t(`verdict.${v}` as never)}</span>;
}

function Meter({ p, delay = 0 }: { p: number; delay?: number }) {
  return <span className="cbar"><i className={verdict(p)} style={{ '--w': `${Math.min(100, Math.max(0.8, p * 100))}%`, '--d': `${delay}ms` } as CSSProperties} /></span>;
}

function Chances({ hunt, now }: { hunt: HuntReport; now: number }) {
  const { t, n, lang } = useLocale();
  const week = hunt.week;
  const weekly = (kind: string) => hunt.weekChances.find((c) => c.kind === kind);
  const season = (kind: string) => hunt.seasonChances.find((c) => c.kind === kind)!;
  const prize = (kind: string) => week?.prizes.find((p) => p.kind === kind);
  const got = (p?: HuntPrizeView) => p?.obtained != null;
  const loading = hunt.odds.find((o) => o.kind === 'loading');

  const outfit = weekly('outfit');
  const pirate = season('pirate-shop');
  const outfitPrize = prize('outfit');
  const weekLeft = week ? new Date(week.to).getTime() - now : 0;

  const card = (c: ChanceView, p: HuntPrizeView | undefined, weeklyCard: boolean, extra: JSX.Element, i: number) => (
    <div className={`hcard enter${got(p) ? ' got' : ''}`} style={{ '--delay': `${0.1 + i * 0.1}s` } as CSSProperties}>
      <div className="htop">
        <Thumb src={p?.image} className="big" />
        <div className="hname">
          <div className="k">{t(`hunt.kind.${c.kind}` as never)}</div>
          <div className="ciname">{weeklyCard ? p?.name ?? '' : t('hunt.lastsSeason')}</div>
        </div>
        <span className={`tag ${weeklyCard ? 'weekly' : 'season'}`}>{weeklyCard ? t('hunt.weekly') : t('hunt.season')}</span>
        {got(p) && <span className="tag got"><Check size={12} /> {t('hunt.gotIt')}</span>}
      </div>
      <div className="hval"><span className="big num"><Pct c={c} /></span><Chip p={c.p} /></div>
      <div className="hexp">{weeklyCard ? t('hunt.droppedWeek') : t('hunt.droppedSeason')}{c.p > 0 && c.p < 0.5 ? ` · ${t('hunt.aboutOneIn', { n: n(Math.round(1 / c.p)) })}` : ''}</div>
      <div className="mute small">{t('hunt.eachChestLine', { odds: n(c.oneIn), n: n(c.chests), scope: weeklyCard ? t('hunt.scopeWeek') : t('hunt.scopeSeason') })}</div>
      {extra}
      {c.atPace != null && <div className="cfoot">{t(weeklyCard ? 'hunt.atPaceWeek' : 'hunt.atPaceHunt', { p: percent(c.atPace, lang) })}</div>}
    </div>
  );

  const row = (c: ChanceView, p: HuntPrizeView | undefined, note: string | undefined, i: number) => (
    <motion.tr key={c.kind} className={got(p) ? 'got' : ''} initial={{ opacity: 0, x: -10 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: 0.15 + i * 0.06, duration: 0.5, ease: easeOut }}>
      <td>
        <div className="tflex">
          <Thumb src={p?.image} />
          <div>
            <div className="tk">{t(`hunt.kind.${c.kind}` as never)}{got(p) && <span className="tag got"><Check size={11} /> {t('hunt.gotIt')}</span>}</div>
            {p && <div className="iname">{p.name}</div>}
            {note && <div className="iname">{note}</div>}
            {c.weekly && c.atPace != null && <div className="iname">{t('hunt.atPaceWeek', { p: percent(c.atPace, lang) })}</div>}
          </div>
        </div>
      </td>
      <td className="r num">{t('hunt.oneIn', { n: n(c.oneIn) })}</td>
      <td className="tchance"><div className="tcv"><Chip p={c.p} /><b className="num"><Pct c={c} /></b></div><Meter p={c.p} delay={200 + i * 80} /></td>
      <td className="r num">{expected(c.expected, lang)}</td>
    </motion.tr>
  );
  const head = <tr><th>{t('hunt.col.treasure')}</th><th className="r">{t('hunt.col.each')}</th><th>{t('hunt.col.chance')}</th><th className="r">{t('hunt.col.expected')}</th></tr>;

  return (
    <Fold id="chances" title={t('hunt.chances')} info={t('hunt.chancesIntro')}>
      <p className="note intro">{t('hunt.chancesIntro')}</p>
      <div className="chances2">
        {outfit && card(outfit, outfitPrize, true, (
          <>
            <div className="cfoot">{t('hunt.allWeeks')}: <b>{percent(season('outfit').p, lang)}</b> {t('hunt.overChests', { n: n(season('outfit').chests) })}</div>
            {weekLeft > 0 && <div className="cfoot">{t('hunt.outfitLeaves')} <b className="num">{countdown(weekLeft, lang)}</b></div>}
          </>
        ), 0)}
        {card(pirate, hunt.pirateShop, false, (
          <div className="halfway">
            <div className="hrow"><span>{t('hunt.halfway')}</span><span><b>{n(pirate.chests)}</b> / {n(hunt.pirateHalfway)}</span></div>
            <span className="cbar wide"><i className="gold" style={{ '--w': `${Math.min(100, Math.max(0.8, (pirate.chests / hunt.pirateHalfway) * 100))}%`, '--d': '400ms' } as CSSProperties} /></span>
          </div>
        ), 1)}
      </div>

      {week && (
        <div className="cgroup">
          <div className="ghead">
            <span className="tag weekly">{t('hunt.weekly')}</span>
            <div>
              <div className="gt">{t('hunt.week', { n: week.number, title: week.title })}</div>
              <div className="gs"><b>{n(hunt.weekChests)}</b> {t('feed.chests')} {t('hunt.thisWeekChests')}{hunt.weekManual > 0 ? ` · ${t('hunt.byHandShort', { n: n(hunt.weekManual) })}` : ''}</div>
            </div>
            {weekLeft > 0 && <div className="cdbox"><span className="mute small">{t('hunt.leavesIn')}</span><b className="num">{countdown(weekLeft, lang)}</b></div>}
          </div>
          <div className="card-table">
            <table className="ctable"><thead>{head}</thead><tbody>
              {hunt.weekChances.filter((c) => c.kind !== 'outfit').map((c, i) => row(c, prize(c.kind), undefined, i))}
            </tbody></table>
          </div>
        </div>
      )}

      <div className="cgroup">
        <div className="ghead">
          <span className="tag season">{t('hunt.season')}</span>
          <div>
            <div className="gt">{t('hunt.allSeason')}</div>
            <div className="gs"><b>{n(hunt.seasonChests)}</b> {t('feed.chests')} {t('hunt.sinceStart', { date: new Date(hunt.huntStart).toLocaleDateString(lang === 'fr' ? 'fr-FR' : undefined, { day: 'numeric', month: 'long' }) })}{hunt.manual > 0 ? ` · ${t('hunt.byHandShort', { n: n(hunt.manual) })}` : ''}</div>
          </div>
        </div>
        <div className="card-table">
          <table className="ctable"><thead>{head}</thead><tbody>
            {hunt.seasonChances.filter((c) => !c.weekly && c.kind !== 'pirate-shop').map((c, i) => row(c, undefined,
              c.kind === 'gizmo' && hunt.gizmosFound > 0 ? t('hunt.gizmoNote', { n: n(hunt.gizmosFound), k: n(hunt.chestsSinceGizmo ?? 0) }) : undefined, i))}
          </tbody></table>
        </div>
      </div>

      <details className="cmore">
        <summary>{t('hunt.combined')}</summary>
        <motion.div initial={{ opacity: 0 }} animate={{ opacity: 1 }}>
          <p className="note">{t('hunt.combinedLead', { n: n(hunt.seasonChests) })}</p>
          <div className="card-table">
            <table className="ctable mini"><thead><tr><th>{t('hunt.col.treasure')}</th><th className="r">{t('hunt.col.chance')}</th><th className="r">{t('hunt.col.expected')}</th></tr></thead><tbody>
              {hunt.seasonChances.filter((c) => c.weekly).map((c) => (
                <tr key={c.kind}><td>{t(`hunt.kind.${c.kind}` as never)}</td><td className="r num"><b>{percent(c.p, lang)}</b></td><td className="r num">{expected(c.expected, lang)}</td></tr>
              ))}
            </tbody></table>
          </div>
        </motion.div>
      </details>
      <details className="cmore">
        <summary>{t('hunt.how')}</summary>
        <ul className="plain">
          <li>{t('hunt.how1', { n: n(loading?.oneIn ?? 272) })}</li>
          <li>{t('hunt.how2')}</li>
          <li>{t('hunt.how3')}</li>
          <li>{t('hunt.how4')}</li>
          <li>{t('hunt.how5', { n: n(hunt.boostedChests) })}</li>
          <li>{t('hunt.how6')}</li>
          <li><a href={hunt.oddsSource} target="_blank" rel="noopener noreferrer">{t('hunt.oddsBy')}</a> · <a href={hunt.hoursSource} target="_blank" rel="noopener noreferrer">{t('hunt.hoursFrom')}</a></li>
        </ul>
      </details>
    </Fold>
  );
}

function Collection({ hunt }: { hunt: HuntReport }) {
  const { t, lang } = useLocale();
  const now = Date.now();
  const [local, setLocal] = useState<Record<string, boolean>>({});
  const [fired, setFired] = useState<Record<string, number>>({});
  const toggle = async (p: HuntPrizeView) => {
    if (p.obtained === 'auto') return;
    const on = !(local[p.id] ?? p.obtained === 'manual');
    setLocal((s) => ({ ...s, [p.id]: on }));
    if (on) setFired((f) => ({ ...f, [p.id]: (f[p.id] ?? 0) + 1 }));
    try { await api.post('/api/hunt/obtained', { id: p.id, on }); }
    catch { setLocal((s) => ({ ...s, [p.id]: !on })); }
  };
  const dates = (from: string, to: string) => {
    const f = new Intl.DateTimeFormat(lang === 'fr' ? 'fr-FR' : undefined, { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });
    return `${f.format(new Date(from))} – ${f.format(new Date(to))}`;
  };
  const card = (p: HuntPrizeView, i: number) => {
    const on = p.obtained === 'auto' || (local[p.id] ?? p.obtained === 'manual');
    return (
      <button key={p.id} type="button" className={`prize enter${on ? ' got' : ''}${p.obtained === 'auto' ? ' auto' : ''}`} onClick={() => toggle(p)}
        aria-pressed={on} title={p.obtained === 'auto' ? t('hunt.seenInReplays') : undefined} style={{ '--delay': `${0.1 + (i % 6) * 0.05}s` } as CSSProperties}>
        <span className={`pcheck${on ? ' on' : ''}`}>
          {p.obtained === 'auto' ? <Lock size={11} /> : <svg viewBox="0 0 16 16" width="12" height="12"><path d="M3 8.5 6.5 12 13 4.5" /></svg>}
        </span>
        <Thumb src={p.image} className="pimg" />
        <span className="pn">{p.name}</span>
        <span className="pk">{t(`hunt.kind.${p.kind}` as never)}{p.obtained === 'auto' ? ` · ${t('hunt.seenInReplays')}` : ''}</span>
        <Burst fire={fired[p.id] ?? 0} />
      </button>
    );
  };
  const got = hunt.collectionGot - [...hunt.weeks.flatMap((w) => w.prizes), ...(hunt.pirateShop ? [hunt.pirateShop] : [])]
    .reduce((d, p) => d + (p.obtained === 'manual' && local[p.id] === false ? 1 : p.obtained == null && local[p.id] ? -1 : 0), 0);
  return (
    <Fold id="collection" open={false} title={<>{t('hunt.collectionTitle')} · <span className="num">{got}/{hunt.collectionTotal}</span></>} info={t('hunt.collectionHint')}>
      <div className="collection">
        {hunt.pirateShop && (
          <div className="wk">
            <div className="wt">{t('hunt.allSeason')}</div>
            <div className="prizes">{card(hunt.pirateShop, 0)}</div>
          </div>
        )}
        {hunt.weeks.map((w) => {
          const current = now >= new Date(w.from).getTime() && now < new Date(w.to).getTime();
          return (
            <div key={w.number} className={`wk${current ? ' now' : ''}`}>
              <div className="wt">{t('hunt.week', { n: w.number, title: w.title })} <span className="mute small">{dates(w.from, w.to)}{current ? ` · ${t('hunt.thisWeek')}` : ''}</span></div>
              {w.prizes.length > 0 ? <div className="prizes">{w.prizes.map(card)}</div> : <div className="mute small">{t('hunt.notRevealed')}</div>}
            </div>
          );
        })}
      </div>
    </Fold>
  );
}

const chestKinds = ['chest', 'buried_chest', 'rare_chest', 'sprite_chest', 'poster_chest', 'other_chest'];
const otherKinds = ['ammo_box', 'food_box', 'loot_cannon', 'other_container'];

function ByKind({ hunt }: { hunt: HuntReport }) {
  const { t, n } = useLocale();
  const chests = chestKinds.map((k) => [k, hunt.byKind[k] ?? 0] as const).filter(([, v]) => v > 0).sort((a, b) => b[1] - a[1]);
  const others = otherKinds.map((k) => [k, hunt.byKind[k] ?? 0] as const).filter(([, v]) => v > 0).sort((a, b) => b[1] - a[1]);
  const max = Math.max(1, ...chests.map(([, v]) => v));
  return (
    <div className="panel">
      <div className="panel-head"><h2>{t('hunt.byKind')}</h2></div>
      <div className="kind-rows">
        {chests.map(([k, v], i) => (
          <div key={k} className="kind-row">
            <span><i className={`swatch k-${k}`} />{t(`src.${k}` as never)}</span>
            <span className="meter"><i className={`k-${k}`} style={{ '--w': `${Math.max(2, (v / max) * 100)}%`, '--d': `${150 + i * 90}ms` } as CSSProperties} /></span>
            <b className="num"><Count value={v} /></b>
          </div>
        ))}
        {others.map(([k, v]) => (
          <div key={k} className="kind-row other">
            <span>{t(`src.${k}` as never)} <span className="mute">{t('hunt.notInTotal')}</span></span>
            <span />
            <b className="num">{n(v)}</b>
          </div>
        ))}
      </div>
      {hunt.destroyed > 0 && <div className="mute small" style={{ marginTop: 10 }}>{t('hunt.destroyedNote', { n: n(hunt.destroyed) })}</div>}
    </div>
  );
}

function Sources({ hunt }: { hunt: HuntReport }) {
  const { t, n } = useLocale();
  const odd = hunt.sources.filter((s) => !s.isChest).reduce((s, x) => s + x.count, 0);
  return (
    <div className="sources-col">
      <div className="panel">
        <div className="panel-head"><h2>{t('hunt.sources')}</h2></div>
        {hunt.sources.length === 0 && <div className="empty"><span>{t('hunt.noTreasure')}</span></div>}
        <div className="kind-rows">
          {hunt.sources.map((s, i) => (
            <motion.div key={`${s.kind}-${s.destroyed}-${s.isChest}`} className={`kind-row src${s.isChest ? '' : ' unusual'}`}
              initial={{ opacity: 0, x: 10 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: 0.1 + i * 0.06 }}>
              <span>{t(`src.${s.kind}` as never)}{s.destroyed ? ` ${t('hunt.destroyedSuffix')}` : ''}{!s.isChest && <span className="small"> · {t('hunt.lowReliability')}</span>}</span>
              <b className="num"><Count value={s.count} /></b>
            </motion.div>
          ))}
        </div>
      </div>
      {odd > 0 && (
        <motion.div className="flag-panel" initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.5 }}>
          <Flag size={16} />
          <span><b>{t('hunt.flag', { n: n(odd) })}</b> {t('hunt.flagHint')}</span>
        </motion.div>
      )}
    </div>
  );
}

function Finds({ hunt }: { hunt: HuntReport }) {
  const { t, n, dateTime, duration } = useLocale();
  const [team, setTeam] = useStoredFlag('cc-showteam', false);
  const rows = hunt.finds.filter((f) => team || !f.tag.team);
  const pager = usePager('treasures', rows.length);
  const source = (f: TreasureView) => {
    if (f.tag.team && !f.teammate) return <span className="mute">{t('hunt.outOfSight')}</span>;
    if (f.tag.team) return <span className="mute">—</span>;
    if (!f.sourceIsChest) return <span className="warn">{t(`src.${f.sourceKind ?? 'other_container'}` as never)} · {t('hunt.lowReliability')}</span>;
    return <>{t(`src.${f.sourceKind ?? 'chest'}` as never)}{f.sourceDestroyed ? ` ${t('hunt.destroyedSuffix')}` : ''}{!f.sourceCertain && <span className="mute"> ({t('hunt.likely')})</span>}</>;
  };
  const at = (f: TreasureView) => f.timeSeconds == null ? '?' : f.timeExact ? duration(Math.round(f.timeSeconds)) : t('hunt.around', { t: duration(Math.round(f.timeSeconds)) });
  return (
    <Fold id="finds" open={false} title={<>{t('hunt.found')} · <span className="num">{n(hunt.treasures)}</span></>} info={t('hunt.foundHint')}
      right={hunt.teamTreasures > 0 ? <Switch checked={team} onChange={setTeam}>{t('hunt.showTeam', { n: hunt.teamTreasures })}</Switch> : undefined}>
      <div className="panel" style={{ paddingTop: 10 }}>
        {rows.length === 0 ? <div className="empty"><span>{t('hunt.noTreasure')}</span></div> : (
          <table className="ftable">
            <thead><tr><th>{t('hunt.col.match')}</th><th>{t('hunt.col.treasure')}</th><th>{t('hunt.col.at')}</th><th>{t('hunt.col.source')}</th><th>{t('hunt.col.by')}</th></tr></thead>
            <tbody>
              {rows.slice(0, pager.shown).map((f, i) => (
                <motion.tr key={`${f.matchId}-${i}-${f.tag.team}`} className={`${f.tag.team ? 'team' : ''}${!f.sourceIsChest ? ' unusual' : ''}`}
                  initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: Math.min(i % 20, 10) * 0.03 }}>
                  <td className="mute small nw">{dateTime(f.matchWhen)}</td>
                  <td><TreasureLabel tag={f.tag} t={t} /></td>
                  <td className="num nw">{at(f)}</td>
                  <td>{source(f)}</td>
                  <td className="nw">{f.tag.team ? <span className="t1">{f.teammate ?? t('hunt.teammate')}</span> : t('hunt.you')}</td>
                </motion.tr>
              ))}
            </tbody>
          </table>
        )}
        <Pager pager={pager} total={rows.length} />
      </div>
    </Fold>
  );
}
