'use strict';
const $ = (s) => document.querySelector(s);
const el = (t, c) => { const e = document.createElement(t); if (c) e.className = c; return e; };
const SVGNS = 'http://www.w3.org/2000/svg';
const svg = (t, a) => { const e = document.createElementNS(SVGNS, t); for (const k in a) e.setAttribute(k, a[k]); return e; };
const esc = (s) => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let LAST = null, SELECTED = null;
const APPROVALS = new Set();   // node ids the user has approved (client-side)

// ── Demos ──────────────────────────────────────────────────────────
fetch('/api/demos').then(r => r.json()).then(demos => {
  const row = $('#demoRow');
  demos.forEach(d => {
    const b = el('button', 'demo-btn' + (d.id === 3 ? ' attack' : ''));
    b.textContent = (d.id === 3 ? '⚠ ' : '') + d.title;
    b.onclick = () => { $('#prompt').value = d.prompt; run(); };
    row.appendChild(b);
  });
});

$('#runBtn').onclick = run;
$('#prompt').addEventListener('keydown', e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) run(); });
document.querySelectorAll('.mode-btn').forEach(b => b.onclick = () => {
  document.querySelectorAll('.mode-btn').forEach(x => x.classList.remove('active'));
  b.classList.add('active');
  const compare = b.dataset.mode === 'compare';
  $('#compareView').classList.toggle('hidden', !compare);
  $('#controlView').classList.toggle('hidden', compare);
});

function run(keepApprovals = false) {
  const prompt = $('#prompt').value.trim();
  if (!prompt) return;
  if (!keepApprovals) APPROVALS.clear();   // a new prompt resets the approval set
  $('#runBtn').textContent = 'Running…';
  fetch('/api/run', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt, approvals: [...APPROVALS] })
  })
    .then(r => r.json()).then(res => { LAST = res; SELECTED = null; render(res); })
    .finally(() => $('#runBtn').textContent = 'Run pipeline ▸');
}

function approve(id) { APPROVALS.add(id); run(true); }
function undo(id) { APPROVALS.delete(id); run(true); }

function exportTrace(format) {
  const prompt = $('#prompt').value.trim();
  if (!prompt) return;
  fetch('/api/export', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt, approvals: [...APPROVALS], format })
  }).then(r => r.text()).then(text => {
    const md = format === 'md';
    const ext = md ? 'md' : (format === 'signed' ? 'signed.json' : (format === 'bundle' ? 'bundle.json' : 'json'));
    const blob = new Blob([text], { type: md ? 'text/markdown' : 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'intentmesh-trace.' + ext;
    document.body.appendChild(a); a.click(); a.remove();
    URL.revokeObjectURL(a.href);
  });
}
$('#exportJson').onclick = () => exportTrace('json');
$('#exportMd').onclick = () => exportTrace('md');
$('#exportSigned').onclick = () => exportTrace('signed');
$('#exportBundle').onclick = () => exportTrace('bundle');

// ── Render ─────────────────────────────────────────────────────────
function render(r) {
  $('#promptEcho').textContent = '“' + r.prompt + '”';
  const res = $('#resolver'); res.innerHTML = '';
  (r.resolverFired || []).forEach(f => {
    const d = el('div', 'fire'); d.innerHTML = esc(f).replace(/^([\w.]+)/, '<b>$1</b>'); res.appendChild(d);
  });
  (r.unsupported || []).forEach(u => { const d = el('div', 'fire'); d.textContent = '⚠ ' + u; res.appendChild(d); });

  renderMesh(r);
  renderPolicy(r);
  renderExecution(r);
  renderVerification(r);
  renderAudit(r);
  renderSkills(r);
}

function renderSkills(r) {
  const box = $('#skills'); box.innerHTML = '';
  const s = r.skills;
  if (!s || !s.items.length) { box.innerHTML = '<p class="empty">No skills registered.</p>'; return; }
  s.items.forEach(it => {
    const card = el('div', 'skill-card' + (it.matchedThisRun ? ' matched' : ''));
    const head = el('div', 'skill-head');
    head.innerHTML = `<span class="name">${esc(it.label)}</span>` +
      `<span class="badge st-NeedsConfirmation">${esc(it.status)}</span>` +
      `<span class="tag risk-${esc(it.risk)}">risk: ${esc(it.risk)}</span>` +
      (it.matchedThisRun ? '<span class="badge st-Verified">matched this run</span>' : '');
    card.appendChild(head);
    const note = el('div', 'skill-note' + (it.matchedThisRun ? ' matched' : '')); note.textContent = it.note; card.appendChild(note);
    const comp = el('div', 'skill-comp'); comp.textContent = 'composition: ' + it.composition.join(' → '); card.appendChild(comp);
    // lifecycle pipeline with the current state highlighted
    const lc = el('div', 'lifecycle');
    s.lifecycle.forEach((st, i) => {
      if (i) { const sep = el('span', 'sep'); sep.textContent = '›'; lc.appendChild(sep); }
      const cls = i === it.statusOrder ? ' cur' : (i < it.statusOrder ? ' past' : '');
      const stg = el('span', 'stg' + cls); stg.textContent = st; lc.appendChild(stg);
    });
    card.appendChild(lc);
    const gov = el('div', 'gov-note');
    gov.textContent = 'Emergence may propose; governance grants authority. This skill is a proposal — it never executes on its own and advances only when a human moves it through the lifecycle.';
    card.appendChild(gov);
    box.appendChild(card);
  });
}

function renderPolicy(r) {
  const box = $('#policy'); box.innerHTML = '';
  r.policy.forEach(p => {
    const blocked = p.decision === 'Block';
    const row = el('div', 'row' + (blocked ? ' block' : '')); row.dataset.node = p.nodeId;
    const top = el('div', 'top');
    const lbl = el('div', 'lbl'); lbl.textContent = p.label;
    const badge = el('span', 'badge ' + statusClassFromDecision(p.decision)); badge.textContent = p.decision.toUpperCase();
    top.append(lbl, badge); row.appendChild(top);
    const reason = el('div', 'reason'); reason.textContent = p.reason; row.appendChild(reason);
    const rules = el('div', 'rules'); rules.textContent = 'rules: ' + p.triggeredRules.join(', '); row.appendChild(rules);
    const tags = el('div'); tags.style.marginTop = '5px';
    tags.innerHTML = `<span class="tag risk-${p.risk}">risk: ${p.risk}</span>` +
      (p.requiresConfirmation ? '<span class="tag">confirm required</span>' : '') +
      (p.sensitive ? '<span class="tag risk-high">sensitive</span>' : '') +
      (p.externalSideEffect ? '<span class="tag risk-medium">external</span>' : '') +
      (p.destructive ? '<span class="tag risk-high">destructive</span>' : '') +
      `<span class="tag">trust: ${p.trustSource}</span>`;
    row.appendChild(tags);
    row.onclick = () => select(p.nodeId);
    box.appendChild(row);
  });
}

function renderExecution(r) {
  const box = $('#execution'); box.innerHTML = '';
  r.nodes.forEach(n => {
    const e = r.execution.find(x => x.nodeId === n.id);
    const row = el('div', 'row'); row.dataset.node = n.id;
    const top = el('div', 'top');
    const lbl = el('div', 'lbl'); lbl.textContent = n.label;
    const badge = el('span', 'badge st-' + n.status); badge.textContent = pretty(n.status);
    top.append(lbl, badge); row.appendChild(top);
    if (n.status === 'Blocked') {
      const ef = el('div', 'effect block'); ef.textContent = '⛔ ' + (n.blockedReason || 'blocked'); row.appendChild(ef);
    } else if (e) {
      const s = el('div', 'effect' + (e.halted ? ' halt' : '')); s.textContent = (e.halted ? '⏸ ' : '▸ ') + e.summary; row.appendChild(s);
      e.effects.forEach(x => { const f = el('div', 'effect'); f.textContent = '· ' + x; row.appendChild(f); });
    }
    // Confirmation controls — ONLY for full-authority nodes the gate gated for confirmation.
    if (n.status === 'NeedsConfirmation' && n.trustSource === 'User') {
      const act = el('div', 'confirm-actions');
      const ok = el('button', 'approve'); ok.textContent = '✓ Approve';
      ok.onclick = (ev) => { ev.stopPropagation(); approve(n.id); };
      const note = el('span', 'confirm-note'); note.textContent = 'requires your confirmation';
      act.append(ok, note); row.appendChild(act);
    } else if (APPROVALS.has(n.id) && (n.status === 'Executed' || n.status === 'Verified')) {
      const act = el('div', 'confirm-actions');
      const undoBtn = el('button', 'undo'); undoBtn.textContent = '↩ Undo approval';
      undoBtn.onclick = (ev) => { ev.stopPropagation(); undo(n.id); };
      const note = el('span', 'confirm-note approved'); note.textContent = '✓ approved by you';
      act.append(note, undoBtn); row.appendChild(act);
    }
    row.onclick = () => select(n.id);
    box.appendChild(row);
  });
}

function renderVerification(r) {
  const box = $('#verification'); box.innerHTML = '';
  if (!r.verification.length) { box.innerHTML = '<p class="empty">No postconditions for this run.</p>'; }
  r.verification.forEach(v => {
    const row = el('div', 'vrow');
    const m = el('div', 'vmark ' + (v.pass ? 'pass' : 'fail')); m.textContent = v.pass ? '✓' : '✕';
    const txt = el('div');
    txt.innerHTML = `<div class="vid">${esc(v.id)}</div><div class="vexp">${esc(v.expected)} — <b>${esc(v.actual)}</b></div>`;
    row.append(m, txt); box.appendChild(row);
  });
  const allPass = r.verification.length && r.verification.every(v => v.pass);
  const verdict = el('div', 'verdict ' + (allPass ? 'ok' : (r.verification.length ? 'bad' : 'ok')));
  const s = r.summary;
  verdict.textContent = (allPass ? '✓ MATCHES APPROVED INTENT' : 'VERIFICATION FAILED') +
    `  ·  blocked ${s.blocked} · confirm ${s.needsConfirmation} · verified ${s.verified}`;
  box.appendChild(verdict);
}

function renderAudit(r) {
  const box = $('#audit'); box.innerHTML = '';
  r.audit.forEach(a => {
    const row = el('div', 'a');
    row.innerHTML = `<span class="ph ${esc(a.phase)}">${esc(a.phase)}</span><span class="nid">${esc(a.nodeId)}</span><span>${esc(a.message)}</span>`;
    box.appendChild(row);
  });
}

// ── The hero: SVG intent mesh ──────────────────────────────────────
function renderMesh(r) {
  const svgEl = $('#mesh'); svgEl.innerHTML = '';
  const W = 720, NW = 250, NH = 56, GAP = 74, PAD = 18;
  const user = r.nodes.filter(n => n.trustSource === 'User');
  const zt = r.nodes.filter(n => n.trustSource !== 'User');
  const pos = {};
  user.forEach((n, i) => pos[n.id] = { x: PAD, y: PAD + i * GAP });
  // place each zero-trust node in the right "quarantine" lane, near its parent row
  zt.forEach((n, i) => {
    const parent = pos[n.parentId];
    pos[n.id] = { x: W - NW - PAD, y: parent ? parent.y + GAP * 0.5 + i * GAP : PAD + (user.length + i) * GAP };
  });
  const H = PAD * 2 + Math.max(user.length, 1) * GAP + (zt.length ? GAP : 0);
  svgEl.setAttribute('viewBox', `0 0 ${W} ${H}`);

  // sequential flow edges between consecutive user nodes
  for (let i = 0; i < user.length - 1; i++) edge(svgEl, pos[user[i].id], pos[user[i + 1].id], NW, NH, false);
  // parent->child edges (incl. the crossing zero-trust quarantine edge)
  r.nodes.forEach(n => { if (n.parentId && pos[n.parentId]) edge(svgEl, pos[n.parentId], pos[n.id], NW, NH, n.trustSource !== 'User'); });

  r.nodes.forEach(n => {
    const p = pos[n.id]; if (!p) return;
    const g = svg('g', { class: 'node ' + (n.trustSource === 'User' ? 'user' : 'zt') + (n.id === SELECTED ? ' sel' : '') });
    g.dataset.node = n.id;
    g.appendChild(svg('rect', { x: p.x, y: p.y, width: NW, height: NH, rx: 9 }));
    const t1 = svg('text', { x: p.x + 12, y: p.y + 22, class: 'nlabel' }); t1.textContent = clip(n.label, 30); g.appendChild(t1);
    const t2 = svg('text', { x: p.x + 12, y: p.y + 40, class: 'ntype' }); t2.textContent = (n.trustSource === 'User' ? '' : '🔒 ') + n.type; g.appendChild(t2);
    // status pill
    const col = statusColor(n.status);
    g.appendChild(svg('rect', { x: p.x + NW - 92, y: p.y + 9, width: 82, height: 18, rx: 9, fill: col.bg, stroke: col.fg }));
    const st = svg('text', { x: p.x + NW - 51, y: p.y + 21, class: 'nstatus', 'text-anchor': 'middle', fill: col.fg }); st.textContent = pretty(n.status); g.appendChild(st);
    g.style.cursor = 'pointer';
    g.onclick = () => select(n.id);
    svgEl.appendChild(g);
  });
}

function edge(svgEl, a, b, NW, NH, isZt) {
  const x1 = a.x + NW / 2, y1 = a.y + NH, x2 = b.x + (b.x > a.x ? 0 : NW / 2), y2 = b.y + (b.x > a.x ? NH / 2 : 0);
  const my = (y1 + y2) / 2;
  const d = `M ${x1} ${y1} C ${x1} ${my}, ${x2} ${my}, ${x2} ${y2}`;
  svgEl.appendChild(svg('path', { class: 'edge' + (isZt ? ' zt' : ''), d }));
}

// ── Selection sync across panels ───────────────────────────────────
function select(id) {
  SELECTED = (SELECTED === id) ? null : id;
  document.querySelectorAll('.row').forEach(r => r.classList.toggle('sel', r.dataset.node === SELECTED));
  if (LAST) renderMesh(LAST);
  if (SELECTED) {
    const t = document.querySelector(`.row[data-node="${SELECTED}"]`);
    if (t) t.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }
}

// ── helpers ────────────────────────────────────────────────────────
function pretty(s) { return ({ NeedsConfirmation: 'needs-confirm', Verified: 'verified', Executed: 'executed', Blocked: 'blocked', Allowed: 'allowed', Resolved: 'resolved', Pending: 'pending' })[s] || s; }
function clip(s, n) { return s.length > n ? s.slice(0, n - 1) + '…' : s; }
function statusClassFromDecision(d) { return ({ Block: 'st-Blocked', Confirm: 'st-NeedsConfirmation', Allow: 'st-Allowed', Warn: 'st-NeedsConfirmation', Review: 'st-NeedsConfirmation' })[d] || 'st-Resolved'; }
function statusColor(s) {
  return ({
    Verified: { bg: '#07331f', fg: '#34d399' }, Executed: { bg: '#07262f', fg: '#67e8f9' },
    Allowed: { bg: '#07262f', fg: '#67e8f9' }, NeedsConfirmation: { bg: '#33260a', fg: '#fbbf24' },
    Blocked: { bg: '#350f12', fg: '#f87171' }
  })[s] || { bg: '#1a2230', fg: '#8b9bb0' };
}

// auto-run the attack demo on load so the page is never empty
window.addEventListener('load', () => {
  fetch('/api/demos').then(r => r.json()).then(d => { $('#prompt').value = d[2].prompt; run(); });
});
