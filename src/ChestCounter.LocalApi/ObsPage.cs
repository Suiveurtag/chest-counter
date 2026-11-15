namespace ChestCounter.LocalApi;

/// <summary>
/// The overlay as a transparent web page, for an OBS "Browser" source (http://127.0.0.1:47821/obs). Self-contained:
/// no external request. Options in the address: ?members=1 (per-member line), ?scale=1.5, ?align=right.
/// </summary>
public static class ObsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<meta charset="utf-8">
<title>Chest Counter overlay</title>
<style>
  html, body { margin: 0; background: transparent; overflow: hidden; }
  body { font: 600 15px/1.2 "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif; color: #f1eefc; padding: 12px; }
  #root { display: inline-flex; flex-direction: column; gap: 8px; transform-origin: top left; }
  body.right { text-align: right; } body.right #root { transform-origin: top right; }
  .row { display: inline-flex; gap: 18px; padding: 10px 16px; border-radius: 14px; background: rgba(16, 13, 34, .72);
         box-shadow: inset 0 0 0 1px rgba(255,255,255,.08); }
  .cell { display: flex; flex-direction: column; gap: 2px; min-width: 54px; text-align: left; }
  .cell small { font-size: 11px; font-weight: 500; color: #a9a3cb; }
  .cell b { font-size: 26px; font-weight: 700; font-variant-numeric: tabular-nums; letter-spacing: -.01em; }
  .personal b { color: #f3c96b; } .squad b { color: #6ad9ea; } .session b { color: #f593c9; } .last b { color: #f1eefc; }
  .cell b.bump { animation: bump .6s ease-out; }
  @keyframes bump { 0% { transform: translateY(6px); opacity: .3; } 100% { transform: none; opacity: 1; } }
  .members { display: inline-flex; gap: 14px; padding: 6px 16px; border-radius: 12px; background: rgba(16, 13, 34, .6); font-size: 13px; font-weight: 500; color: #cfc9ea; }
  .members b { color: #6ad9ea; font-variant-numeric: tabular-nums; margin-left: 5px; }
  .partial::after { content: "+"; color: #f593c9; }
</style>
<body>
<div id="root"></div>
<script>
  const q = new URLSearchParams(location.search);
  const root = document.getElementById('root');
  if (q.get('align') === 'right') document.body.classList.add('right');
  root.style.transform = 'scale(' + (parseFloat(q.get('scale')) || 1) + ')';
  const fmt = n => n == null ? '–' : Number(n).toLocaleString();
  let last = {};
  function cell(key, label, value, extra) {
    const bump = last[key] !== undefined && last[key] !== value ? ' bump' : '';
    last[key] = value;
    return '<div class="cell ' + key + '"><small>' + label + '</small><b class="' + (extra || '') + bump + '">' + fmt(value) + '</b></div>';
  }
  async function refresh() {
    try {
      const d = await (await fetch('/api/overlay')).json();
      const s = d.settings;
      let html = '<div class="row">';
      if (s.showPersonal) html += cell('personal', 'Chests', d.personal);
      if (s.showSquad && d.squad != null) html += cell('squad', d.squadScope === 'shared' ? 'Squad together' : 'Squad', d.squad);
      if (s.showSession) html += cell('session', 'Session', d.session);
      if (s.showLastMatch) html += cell('last', 'Last match', d.lastMatch, d.lastMatchPartial ? 'partial' : '');
      html += '</div>';
      if ((s.showMembers || q.get('members') === '1') && d.members.length)
        html += '<div class="members">' + d.members.map(m => '<span>' + m.name.replace(/[<&>]/g, '') + '<b>' + fmt(m.chests) + '</b></span>').join('') + '</div>';
      root.innerHTML = html;
    } catch (e) { /* app closed: keep the last figures */ }
  }
  refresh();
  function listen() {
    const es = new EventSource('/api/overlay/events');
    es.onmessage = refresh;
    es.onerror = () => { es.close(); setTimeout(listen, 5000); };
  }
  listen();
</script>
</body>
</html>
""";
}
