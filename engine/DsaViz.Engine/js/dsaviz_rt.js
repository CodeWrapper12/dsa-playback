// The runtime a JavaScript or TypeScript solution is built against. It counts every
// statement, charges library calls for the work they hide, tracks call depth, and — in a
// step-through run — records the variables and the objects they reach. It speaks the same
// protocol as the C++, Rust and Java runtimes: inputs in on stdin, one per line in
// LeetCode's format; one JSON object per step, per return and per design call; and a last
// one saying how the run ended and what it counted.
//
// The student's code runs on a worker thread with a deep stack, so recursion a thousand
// levels deep — a skewed tree — runs as it would on LeetCode, and runaway recursion is
// stopped by the depth limit rather than by the stack.
//
// Memory is estimated where it is created. Node can say how big its heap is, but not what
// one line allocated: its counter moves in steps, and reading it often enough to place
// allocations on lines leaves garbage of its own that moves it more. So every array,
// object, map, set and string the code creates — a literal, new, slice or map, a push or a
// set — is charged to the line creating it, at a fixed size per element.
"use strict";

const { isMainThread, Worker, parentPort, workerData } = require("worker_threads");

// ---- starting: the main thread reads the inputs and runs the program on a worker ---------

/** Called by the program's launcher: runs the solution file on a worker and passes its output on. */
function launch(solution) {
  const input = require("fs").readFileSync(0, "utf8");
  const worker = new Worker(solution, {
    workerData: { input, argv: process.argv.slice(2) },
    resourceLimits: { stackSizeMb: 256, maxYoungGenerationSizeMb: 256 },
    stdout: true,
    stderr: true
  });
  let ended = false;
  worker.on("message", text => {
    ended = true;
    process.stdout.write(text + "\n");
  });
  // Whatever the student printed is theirs, not the protocol's.
  worker.stdout.resume();
  worker.stderr.on("data", chunk => process.stderr.write(chunk));
  worker.on("error", error => {
    if (ended) return;
    ended = true;
    // Thrown while loading — a syntax error Node found, or the visualizer's own bug.
    const message = error && error.name === "SyntaxError"
      ? `Your code could not be read: ${error.message}`
      : `The visualizer could not run this code (${error && error.message}). This is a limitation of the visualizer, not a mistake in your code.`;
    process.stdout.write(JSON.stringify({ end: error && error.name === "SyntaxError" ? "RuntimeError" : "InputError", error: message,
      statement: -1, ops: 0, statements: 0, allocated: 0, peak: -1, maxDepth: 0, truncated: false,
      hits: [], bytes: [], largest: [], work: [], sites: [] }) + "\n");
  });
  // Nothing else keeps the process alive: it ends once the worker has, with its output written.
}

// ---- state ------------------------------------------------------------------------------------

const MAX_ITEMS = 1000, MAX_OBJECTS = 300, MAX_TEXT = 200, MAX_FIELDS = 30;

const st = {
  budget: 50_000_000, operations: 0, statementsRun: 0,
  depth: 0, maxDepth: 0, depthLimit: 20_000,
  hits: [], bytes: [], largest: [], work: [], siteCalls: [], siteWork: [], siteKinds: [], variables: [],
  stepping: false, maxSteps: 10_000, steps: 0, truncated: false,
  current: -1, active: false, stopped: null,
  out: [], callStarts: null,
  allocated: 0, since: 0
};

class Stop extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function setup(table) {
  st.hits = new Array(table.count).fill(0);
  st.bytes = new Array(table.count).fill(0);
  st.largest = new Array(table.count).fill(0);
  st.work = new Array(table.count).fill(0);
  st.siteKinds = table.sites;
  st.siteCalls = new Array(table.sites.length).fill(0);
  st.siteWork = new Array(table.sites.length).fill(0);
  st.variables = table.variables;
  const argv = (workerData && workerData.argv) || process.argv.slice(2);
  st.stepping = argv[0] === "step";
  if (argv[1]) st.budget = Number(argv[1]);
  if (argv[2]) st.depthLimit = Number(argv[2]);
  if (argv[3]) st.maxSteps = Number(argv[3]);
}

// ---- memory: estimated where it is created -------------------------------------------------

/** Bytes for a value just created: a header, and a fixed size per element. */
function created(v) {
  if (v === null || v === undefined) return 0;
  if (typeof v === "string") return 16 + 2 * v.length;
  if (typeof v === "function") return 32;
  if (typeof v !== "object") return 0;
  if (Array.isArray(v)) return 16 + 8 * v.length;
  if (ArrayBuffer.isView(v)) return 64 + v.byteLength;
  if (v instanceof Map) return 48 + 48 * v.size;
  if (v instanceof Set) return 40 + 16 * v.size;
  return 16 + 8 * Object.keys(v).length;
}

/** Bytes each element added to a collection takes. */
const perElement = o => Array.isArray(o) ? 8 : o instanceof Map ? 48 : o instanceof Set ? 16 : o instanceof Heap || o instanceof Line ? 8 : 0;

// A collection does not grow by one element at a time: when it is full it moves to new
// storage half as big again (an array) or twice as big (a map or set), and that move is the
// allocation. Modelled so, a list that keeps growing shows its largest allocation growing too —
// data that is kept — where one element at a time would look like short-lived garbage.
const capacity = new WeakMap();
function grown(o, now) {
  const unit = perElement(o);
  if (unit === 0) return 0;
  let room = capacity.has(o) ? capacity.get(o) : now === 0 ? 0 : now - 1;
  if (now <= room) return 0;
  const map = o instanceof Map || o instanceof Set;
  while (room < now) room = map ? Math.max(4, room * 2) : room + (room >> 1) + 16;
  capacity.set(o, room);
  return 16 + room * unit;
}

function allocate(bytes) {
  if (bytes <= 0 || st.current < 0) return;
  st.bytes[st.current] += bytes;
  st.allocated += bytes;
  st.since += bytes;
  if (st.since > st.largest[st.current]) st.largest[st.current] = st.since;
}

/** A value the code just created — a literal, new, slice — charged to the line creating it. */
function a(v) {
  if (st.active) {
    allocate(created(v));
    if (v && typeof v === "object" && perElement(v) > 0) capacity.set(v, sizeOf(v));
  }
  return v;
}

// A collection about to grow — push, set, add — and what it held before; d() charges what was added.
const growing = [];
function b(o) {
  if (st.active) growing.push(o, sizeOf(o) + (o instanceof Heap || o instanceof Line ? o.size() : 0));
  return o;
}
function d(result) {
  if (st.active && growing.length > 0) {
    const before = growing.pop(), o = growing.pop();
    const now = sizeOf(o) + (o instanceof Heap || o instanceof Line ? o.size() : 0);
    if (now > before) allocate(grown(o, now));
  }
  return result;
}

// ---- counting -----------------------------------------------------------------------------------

/** A statement about to run, in a measured run. */
function h(id) {
  if (!st.active) return;
  if (st.stopped) throw st.stopped;
  st.since = 0;
  st.current = id;
  st.hits[id]++;
  st.statementsRun++;
  if (++st.operations > st.budget) stop("BudgetExceeded", `Execution budget of ${st.budget.toLocaleString("en-US")} operations was exceeded.`);
}

/** A statement about to run, in a step-through run: true when its variables are to be recorded. */
function s(id) {
  if (!st.active) return false;
  if (st.stopped) throw st.stopped;
  st.since = 0;
  st.current = id;
  st.hits[id]++;
  st.statementsRun++;
  if (++st.operations > st.budget) stop("BudgetExceeded", `Execution budget of ${st.budget.toLocaleString("en-US")} operations was exceeded.`);
  if (st.steps >= st.maxSteps) { st.truncated = true; return false; }
  return true;
}

function stop(status, message) {
  // Kept, so a student's catch cannot swallow it: the next statement throws it again.
  st.stopped = new Stop(status, message);
  throw st.stopped;
}

/** A call begins. */
function e() {
  if (!st.active) return;
  if (++st.depth > st.maxDepth) st.maxDepth = st.depth;
  if (st.depth > st.depthLimit)
    stop("RecursionLimitExceeded", `Recursion went deeper than ${st.depthLimit.toLocaleString("en-US")} calls. That usually means a missing or unreachable base case.`);
}

/** A call ends. */
function x() { if (st.active) st.depth--; }

function charge(site, amount) {
  if (!st.active) return;
  st.siteCalls[site]++;
  if (amount <= 0) return;
  st.siteWork[site] += amount;
  if (st.current >= 0) st.work[st.current] += amount;
  st.operations += amount;
  if (st.operations > st.budget) stop("BudgetExceeded", `Execution budget of ${st.budget.toLocaleString("en-US")} operations was exceeded.`);
}

const sizeOf = o => o == null ? 0 : typeof o === "string" || Array.isArray(o) || ArrayBuffer.isView(o) ? o.length
  : o instanceof Map || o instanceof Set ? o.size : 0;
const sequence = o => typeof o === "string" || Array.isArray(o) || ArrayBuffer.isView(o);
const receivers = [];

/** The object a charged library call is made on, charged for what that call does to it. */
function k(site, o) {
  if (!st.active) return o;
  const n = sizeOf(o);
  switch (st.siteKinds[site]) {
    case "scan": case "front": case "copy": charge(site, sequence(o) ? n : 0); break;
    case "sort": charge(site, Array.isArray(o) && n > 1 ? Math.round(n * Math.log2(n)) : 0); break;
    case "spread": charge(site, n); break;
    case "splice": receivers[site] = o; break;        // charged by where it splices, in i()
    case "result": break;
  }
  return o;
}

/** splice(start, …): what is after start moves. */
function i(site, start) {
  const o = receivers[site];
  receivers[site] = undefined;
  if (st.active && Array.isArray(o)) {
    const n = o.length;
    const at = start < 0 ? Math.max(0, n + start) : Math.min(start, n);
    charge(site, n - at);
  }
  return start;
}

/** a += b: a new string as long as both, when it is a string. */
function p(site, left, right) {
  const result = left + right;
  if (st.active && typeof result === "string") { charge(site, result.length); allocate(created(result)); }
  return result;
}

// ---- recording a step ---------------------------------------------------------------------------

let nextId = 1;
const ids = new WeakMap();
const idOf = o => { let id = ids.get(o); if (!id) { id = nextId++; ids.set(o, id); } return id; };

const clip = t => t.length > MAX_TEXT ? t.slice(0, MAX_TEXT) + "…" : t;

function decimal(d) {
  if (Number.isNaN(d)) return "NaN";
  if (!Number.isFinite(d)) return d > 0 ? "Infinity" : "-Infinity";
  let t = d.toFixed(5);
  while (t.endsWith("0")) t = t.slice(0, -1);
  if (t.endsWith(".")) t += "0";
  return t;
}

const scalar = text => ({ x: text });
const number = n => ({ x: String(n), num: n });

const isTree = o => o && typeof o === "object" && "val" in o && "left" in o && "right" in o;
const isList = o => o && typeof o === "object" && "val" in o && "next" in o && !("left" in o);
const typeName = o => (o.constructor && o.constructor.name) || "Object";

function text(o) {
  if (o === null) return "null";
  if (o === undefined) return "undefined";
  if (typeof o === "string") return `"${clip(o)}"`;
  if (typeof o === "number") return Number.isInteger(o) ? String(o) : decimal(o);
  if (typeof o !== "object") return String(o);
  if (isTree(o) || isList(o)) return `${typeName(o)}(${text(o.val)})`;
  if (o instanceof Heap || o instanceof Line) return `(${o.size()})`;
  return `(${sizeOf(o)})`;
}

/**
 * An object's own data fields. A getter is left out: reading it would run the student's
 * code from inside the recording.
 */
function dataKeys(o) {
  const keys = [];
  for (const key of Object.keys(o)) {
    const descriptor = Object.getOwnPropertyDescriptor(o, key);
    if (descriptor && "value" in descriptor && typeof descriptor.value !== "function") keys.push(key);
    if (keys.length >= MAX_FIELDS) break;
  }
  return keys;
}

/** Every object reachable from one step's variables, written once each. */
class Shot {
  constructor() { this.objects = []; this.written = new Set(); }
  begin(id) { if (this.written.size >= MAX_OBJECTS || this.written.has(id)) return false; this.written.add(id); return true; }
}

function value(o, role, shot) {
  if (o === null) return scalar("null");
  if (o === undefined) return scalar("undefined");
  switch (typeof o) {
    case "number": return Number.isSafeInteger(o) ? number(o) : scalar(Number.isInteger(o) ? String(o) : decimal(o));
    case "bigint": return scalar(String(o));
    case "boolean": return scalar(String(o));
    case "string": return scalar(`"${clip(o)}"`);
    case "function": return scalar("(function)");
    case "symbol": return scalar(String(o));
  }
  const id = idOf(o);
  const reference = { r: id, x: text(o) };
  if (!shot.begin(id)) return reference;

  const w = { id };
  if (isTree(o)) {
    Object.assign(w, { k: "tree", t: typeName(o), b: 32, f: [["val", value(o.val, 0, shot)], ["left", value(o.left, 0, shot)], ["right", value(o.right, 0, shot)]] });
  } else if (isList(o)) {
    Object.assign(w, { k: "listnode", t: typeName(o), b: 24, f: [["val", value(o.val, 0, shot)], ["next", value(o.next, 0, shot)]] });
  } else if (Array.isArray(o) || ArrayBuffer.isView(o)) {
    const n = o.length;
    const kind = role === 1 ? "stack" : role === 2 ? "queue" : "array";
    const order = [];
    if (kind === "stack") for (let i = n - 1; i >= 0 && order.length < MAX_ITEMS; i--) order.push(o[i]);
    else for (let i = 0; i < n && i < MAX_ITEMS; i++) order.push(o[i]);
    Object.assign(w, { k: kind, t: typeName(o), c: n, b: 16 + n * 8, i: order.map(v => value(v, 0, shot)) });
  } else if (o instanceof Map) {
    const entries = [];
    for (const [key, v] of o) { if (entries.length >= MAX_ITEMS) break; entries.push([value(key, 0, shot), value(v, 0, shot)]); }
    Object.assign(w, { k: "map", t: "Map", c: o.size, b: 48 + o.size * 48, e: entries });
  } else if (o instanceof Set) {
    const items = [];
    for (const v of o) { if (items.length >= MAX_ITEMS) break; items.push(value(v, 0, shot)); }
    Object.assign(w, { k: "set", t: "Set", c: o.size, b: 40 + o.size * 16, i: items });
  } else if (o instanceof Heap) {
    const items = o._items.slice(0, MAX_ITEMS).map(v => value(o._shown(v), 0, shot));
    Object.assign(w, { k: "heap", ar: 2, t: typeName(o), c: o.size(), b: 40 + o.size() * 16, i: items });
  } else if (o instanceof Line) {
    const items = o.toArray().slice(0, MAX_ITEMS).map(v => value(v, 0, shot));
    Object.assign(w, { k: "queue", t: typeName(o), c: o.size(), b: 40 + o.size() * 16, i: items });
  } else {
    // The student's own object: its fields — a design problem's state, most often.
    const fields = dataKeys(o);
    Object.assign(w, { k: "object", t: typeName(o), b: 16 + fields.length * 8, f: fields.map(key => [key, value(o[key], 0, shot)]) });
  }
  shot.objects.push(w);
  return reference;
}

/** The variables of the statement just counted, read through a function so none is read before it exists. */
function r(read) {
  // Nothing the recording touches is the student's work, and none of it may record again.
  st.active = false;
  try {
    const [names, roles] = st.variables[st.current] || [[], []];
    let values;
    try { values = read(); } catch { values = null; }
    const shot = new Shot();
    const vars = [];
    names.forEach((name, index) => {
      if (values === null) { vars.push({ n: name, t: "", v: scalar("(not set yet)") }); return; }
      const v = values[index];
      if (name === "this") {
        // A design problem's object: its fields, each a variable of its own.
        if (v && typeof v === "object")
          for (const key of dataKeys(v)) vars.push({ n: `this.${key}`, t: "", v: value(v[key], 0, shot) });
        return;
      }
      vars.push({ n: name, t: "", v: value(v, roles[index], shot) });
    });
    st.steps++;
    st.out.push(JSON.stringify({ s: st.current, d: st.depth, a: st.allocated, v: vars, o: shot.objects }));
  } finally {
    st.active = true;
  }
  return false;
}

/** A value being returned, noted for the recursion tree; the value itself is passed on. */
function ret(v) {
  if (st.active && st.stepping && st.steps < st.maxSteps) {
    st.active = false;
    try { st.out.push(JSON.stringify({ ret: st.depth, after: st.steps, v: value(v, 0, new Shot()) })); }
    finally { st.active = true; }
  }
  return v;
}

// ---- LeetCode's built-in classes ------------------------------------------------------------

class TreeNode {
  constructor(val, left, right) {
    this.val = val === undefined ? 0 : val;
    this.left = left === undefined ? null : left;
    this.right = right === undefined ? null : right;
  }
}

class ListNode {
  constructor(val, next) {
    this.val = val === undefined ? 0 : val;
    this.next = next === undefined ? null : next;
  }
}

/**
 * A binary heap, as LeetCode's @datastructures-js/priority-queue offers it: enqueue,
 * dequeue, front, back, size, isEmpty, toArray, clear. Built with a compare function,
 * a value function, or the older { priority } and { compare } options; an element enqueued
 * with a priority is handed back as { element, priority }, as the older version did.
 */
/** Work done inside one of LeetCode's classes — a heap's sift — charged to the line that called it. */
function work(amount) {
  if (!st.active || amount <= 0) return;
  if (st.current >= 0) st.work[st.current] += amount;
  st.operations += amount;
  if (st.operations > st.budget) stop("BudgetExceeded", `Execution budget of ${st.budget.toLocaleString("en-US")} operations was exceeded.`);
}

class Heap {
  constructor(compare) { this._items = []; this._compare = compare; this._pairs = false; }
  _shown(v) { return this._pairs ? v.element : v; }
  size() { return this._items.length; }
  isEmpty() { return this._items.length === 0; }
  front() { const top = this._items[0]; return top === undefined ? null : top; }
  back() { let last = null; for (const v of this._items) if (last === null || this._compare(v, last) > 0) last = v; return last; }
  toArray() { return this._items.slice().sort(this._compare); }
  clear() { this._items = []; }
  enqueue(element, priority) {
    if (priority !== undefined && !this._pairs) {
      this._pairs = true;
      const order = this._compare;
      this._compare = (a, b) => order(a.priority, b.priority);
    }
    const items = this._items;
    items.push(this._pairs ? { element, priority } : element);
    work(Math.ceil(Math.log2(items.length + 1)));
    for (let at = items.length - 1; at > 0;) {
      const parent = (at - 1) >> 1;
      if (this._compare(items[at], items[parent]) >= 0) break;
      [items[at], items[parent]] = [items[parent], items[at]];
      at = parent;
    }
    return this;
  }
  push(element, priority) { return this.enqueue(element, priority); }
  dequeue() {
    const items = this._items;
    if (items.length === 0) return null;
    const top = items[0];
    work(Math.ceil(Math.log2(items.length + 1)));
    const last = items.pop();
    if (items.length > 0) {
      items[0] = last;
      for (let at = 0; ;) {
        const left = 2 * at + 1, right = left + 1;
        let least = at;
        if (left < items.length && this._compare(items[left], items[least]) < 0) least = left;
        if (right < items.length && this._compare(items[right], items[least]) < 0) least = right;
        if (least === at) break;
        [items[at], items[least]] = [items[least], items[at]];
        at = least;
      }
    }
    return top;
  }
  pop() { return this.dequeue(); }
}

const byValue = (get, sign) => (a, b) => { const x = get(a), y = get(b); return x < y ? -sign : x > y ? sign : 0; };
const valueOf = option => typeof option === "function" ? option : option && typeof option.priority === "function" ? option.priority : v => v;

class MinPriorityQueue extends Heap {
  constructor(option) { super(byValue(valueOf(option), 1)); }
}
class MaxPriorityQueue extends Heap {
  constructor(option) { super(byValue(valueOf(option), -1)); }
}
class PriorityQueue extends Heap {
  constructor(option) { super(typeof option === "function" ? option : option && option.compare ? option.compare : (a, b) => a < b ? -1 : a > b ? 1 : 0); }
}

/** LeetCode's @datastructures-js/queue and deque: elements in order, front first. */
class Line {
  constructor(items) { this._items = Array.isArray(items) ? items.slice() : []; this._head = 0; }
  size() { return this._items.length - this._head; }
  isEmpty() { return this.size() === 0; }
  toArray() { return this._items.slice(this._head); }
  clear() { this._items = []; this._head = 0; }
  front() { return this.isEmpty() ? null : this._items[this._head]; }
  back() { return this.isEmpty() ? null : this._items[this._items.length - 1]; }
}
class Queue extends Line {
  enqueue(v) { this._items.push(v); return this; }
  push(v) { return this.enqueue(v); }
  dequeue() {
    if (this.isEmpty()) return null;
    const v = this._items[this._head++];
    if (this._head * 2 > this._items.length) { this._items = this._items.slice(this._head); this._head = 0; }
    return v;
  }
  pop() { return this.dequeue(); }
}
class Deque extends Line {
  pushFront(v) { this._items.splice(this._head, 0, v); return this; }
  pushBack(v) { this._items.push(v); return this; }
  popFront() { return this.isEmpty() ? null : this._items[this._head++]; }
  popBack() { return this.isEmpty() ? null : this._items.pop(); }
}

// ---- inputs and results, in LeetCode's format --------------------------------------------------

function build(textValue, kind) {
  const v = JSON.parse(textValue);
  if (kind === "tree") return tree(v);
  if (kind === "list") return list(v);
  return v;
}

function tree(values) {
  if (!Array.isArray(values) || values.length === 0 || values[0] === null) return null;
  const root = new TreeNode(values[0]);
  const waiting = [root];
  let at = 1;
  for (let head = 0; head < waiting.length && at < values.length; head++) {
    const node = waiting[head];
    if (at < values.length) { const v = values[at++]; if (v !== null) { node.left = new TreeNode(v); waiting.push(node.left); } }
    if (at < values.length) { const v = values[at++]; if (v !== null) { node.right = new TreeNode(v); waiting.push(node.right); } }
  }
  return root;
}

function list(values) {
  let head = null;
  for (let i = (values || []).length - 1; i >= 0; i--) head = new ListNode(values[i], head);
  return head;
}

function format(o) {
  if (o === null || o === undefined) return "null";
  if (typeof o === "string") return JSON.stringify(o);
  if (typeof o === "number") return Number.isInteger(o) ? String(o) : decimal(o);
  if (typeof o === "boolean" || typeof o === "bigint") return String(o);
  if (Array.isArray(o) || ArrayBuffer.isView(o)) return "[" + Array.from(o, format).join(",") + "]";
  if (o instanceof Set) return "[" + [...o].map(format).join(",") + "]";
  if (o instanceof Heap || o instanceof Line) return "[" + o.toArray().map(format).join(",") + "]";
  if (isList(o)) {
    const out = [];
    for (let n = o; n && out.length < 100_000; n = n.next) out.push(format(n.val));
    return "[" + out.join(",") + "]";
  }
  if (isTree(o)) {
    const order = [];
    let level = [o];
    while (level.length > 0 && order.length < 100_000) {
      const next = [];
      for (const n of level) { order.push(n); if (n) next.push(n.left, n.right); }
      level = next.some(n => n) ? next : [];
    }
    while (order.length && order[order.length - 1] === null) order.pop();
    return "[" + order.map(n => n ? format(n.val) : "null").join(",") + "]";
  }
  return JSON.stringify(String(o));
}

// ---- the run ----------------------------------------------------------------------------------------

function inputs() {
  const text = (workerData && workerData.input) || "";
  return text.split("\n").map(l => l.replace(/\r$/, "")).filter(l => l.length > 0);
}

function begin() {
  for (const counts of [st.hits, st.bytes, st.largest, st.work, st.siteCalls, st.siteWork]) counts.fill(0);
  st.steps = 0;
  st.truncated = false;
  st.stopped = null;
  st.out = [];
  st.operations = st.statementsRun = 0;
  st.depth = st.maxDepth = 0;
  st.current = -1;
  st.allocated = 0;
  st.since = 0;
  growing.length = 0;
  st.active = true;
}

function end(record) {
  st.active = false;
  if (st.current >= 0 && st.since > st.largest[st.current]) st.largest[st.current] = st.since;
  Object.assign(record, {
    statement: record.statement === undefined ? st.current : record.statement,
    ops: st.operations, statements: st.statementsRun,
    // The heap counter cannot tell held memory from garbage not yet collected, so the peak is
    // left to be inferred, as it is for C# and Java.
    allocated: st.allocated, peak: -1, maxDepth: st.maxDepth, truncated: st.truncated,
    hits: st.hits, bytes: st.bytes.map(Math.round), largest: st.largest.map(Math.round), work: st.work,
    sites: st.siteCalls.map((calls, index) => [calls, st.siteWork[index]])
  });
  if (!st.stepping && st.callStarts && st.callStarts.length > 0) {
    record.callCosts = st.callStarts.map((start, index) =>
      (index + 1 < st.callStarts.length ? st.callStarts[index + 1] : st.operations) - start);
  }
  st.out.push(JSON.stringify(record));
  // The worker then runs off the end of the program and stops; the message goes with it.
  parentPort.postMessage(st.out.join("\n"));
}

function finish(status, error) { end({ end: status, error }); }

class DesignInputError extends Error {}

/** The student's own error, said the way they would want to hear it. */
function failed(error) {
  if (error instanceof Stop) return finish(error.status, error.message);
  if (error instanceof DesignInputError) return end({ end: "InvalidInput", error: error.message, statement: -1 });
  if (error instanceof RangeError && /call stack/i.test(error.message))
    return finish("RecursionLimitExceeded", "Recursion went too deep for the stack. That usually means a missing or unreachable base case.");
  const name = error && error.constructor && error.constructor.name ? error.constructor.name : "Error";
  const message = error && error.message !== undefined ? error.message : String(error);
  return finish("RuntimeError", `Your code threw ${name}: ${message}`);
}

/** Runs one function on the inputs, built as `kinds` says: a tree, a linked list, or a plain value. */
function run(kinds, call, isVoid) {
  let args;
  try {
    const lines = inputs();
    args = kinds.map((kind, index) => build(lines[index] !== undefined ? lines[index] : "null", kind));
  } catch (error) {
    return end({ end: "InputError", error: String(error && error.message || error), statement: -1 });
  }
  begin();
  let result;
  try {
    result = call(args);
  } catch (error) {
    return failed(error);
  }
  st.active = false;
  end({ end: "Completed", result: isVoid ? null : format(result) });
}

function invalid(message) { throw new DesignInputError(message); }

/**
 * LeetCode's design problems: the first operation creates the object, every later one calls
 * a method on it, and each result is collected, null for the constructor and for nothing
 * returned. Every call is checked against the class before the first is made.
 */
function design(type, name, constructorArity, methods) {
  let operations, args;
  try {
    const lines = inputs();
    operations = JSON.parse(lines[0] || "[]");
    args = JSON.parse(lines[1] || "[]");
  } catch (error) {
    return end({ end: "InputError", error: String(error && error.message || error), statement: -1 });
  }
  try {
    if (!Array.isArray(operations) || operations.length === 0)
      invalid(`The list of operations is empty. The first one creates the object, like "${name}".`);
    if (!Array.isArray(args) || args.length !== operations.length)
      invalid(`There are ${operations.length} operations but ${Array.isArray(args) ? args.length : 0} argument lists. Each operation needs its own list, [] when it takes none.`);
    if (operations[0] !== name)
      invalid(`The first operation creates the object, so it must be "${name}", not "${operations[0]}".`);
    if ((args[0] || []).length !== constructorArity)
      invalid(`${name} takes ${constructorArity} argument(s) to be created, but the first call gives ${(args[0] || []).length}.`);
    const arity = new Map(methods);
    for (let i = 1; i < operations.length; i++) {
      if (!arity.has(operations[i])) invalid(`Operation ${i + 1} is "${operations[i]}", but ${name} has no method by that name.`);
      if (!Array.isArray(args[i]) || args[i].length !== arity.get(operations[i]))
        invalid(`Operation ${i + 1} calls ${operations[i]} with ${(args[i] || []).length} argument(s), but it takes ${arity.get(operations[i])}.`);
    }
  } catch (error) {
    return failed(error);
  }

  const results = [];
  st.callStarts = st.stepping ? null : [];
  begin();
  try {
    let target = null;
    for (let i = 0; i < operations.length; i++) {
      if (st.stepping) {
        if (st.steps < st.maxSteps) st.out.push(JSON.stringify({ call: i, step: st.steps }));
      } else st.callStarts.push(st.operations);
      if (i === 0) { target = new type(...args[0]); results.push(null); continue; }
      const value = target[operations[i]](...args[i]);
      results.push(value === undefined ? null : value);
    }
  } catch (error) {
    return failed(error);
  }
  st.active = false;
  end({ end: "Completed", result: "[" + results.map(format).join(",") + "]" });
}

module.exports = {
  launch, setup, h, s, r, e, x, k, i, p, a, b, d, ret, run, design,
  TreeNode, ListNode, MinPriorityQueue, MaxPriorityQueue, PriorityQueue, Queue, Deque,
  get inWorker() { return !isMainThread; }
};
