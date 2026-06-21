'use strict';

// When the Control Room is run token-gated (INTENTMESH_WEB_TOKEN set, e.g. for remote access), every
// /api call must carry the token. The operator stores it once via localStorage['intentmesh_token'];
// we attach it to every same-origin /api request so the SPA keeps working under token auth. On a
// loopback/local host no token is configured and nothing is added.
(() => {
  const _fetch = window.fetch.bind(window);
  window.fetch = (url, opts = {}) => {
    if (typeof url === 'string' && url.startsWith('/api')) {
      const tok = localStorage.getItem('intentmesh_token');
      if (tok) opts = { ...opts, headers: { ...(opts.headers || {}), 'X-Api-Token': tok } };
    }
    return _fetch(url, opts);
  };
})();

const $ = (s) => document.querySelector(s);
const el = (t, c) => { const e = document.createElement(t); if (c) e.className = c; return e; };
const SVGNS = 'http://www.w3.org/2000/svg';
const svg = (t, a) => { const e = document.createElementNS(SVGNS, t); for (const k in a) e.setAttribute(k, a[k]); return e; };
const esc = (s) => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let LAST = null, SELECTED = null;
const APPROVALS = new Set();   // node ids the user has approved (client-side)

// ── Scenario vector labels ──────────────────────────────────────────
const SCENARIO_VECTORS = {
  0: { vector: 'indirect prompt injection', icon: '⚠' },
  1: { vector: 'shell execution blocked by default', icon: '🔒' },
  2: { vector: 'destructive SQL blocked', icon: '🛑' },
  3: { vector: 'safe action after confirmation', icon: '✓' },
  4: { vector: 'destructive action needs approval', icon: '⚠' }
};

// ── Demos ──────────────────────────────────────────────────────────
fetch('/api/demos').then(r => r.json()).then(demos => {
  const row = $('#demoRow');
  const heading = el('div', 'scenario-heading');
  heading.textContent = 'Attack Scenarios';
  row.appendChild(heading);
  demos.forEach((d, idx) => {
    const card = el('div', 'scenario-card' + (idx === 0 ? ' attack' : ''));
    const titleRow = el('div', 'scenario-title-row');
    const icon = el('span', 'scenario-icon');
    const sv = SCENARIO_VECTORS[idx] || { vector: '', icon: '▸' };
    icon.textContent = sv.icon;
    const title = el('span', 'scenario-name');
    title.textContent = d.title;
    titleRow.append(icon, title);
    const vector = el('div', 'scenario-vector');
    vector.textContent = sv.vector;
    card.append(titleRow, vector);
    card.onclick = () => { $('#prompt').value = d.prompt; run(); };
    row.appendChild(card);
  });
});

$('#runBtn').onclick = run;
$('#prompt').addEventListener('keydown', e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) run(); });
document.querySelectorAll('.mode-btn').forEach(b => b.onclick = () => {
  document.querySelectorAll('.mode-btn').forEach(x => x.classList.remove('active'));
  b.classList.add('active');
  const mode = b.dataset.mode;
  $('#controlView').classList.toggle('hidden', mode !== 'control');
  $('#opsView').classList.toggle('hidden', mode !== 'ops');
  $('#compareView').classList.toggle('hidden', mode !== 'compare');
  if (mode === 'compare') startCompareAnimation();
  if (mode === 'ops') loadRuns();
});

function run(keepApprovals) {
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
  $('#promptEcho').textContent = '"' + r.prompt + '"';
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
      const fileRefs = n.type === 'act-delete-files' ? deleteFileRefs(n) : [];
      if (fileRefs.length > 0) {
        // Destructive deletion needs explicit PER-FILE approval — one button per file (node#fileRef).
        const note = el('span', 'confirm-note'); note.textContent = 'approve each file to delete:';
        act.appendChild(note);
        fileRefs.forEach(ref => {
          const b = el('button', 'approve'); b.textContent = '✓ ' + ref;
          b.onclick = (ev) => { ev.stopPropagation(); approve(n.id + '#' + ref); };
          act.appendChild(b);
        });
      } else {
        const ok = el('button', 'approve'); ok.textContent = '✓ Approve';
        ok.onclick = (ev) => { ev.stopPropagation(); approve(n.id); };
        const note = el('span', 'confirm-note'); note.textContent = 'requires your confirmation';
        act.append(ok, note);
      }
      row.appendChild(act);
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

// ── Audit Timeline (v1) ────────────────────────────────────────────
const PHASE_COLORS = {
  resolve: '#4cc2ff',
  policy:  '#fbbf24',
  execute: '#67e8f9',
  verify:  '#34d399'
};

function renderAudit(r) {
  const box = $('#audit'); box.innerHTML = '';
  if (!r.audit || !r.audit.length) {
    box.innerHTML = '<p class="empty">No audit entries.</p>';
    return;
  }
  const tl = el('div', 'timeline');
  r.audit.forEach((a, idx) => {
    const entry = el('div', 'tl-entry');
    // left rail
    const rail = el('div', 'tl-rail');
    const dot = el('div', 'tl-dot');
    const phaseColor = PHASE_COLORS[a.phase] || '#4cc2ff';
    dot.style.background = phaseColor;
    dot.style.boxShadow = '0 0 6px ' + phaseColor + '88';
    rail.appendChild(dot);
    if (idx < r.audit.length - 1) {
      const line = el('div', 'tl-line');
      rail.appendChild(line);
    }
    // content
    const content = el('div', 'tl-content');
    const meta = el('div', 'tl-meta');
    const ph = el('span', 'tl-phase ph-' + esc(a.phase));
    ph.textContent = a.phase;
    ph.style.color = phaseColor;
    const nid = el('span', 'tl-nid');
    nid.textContent = a.nodeId;
    meta.append(ph, nid);
    const msg = el('div', 'tl-msg');
    msg.textContent = a.message;
    content.append(meta, msg);
    entry.append(rail, content);
    tl.appendChild(entry);
  });
  box.appendChild(tl);
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
    const isBlocked = n.status === 'Blocked';
    const isZt = n.trustSource !== 'User';
    const g = svg('g', { class: 'node ' + (isZt ? 'zt' : 'user') + (n.id === SELECTED ? ' sel' : '') + (isBlocked ? ' quarantined' : '') });
    g.dataset.node = n.id;
    g.appendChild(svg('rect', { x: p.x, y: p.y, width: NW, height: NH, rx: 9 }));
    const t1 = svg('text', { x: p.x + 12, y: p.y + 22, class: 'nlabel' }); t1.textContent = clip(n.label, 30); g.appendChild(t1);
    const t2 = svg('text', { x: p.x + 12, y: p.y + 40, class: 'ntype' }); t2.textContent = (isZt ? '🔒 ' : '') + n.type; g.appendChild(t2);
    // status pill
    const col = statusColor(n.status);
    g.appendChild(svg('rect', { x: p.x + NW - 92, y: p.y + 9, width: 82, height: 18, rx: 9, fill: col.bg, stroke: col.fg }));
    const st = svg('text', { x: p.x + NW - 51, y: p.y + 21, class: 'nstatus', 'text-anchor': 'middle', fill: col.fg }); st.textContent = pretty(n.status); g.appendChild(st);

    // QUARANTINE badge for blocked zero-trust nodes
    if (isBlocked && isZt) {
      const foreignObj = svg('foreignObject', { x: p.x + 4, y: p.y - 20, width: NW - 8, height: 20 });
      const qDiv = document.createElementNS('http://www.w3.org/1999/xhtml', 'div');
      qDiv.className = 'quarantine-badge';
      qDiv.textContent = 'QUARANTINED';
      foreignObj.appendChild(qDiv);
      g.appendChild(foreignObj);
    }

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

// ── Selection sync + Node Detail panel (v1) ────────────────────────
function select(id) {
  SELECTED = (SELECTED === id) ? null : id;
  document.querySelectorAll('.row').forEach(r => r.classList.toggle('sel', r.dataset.node === SELECTED));
  if (LAST) renderMesh(LAST);
  if (SELECTED && LAST) {
    const t = document.querySelector(`.row[data-node="${SELECTED}"]`);
    if (t) t.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    openNodeDetail(SELECTED, LAST);
  } else {
    closeNodeDetail();
  }
}

function openNodeDetail(id, r) {
  const node = r.nodes.find(n => n.id === id);
  if (!node) return;
  const policy = r.policy.find(p => p.nodeId === id);
  const exec = r.execution ? r.execution.find(e => e.nodeId === id) : null;
  const verif = r.verification ? r.verification.filter(v => v.nodeId === id) : [];

  $('#ndTitle').textContent = node.label || id;
  const body = $('#ndBody');
  body.innerHTML = '';

  // Identity section
  const secId = el('div', 'nd-section');
  secId.innerHTML = `<div class="nd-sec-title">Identity</div>
    <div class="nd-row"><span class="nd-key">id</span><span class="nd-val mono">${esc(node.id)}</span></div>
    <div class="nd-row"><span class="nd-key">type</span><span class="nd-val mono">${esc(node.type)}</span></div>
    <div class="nd-row"><span class="nd-key">label</span><span class="nd-val">${esc(node.label)}</span></div>
    <div class="nd-row"><span class="nd-key">trust source</span><span class="nd-val ${node.trustSource === 'User' ? 'nd-user' : 'nd-zt'}">${esc(node.trustSource)}</span></div>
    ${node.authority ? `<div class="nd-row"><span class="nd-key">authority</span><span class="nd-val">${esc(node.authority)}</span></div>` : ''}
    <div class="nd-row"><span class="nd-key">status</span><span class="nd-val"><span class="badge st-${esc(node.status)}">${esc(pretty(node.status))}</span></span></div>`;
  body.appendChild(secId);

  // Fields section
  const fields = node.fields || {};
  const fieldKeys = Object.keys(fields);
  if (fieldKeys.length) {
    const secF = el('div', 'nd-section');
    let fhtml = '<div class="nd-sec-title">Typed Fields</div>';
    fieldKeys.forEach(k => {
      fhtml += `<div class="nd-row"><span class="nd-key">${esc(k)}</span><span class="nd-val mono">${esc(String(fields[k]))}</span></div>`;
    });
    secF.innerHTML = fhtml;
    body.appendChild(secF);
  }

  // Policy section
  if (policy) {
    const secP = el('div', 'nd-section');
    secP.innerHTML = `<div class="nd-sec-title">Policy Decision</div>
      <div class="nd-row"><span class="nd-key">decision</span><span class="nd-val"><span class="badge ${statusClassFromDecision(policy.decision)}">${esc(policy.decision)}</span></span></div>
      <div class="nd-row"><span class="nd-key">risk</span><span class="nd-val"><span class="tag risk-${esc(policy.risk)}">${esc(policy.risk)}</span></span></div>
      <div class="nd-row"><span class="nd-key">reason</span><span class="nd-val">${esc(policy.reason)}</span></div>
      ${policy.triggeredRules && policy.triggeredRules.length ? `<div class="nd-row"><span class="nd-key">rules</span><span class="nd-val mono">${esc(policy.triggeredRules.join(', '))}</span></div>` : ''}
      <div class="nd-row nd-flags">
        ${policy.sensitive ? '<span class="tag risk-high">sensitive</span>' : ''}
        ${policy.externalSideEffect ? '<span class="tag risk-medium">external</span>' : ''}
        ${policy.destructive ? '<span class="tag risk-high">destructive</span>' : ''}
        ${policy.requiresConfirmation ? '<span class="tag">confirm required</span>' : ''}
      </div>`;
    body.appendChild(secP);
  }

  // Execution section
  if (exec) {
    const secE = el('div', 'nd-section');
    let ehtml = `<div class="nd-sec-title">Execution</div>
      <div class="nd-row"><span class="nd-key">summary</span><span class="nd-val">${esc(exec.summary)}</span></div>`;
    if (exec.effects && exec.effects.length) {
      ehtml += '<div class="nd-row"><span class="nd-key">effects</span><span class="nd-val"><ul class="nd-effects">' +
        exec.effects.map(x => `<li>${esc(x)}</li>`).join('') + '</ul></span></div>';
    }
    if (exec.halted) ehtml += '<div class="nd-row"><span class="nd-key">halted</span><span class="nd-val nd-zt">yes — pipeline paused</span></div>';
    secE.innerHTML = ehtml;
    body.appendChild(secE);
  } else if (node.status === 'Blocked') {
    const secE = el('div', 'nd-section');
    secE.innerHTML = `<div class="nd-sec-title">Execution</div>
      <div class="nd-row"><span class="nd-key">blocked</span><span class="nd-val nd-zt">${esc(node.blockedReason || 'blocked by policy gate')}</span></div>`;
    body.appendChild(secE);
  }

  // Verification section
  if (verif.length) {
    const secV = el('div', 'nd-section');
    let vhtml = '<div class="nd-sec-title">Verification</div>';
    verif.forEach(v => {
      vhtml += `<div class="nd-row">
        <span class="nd-key">${esc(v.id)}</span>
        <span class="nd-val ${v.pass ? 'nd-pass' : 'nd-fail'}">${v.pass ? '✓' : '✕'} ${esc(v.actual)}</span>
      </div>`;
    });
    secV.innerHTML = vhtml;
    body.appendChild(secV);
  }

  const detail = $('#nodeDetail');
  const backdrop = $('#ndBackdrop');
  detail.classList.remove('hidden');
  backdrop.classList.remove('hidden');
  // trigger animation
  requestAnimationFrame(() => detail.classList.add('open'));
}

function closeNodeDetail() {
  const detail = $('#nodeDetail');
  const backdrop = $('#ndBackdrop');
  detail.classList.remove('open');
  backdrop.classList.add('hidden');
  setTimeout(() => detail.classList.add('hidden'), 220);
  SELECTED = null;
  document.querySelectorAll('.row').forEach(r => r.classList.remove('sel'));
  if (LAST) renderMesh(LAST);
}

$('#ndClose').onclick = closeNodeDetail;
$('#ndBackdrop').onclick = closeNodeDetail;
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeNodeDetail(); });

// ── Compare mode animation (v1) ────────────────────────────────────
let cmpTimer = null;

function startCompareAnimation() {
  stopCompareAnimation();
  resetCompareSteps();
  const badSteps = document.querySelectorAll('#cmpBadFlow .cmp-step');
  const goodSteps = document.querySelectorAll('#cmpGoodFlow .cmp-step');
  const totalBad = badSteps.length;
  const totalGood = goodSteps.length;
  const total = Math.max(totalBad, totalGood);
  let step = 0;
  function tick() {
    if (step < totalBad) {
      badSteps[step].classList.add('active');
      if (step === totalBad - 1) badSteps[step].classList.add('leaked');
    }
    if (step < totalGood) {
      goodSteps[step].classList.add('active');
      if (step === 2) goodSteps[step].classList.add('quarantine-step');
      if (step === totalGood - 1) goodSteps[step].classList.add('complete-step');
    }
    step++;
    if (step <= total) {
      cmpTimer = setTimeout(tick, 800);
    }
  }
  cmpTimer = setTimeout(tick, 400);
}

function stopCompareAnimation() {
  if (cmpTimer) { clearTimeout(cmpTimer); cmpTimer = null; }
}

function resetCompareSteps() {
  document.querySelectorAll('.cmp-step').forEach(s => {
    s.classList.remove('active', 'leaked', 'quarantine-step', 'complete-step');
  });
}

$('#replayBtn').onclick = startCompareAnimation;

// ── Operations: run history · approval reasoning · replay · integrity ──
function loadRuns() {
  fetch('/api/runs').then(r => r.json()).then(rows => {
    const box = $('#opsRuns'); box.innerHTML = '';
    if (!rows.length) { box.innerHTML = '<p class="empty">No runs yet — run a pipeline to populate history.</p>'; return; }
    const tbl = el('table', 'ops-table');
    tbl.innerHTML = '<thead><tr><th>run</th><th>prompt</th><th>blocked</th><th>confirm</th><th>verified</th><th>key</th><th>appr.</th></tr></thead>';
    const tb = el('tbody');
    rows.forEach(r => {
      const tr = el('tr', 'ops-row');
      tr.innerHTML =
        `<td class="mono">${esc(r.runId)}</td>` +
        `<td class="ops-prompt">${esc(clip(r.prompt, 64))}</td>` +
        `<td class="num ${r.blocked ? 'bad' : ''}">${r.blocked}</td>` +
        `<td class="num">${r.needsConfirmation}</td>` +
        `<td class="num ok">${r.verified}</td>` +
        `<td class="mono">${esc(r.keyId)}</td>` +
        `<td class="num">${r.approvalCount}</td>`;
      tr.onclick = () => openRun(r.runId);
      tb.appendChild(tr);
    });
    tbl.appendChild(tb); box.appendChild(tbl);
  });
}

function openRun(id) {
  const box = $('#opsDetail');
  box.innerHTML = '<p class="empty">Loading…</p>';
  fetch('/api/runs/' + encodeURIComponent(id)).then(r => r.json()).then(b => {
    box.innerHTML = '';
    const head = el('div', 'ops-detail-head');
    head.innerHTML = `<div class="mono ops-id">${esc(id)}</div><div class="ops-detail-prompt">"${esc(b.prompt)}"</div>`;
    box.appendChild(head);

    // Action bar: verify integrity, replay (diff), and links to each signed split artifact.
    const bar = el('div', 'ops-actions');
    const vBtn = el('button', 'exp-btn'); vBtn.textContent = '✓ Verify integrity';
    vBtn.onclick = () => verifyRun(id);
    const rBtn = el('button', 'exp-btn'); rBtn.textContent = '↺ Replay (diff)';
    rBtn.onclick = () => replayRun(id);
    bar.append(vBtn, rBtn);
    ['intent.graph.json', 'policy.decisions.json', 'execution.trace.json', 'verification.report.json', 'audit.signed.json'].forEach(name => {
      const a = el('a', 'exp-btn artifact-link');
      a.textContent = '⬇ ' + name.replace('.json', '');
      a.href = '/api/runs/' + encodeURIComponent(id) + '/artifact/' + name;
      a.target = '_blank';
      bar.appendChild(a);
    });
    box.appendChild(bar);

    const out = el('div', 'ops-out'); out.id = 'opsOut'; box.appendChild(out);

    // Policy evidence — the decisions that produced this run.
    const sec = el('div', 'ops-policy');
    sec.innerHTML = '<div class="nd-sec-title">Policy evidence</div>';
    (b.policyDecisions.decisions || []).forEach(p => {
      const row = el('div', 'row' + (p.decision === 'Block' ? ' block' : ''));
      row.innerHTML = `<div class="top"><div class="lbl">${esc(p.label)}</div>` +
        `<span class="badge ${statusClassFromDecision(p.decision)}">${esc(p.decision.toUpperCase())}</span></div>` +
        `<div class="reason">${esc(p.reason)}</div>` +
        `<div class="rules">rules: ${esc((p.triggeredRules || []).join(', '))}</div>`;
      sec.appendChild(row);
    });
    box.appendChild(sec);
  }).catch(() => { box.innerHTML = '<p class="empty">Could not load run.</p>'; });
}

function verifyRun(id) {
  const out = $('#opsOut'); if (out) out.innerHTML = 'Verifying…';
  fetch('/api/runs/' + encodeURIComponent(id) + '/verify').then(r => r.json()).then(v => {
    const ok = v.signatureValid && v.artifactsValid;
    out.innerHTML = `<div class="verdict ${ok ? 'ok' : 'bad'}">` +
      `${ok ? '✓ TAMPER-EVIDENT: bundle + all artifacts verify' : '✕ INTEGRITY FAILED'} · ` +
      `signature ${v.signatureValid ? '✓' : '✕'} · artifacts ${v.artifactsValid ? '✓' : '✕'} · key ${esc(v.keyId)}</div>`;
  });
}

function replayRun(id) {
  const out = $('#opsOut'); if (out) out.innerHTML = 'Replaying…';
  fetch('/api/runs/' + encodeURIComponent(id) + '/replay', { method: 'POST' }).then(r => r.json()).then(v => {
    const ok = v.signatureVerified && v.reproduced;
    let html = `<div class="verdict ${ok ? 'ok' : 'bad'}">` +
      `${ok ? '✓ REPRODUCED byte-for-byte' : (v.reproduced ? '' : '✕ NON-REPRODUCTION')}` +
      ` · signature ${v.signatureVerified ? '✓' : '✕'} · reproduced ${v.reproduced ? '✓' : '✕'}</div>`;
    html += `<div class="ops-diff"><div class="mono">stored:     ${esc(clip(v.storedSignature, 40))}</div>` +
      `<div class="mono">recomputed: ${esc(clip(v.recomputedSignature, 40))}</div></div>`;
    out.innerHTML = html;
  });
}

function explainPrompt() {
  const prompt = $('#prompt').value.trim();
  const box = $('#explain');
  if (!prompt) { box.innerHTML = '<p class="empty">Enter a prompt above first.</p>'; return; }
  box.innerHTML = '<p class="empty">Explaining…</p>';
  fetch('/api/explain', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt, approvals: [...APPROVALS] })
  }).then(r => r.json()).then(x => {
    box.innerHTML = '';
    section('Blocked — cannot proceed (approval can’t lift these)', x.blocked, true);
    section('Awaiting approval', x.awaitingApproval, false);
    if (x.ifApproved && x.ifApproved.length) {
      const sec = el('div', 'ops-policy');
      sec.innerHTML = '<div class="nd-sec-title">If you approve every pending item…</div>';
      x.ifApproved.forEach(d => {
        const row = el('div', 'row');
        row.innerHTML = `<div class="top"><div class="lbl">${esc(d.label)}</div>` +
          `<span class="badge ${d.changed ? 'st-Verified' : 'st-Blocked'}">${esc(d.before)} → ${esc(d.after)}</span></div>`;
        sec.appendChild(row);
      });
      box.appendChild(sec);
    }
    if (!x.blocked.length && !x.awaitingApproval.length) box.innerHTML = '<p class="empty">Nothing gated — every node was allowed.</p>';

    function section(title, items, isBlock) {
      if (!items || !items.length) return;
      const sec = el('div', 'ops-policy');
      sec.innerHTML = `<div class="nd-sec-title">${esc(title)}</div>`;
      items.forEach(g => {
        const row = el('div', 'row' + (isBlock ? ' block' : ''));
        row.innerHTML = `<div class="top"><div class="lbl">${esc(g.label)}</div>` +
          `<span class="badge ${statusClassFromDecision(g.decision)}">${esc(g.decision.toUpperCase())}</span></div>` +
          `<div class="reason">${esc(g.reason)}</div>` +
          `<div class="rules">rules: ${esc((g.triggeredRules || []).join(', '))}</div>`;
        sec.appendChild(row);
      });
      box.appendChild(sec);
    }
  });
}

$('#refreshRuns').onclick = loadRuns;
$('#explainBtn').onclick = explainPrompt;

// ── helpers ────────────────────────────────────────────────────────
function deleteFileRefs(n) {
  const f = (n.fields || []).find(x => x.field === 'fileRefs');
  return f ? f.value.split(',').map(s => s.trim()).filter(Boolean) : [];
}
function pretty(s) { return ({ NeedsConfirmation: 'needs-confirm', Verified: 'verified', Executed: 'executed', Blocked: 'blocked', Halted: 'halted', Allowed: 'allowed', Resolved: 'resolved', Pending: 'pending' })[s] || s; }
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
