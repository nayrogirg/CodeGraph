'use strict';

cytoscape.use(cytoscapeElk);
cytoscape.use(cytoscapeFcose);

// ------------------------------------------------------------------ constants

const LEVELS = ['architecture', 'namespaces', 'classes', 'methods', 'messaging'];
const LEVEL_RANK = { architecture: 0, namespaces: 1, classes: 2, methods: 3 };
// Exchanges/queues sit at namespace rank so they roll up into the RabbitMQ node in Architecture
// and stay individual nodes in every finer view.
const RANK = { Solution: 0, Project: 0, Package: 0, Broker: 0, Namespace: 1, Exchange: 1, Queue: 1, Type: 2, Method: 3 };
const MQ_KINDS = new Set(['Broker', 'Exchange', 'Queue']);
const MQ_EDGES = new Set(['Publishes', 'Binds', 'Consumes', 'Carries']);
/** Kinds that stay leaf nodes in views finer than their own rank. */
const LEAF_ANY_RANK = new Set(['Package', 'Exchange', 'Queue']);
const FRAMEWORK_ID = 'pkg:.NET';

const EDGE_KINDS = [
  { kind: 'Calls', label: 'Calls', color: '#5c7cfa', style: 'solid' },
  { kind: 'Instantiates', label: 'Creates (new)', color: '#2f9e44', style: 'solid' },
  { kind: 'UsesType', label: 'Uses type', color: '#98a1b0', style: 'dotted' },
  { kind: 'Inherits', label: 'Inherits', color: '#8b5cf6', style: 'solid' },
  { kind: 'Implements', label: 'Implements', color: '#8b5cf6', style: 'dashed' },
  { kind: 'Overrides', label: 'Overrides', color: '#d6336c', style: 'dotted' },
  { kind: 'ProjectRef', label: 'Project reference', color: '#3b6fd8', style: 'solid' },
  { kind: 'PackageRef', label: 'Package reference', color: '#d8912b', style: 'dashed' },
  { kind: 'Publishes', label: 'Publishes / sends', color: '#f76707', style: 'solid' },
  { kind: 'Binds', label: 'Routes to queue', color: '#f59f00', style: 'dashed' },
  { kind: 'Consumes', label: 'Consumed by', color: '#12b886', style: 'solid' },
  { kind: 'Carries', label: 'Carries message', color: '#adb5bd', style: 'dotted' },
];
const EDGE_BY_KIND = Object.fromEntries(EDGE_KINDS.map(k => [k.kind, k]));
// When several relationship kinds connect the same pair, the edge is drawn as the most structural one.
const EDGE_PRIORITY = ['Inherits', 'Implements', 'ProjectRef', 'PackageRef', 'Publishes', 'Consumes', 'Binds',
                       'Calls', 'Instantiates', 'Overrides', 'UsesType', 'Carries'];

const NODE_COLORS = {
  solution: '#64748b', project: '#3b6fd8', package: '#d8912b', framework: '#8a8f99', namespace: '#12a594',
  class: '#7c4dff', interface: '#a855f7', record: '#5a67d8', struct: '#0ea5b7', enum: '#e8590c', delegate: '#c2255c',
  method: '#2f9e44', constructor: '#1971c2', property: '#f08c00', event: '#d6336c',
  broker: '#ff6600', exchange: '#f76707', queue: '#e03131',
};
const LEGEND = [
  ['Solution', 'solution'], ['Project', 'project'], ['NuGet package', 'package'], ['.NET framework', 'framework'], ['Namespace', 'namespace'],
  ['Class', 'class'], ['Interface', 'interface'], ['Record', 'record'], ['Struct', 'struct'], ['Enum', 'enum'],
  ['Method', 'method'], ['Constructor', 'constructor'], ['Property', 'property'], ['Event', 'event'],
  ['Exchange', 'exchange'], ['Queue', 'queue'],
];

const $ = sel => document.querySelector(sel);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ------------------------------------------------------------------ state

const S = {
  graph: null,
  nodes: new Map(),     // id -> node (with _anc: [self, parent, ...] and _root)
  children: new Map(),  // id -> [child nodes]
  level: 'architecture',
  focus: null,
  selected: null,
  pendingSelect: null,
  opts: {
    kinds: EDGE_KINDS.map(k => k.kind),
    external: true, framework: false, publicOnly: false, hideIsolated: false, messaging: true,
    depth: 2, direction: 'both', layout: 'elk', mqGranularity: 'Type', mqMessages: true,
  },
};

const storage = {
  get(key, fallback) { try { const v = localStorage.getItem(key); return v == null ? fallback : JSON.parse(v); } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* storage unavailable */ } },
};

// ------------------------------------------------------------------ cytoscape

const cy = cytoscape({
  container: $('#cy'),
  wheelSensitivity: 0.25,
  minZoom: 0.05,
  maxZoom: 3,
  boxSelectionEnabled: false,
  style: [],
});

function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

function buildStyle() {
  const text = cssVar('--text'), muted = cssVar('--muted'), accent = cssVar('--accent'), panel = cssVar('--panel');
  const style = [
    {
      selector: 'node', style: {
        'label': 'data(label)', 'font-size': 12, 'color': text, 'font-family': 'system-ui, "Segoe UI", sans-serif',
        'text-valign': 'center', 'text-halign': 'center', 'shape': 'round-rectangle',
        'width': 'data(w)', 'height': 'data(h)',
        'background-color': 'data(color)', 'background-opacity': 0.16,
        'border-width': 1.5, 'border-color': 'data(color)', 'min-zoomed-font-size': 7,
      },
    },
    {
      selector: ':parent', style: {
        'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -3,
        'background-opacity': 0.045, 'border-width': 1, 'padding': 14,
        'font-weight': 600, 'font-size': 12, 'color': 'data(color)',
      },
    },
    { selector: 'node.k-project', style: { 'font-weight': 700, 'font-size': 13 } },
    { selector: 'node.k-package', style: { 'shape': 'barrel' } },
    { selector: 'node.k-package:parent', style: { 'shape': 'round-rectangle' } },
    { selector: 'node.t-interface', style: { 'border-style': 'dashed', 'font-style': 'italic' } },
    { selector: 'node.abstract', style: { 'font-style': 'italic' } },
    { selector: 'node.t-enum', style: { 'shape': 'cut-rectangle' } },
    { selector: 'node.k-method', style: { 'font-size': 11 } },
    { selector: 'node.m-property, node.m-indexer', style: { 'shape': 'tag' } },
    { selector: 'node.m-constructor', style: { 'shape': 'rhomboid' } },
    { selector: 'node.ext', style: { 'border-style': 'dashed', 'background-opacity': 0.06, 'color': muted } },
    { selector: 'node.k-exchange', style: { 'shape': 'hexagon', 'border-width': 2 } },
    { selector: 'node.k-queue', style: { 'shape': 'rectangle', 'border-width': 2, 'background-opacity': 0.22 } },
    { selector: 'node.k-broker', style: { 'shape': 'round-rectangle', 'font-weight': 700 } },
    { selector: 'node.unresolved', style: { 'border-style': 'dashed', 'font-style': 'italic' } },
    { selector: 'node.ctx', style: { 'opacity': 0.62 } },
    { selector: 'node.seed', style: { 'border-width': 3, 'background-opacity': 0.28 } },
    {
      selector: 'node:selected', style: {
        'border-width': 3, 'border-color': accent, 'background-opacity': 0.34,
        'overlay-opacity': 0, 'underlay-color': accent, 'underlay-opacity': 0.18, 'underlay-padding': 5,
      },
    },
    {
      selector: 'edge', style: {
        'curve-style': 'bezier', 'width': 'data(width)', 'line-color': 'data(color)',
        'target-arrow-color': 'data(color)', 'target-arrow-shape': 'triangle', 'arrow-scale': 0.9, 'opacity': 0.75,
      },
    },
    { selector: 'edge:selected', style: { 'opacity': 1, 'overlay-opacity': 0 } },
    { selector: '.faded', style: { 'opacity': 0.1 } },
    { selector: 'node.hl', style: { 'opacity': 1 } },
    { selector: 'edge.hl', style: { 'opacity': 1, 'z-index': 20, 'label': 'data(label)', 'font-size': 10, 'color': text,
                                    'text-background-color': panel, 'text-background-opacity': 0.85, 'text-background-padding': 2 } },
  ];
  for (const k of EDGE_KINDS) {
    const s = { 'line-style': k.style };
    if (['Inherits', 'Implements', 'Overrides'].includes(k.kind)) s['target-arrow-fill'] = 'hollow';
    if (k.kind === 'Instantiates') s['target-arrow-shape'] = 'vee';
    if (k.kind === 'UsesType') s['target-arrow-shape'] = 'chevron';
    style.push({ selector: `edge.ek-${k.kind}`, style: s });
  }
  return style;
}

// ------------------------------------------------------------------ graph helpers

function setGraph(graph) {
  S.graph = graph;
  S.nodes = new Map(graph.nodes.map(n => [n.id, n]));
  S.children = new Map();
  for (const n of graph.nodes) {
    n.meta ??= {};
    if (!n.parentId) continue;
    if (!S.children.has(n.parentId)) S.children.set(n.parentId, []);
    S.children.get(n.parentId).push(n);
  }
  for (const n of graph.nodes) {
    const anc = [];
    for (let a = n; a && anc.length < 10; a = S.nodes.get(a.parentId)) anc.push(a.id);
    n._anc = anc;
    n._root = anc[anc.length - 1];
    // The project a node belongs to (solutions may sit above projects when a whole folder is analyzed).
    n._project = anc.find(id => S.nodes.get(id)?.kind === 'Project') ?? n._root;
  }
  S.selected = null;
  renderStats();
  renderWarnings();
  renderEdgeFilters();
  $('#path-input').value = graph.sourcePath;
  document.title = `${graph.name} · CodeGraph`;
  applyHash(true);
}

const rep = (n, rank) => { while (n && RANK[n.kind] > rank) n = S.nodes.get(n.parentId); return n; };
/** Like rep, but in the Classes view external types collapse into their package unless that package is focused. */
const viewRep = (n, rank) => (rank === 2 && n?.external && !inFocus(n) ? rep(n, 0) : rep(n, rank));
const isFramework = n => n._root === FRAMEWORK_ID;
const inFocus = n => !!S.focus && n._anc.includes(S.focus);

function isVisible(n) {
  if (!n) return false;
  if (MQ_KINDS.has(n.kind)) return S.opts.messaging || S.level === 'messaging';
  if (n.external && !inFocus(n)) {
    if (isFramework(n) ? !S.opts.framework : !S.opts.external) return false;
  }
  if (S.opts.publicOnly && !n.external) {
    for (const id of n._anc) {
      const a = S.nodes.get(id);
      if ((a.kind === 'Type' || a.kind === 'Method') && a.meta.accessibility && a.meta.accessibility !== 'public') return false;
    }
  }
  return true;
}

/** Rolls every raw edge up to the nodes shown at `rank`, merging parallel edges and summing counts. */
function aggregate(rank) {
  const kinds = new Set(S.opts.kinds);
  const agg = new Map();
  for (const e of S.graph.edges) {
    if (!kinds.has(e.kind)) continue;
    if (rank > 0 && (e.kind === 'ProjectRef' || e.kind === 'PackageRef')) continue;
    const s = S.nodes.get(e.source), t = S.nodes.get(e.target);
    if (!isVisible(s) || !isVisible(t)) continue;
    const rs = viewRep(s, rank), rt = viewRep(t, rank);
    if (!rs || !rt || rs === rt || !isVisible(rs) || !isVisible(rt)) continue;
    const key = rs.id + '\u0000' + rt.id;
    addToAggregate(agg, rs.id, rt.id, e);
  }
  return [...agg.values()];
}

function addToAggregate(agg, source, target, e) {
  const key = source + '\u0000' + target;
  let a = agg.get(key);
  if (!a) agg.set(key, a = { source, target, counts: {}, total: 0, meta: {} });
  a.counts[e.kind] = (a.counts[e.kind] || 0) + e.count;
  a.total += e.count;
  // Merge comma-separated meta lists (message types, routing keys...) from all rolled-up edges.
  for (const [k, v] of Object.entries(e.meta || {})) {
    const set = new Set((a.meta[k] ? a.meta[k].split(', ') : []).concat(v.split(', ')));
    a.meta[k] = [...set].join(', ');
  }
}

function nodeColor(n) {
  switch (n.kind) {
    case 'Solution': return NODE_COLORS.solution;
    case 'Project': return NODE_COLORS.project;
    case 'Package': return isFramework(n) ? NODE_COLORS.framework : NODE_COLORS.package;
    case 'Namespace': return NODE_COLORS.namespace;
    case 'Broker': return NODE_COLORS.broker;
    case 'Exchange': return NODE_COLORS.exchange;
    case 'Queue': return NODE_COLORS.queue;
    case 'Type': {
      const k = n.meta.typeKind || 'class';
      if (k === 'record struct') return NODE_COLORS.struct;
      return NODE_COLORS[k] || NODE_COLORS.class;
    }
    case 'Method': {
      const k = n.meta.memberKind;
      if (k === 'indexer') return NODE_COLORS.property;
      return NODE_COLORS[k] || NODE_COLORS.method;
    }
  }
  return '#888';
}

function kindLabel(n) {
  if (n.kind === 'Type') return n.meta.typeKind || 'type';
  if (n.kind === 'Method') return n.meta.memberKind || 'method';
  if (n.kind === 'Package') return isFramework(n) ? 'framework' : (n.meta.source === 'assembly' ? 'assembly' : 'NuGet package');
  if (n.kind === 'Exchange') return n.meta.exchangeType ? `${n.meta.exchangeType} exchange` : 'exchange';
  if (n.kind === 'Broker') return 'message broker';
  return n.kind.toLowerCase();
}

function shortLabel(n) {
  if (n.meta.unresolved) return `? ${n.name}`;
  if (n.kind === 'Method') {
    const i = n.name.indexOf('(');
    return i < 0 ? n.name : n.name.slice(0, i) + (n.name.slice(i) === '()' ? '()' : '(…)');
  }
  if (n.kind === 'Package' && n.meta.version) return `${n.name} ${n.meta.version}`;
  return n.name;
}

const measureCtx = document.createElement('canvas').getContext('2d');
function textWidth(text, size) {
  measureCtx.font = `${size}px system-ui, "Segoe UI", sans-serif`;
  return measureCtx.measureText(text).width;
}

function nodeElement(n, parent, classes = []) {
  const label = shortLabel(n);
  const size = n.kind === 'Method' ? 11 : n.kind === 'Project' ? 13 : 12;
  const cls = [`k-${n.kind.toLowerCase()}`, ...classes];
  if (n.kind === 'Type') cls.push(`t-${(n.meta.typeKind || 'class').replace(' ', '-')}`);
  if (n.kind === 'Method') cls.push(`m-${n.meta.memberKind || 'method'}`);
  if (n.external) cls.push('ext');
  if (n.meta.abstract) cls.push('abstract');
  if (n.meta.unresolved) cls.push('unresolved');
  const padding = n.kind === 'Exchange' ? 44 : n.kind === 'Queue' ? 30 : 22; // hexagons need room for their slanted sides
  return {
    group: 'nodes',
    data: {
      id: n.id, parent: parent || undefined, label, color: nodeColor(n),
      w: Math.max(36, Math.ceil(textWidth(label, size) + padding)),
      h: n.kind === 'Method' ? 24 : n.kind === 'Project' || MQ_KINDS.has(n.kind) ? 34 : 28,
    },
    classes: cls.join(' '),
  };
}

function edgeElement(a) {
  // Between projects/packages, a declared reference is the defining relationship.
  const priority = S.level === 'architecture' ? ['ProjectRef', 'PackageRef', ...EDGE_PRIORITY] : EDGE_PRIORITY;
  const kind = priority.find(k => a.counts[k]) || Object.keys(a.counts)[0];
  const def = EDGE_BY_KIND[kind];
  return {
    group: 'edges',
    data: {
      id: `${a.source}->${a.target}`, source: a.source, target: a.target, kind, counts: a.counts, total: a.total, meta: a.meta,
      color: def.color, width: Math.min(7, 1 + Math.log2(a.total)), label: a.total > 1 ? `×${a.total}` : '',
    },
    classes: `ek-${kind}`,
  };
}

// ------------------------------------------------------------------ view building

/** Architecture / namespaces / classes: nodes of the level's rank, boxed inside their ancestors. */
function buildStructuralView(rank) {
  const repIds = new Set();
  for (const n of S.graph.nodes) {
    if (!isVisible(n)) continue;
    const r = viewRep(n, rank);
    if (r && isVisible(r)) repIds.add(r.id);
  }
  // A candidate that contains other candidates is a box, not a leaf.
  const containers = new Set();
  for (const id of repIds) for (const a of S.nodes.get(id)._anc.slice(1)) containers.add(a);
  let leaves = [...repIds].filter(id => (!containers.has(id) || RANK[S.nodes.get(id).kind] === rank) && S.nodes.get(id).kind !== 'Solution');
  if (rank > 0) leaves = leaves.filter(id => RANK[S.nodes.get(id).kind] === rank || LEAF_ANY_RANK.has(S.nodes.get(id).kind));

  let edges = aggregate(rank);
  const leafSet = new Set(leaves);
  edges = edges.filter(e => leafSet.has(e.source) && leafSet.has(e.target));

  let scope = null;
  if (S.focus) {
    const f = S.nodes.get(S.focus);
    const focusRep = f && RANK[f.kind] > rank ? rep(f, rank)?.id : null;
    scope = new Set(leaves.filter(id => S.nodes.get(id)._anc.includes(S.focus) || id === focusRep));
    if (!scope.size) return { empty: 'Nothing to show for this focus with the current filters.' };
    edges = edges.filter(e => scope.has(e.source) || scope.has(e.target));
    const shown = new Set(scope);
    for (const e of edges) { shown.add(e.source); shown.add(e.target); }
    leaves = [...shown];
  } else if (rank > 0) {
    // Packages at inner levels are only interesting when something uses them.
    const used = new Set(edges.flatMap(e => [e.source, e.target]));
    leaves = leaves.filter(id => S.nodes.get(id).kind !== 'Package' || used.has(id));
  }

  if (S.opts.hideIsolated) {
    const connected = new Set(edges.flatMap(e => [e.source, e.target]));
    leaves = leaves.filter(id => connected.has(id) || id === S.focus);
  }
  if (!leaves.length) return { empty: 'Nothing to show with the current filters.' };

  const elements = [];
  const boxes = new Set();
  for (const id of leaves) {
    const n = S.nodes.get(id);
    const parent = n.parentId; // at the architecture level this is the solution box (folder analysis), if any
    const cls = scope && !scope.has(id) ? ['ctx'] : [];
    if (S.focus === id) cls.push('seed');
    elements.push(nodeElement(n, parent, cls));
    for (const a of n._anc.slice(1)) boxes.add(a);
  }
  for (const id of boxes) {
    const n = S.nodes.get(id);
    elements.push(nodeElement(n, n.parentId, scope && !n._anc.includes(S.focus) && !S.nodes.get(S.focus)?._anc.includes(id) ? ['ctx'] : []));
  }
  for (const e of edges) elements.push(edgeElement(e));

  const note = S.level === 'classes' && !S.focus && leaves.length > 400
    ? `${leaves.length} classes. Double-click a namespace or project to focus on it.` : null;
  return { elements, note, count: leaves.length };
}

/** Methods: call graph around the focused type/method, N hops in the chosen direction. */
function buildMethodsView() {
  const agg = aggregate(3);
  const f = S.focus && S.nodes.get(S.focus);
  let seeds;
  if (f && f.kind === 'Method') seeds = [f.id];
  else if (f) seeds = S.graph.nodes.filter(n => (n.kind === 'Method' || n.id === f.id) && n._anc.includes(f.id) && isVisible(n)).map(n => n.id);
  else {
    seeds = S.graph.nodes.filter(n => n.kind === 'Method' && !n.external && isVisible(n)).map(n => n.id);
    if (seeds.length > 300)
      return { empty: `This solution has ${seeds.length} methods.\nPick a class (double-click it in the Classes view) or search for a method to explore its call graph.` };
  }
  if (!seeds.length) return { empty: 'No methods to show for this selection.' };

  const out = new Map(), inc = new Map();
  for (const e of agg) {
    if (!out.has(e.source)) out.set(e.source, []);
    if (!inc.has(e.target)) inc.set(e.target, []);
    out.get(e.source).push(e.target);
    inc.get(e.target).push(e.source);
  }
  const visited = new Set(seeds);
  const walk = adjacency => {
    let frontier = [...seeds];
    for (let d = 0; d < S.opts.depth && frontier.length; d++) {
      const next = [];
      for (const id of frontier) for (const nb of adjacency.get(id) || []) if (!visited.has(nb)) { visited.add(nb); next.push(nb); }
      frontier = next;
    }
  };
  if (S.opts.direction !== 'in') walk(out);
  if (S.opts.direction !== 'out') walk(inc);

  const seedSet = new Set(seeds);
  let edges = agg.filter(e => visited.has(e.source) && visited.has(e.target));
  let leaves = [...visited];
  if (S.opts.hideIsolated) {
    const connected = new Set(edges.flatMap(e => [e.source, e.target]));
    leaves = leaves.filter(id => connected.has(id) || id === S.focus);
  }

  const elements = [];
  const leafSet = new Set(leaves);
  const boxes = new Set();
  for (const id of leaves) {
    const n = S.nodes.get(id);
    const parent = n.kind === 'Method' ? n.parentId : null;
    if (parent && !leafSet.has(parent)) boxes.add(parent);
    elements.push(nodeElement(n, parent, seedSet.has(id) ? (f ? ['seed'] : []) : ['ctx']));
  }
  for (const id of boxes) elements.push(nodeElement(S.nodes.get(id), null, f && !seedSet.has(id) && id !== f.id && id !== f.parentId ? ['ctx'] : []));
  for (const e of edges) elements.push(edgeElement(e));

  const note = leaves.length > 500 ? `${leaves.length} members. Reduce the depth to simplify.` : null;
  return { elements, note, count: leaves.length };
}

/** Messaging: publishers → exchanges → queues → consumers (+ message types), grouped by project. */
function buildMessagingView() {
  const endpointRank = RANK[S.opts.mqGranularity] ?? 2;
  const kinds = new Set(S.opts.kinds);
  // Endpoints (publishers/consumers/message types) at the chosen granularity; exchanges/queues as themselves.
  const endpoint = n => (MQ_KINDS.has(n.kind) ? n : rep(n, endpointRank));
  const agg = new Map();
  for (const e of S.graph.edges) {
    if (!MQ_EDGES.has(e.kind) || !kinds.has(e.kind)) continue;
    if (e.kind === 'Carries' && !S.opts.mqMessages) continue;
    const s = S.nodes.get(e.source), t = S.nodes.get(e.target);
    if (!s || !t || (!s.external && !isVisible(s)) || (!t.external && !isVisible(t))) continue;
    const rs = endpoint(s), rt = endpoint(t);
    if (rs && rt && rs !== rt) addToAggregate(agg, rs.id, rt.id, e);
  }
  let edges = [...agg.values()];
  let leafIds = new Set(edges.flatMap(e => [e.source, e.target]));
  // Declared exchanges/queues that nothing uses are worth seeing too (e.g. a queue without consumers).
  for (const n of S.graph.nodes) if (n.kind === 'Exchange' || n.kind === 'Queue') leafIds.add(n.id);
  if (!leafIds.size) return { empty: 'No RabbitMQ usage found.\nCodeGraph detects RabbitMQ.Client (ExchangeDeclare, QueueBind, BasicPublish, BasicConsume) and MassTransit (IConsumer<T>, Publish, Send, ReceiveEndpoint).' };

  let seeds = null;
  const f = S.focus && S.nodes.get(S.focus);
  if (f) {
    const focusRep = endpoint(f)?.id;
    seeds = [...leafIds].filter(id => id === focusRep || S.nodes.get(id)._anc.includes(f.id));
    if (!seeds.length) return { empty: `${f.name} is not involved in any RabbitMQ flow.` };
    // The whole connected flow, regardless of direction.
    const adjacent = new Map();
    for (const e of edges) {
      (adjacent.get(e.source) || adjacent.set(e.source, []).get(e.source)).push(e.target);
      (adjacent.get(e.target) || adjacent.set(e.target, []).get(e.target)).push(e.source);
    }
    const seen = new Set(seeds), queue = [...seeds];
    while (queue.length) for (const nb of adjacent.get(queue.pop()) || []) if (!seen.has(nb)) { seen.add(nb); queue.push(nb); }
    leafIds = seen;
    edges = edges.filter(e => seen.has(e.source) && seen.has(e.target));
  }

  const elements = [];
  const boxes = new Set();
  const seedSet = new Set(seeds || []);
  for (const id of leafIds) {
    const n = S.nodes.get(id);
    // Exchanges/queues sit in the broker box; code sits in its project box (and class box at method granularity).
    const parent = MQ_KINDS.has(n.kind) ? n.parentId : n.kind === 'Method' ? n.parentId : n._project;
    if (parent && parent !== id) {
      boxes.add(parent);
      const p = S.nodes.get(parent);
      if (p?.kind === 'Type') boxes.add(p._project);
    }
    elements.push(nodeElement(n, parent !== id ? parent : null, seedSet.has(id) ? ['seed'] : []));
  }
  for (const id of boxes) {
    if (leafIds.has(id)) continue;
    const n = S.nodes.get(id);
    elements.push(nodeElement(n, n.kind === 'Type' ? n._project : null));
  }
  for (const e of edges) elements.push(edgeElement(e));
  return { elements, count: leafIds.size };
}

// ------------------------------------------------------------------ rendering

let layoutRun = null;

function render() {
  updateTabs();
  updateCrumbs();
  $('#method-options').hidden = S.level !== 'methods';
  $('#messaging-options').hidden = S.level !== 'messaging';
  hideTooltip();
  if (!S.graph) return;

  const view = S.level === 'methods' ? buildMethodsView()
    : S.level === 'messaging' ? buildMessagingView()
    : buildStructuralView(LEVEL_RANK[S.level]);
  cy.elements().remove();
  if (view.empty) { showEmpty(view.empty); setNote(null); return; }
  showEmpty(null);
  cy.add(view.elements);
  setNote(view.note);
  runLayout();
}

function runLayout() {
  layoutRun?.stop();
  let name = S.opts.layout;
  const count = cy.nodes().length;
  // ELK runs on the main thread; beyond this size the organic layout is much faster.
  if (name === 'elk' && count > 700) {
    name = 'fcose';
    setNote(`${count} nodes: switched to the organic layout for speed.`);
  }
  const options = name === 'elk'
    ? {
      name: 'elk', fit: true, padding: 30, animate: false, nodeDimensionsIncludeLabels: true,
      elk: {
        'algorithm': 'layered',
        'elk.direction': S.level === 'methods' || S.level === 'messaging' ? 'RIGHT' : 'DOWN',
        'elk.hierarchyHandling': 'INCLUDE_CHILDREN',
        'elk.layered.spacing.nodeNodeBetweenLayers': 55,
        'elk.spacing.nodeNode': 20,
        'elk.spacing.componentComponent': 40,
        // Note: 'elk.layered.considerModelOrder' makes compound layouts take 100x longer; keep it off.
        'elk.padding': '[top=26,left=14,bottom=14,right=14]',
      },
    }
    : {
      name: 'fcose', quality: 'default', randomize: true, animate: false, fit: true, padding: 30,
      nodeDimensionsIncludeLabels: true, idealEdgeLength: 90, nodeRepulsion: 7000, nestingFactor: 0.15,
      gravity: 0.35, gravityCompound: 1.2, packComponents: true, numIter: 2500,
    };
  layoutRun = cy.layout(options);
  layoutRun.one('layoutstop', () => {
    if (cy.zoom() > 1.4) { cy.zoom(1.4); cy.center(); }
    applyPendingSelection();
  });
  layoutRun.run();
}

function applyPendingSelection() {
  const explicit = S.pendingSelect != null;
  const id = S.pendingSelect ?? S.selected;
  S.pendingSelect = null;
  if (!id) return;
  const ele = cy.getElementById(id);
  if (ele.nonempty()) {
    cy.$(':selected').unselect();
    ele.select();
    // Only move the camera when the user asked for this node (search, list click), not on filter changes.
    if (explicit && id !== S.focus && cy.nodes().length > 12)
      cy.animate({ center: { eles: ele }, zoom: Math.max(cy.zoom(), 0.9) }, { duration: 300 });
  }
  showDetails(id);
}

function showEmpty(message) {
  const el = $('#empty');
  el.hidden = !message;
  el.innerHTML = message ? esc(message).replace(/\n/g, '<br>') : '';
}

let noteTimer = null;
function setNote(message, { error = false, timeout = 0 } = {}) {
  const el = $('#view-note');
  clearTimeout(noteTimer);
  el.hidden = !message;
  el.textContent = message || '';
  el.style.color = error ? 'var(--warn)' : '';
  if (message && timeout) noteTimer = setTimeout(() => { el.hidden = true; }, timeout);
}

function updateTabs() {
  for (const b of document.querySelectorAll('.tabs button')) b.setAttribute('aria-selected', String(b.dataset.level === S.level));
}

function updateCrumbs() {
  const el = $('#crumbs');
  const parts = [`<a data-crumb="">All</a>`];
  const f = S.focus && S.nodes.get(S.focus);
  if (f) for (const id of [...f._anc].reverse()) {
    parts.push('<span class="sep">›</span>', `<a data-crumb="${esc(id)}" title="${esc(S.nodes.get(id).fullName)}">${esc(shortLabel(S.nodes.get(id)))}</a>`);
  }
  el.innerHTML = parts.join('');
}

function renderStats() {
  const s = S.graph.stats;
  const members = S.graph.nodes.filter(n => n.kind === 'Method' && !n.external).length;
  $('#stats').textContent =
    `${S.graph.name}: ${s.Solution ? `${s.Solution} solutions · ` : ''}${s.Project || 0} projects · ${s.Namespace || 0} namespaces · ${s.Type || 0} types · ${members} members · ` +
    (S.graph.fromCache ? `cached (analyzed in ${S.graph.durationSeconds}s)` : `${S.graph.durationSeconds}s`);
  const phases = Object.entries(S.graph.timings || {}).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k}: ${v}s`).join('\n');
  $('#stats').title = `Analyzed ${new Date(S.graph.analyzedAtUtc).toLocaleString()}` +
    (S.graph.fromCache ? ' (loaded from cache; check "fresh" to re-analyze)' : '') + (phases ? `\n\n${phases}` : '');
}

function renderWarnings() {
  const w = S.graph.warnings || [];
  $('#warnings-btn').hidden = !w.length;
  $('#warnings-count').textContent = w.length;
  $('#warnings-list').innerHTML = w.map(x => `<li>${esc(x)}</li>`).join('');
}

// ------------------------------------------------------------------ navigation

function levelForFocus(n) {
  if (!n) return 'architecture';
  if (MQ_KINDS.has(n.kind)) return 'messaging';
  switch (n.kind) {
    case 'Project': return 'namespaces';
    case 'Package': return 'classes';
    case 'Namespace': return 'classes';
    default: return 'methods';
  }
}

function navigate(level, focus = null, select = null) {
  S.pendingSelect = select;
  const hash = '#/' + level + (focus ? '/' + encodeURIComponent(focus) : '');
  if (location.hash === hash) { S.level = level; S.focus = focus; render(); }
  else location.hash = hash; // triggers applyHash via hashchange, which keeps browser back/forward working
}

function applyHash(initial = false) {
  const [, level, focus] = location.hash.match(/^#\/(\w+)(?:\/(.+))?$/) || [];
  S.level = LEVELS.includes(level) ? level : 'architecture';
  const f = focus ? decodeURIComponent(focus) : null;
  S.focus = f && S.nodes.has(f) ? f : null;
  if (initial) S.pendingSelect = null;
  render();
}

function drillDown(id) {
  const n = S.nodes.get(id);
  if (!n) return;
  if (MQ_KINDS.has(n.kind)) { navigate('messaging', n.kind === 'Broker' ? null : id, id); return; }
  if (S.level === 'messaging') { navigate(n.kind === 'Project' ? 'messaging' : 'methods', id, id); return; }
  if (S.level === 'methods') { navigate('methods', id, id); return; }
  if (S.level === 'classes') { navigate(n.kind === 'Type' ? 'methods' : 'classes', id, n.kind === 'Type' ? null : id); return; }
  if (S.level === 'namespaces') { navigate('classes', id); return; }
  navigate(n.kind === 'Package' ? 'classes' : 'namespaces', id);
}

/** Opens the most useful view for a node and selects it there. */
function goTo(id) {
  const n = S.nodes.get(id);
  if (!n) return;
  if (cy.getElementById(id).nonempty()) { S.pendingSelect = id; applyPendingSelection(); return; }
  if (n.external) {
    if (isFramework(n) && !S.opts.framework) { S.opts.framework = true; syncOptionInputs(); saveOptions(); }
    if (!isFramework(n) && !S.opts.external) { S.opts.external = true; syncOptionInputs(); saveOptions(); }
  }
  switch (n.kind) {
    case 'Broker': navigate('messaging', null, id); break;
    case 'Exchange': case 'Queue': navigate('messaging', id, id); break;
    case 'Solution': case 'Project': case 'Package': navigate('architecture', null, id); break;
    case 'Namespace': navigate('namespaces', n.parentId, id); break;
    case 'Type': navigate('classes', n.parentId, id); break;
    case 'Method': navigate('methods', id, id); break;
  }
}

// ------------------------------------------------------------------ details panel

function relations(n) {
  // For exchanges/queues the interesting counterparts are the publishing/consuming members themselves.
  const rank = MQ_KINDS.has(n.kind) ? 3 : RANK[n.kind];
  const out = new Map(), inc = new Map();
  const add = (map, kind, other, count) => {
    if (!map.has(kind)) map.set(kind, new Map());
    const m = map.get(kind);
    m.set(other.id, (m.get(other.id) || 0) + count);
  };
  for (const e of S.graph.edges) {
    const s = S.nodes.get(e.source), t = S.nodes.get(e.target);
    const sIn = s._anc.includes(n.id), tIn = t._anc.includes(n.id);
    if (sIn === tIn) continue;
    if (sIn) { const r = rep(t, rank); if (r) add(out, e.kind, r, e.count); }
    else { const r = rep(s, rank); if (r) add(inc, e.kind, r, e.count); }
  }
  return { out, inc };
}

function relationList(map) {
  if (!map.size) return '<div class="d-group">None</div>';
  let html = '';
  for (const kind of EDGE_PRIORITY.filter(k => map.has(k))) {
    const items = [...map.get(kind)].sort((a, b) => b[1] - a[1]);
    html += `<div class="d-group">${esc(EDGE_BY_KIND[kind].label)} (${items.length})</div><ul class="d-list">`;
    for (const [id, count] of items.slice(0, 200)) {
      const o = S.nodes.get(id);
      html += `<li data-goto="${esc(id)}" title="${esc(o.fullName)}"><span class="nm">${esc(shortLabel(o))}</span><span class="ct">${o.external ? 'ext · ' : ''}×${count}</span></li>`;
    }
    if (items.length > 200) html += `<li class="ct">… ${items.length - 200} more</li>`;
    html += '</ul>';
  }
  return html;
}

function relativePath(file) {
  const root = S.graph.sourcePath.replace(/[\\/][^\\/]*$/, '');
  return file && file.toLowerCase().startsWith(root.toLowerCase()) ? file.slice(root.length + 1) : file;
}

function showDetails(id) {
  S.selected = id;
  const panel = $('#details');
  const n = id && S.nodes.get(id);
  if (!n) {
    panel.innerHTML = `<div class="placeholder">Select a node to see its details.<br><br><b>Click</b> select · <b>Double-click</b> drill down · <b>Hover</b> highlight neighbours · <b>Backspace</b> go back</div>`;
    return;
  }
  const { out, inc } = relations(n);
  const children = (S.children.get(n.id) || []).slice().sort((a, b) => a.name.localeCompare(b.name));
  const meta = [];
  const m = n.meta;
  if (m.accessibility) meta.push(['Access', m.accessibility]);
  const mods = ['static', 'abstract', 'sealed', 'virtual', 'override', 'async'].filter(k => m[k]);
  if (mods.length) meta.push(['Modifiers', mods.join(' ')]);
  if (m.namespace) meta.push(['Namespace', m.namespace]);
  if (m.version) meta.push(['Version', m.version]);
  if (m.assembly) meta.push(['Assembly', m.assembly]);
  if (m.outputKind) meta.push(['Output', m.outputKind]);
  if (m.members) meta.push(['Members', m.members]);
  if (m.exchangeType) meta.push(['Exchange type', m.exchangeType]);
  if (MQ_KINDS.has(n.kind) && m.source) meta.push(['Origin', {
    declared: 'declared in code (ExchangeDeclare / QueueDeclare)',
    inferred: 'inferred from publish/consume calls',
    'masstransit-convention': 'MassTransit naming convention',
    'masstransit-endpoint': 'MassTransit ReceiveEndpoint',
  }[m.source] || m.source]);
  if (m.unresolved) meta.push(['Name', 'computed at run time; shown as the source expression']);
  if (n.file) meta.push(['File', `${relativePath(n.file)}${n.line ? ':' + n.line : ''}`]);

  const actions = [];
  if (n.kind !== 'Method' && !MQ_KINDS.has(n.kind) && S.focus !== n.id) actions.push(`<button data-act="focus">Focus</button>`);
  if (n.kind === 'Exchange' || n.kind === 'Queue' || n.kind === 'Type' || n.kind === 'Method' || n.kind === 'Project')
    actions.push(`<button data-act="flow">Messaging flow</button>`);
  if (n.kind === 'Type' || n.kind === 'Method') actions.push(`<button data-act="calls">Call graph</button>`);
  if (n.kind === 'Type' && S.level !== 'classes') actions.push(`<button data-act="classes">Show in classes</button>`);

  const chain = [...n._anc].reverse().slice(0, -1)
    .map(a => `<a data-goto="${esc(a)}">${esc(shortLabel(S.nodes.get(a)))}</a>`).join(' › ');

  panel.innerHTML = `
    <span class="d-kind" style="background:${nodeColor(n)}">${esc(kindLabel(n))}${n.external ? ' · external' : ''}</span>
    <div class="d-title">${esc(n.name)}</div>
    <div class="d-full">${esc(n.fullName)}</div>
    ${chain ? `<div class="crumbs" style="margin-top:6px;flex-wrap:wrap;white-space:normal">${chain}</div>` : ''}
    ${m.signature ? `<div class="d-sig">${esc(m.signature)}</div>` : ''}
    <dl class="d-meta">${meta.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join('')}</dl>
    <div class="d-actions">${actions.join('')}</div>
    <details class="d-sec" open><summary>Outgoing (${[...out.values()].reduce((s, x) => s + x.size, 0)})</summary>${relationList(out)}</details>
    <details class="d-sec" open><summary>Incoming (${[...inc.values()].reduce((s, x) => s + x.size, 0)})</summary>${relationList(inc)}</details>
    ${children.length ? `<details class="d-sec" ${children.length <= 40 ? 'open' : ''}><summary>Contains (${children.length})</summary><ul class="d-list">${
      children.slice(0, 300).map(c => `<li data-goto="${esc(c.id)}" title="${esc(c.fullName)}"><span class="nm">${esc(shortLabel(c))}</span><span class="ct">${esc(kindLabel(c))}</span></li>`).join('')}</ul></details>` : ''}
    ${n.file ? `<details class="d-sec" id="src-sec" ${n.kind === 'Type' || n.kind === 'Method' ? 'open' : ''}><summary>Source</summary><div id="src">Loading…</div></details>` : ''}
  `;
  const src = $('#src-sec');
  if (src) {
    const load = () => loadSource(n.id);
    if (src.open) load(); else src.addEventListener('toggle', load, { once: true });
  }
}

const CS_KEYWORDS = 'abstract|as|async|await|base|bool|break|case|catch|char|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|goto|if|implicit|in|init|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|required|return|sealed|set|short|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|using|value|var|virtual|void|volatile|when|where|while|yield';
const TOKEN_RE = new RegExp(`(//.*$|/\\*.*?\\*/)|(@?"(?:[^"\\\\]|\\\\.)*"|'(?:[^'\\\\]|\\\\.)*')|\\b(${CS_KEYWORDS})\\b`, 'g');
function highlightCs(line) {
  let html = '', last = 0;
  for (const m of line.matchAll(TOKEN_RE)) {
    html += esc(line.slice(last, m.index));
    const cls = m[1] ? 'cm' : m[2] ? 'st' : 'kw';
    html += `<span class="${cls}">${esc(m[0])}</span>`;
    last = m.index + m[0].length;
  }
  return html + esc(line.slice(last));
}

async function loadSource(id) {
  const target = $('#src');
  try {
    const res = await fetch(`/api/source?nodeId=${encodeURIComponent(id)}&context=18`);
    if (!res.ok) throw new Error(res.status === 404 ? 'Source file not available.' : res.statusText);
    const s = await res.json();
    if (S.selected !== id || !$('#src')) return;
    target.innerHTML = `<div class="file-link">${esc(relativePath(s.file))}:${s.line}</div><div class="code">${
      s.lines.map((l, i) => {
        const no = s.startLine + i;
        return `<div class="ln${no === s.line ? ' hl' : ''}"><span class="no">${no}</span><span>${highlightCs(l) || ' '}</span></div>`;
      }).join('')}</div>`;
    // Scroll only the code box (scrollIntoView would also scroll the details panel away from the title).
    const code = target.querySelector('.code'), hl = target.querySelector('.hl');
    if (code && hl) code.scrollTop = Math.max(0, hl.offsetTop - code.offsetTop - 40);
  } catch (err) {
    target.textContent = err.message;
  }
}

// ------------------------------------------------------------------ tooltip & hover

const tooltip = $('#tooltip');
function showTooltip(html, ev) {
  tooltip.innerHTML = html;
  tooltip.hidden = false;
  moveTooltip(ev);
}
function moveTooltip(ev) {
  const oe = ev?.originalEvent;
  if (!oe || tooltip.hidden) return;
  const x = Math.min(oe.clientX + 14, window.innerWidth - tooltip.offsetWidth - 8);
  const y = Math.min(oe.clientY + 14, window.innerHeight - tooltip.offsetHeight - 8);
  tooltip.style.left = `${x}px`;
  tooltip.style.top = `${y}px`;
}
function hideTooltip() { tooltip.hidden = true; }

function highlightNeighbourhood(ele) {
  let keep = ele.closedNeighborhood();
  if (ele.isParent()) {
    const inner = ele.descendants();
    keep = keep.union(inner).union(inner.connectedEdges()).union(inner.connectedEdges().connectedNodes());
  }
  keep = keep.union(keep.nodes().ancestors());
  cy.batch(() => {
    cy.elements().not(keep).addClass('faded');
    keep.addClass('hl');
  });
}
function clearHighlight() { cy.batch(() => cy.elements().removeClass('faded hl')); }

cy.on('mouseover', 'node', ev => {
  const n = S.nodes.get(ev.target.id());
  if (!n) return;
  highlightNeighbourhood(ev.target);
  showTooltip(`<div class="tt-kind">${esc(kindLabel(n))}${n.external ? ' · external' : ''}</div><b>${esc(n.name)}</b><div class="tt-kind">${esc(n.fullName)}</div>`, ev);
});
cy.on('mouseover', 'edge', ev => {
  const d = ev.target.data();
  const s = S.nodes.get(d.source), t = S.nodes.get(d.target);
  const counts = Object.entries(d.counts).map(([k, c]) => `${esc(EDGE_BY_KIND[k].label)} ×${c}`).join(' · ');
  const labels = { messageTypes: 'Messages', routingKeys: 'Routing keys', mode: 'Mode', via: 'Via' };
  const meta = Object.entries(d.meta || {}).filter(([k]) => labels[k])
    .map(([k, v]) => `<div class="tt-kind">${labels[k]}: ${esc(v)}</div>`).join('');
  ev.target.addClass('hl');
  showTooltip(`<b>${esc(shortLabel(s))}</b> → <b>${esc(shortLabel(t))}</b><div class="tt-kind">${counts}</div>${meta}`, ev);
});
cy.on('mousemove', ev => moveTooltip(ev));
cy.on('mouseout', 'node, edge', () => { clearHighlight(); hideTooltip(); });
cy.on('tap', 'node', ev => { if (ev.target.id() !== S.selected) showDetails(ev.target.id()); });
cy.on('tap', 'edge', ev => showDetails(ev.target.data('source')));
cy.on('tap', ev => { if (ev.target === cy) { cy.$(':selected').unselect(); showDetails(null); } });
cy.on('dbltap', 'node', ev => { hideTooltip(); drillDown(ev.target.id()); });

// ------------------------------------------------------------------ search

const searchInput = $('#search-input'), searchResults = $('#search-results');
let searchItems = [], searchActive = 0;
const SEARCH_KIND_ORDER = { Type: 0, Method: 1, Exchange: 2, Queue: 2, Namespace: 3, Project: 4, Solution: 5, Package: 6, Broker: 7 };

function runSearch() {
  const q = searchInput.value.trim().toLowerCase();
  if (!q || !S.graph) { searchResults.hidden = true; return; }
  const scored = [];
  for (const n of S.graph.nodes) {
    const name = n.name.toLowerCase();
    let score;
    if (name === q || name.startsWith(q + '(')) score = 0;
    else if (name.startsWith(q)) score = 1;
    else if (name.includes(q)) score = 2;
    else if (n.fullName.toLowerCase().includes(q)) score = 3;
    else continue;
    scored.push([score + (n.external ? 4 : 0), SEARCH_KIND_ORDER[n.kind], n]);
  }
  scored.sort((a, b) => a[0] - b[0] || a[1] - b[1] || a[2].name.length - b[2].name.length);
  searchItems = scored.slice(0, 30).map(x => x[2]);
  searchActive = 0;
  renderSearch();
}

function renderSearch() {
  searchResults.hidden = false;
  searchResults.innerHTML = searchItems.length
    ? searchItems.map((n, i) => `<li data-i="${i}" class="${i === searchActive ? 'active' : ''}">
        <span class="d-kind" style="background:${nodeColor(n)}">${esc(kindLabel(n))}</span>
        <span class="sr-name">${esc(shortLabel(n))}</span><span class="sr-full">${esc(n.fullName)}</span></li>`).join('')
    : '<li class="sr-full">No matches</li>';
  searchResults.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
}

function chooseSearch(i) {
  const n = searchItems[i];
  if (!n) return;
  searchResults.hidden = true;
  searchInput.blur();
  goTo(n.id);
}

searchInput.addEventListener('input', runSearch);
searchInput.addEventListener('focus', () => { if (searchInput.value) runSearch(); });
searchInput.addEventListener('blur', () => setTimeout(() => { searchResults.hidden = true; }, 150));
searchInput.addEventListener('keydown', ev => {
  if (ev.key === 'ArrowDown') { searchActive = Math.min(searchActive + 1, searchItems.length - 1); renderSearch(); ev.preventDefault(); }
  else if (ev.key === 'ArrowUp') { searchActive = Math.max(searchActive - 1, 0); renderSearch(); ev.preventDefault(); }
  else if (ev.key === 'Enter') { chooseSearch(searchActive); ev.preventDefault(); }
  else if (ev.key === 'Escape') { searchResults.hidden = true; searchInput.blur(); }
});
searchResults.addEventListener('mousedown', ev => {
  const li = ev.target.closest('li[data-i]');
  if (li) { ev.preventDefault(); chooseSearch(Number(li.dataset.i)); }
});

// ------------------------------------------------------------------ options panel

function renderEdgeFilters() {
  const counts = {};
  for (const e of S.graph?.edges || []) counts[e.kind] = (counts[e.kind] || 0) + 1;
  $('#edge-filters').innerHTML = EDGE_KINDS.map(k => `
    <label class="check" title="${esc(k.label)}">
      <span class="lbl"><input type="checkbox" data-kind="${k.kind}" ${S.opts.kinds.includes(k.kind) ? 'checked' : ''}>
      <span class="swatch-line" style="border-top-color:${k.color};border-top-style:${k.style}"></span>${esc(k.label)}</span>
      <span class="ct" style="color:var(--muted);font-size:11px">${counts[k.kind] || 0}</span>
    </label>`).join('');
}

function renderLegend() {
  $('#legend').innerHTML = LEGEND.map(([label, key]) =>
    `<div><span class="swatch" style="background:${NODE_COLORS[key]}33;border:1.5px solid ${NODE_COLORS[key]}"></span>${esc(label)}</div>`).join('')
    + `<div><span class="swatch" style="border:1.5px dashed var(--muted)"></span>External</div>`;
}

function syncOptionInputs() {
  $('#opt-external').checked = S.opts.external;
  $('#opt-framework').checked = S.opts.framework;
  $('#opt-public').checked = S.opts.publicOnly;
  $('#opt-isolated').checked = S.opts.hideIsolated;
  $('#opt-depth').value = S.opts.depth;
  $('#depth-out').textContent = S.opts.depth;
  $('#opt-direction').value = S.opts.direction;
  $('#opt-layout').value = S.opts.layout;
  $('#opt-messaging').checked = S.opts.messaging;
  $('#opt-mq-granularity').value = S.opts.mqGranularity;
  $('#opt-mq-messages').checked = S.opts.mqMessages;
}

function saveOptions() { storage.set('codegraph.opts', S.opts); }

$('#edge-filters').addEventListener('change', ev => {
  const kind = ev.target.dataset.kind;
  if (!kind) return;
  S.opts.kinds = ev.target.checked ? [...new Set([...S.opts.kinds, kind])] : S.opts.kinds.filter(k => k !== kind);
  saveOptions();
  render();
});
$('#opt-mq-granularity').addEventListener('change', ev => { S.opts.mqGranularity = ev.target.value; saveOptions(); render(); });
for (const [id, key] of [['#opt-external', 'external'], ['#opt-framework', 'framework'], ['#opt-public', 'publicOnly'],
                         ['#opt-isolated', 'hideIsolated'], ['#opt-messaging', 'messaging'], ['#opt-mq-messages', 'mqMessages']]) {
  $(id).addEventListener('change', ev => { S.opts[key] = ev.target.checked; saveOptions(); render(); });
}
$('#opt-depth').addEventListener('input', ev => { $('#depth-out').textContent = ev.target.value; });
$('#opt-depth').addEventListener('change', ev => { S.opts.depth = Number(ev.target.value); saveOptions(); render(); });
$('#opt-direction').addEventListener('change', ev => { S.opts.direction = ev.target.value; saveOptions(); render(); });
$('#opt-layout').addEventListener('change', ev => { S.opts.layout = ev.target.value; saveOptions(); runLayout(); });
$('#relayout-btn').addEventListener('click', () => runLayout());
$('#fit-btn').addEventListener('click', () => cy.animate({ fit: { padding: 30 } }, { duration: 250 }));

// ------------------------------------------------------------------ misc UI wiring

for (const b of document.querySelectorAll('.tabs button')) {
  b.addEventListener('click', () => {
    const level = b.dataset.level;
    // Keep the focus across tabs; a focused method becomes its class outside the Methods view.
    let focus = level === 'architecture' ? null : S.focus;
    const f = focus && S.nodes.get(focus);
    if (f?.kind === 'Method' && level !== 'methods' && level !== 'messaging') focus = f.parentId;
    if (f && MQ_KINDS.has(f.kind) && level !== 'messaging') focus = null;
    navigate(level, focus, S.selected);
  });
}

$('#crumbs').addEventListener('click', ev => {
  const a = ev.target.closest('a[data-crumb]');
  if (!a) return;
  const id = a.dataset.crumb || null;
  const n = id && S.nodes.get(id);
  let level = S.level;
  if (!n) level = S.level === 'methods' ? 'classes' : S.level;
  else if (S.level === 'methods' && RANK[n.kind] < 2) level = 'classes';
  navigate(level, id, id);
});

$('#details').addEventListener('click', ev => {
  const go = ev.target.closest('[data-goto]');
  if (go) { goTo(go.dataset.goto); return; }
  const act = ev.target.closest('[data-act]')?.dataset.act;
  const n = S.selected && S.nodes.get(S.selected);
  if (!act || !n) return;
  if (act === 'focus') {
    const level = S.level === 'methods' ? 'methods' : n.kind === 'Type' ? 'classes' : levelForFocus(n);
    navigate(level, n.id, n.id);
  }
  if (act === 'calls') navigate('methods', n.id, n.id);
  if (act === 'flow') navigate('messaging', n.id, n.id);
  if (act === 'classes') navigate('classes', n.parentId, n.id);
});

$('#warnings-btn').addEventListener('click', () => $('#warnings-dialog').showModal());

$('#export-btn').addEventListener('click', () => {
  if (!cy.elements().length) return;
  const blob = cy.png({ output: 'blob', full: true, scale: 2, bg: cssVar('--bg') });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `${S.graph?.name || 'codegraph'}-${S.level}.png`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
});

function applyTheme(theme) {
  if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
  $('#theme-btn').title = `Theme: ${theme} (click to change)`;
  cy.style(buildStyle());
}
$('#theme-btn').addEventListener('click', () => {
  const order = ['system', 'light', 'dark'];
  const next = order[(order.indexOf(storage.get('codegraph.theme', 'system')) + 1) % order.length];
  storage.set('codegraph.theme', next);
  applyTheme(next);
});
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => cy.style(buildStyle()));

document.addEventListener('keydown', ev => {
  const typing = ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName);
  if (ev.key === '/' && !typing) { searchInput.focus(); searchInput.select(); ev.preventDefault(); }
  else if (ev.key === 'Backspace' && !typing) { history.back(); ev.preventDefault(); }
  else if (ev.key === 'Escape' && !typing) { cy.$(':selected').unselect(); showDetails(null); }
});

window.addEventListener('hashchange', () => { if (S.graph) applyHash(); });

// ------------------------------------------------------------------ loading & analysis

function renderRecent() {
  $('#recent-paths').innerHTML = storage.get('codegraph.recent', []).map(p => `<option value="${esc(p)}">`).join('');
}
function addRecent(path) {
  storage.set('codegraph.recent', [path, ...storage.get('codegraph.recent', []).filter(p => p.toLowerCase() !== path.toLowerCase())].slice(0, 10));
  renderRecent();
}

function showOverlay(message) {
  $('#overlay').hidden = !message;
  if (message) $('#overlay-msg').textContent = message;
}

async function getStatus() {
  const res = await fetch('/api/status');
  return res.json();
}

async function pollWhileBusy(isDone = () => false) {
  for (;;) {
    const st = await getStatus().catch(() => ({ busy: false }));
    if (!st.busy || isDone()) return st;
    showOverlay(st.message || 'Analyzing…');
    await sleep(600);
  }
}

async function loadCurrentGraph() {
  const res = await fetch('/api/graph');
  if (res.status !== 200) return false;
  setGraph(await res.json());
  return true;
}

async function analyze(path, force) {
  let done = false;
  showOverlay(`Analyzing ${path}…`);
  const poll = pollWhileBusy(() => done);
  try {
    const res = await fetch('/api/analyze', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path, force }),
    });
    if (!res.ok) {
      const problem = await res.json().catch(() => null);
      throw new Error(problem?.detail || problem?.title || `${res.status} ${res.statusText}`);
    }
    const graph = await res.json();
    addRecent(graph.sourcePath);
    history.replaceState(null, '', '#/architecture');
    setGraph(graph);
  } catch (err) {
    setNote(`Analysis failed: ${err.message}`, { error: true, timeout: 15000 });
  } finally {
    done = true;
    await poll;
    showOverlay(null);
  }
}

$('#analyze-form').addEventListener('submit', ev => {
  ev.preventDefault();
  const path = $('#path-input').value.trim().replace(/^"|"$/g, '');
  if (path) analyze(path, $('#force-input').checked);
});

async function init() {
  const saved = storage.get('codegraph.opts', {});
  S.opts = { ...S.opts, ...saved };
  // Relationship kinds added after the options were saved start out enabled.
  const known = new Set(saved.knownKinds || ['Calls', 'Instantiates', 'UsesType', 'Inherits', 'Implements', 'Overrides', 'ProjectRef', 'PackageRef']);
  S.opts.kinds = [...new Set([...S.opts.kinds, ...EDGE_KINDS.map(k => k.kind).filter(k => !known.has(k))])];
  S.opts.knownKinds = EDGE_KINDS.map(k => k.kind);
  syncOptionInputs();
  renderEdgeFilters();
  renderLegend();
  renderRecent();
  applyTheme(storage.get('codegraph.theme', 'system'));
  updateTabs();
  $('#method-options').hidden = true;
  $('#messaging-options').hidden = true;

  try {
    const st = await getStatus();
    if (st.busy) {
      showOverlay(st.message || 'Analyzing…');
      const final = await pollWhileBusy();
      showOverlay(null);
      if (final.error) setNote(`Analysis failed: ${final.error}`, { error: true, timeout: 15000 });
    }
    if (!(await loadCurrentGraph())) {
      showEmpty('Enter the path to a .sln, .slnx or .csproj, or a folder (every solution in it is analyzed together), then press Analyze.\n\nTip: start the server with --path <solution or folder> to load it automatically.');
      $('#path-input').focus();
    }
  } catch (err) {
    showEmpty(`Could not reach the CodeGraph server: ${err.message}`);
  }
}

init();
