import { useEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronUp } from 'lucide-react';
import { OVERLAY_ITEMS, type AppState, type OverlayItem, type OverlaySettings } from '../api';
import { useLocale } from '../i18n';
import { Seg, Switch } from '../parts';
import './overlay-section.css';

type Patch = (settings: Record<string, unknown>) => Promise<void>;

/** Everything about the in-game overlay: look, layout, counters and their order, with a live preview. */
export function OverlaySection({ state, patch }: { state: AppState; patch: Patch }) {
  const { t, lang } = useLocale();
  const o = state.settings.overlay;
  const overlay = (change: Partial<OverlaySettings>) => patch({ overlay: { ...o, ...change } });
  const items = o.items ?? [];
  const order = [...items, ...OVERLAY_ITEMS.filter((k) => !items.includes(k))];
  const toggle = (key: OverlayItem, on: boolean) => overlay({ items: on ? [...items, key] : items.filter((k) => k !== key) });
  const move = (key: OverlayItem, by: -1 | 1) => {
    const list = [...items];
    const i = list.indexOf(key), j = i + by;
    if (i < 0 || j < 0 || j >= list.length) return;
    [list[i], list[j]] = [list[j], list[i]];
    overlay({ items: list });
  };
  const needsSquad = (key: OverlayItem) => (key === 'squad' || key === 'squadShared') && !state.squad;

  // The preview is the overlay page itself, in a frame: it tells its size, and plays a demo on request.
  const frame = useRef<HTMLIFrameElement>(null);
  const [height, setHeight] = useState(120);
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.origin !== location.origin || e.source !== frame.current?.contentWindow || e.data?.cc !== 'overlay-size') return;
      setHeight(Math.max(90, Math.min(560, Number(e.data.height) + 24)));
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);
  const demo = (what: 'result' | 'victory' | 'live') => frame.current?.contentWindow?.postMessage({ cc: 'overlay-demo', what }, location.origin);

  return (
    <section className="panel setting-list">
      <h2>{t('settings.overlay')}</h2>
      <Switch checked={o.enabled} onChange={(v) => overlay({ enabled: v })} hint={t('settings.hotkeys', { toggle: o.toggleHotkey, lock: o.lockHotkey })}>{t('settings.overlayOn')}</Switch>
      <Switch checked={o.locked} onChange={(v) => overlay({ locked: v })}>{t('settings.overlayLocked')}</Switch>

      <h3>{t('settings.look')}</h3>
      <div className="ov-themes" role="radiogroup" aria-label={t('settings.look')}>
        {(['aurora', 'rift'] as const).map((theme) => (
          <button key={theme} role="radio" aria-checked={o.theme === theme} className={`ov-theme${o.theme === theme ? ' on' : ''}`} onClick={() => overlay({ theme })}>
            <span className={`ov-swatch ${theme}`} aria-hidden="true">
              <i className="a" /><i className="b" /><i className="c" />
            </span>
            <span className="ov-theme-text">
              <strong>{t(`settings.theme.${theme}` as never)}</strong>
              <small>{t(`settings.theme.${theme}Hint` as never)}</small>
            </span>
          </button>
        ))}
      </div>
      <div className="actions"><span className="soft" style={{ width: 110 }}>{t('settings.layout')}</span>
        <Seg value={o.layout ?? 'row'} onChange={(v) => overlay({ layout: v })}
          options={[['row', t('settings.layout.row')], ['column', t('settings.layout.column')], ['compact', t('settings.layout.compact')]]} />
      </div>

      <div className="ov-preview">
        <div className="ov-preview-head">
          <span><strong>{t('settings.preview')}</strong> <span className="mute small">{t('settings.previewHint')}</span></span>
          <span className="ov-demo">
            <button className="btn quiet small" onClick={() => demo('live')}>{t('settings.demoLive')}</button>
            <button className="btn quiet small" onClick={() => demo('result')}>{t('settings.demoEnd')}</button>
            <button className="btn quiet small" onClick={() => demo('victory')}>{t('settings.demoWin')}</button>
          </span>
        </div>
        <div className="ov-scene" style={{ height }}>
          <iframe ref={frame} title={t('settings.preview')} src={`overlay.html?preview&lang=${lang}`} tabIndex={-1} />
        </div>
      </div>

      <h3>{t('settings.counters')}</h3>
      <p className="note mute" style={{ marginTop: -4 }}>{t('settings.countersHint')}</p>
      <div className="ov-items">
        {order.map((key) => {
          const on = items.includes(key);
          const i = items.indexOf(key);
          return (
            <div key={key} className={`ov-item${on ? ' on' : ''}`}>
              <Switch checked={on} onChange={(v) => toggle(key, v)} hint={needsSquad(key) ? t('settings.itemNeedsSquad') : undefined}>{t(`settings.item.${key}` as never)}</Switch>
              {on && (
                <span className="ov-move">
                  <button className="icon-btn" aria-label={t('settings.moveUp')} title={t('settings.moveUp')} disabled={i === 0} onClick={() => move(key, -1)}><ChevronUp size={16} /></button>
                  <button className="icon-btn" aria-label={t('settings.moveDown')} title={t('settings.moveDown')} disabled={i === items.length - 1} onClick={() => move(key, 1)}><ChevronDown size={16} /></button>
                </span>
              )}
            </div>
          );
        })}
      </div>

      <h3>{t('settings.extras')}</h3>
      <div className="checks">
        <Switch checked={o.showMembers} onChange={(v) => overlay({ showMembers: v })}>{t('settings.members')}</Switch>
        <Switch checked={o.showIcons ?? true} onChange={(v) => overlay({ showIcons: v })}>{t('settings.icons')}</Switch>
        <Switch checked={o.showStatus ?? true} onChange={(v) => overlay({ showStatus: v })}>{t('settings.statusLine')}</Switch>
        <Switch checked={o.showResult ?? true} onChange={(v) => overlay({ showResult: v })}>{t('settings.resultCard')}</Switch>
      </div>
      {o.showMembers && (
        <div className="actions"><span className="soft">{t('settings.squadScope')}</span>
          <Seg value={o.squadScope} onChange={(v) => overlay({ squadScope: v })} options={[['combined', t('settings.scopeCombined')], ['shared', t('settings.scopeShared')]]} />
        </div>
      )}
      {(o.showResult ?? true) && (
        <div className="range"><span>{t('settings.resultTime')}</span><input type="range" min={5} max={60} step={1} value={o.resultSeconds ?? 14} onChange={(e) => overlay({ resultSeconds: Number(e.target.value) })} /><span className="num">{o.resultSeconds ?? 14} s</span></div>
      )}
      <div className="range"><span>{t('settings.opacity')}</span><input type="range" min={20} max={100} step={5} value={o.opacity} onChange={(e) => overlay({ opacity: Number(e.target.value) })} /><span className="num">{o.opacity}%</span></div>
      <div className="range"><span>{t('settings.size')}</span><input type="range" min={60} max={250} step={10} value={o.scale} onChange={(e) => overlay({ scale: Number(e.target.value) })} /><span className="num">{o.scale}%</span></div>
      <p className="note mute">{t('settings.overlayHint')}</p>
      <div className="field">
        <label>{t('settings.obs')}</label>
        <div className="code selectable">{`${location.origin}/obs`}</div>
        <p className="note mute">{t('settings.obsHint')}</p>
      </div>
    </section>
  );
}
