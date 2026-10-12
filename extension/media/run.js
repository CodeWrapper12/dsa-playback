// The animated run view: draws one recorded step, and animates from the step before it.
//
// Animation is worked out by comparing the two steps rather than by keeping elements
// alive between them: each step is drawn fresh, and whatever moved is drawn starting
// where it was. A pointer that moved from cell 2 to cell 5 slides from 2; a value that
// changed flashes; a node whose link was rewired has its arrow bend to the new target.
// So stepping backwards, jumping, and replaying all draw correctly, and only adjacent
// steps animate.

(function () {
  "use strict";

  // ---- constants --------------------------------------------------------------------

  /** Objects are rebuilt from the recorded changes; a full copy is kept this often. */
  const CHECKPOINT_EVERY = 64;

  /** Cells drawn for one array; longer arrays are windowed around their pointers. */
  const MAX_CELLS = 64;

  /** Width of one cell plus the gap after it, in pixels — what a pointer slides by. */
  const CELL_PITCH = 40;

  /** Nodes drawn in one tree, list, graph or recursion tree. */
  const MAX_NODES = 255;

  const TREE_X = 40, TREE_Y = 56, NODE_R = 15;

  // Integer variables drawn as pointers into arrays and strings. A fixed list of the names
  // people give indices, rather than "any int that happens to be in range": a `target` of
  // 3 is not an index, and drawing it under cell 3 would mislead.
  const INDEX_NAMES = new Set(["i", "j", "k", "l", "r", "left", "right", "lo", "hi", "low", "high", "mid",
    "start", "end", "slow", "fast", "p", "q", "idx", "index", "pos", "ptr", "cur", "write", "read", "w"]);
  const ROW_NAMES = new Set(["r", "row", "i", "y"]);
  const COL_NAMES = new Set(["c", "col", "j", "x"]);
  const VISITED_NAMES = /^(visited|seen|vis|marked|used|explored|done)$/i;
  const CURRENT_NODE_NAMES = new Set(["node", "u", "v", "cur", "curr", "current", "vertex", "at", "from", "next", "neighbor", "neighbour", "nei"]);

  const isIndexName = name => INDEX_NAMES.has(name) || /(Index|Idx|Ptr|Pointer)$/.test(name);

  const escapeHtml = text => String(text).replace(/[&<>"]/g,
    ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));

  function bytes(value) {
    if (!value) return "0 B";
    if (value < 1024) return value + " B";
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";
    return (value / (1024 * 1024)).toFixed(1) + " MB";
  }

  // ---- rebuilding the objects at a step ------------------------------------------------

  function apply(heap, changes) {
    for (const change of changes || []) {
      if (change.patch) {
        const previous = heap.get(change.id);
        const items = previous && previous.items ? previous.items.slice() : [];
        for (const p of change.patch) items[p.index] = p.value;
        heap.set(change.id, { ...change, items, patch: undefined });
      } else {
        heap.set(change.id, change);
      }
    }
  }

  /**
   * Everything that can be worked out once per run: checkpoints for rebuilding objects,
   * the calls and returns as a tree, when each node was first reached, and a stable left
   * to right order for linked-list nodes.
   */
  function prepare(trace) {
    const checkpoints = [];
    const heap = new Map();
    const firstReached = new Map();
    const listSlots = new Map();
    const methods = new Map((trace.methods || []).map(m => [m.name, m.parameters]));

    trace.steps.forEach((step, index) => {
      apply(heap, step.heap);
      if (index % CHECKPOINT_EVERY === 0) checkpoints.push(new Map(heap));

      for (const variable of step.variables) {
        if (variable.ref == null) continue;
        if (!firstReached.has(variable.ref)) firstReached.set(variable.ref, index);
        if (variable.kind === "listnode") {
          // Follow the chain from every node a variable reaches, giving each new node the
          // next slot: the order a list is first seen in is the order it is drawn in, and
          // it stays put while its links are rewired.
          let id = variable.ref, guard = 0;
          while (id != null && guard++ < MAX_NODES) {
            if (!listSlots.has(id)) listSlots.set(id, listSlots.size);
            const node = heap.get(id);
            id = node ? refOf(field(node, "next")) : null;
          }
        }
      }
    });

    return { trace, checkpoints, firstReached, listSlots, methods, calls: buildCalls(trace, methods) };
  }

  function heapAt(model, index) {
    // Stepping moves one step at a time, so the step next to the last one built is made
    // from it with one step's changes, rather than replayed from a checkpoint.
    const recent = model.recent || (model.recent = new Map());
    if (recent.has(index)) return recent.get(index);

    let heap;
    if (recent.has(index - 1)) {
      heap = new Map(recent.get(index - 1));
      apply(heap, model.trace.steps[index].heap);
    } else {
      const base = Math.floor(index / CHECKPOINT_EVERY);
      heap = new Map(model.checkpoints[base]);
      for (let i = base * CHECKPOINT_EVERY + 1; i <= index; i++) apply(heap, model.trace.steps[i].heap);
    }

    recent.set(index, heap);
    if (recent.size > 4) recent.delete(recent.keys().next().value);
    return heap;
  }

  /** A node's field value: its `val`, or the reference in its `next`, `left` or `right`. */
  const field = (node, name) => {
    const found = (node.fields || []).find(f => f.name === name);
    return found ? found.value : null;
  };
  const refOf = value => (value && value.ref != null ? value.ref : null);

  // ---- calls, returns, and the call stack ------------------------------------------------

  /**
   * The run as a tree of calls. A step one level deeper than the last starts a call; a
   * recorded return, or a step back at a shallower depth, ends it. Lambdas run at their
   * caller's depth and are part of its frame.
   */
  function buildCalls(trace, methods) {
    const calls = [];
    const stack = [];
    const callAt = new Array(trace.steps.length);
    const returns = (trace.returns || []).slice();
    let r = 0;

    const end = (call, at) => { if (call.end == null) call.end = at; };

    trace.steps.forEach((step, index) => {
      // Returns recorded before this step close their calls, innermost first.
      while (r < returns.length && returns[r].afterStep <= index) {
        const ret = returns[r++];
        while (stack.length > ret.depth) end(stack.pop(), index);
        if (stack.length === ret.depth) {
          const call = stack.pop();
          call.returned = ret.value;
          end(call, index);
        }
      }
      while (stack.length > step.depth) end(stack.pop(), index);

      while (stack.length < step.depth) {
        const parent = stack[stack.length - 1] || null;
        const parameters = methods.get(step.method) || [];
        const args = parameters.map(name => step.variables.find(v => v.name === name)).filter(Boolean);
        const call = { id: calls.length, method: step.method, depth: stack.length + 1, start: index, end: null,
          args, parent: parent ? parent.id : null, children: [], returned: null, steps: [] };
        if (parent) parent.children.push(call.id);
        calls.push(call);
        stack.push(call);
      }
      const owner = stack[stack.length - 1];
      callAt[index] = owner.id;
      owner.steps.push(index);
    });

    // Returns after the last recorded step close what is left.
    while (r < returns.length) {
      const ret = returns[r++];
      const call = stack[ret.depth - 1];
      if (call && call.returned == null) call.returned = ret.value;
    }

    const recursive = calls.some(call => {
      for (let p = call.parent; p != null; p = calls[p].parent) if (calls[p].method === call.method) return true;
      return false;
    });

    return { list: calls, at: callAt, recursive };
  }

  /** The frames on the stack at a step, outermost first, each with its variables at that moment. */
  function framesAt(model, index) {
    const { list, at } = model.calls;
    const chain = [];
    for (let id = at[index]; id != null; id = list[id].parent) chain.unshift(list[id]);

    return chain.map((call, i) => {
      // A caller's variables are as they were at its own last statement before the call
      // — not simply the step before it, which may belong to a sibling call that just
      // returned, as with the left and right halves of a tree walk.
      const stepIndex = i === chain.length - 1 ? index : lastBefore(call.steps, chain[i + 1].start);
      const step = model.trace.steps[stepIndex];
      return { call, variables: step ? step.variables : [] };
    });
  }

  /** The largest of the ascending `indices` below `limit`. */
  function lastBefore(indices, limit) {
    let low = 0, high = indices.length - 1, found = indices[0];
    while (low <= high) {
      const mid = (low + high) >> 1;
      if (indices[mid] < limit) { found = indices[mid]; low = mid + 1; } else high = mid - 1;
    }
    return found;
  }

  // ---- the step area ----------------------------------------------------------------------

  /**
   * Draws step `index`. When `previous` is the step just before or after it, whatever
   * changed between the two is animated; otherwise the step is drawn still.
   */
  function render(model, index, previous, options) {
    const trace = model.trace;
    const step = trace.steps[index];
    const heap = heapAt(model, index);
    const adjacent = previous != null && Math.abs(previous - index) === 1;
    const before = adjacent ? { step: trace.steps[previous], heap: heapAt(model, previous) } : null;
    const statement = trace.statements.find(s => s.id === step.statementId) || { snippet: "" };
    const ms = (options && options.animationMs) || 300;

    const frames = framesAt(model, index);
    const python = !!(options && options.python);
    const context = { model, index, step, heap, before, ms, frames, python, drawn: new Set(), sizes: [] };

    const parts = [
      `<div class="run" style="--anim:${ms}ms">`,
      `<div class="current-step">
        <div class="label">About to run · line ${step.line}</div>
        <code class="statement">${escapeHtml(statement.snippet)}</code>
      </div>`,
      returnedLine(context),
      callStack(frames, context),
      graphPanel(context),
      structures(context),
      recursionTree(context),
      memoryPanel(context),
      "</div>"
    ];
    return parts.join("");
  }

  /** What the method returned, on the run's last step. */
  function returnedLine(context) {
    const trace = context.model.trace;
    if (context.index !== trace.steps.length - 1 || trace.returnValue == null || trace.truncated) return "";
    return `<div class="returned-value">Returned <code>${escapeHtml(trace.returnValue)}</code></div>`;
  }

  // ---- variables and structures ------------------------------------------------------------

  function structures(context) {
    const { step, before } = context;
    const scalars = step.variables.filter(v => v.kind === "scalar" && v.number != null);
    const previousByName = before ? new Map(before.step.variables.map(v => [v.name, v])) : new Map();

    // One drawing per object, labelled with every variable that holds it.
    const byRef = new Map();
    for (const v of step.variables) {
      if (v.ref == null) continue;
      if (!byRef.has(v.ref)) byRef.set(v.ref, []);
      byRef.get(v.ref).push(v.name);
    }

    const rows = [];
    const done = new Set();
    for (const v of step.variables) {
      if (v.ref != null) {
        if (done.has(v.ref)) continue;
        done.add(v.ref);
        const names = byRef.get(v.ref);
        const drawnBefore = new Set(context.drawn);
        const drawing = drawObject(v.ref, names, scalars, context);
        if (drawing === null) continue;   // drawn as part of a linked list or tree already

        // A list or tree drawing shows every variable that points into it, so the row is
        // named after all of them, not only the one that happened to come first.
        const into = step.variables.filter(o => o.ref != null && context.drawn.has(o.ref) && !drawnBefore.has(o.ref))
          .map(o => o.name);
        const label = into.length > 0 ? into.join(", ") : names.join(" = ");
        rows.push(row(label, drawing, false, names.length > 1 ? "shared" : ""));
        continue;
      }
      const old = previousByName.get(v.name);
      const changed = before && old && old.display !== v.display;
      const appeared = before && !old;
      rows.push(row(v.name, drawPlain(v, scalars, context, old), changed, appeared ? "appeared" : ""));
    }

    // Deep in a recursive tree walk the current call often holds null — the call on a
    // missing child — and has nothing of its own to draw. The tree the walk is over is
    // still the thing to look at, so it is drawn from the outermost call that holds it.
    const holdsTree = step.variables.some(v => v.kind === "tree" && v.ref != null);
    if (!holdsTree) {
      const outer = context.frames
        .flatMap(f => f.variables).find(v => v.kind === "tree" && v.ref != null && context.heap.has(v.ref));
      if (outer) rows.unshift(row("tree being walked", treeDiagram(outer.ref, context), false, ""));
    }

    if (rows.length === 0) return '<p class="placeholder">No variables are in scope here yet.</p>';
    return `<div class="variables">${rows.join("")}</div>`;
  }

  function row(name, content, changed, extra) {
    return `<div class="variable ${changed ? "changed" : ""} ${extra || ""}">
      <div class="var-name">${escapeHtml(name)}</div>
      <div class="var-value">${content}</div>
    </div>`;
  }

  /** A value with no object behind it: a number, a character, text drawn as cells. */
  function drawPlain(variable, scalars, context, old) {
    if (variable.kind === "string" && variable.items && variable.items.length > 0) {
      const oldItems = old && old.kind === "string" ? old.items : null;
      return cells(variable.items.map(text => ({ text })), scalars, context,
        oldItems ? oldItems.map(text => ({ text })) : null, previousScalars(context));
    }
    return `<code>${escapeHtml(variable.display)}</code>`;
  }

  function previousScalars(context) {
    return context.before ? context.before.step.variables.filter(v => v.kind === "scalar" && v.number != null) : [];
  }

  function drawObject(id, names, scalars, context) {
    const object = context.heap.get(id);
    if (!object) return `<code>…</code>`;
    const old = context.before ? context.before.heap.get(id) : null;
    context.sizes.push({ names, object });

    switch (object.kind) {
      case "array":
      case "list":
        if (isGrid(object, context.heap)) return grid(object, scalars, context);
        return cells(object.items || [], scalars, context, old && old.items, previousScalars(context));
      case "map": return entries(object, old, true);
      case "set": return entries(object, old, false);
      case "stack": return lane(object, old, "stack");
      case "queue": return lane(object, old, "queue");
      case "heap": return heapTree(object, old);
      case "tree": return context.drawn.has(id) ? null : treeDiagram(id, context);
      case "listnode": return context.drawn.has(id) ? null : listDiagram(id, context);
      default: return fieldsTable(object);
    }
  }

  // ---- arrays, strings and grids -------------------------------------------------------------

  /**
   * Cells, with index variables as pointers under them. A pointer that moved slides from
   * its old cell; a cell whose value changed flashes; two cells that traded values fly
   * past each other.
   */
  function cells(items, scalars, context, oldItems, oldScalars) {
    const count = items.length;
    const pointers = scalars.filter(s => isIndexName(s.name) && s.number >= 0 && s.number < count);
    const oldPositions = new Map((oldScalars || []).map(s => [s.name, s.number]));

    // A long array is windowed around its pointers, so they are always on screen.
    let from = 0, to = count;
    if (count > MAX_CELLS) {
      const at = pointers.length ? pointers.map(p => p.number) : [0];
      const centre = Math.round((Math.min(...at) + Math.max(...at)) / 2);
      from = Math.max(0, Math.min(count - MAX_CELLS, centre - MAX_CELLS / 2));
      to = from + MAX_CELLS;
    }

    const swapWith = new Map();
    if (oldItems && oldItems.length === count) {
      const changed = [];
      for (let i = 0; i < count; i++) if (text(oldItems[i]) !== text(items[i])) changed.push(i);
      if (changed.length === 2) {
        const [a, b] = changed;
        if (text(oldItems[a]) === text(items[b]) && text(oldItems[b]) === text(items[a])) {
          swapWith.set(a, b);
          swapWith.set(b, a);
        }
      }
    }

    const html = [];
    if (from > 0) html.push(`<div class="cell more" title="${from} cells before">… ${from}</div>`);
    for (let i = from; i < to; i++) {
      const here = pointers.filter(p => p.number === i);
      const written = oldItems && oldItems.length === count && text(oldItems[i]) !== text(items[i]);
      const swap = swapWith.has(i) ? ` fly" style="--from:${(swapWith.get(i) - i) * CELL_PITCH}px` : "";
      const labels = here.map(p => {
        const was = oldPositions.get(p.name);
        const slide = was != null && was !== i && was >= from && was < to ? ` slide" style="--from:${(was - i) * CELL_PITCH}px` : "";
        return `<span class="pointer${slide}">${escapeHtml(p.name)}</span>`;
      }).join("");
      html.push(`<div class="cell ${here.length ? "pointed" : ""}">
        <div class="cell-value ${written && !swap ? "written" : ""}"><span class="value${swap}">${escapeHtml(text(items[i]))}</span></div>
        <div class="cell-index">${i}</div>
        <div class="cell-pointers">${labels}</div>
      </div>`);
    }
    if (to < count) html.push(`<div class="cell more" title="${count - to} cells after">… ${count - to}</div>`);
    return `<div class="cells">${html.join("")}</div>`;
  }

  const text = value => (value == null ? "" : typeof value === "string" ? value : value.text);

  /** An array whose every element is an array of plain values: a grid. */
  function isGrid(object, heap) {
    const items = object.items || [];
    if (items.length === 0 || items.some(item => item.ref == null)) return false;
    return items.every(item => {
      const inner = heap.get(item.ref);
      return inner && (inner.kind === "array" || inner.kind === "list") && (inner.items || []).every(v => v.ref == null);
    });
  }

  function grid(object, scalars, context) {
    const row = scalars.find(s => ROW_NAMES.has(s.name));
    const col = scalars.find(s => COL_NAMES.has(s.name) && s !== row);
    const rows = object.items.map(item => context.heap.get(item.ref));
    const oldRows = context.before ? object.items.map(item => context.before.heap.get(item.ref)) : null;

    return `<div class="grid">${rows.map((line, r) => {
      const old = oldRows && oldRows[r];
      return `<div class="grid-row">${(line.items || []).map((value, c) => {
        const here = row && col && row.number === r && col.number === c;
        const written = old && old.items && text(old.items[c]) !== text(value);
        return `<span class="${here ? "grid-here" : ""} ${written ? "written" : ""}">${escapeHtml(text(value))}</span>`;
      }).join("")}</div>`;
    }).join("")}</div>`;
  }

  // ---- maps, sets, stacks, queues ----------------------------------------------------------

  /** Entries animate in when added, out when removed, and flash when their value changes. */
  function entries(object, old, keyed) {
    const keyOf = entry => (keyed ? text(entry.key) : text(entry));
    const current = keyed ? object.entries || [] : object.items || [];
    const previous = old ? (keyed ? old.entries || [] : old.items || []) : null;
    const before = previous ? new Map(previous.map(e => [keyOf(e), e])) : null;
    const now = new Set(current.map(keyOf));

    const html = current.map(entry => {
      const was = before && before.get(keyOf(entry));
      const state = !before ? "" : !was ? "added" : keyed && text(was.value) !== text(entry.value) ? "updated" : "";
      return keyed
        ? `<span class="entry ${state}"><span class="key">${escapeHtml(text(entry.key))}</span><span class="arrow">→</span><span class="val">${escapeHtml(text(entry.value))}</span></span>`
        : `<span class="entry ${state}"><span class="key">${escapeHtml(text(entry))}</span></span>`;
    });

    for (const [key, entry] of before || []) {
      if (now.has(key)) continue;
      html.push(keyed
        ? `<span class="entry removed"><span class="key">${escapeHtml(text(entry.key))}</span><span class="arrow">→</span><span class="val">${escapeHtml(text(entry.value))}</span></span>`
        : `<span class="entry removed"><span class="key">${escapeHtml(key)}</span></span>`);
    }

    const more = object.count > current.length ? `<span class="muted">… ${object.count - current.length} more</span>` : "";
    return `<div class="entries">${html.join("") || '<span class="muted">empty</span>'}${more}</div>`;
  }

  /**
   * A stack, top first and drawn on top; a queue, front first and drawn on the left. What
   * was pushed animates in; what was popped animates out where it left from.
   */
  function lane(object, old, kind) {
    const items = object.items || [];
    const previous = old ? old.items || [] : null;

    // Matched from the end that stays put — a stack's bottom, a queue's back — so a pop
    // reads as a pop and not as every element changing.
    let added = 0, removed = 0;
    if (previous) {
      const a = kind === "stack" ? items.slice().reverse() : items;
      const b = kind === "stack" ? previous.slice().reverse() : previous;
      if (kind === "stack") {
        let common = 0;
        while (common < a.length && common < b.length && text(a[common]) === text(b[common])) common++;
        added = a.length - common;
        removed = b.length - common;
      } else {
        // Queue: elements leave at the front and arrive at the back.
        let shift = 0;
        while (shift <= b.length && !sameRun(b.slice(shift), a)) shift++;
        removed = Math.min(shift, b.length);
        added = Math.max(0, a.length - (b.length - removed));
      }
    }

    const cellsHtml = items.map((item, i) => {
      const isNew = kind === "stack" ? i < added : i >= items.length - added;
      return `<span class="slot ${isNew ? "added" : ""}">${escapeHtml(text(item))}</span>`;
    });
    // What left is drawn fading out at the end it left from: a stack's top, a queue's front.
    const gone = previous ? previous.slice(0, removed) : [];
    const ghosts = gone.map(item => `<span class="slot removed">${escapeHtml(text(item))}</span>`);
    const ordered = ghosts.concat(cellsHtml);
    const label = kind === "stack" ? "top" : "front";
    return `<div class="lane ${kind}"><span class="lane-end">${label}</span>${ordered.join("") || '<span class="muted">empty</span>'}${
      kind === "queue" ? '<span class="lane-end">back</span>' : ""}</div>`;
  }

  function sameRun(prefixOf, whole) {
    if (prefixOf.length > whole.length) return false;
    for (let i = 0; i < prefixOf.length; i++) if (text(prefixOf[i]) !== text(whole[i])) return false;
    return true;
  }

  /**
   * A heap drawn level by level. .NET's PriorityQueue is a four-way heap of element and
   * priority (entries); a Python list kept by heapq is a two-way heap of values (items).
   * Position i's children are at arity·i+1 onwards either way.
   */
  function heapTree(object, old) {
    const arity = object.arity || 4;
    const pairs = object.entries != null;
    const items = pairs ? object.entries : object.items || [];
    if (items.length === 0) return '<span class="muted">empty</span>';
    const keyOf = item => (pairs ? text(item.key) + "|" + text(item.value) : text(item));
    const before = old ? new Set((pairs ? old.entries : old.items || []).map(keyOf)) : null;

    const levels = [];
    for (let start = 0, width = 1; start < items.length; start += width, width *= arity) levels.push(items.slice(start, start + width));

    const note = arity === 4
      ? "Smallest priority at the top. .NET's PriorityQueue is a four-way heap: each element has up to four children."
      : "Smallest at the top (heapq keeps a min-heap in the list): each element has up to two children, at 2i+1 and 2i+2.";
    return `<div class="heap-tree">${levels.map((level, depth) =>
      `<div class="heap-level" title="level ${depth}">${level.map(item => {
        const isNew = before && !before.has(keyOf(item));
        return pairs
          ? `<span class="slot ${isNew ? "added" : ""}">${escapeHtml(text(item.key))}<small>${escapeHtml(text(item.value))}</small></span>`
          : `<span class="slot ${isNew ? "added" : ""}">${escapeHtml(text(item))}</span>`;
      }).join("")}</div>`).join("")}
      <div class="footnote">${note}</div>
    </div>`;
  }

  function fieldsTable(object) {
    return `<div class="fields"><span class="type-name">${escapeHtml(object.type)}</span>${(object.fields || []).map(f =>
      `<span class="entry"><span class="key">${escapeHtml(f.name)}</span><span class="arrow">=</span><span class="val">${escapeHtml(text(f.value))}</span></span>`
    ).join("")}</div>`;
  }

  // ---- trees and linked lists ------------------------------------------------------------------

  /** Variables holding each node, for labelling it. */
  function labelsByNode(step) {
    const labels = new Map();
    for (const v of step.variables) {
      if (v.ref == null) continue;
      if (!labels.has(v.ref)) labels.set(v.ref, []);
      labels.get(v.ref).push(v.name);
    }
    return labels;
  }

  /** In-order position across, depth down. Stable while the tree's shape does not change. */
  function layoutTree(rootId, heap) {
    const positions = new Map();
    let order = 0;
    const walk = (id, depth) => {
      if (id == null || positions.has(id) || positions.size >= MAX_NODES) return;
      const node = heap.get(id);
      if (!node || node.kind !== "tree") return;
      positions.set(id, null);
      walk(refOf(field(node, "left")), depth + 1);
      positions.set(id, { x: order++ * TREE_X + TREE_X / 2, y: depth * TREE_Y + 24 });
      walk(refOf(field(node, "right")), depth + 1);
    };
    walk(rootId, 0);
    for (const [id, p] of positions) if (!p) positions.delete(id);
    return positions;
  }

  /** The root of the tree a node belongs to, as far as the recording shows parents. */
  function rootOf(id, heap) {
    const parent = new Map();
    for (const [nodeId, node] of heap) {
      if (node.kind !== "tree") continue;
      for (const side of ["left", "right"]) {
        const child = refOf(field(node, side));
        if (child != null) parent.set(child, nodeId);
      }
    }
    let root = id, guard = 0;
    while (parent.has(root) && guard++ < MAX_NODES) root = parent.get(root);
    return root;
  }

  function treeDiagram(id, context) {
    const { heap, before, step, model, index } = context;
    const root = rootOf(id, heap);
    const positions = layoutTree(root, heap);
    const oldPositions = before ? layoutTree(rootOf(id, before.heap), before.heap) : null;
    const labels = labelsByNode(step);
    for (const nodeId of positions.keys()) context.drawn.add(nodeId);

    // The nodes the calls on the stack are working on: the path from the root to here.
    const onPath = new Set(context.frames.slice(0, -1)
      .flatMap(f => f.variables).filter(v => v.kind === "tree" && v.ref != null).map(v => v.ref));

    const width = Math.max(...[...positions.values()].map(p => p.x)) + TREE_X / 2 + 10;
    const height = Math.max(...[...positions.values()].map(p => p.y)) + TREE_Y / 2 + 14;
    const parts = [];

    for (const [nodeId, p] of positions) {
      const node = heap.get(nodeId);
      for (const side of ["left", "right"]) {
        const child = refOf(field(node, side));
        if (child == null || !positions.has(child)) continue;
        const c = positions.get(child);
        const oldNode = before && before.heap.get(nodeId);
        const rewired = oldNode && refOf(field(oldNode, side)) !== child;
        parts.push(`<line class="edge ${rewired ? "rewired" : ""}" x1="${p.x}" y1="${p.y}" x2="${c.x}" y2="${c.y}"/>`);
      }
    }

    for (const [nodeId, p] of positions) {
      const node = heap.get(nodeId);
      const reached = model.firstReached.get(nodeId);
      const here = labels.get(nodeId);
      const visited = reached != null && reached < index;
      const oldP = oldPositions && oldPositions.get(nodeId);
      const move = oldP && (oldP.x !== p.x || oldP.y !== p.y)
        ? `<animateTransform attributeName="transform" type="translate" from="${oldP.x} ${oldP.y}" to="${p.x} ${p.y}" dur="${context.ms}ms" fill="freeze"/>`
        : "";
      const oldValue = before && before.heap.get(nodeId);
      const written = oldValue && text(field(oldValue, "val")) !== text(field(node, "val"));
      const path = !here && onPath.has(nodeId);
      parts.push(`<g class="node ${here ? "current" : ""} ${path ? "path" : ""} ${visited ? "visited" : ""} ${written ? "written" : ""}" transform="translate(${p.x} ${p.y})">${move}
        <circle r="${NODE_R}"/><text class="node-value" dy="4">${escapeHtml(text(field(node, "val")))}</text>
        ${here ? `<text class="node-label" dy="${NODE_R + 13}">${escapeHtml(here.join(", "))}</text>` : ""}
      </g>`);
    }

    const legend = '<div class="footnote"><span class="dot current"></span> held by a variable now ' +
      (onPath.size ? '<span class="dot path"></span> being worked on by a call further up the stack ' : "") +
      '<span class="dot visited"></span> reached earlier</div>';
    return `<div class="diagram"><svg width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="tree">${parts.join("")}</svg>${legend}</div>`;
  }

  /**
   * Every list node a variable can reach, in the order the list was first seen. An arrow
   * that was rewired at this step is drawn bending from its old target to its new one.
   */
  function listDiagram(id, context) {
    const { heap, before, step, model } = context;
    const labels = labelsByNode(step);

    // Every node reachable from any variable, so prev, curr and next share one drawing.
    const nodes = new Set();
    for (const v of step.variables) {
      let at = v.kind === "listnode" ? v.ref : null, guard = 0;
      while (at != null && !nodes.has(at) && guard++ < MAX_NODES) {
        nodes.add(at);
        const node = heap.get(at);
        at = node ? refOf(field(node, "next")) : null;
      }
    }
    for (const nodeId of nodes) context.drawn.add(nodeId);

    const slots = [...nodes].sort((a, b) => (model.listSlots.get(a) ?? 1e9) - (model.listSlots.get(b) ?? 1e9));
    const x = new Map(slots.map((nodeId, i) => [nodeId, i * 70 + 30]));
    const y = 40;
    const width = slots.length * 70 + 20;
    const parts = [];

    const path = (from, to) => {
      if (to == null || !x.has(to)) return null;
      const x1 = x.get(from), x2 = x.get(to);
      if (x2 > x1) return `M ${x1 + 18} ${y} L ${x2 - 20} ${y}`;
      // Pointing back: an arc underneath, so it never runs through other nodes.
      const depth = 26 + Math.min(40, Math.abs(x1 - x2) / 6);
      return `M ${x1} ${y + 18} C ${x1} ${y + 18 + depth}, ${x2} ${y + 18 + depth}, ${x2} ${y + 20}`;
    };

    for (const nodeId of slots) {
      const node = heap.get(nodeId);
      const to = refOf(field(node, "next"));
      const d = path(nodeId, to);
      const old = before && before.heap.get(nodeId);
      const oldTo = old ? refOf(field(old, "next")) : to;
      const oldD = oldTo !== to ? path(nodeId, oldTo) : null;
      if (d) {
        parts.push(`<path class="link ${oldTo !== to ? "rewired" : ""}" d="${d}" marker-end="url(#arrowhead)">${
          oldD ? `<animate attributeName="d" from="${oldD}" to="${d}" dur="${context.ms}ms" fill="freeze"/>` : ""}</path>`);
      } else if (to == null) {
        parts.push(`<text class="null-mark" x="${x.get(nodeId) + 22}" y="${y + 4}">∅</text>`);
      }
    }

    for (const nodeId of slots) {
      const node = heap.get(nodeId);
      const here = labels.get(nodeId);
      parts.push(`<g class="node box ${here ? "current" : ""}" transform="translate(${x.get(nodeId)} ${y})">
        <rect x="-18" y="-16" width="36" height="32" rx="4"/><text class="node-value" dy="4">${escapeHtml(text(field(node, "val")))}</text>
        ${here ? `<text class="node-label" dy="-24">${escapeHtml(here.join(", "))}</text>` : ""}
      </g>`);
    }

    return `<div class="diagram"><svg width="${width}" height="110" viewBox="0 0 ${width} 110" role="img" aria-label="linked list">
      <defs><marker id="arrowhead" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z"/></marker></defs>
      ${parts.join("")}</svg></div>`;
  }

  // ---- graphs ---------------------------------------------------------------------------------

  /**
   * The graph input as nodes and edges, with what the run has done to it so far: nodes
   * marked visited by a visited array or set, the node being looked at, and the nodes
   * waiting in a queue or stack.
   */
  function graphPanel(context) {
    const { model, step, heap } = context;
    const graph = model.trace.graph;
    if (!graph) return "";

    const first = model.trace.steps[0];
    const input = first.variables.find(v => v.name === graph.variable);
    // The graph is the input as it arrived: the first step's objects, read without copying.
    const firstHeap = model.checkpoints[0];
    const object = input && input.ref != null ? firstHeap.get(input.ref) : null;
    if (!object) return "";

    let n = 0;
    const edges = [];
    if (graph.kind === "edges") {
      const count = first.variables.find(v => v.name === graph.nodeCount);
      n = count && count.number != null ? count.number : 0;
      for (const item of object.items || []) {
        const pair = firstHeap.get(item.ref);
        if (pair && pair.items && pair.items.length >= 2) edges.push([pair.items[0].number, pair.items[1].number]);
      }
    } else {
      n = (object.items || []).length;
      // An undirected graph lists every edge from both ends; it is drawn once.
      const seen = new Set();
      (object.items || []).forEach((item, from) => {
        const list = firstHeap.get(item.ref);
        for (const to of (list && list.items) || []) {
          const key = Math.min(from, to.number) + "-" + Math.max(from, to.number);
          if (seen.has(key)) continue;
          seen.add(key);
          edges.push([from, to.number]);
        }
      });
    }
    if (n <= 0 || n > MAX_NODES) return "";

    const visited = new Set(), waiting = new Set();
    let current = null;
    const scopes = context.frames.flatMap(f => f.variables).concat(step.variables);
    for (const v of scopes) {
      const target = v.ref != null ? heap.get(v.ref) : null;
      if (target && VISITED_NAMES.test(v.name)) {
        if (target.kind === "array") (target.items || []).forEach((item, i) => { if (item.text === "true") visited.add(i); });
        if (target.kind === "set") (target.items || []).forEach(item => visited.add(item.number));
      }
      if (target && (target.kind === "queue" || target.kind === "stack")) (target.items || []).forEach(item => waiting.add(item.number));
    }
    for (const v of step.variables) {
      if (CURRENT_NODE_NAMES.has(v.name) && v.number != null && v.number >= 0 && v.number < n) { current = v.number; break; }
    }

    const radius = Math.max(60, n * 9);
    const size = radius * 2 + 50;
    const at = i => ({ x: size / 2 + radius * Math.cos((2 * Math.PI * i) / n - Math.PI / 2), y: size / 2 + radius * Math.sin((2 * Math.PI * i) / n - Math.PI / 2) });

    const lines = edges.filter(([a, b]) => a >= 0 && a < n && b >= 0 && b < n).map(([a, b]) => {
      const p = at(a), q = at(b);
      return `<line class="edge" x1="${p.x}" y1="${p.y}" x2="${q.x}" y2="${q.y}"/>`;
    });
    const nodes = Array.from({ length: n }, (_, i) => {
      const p = at(i);
      const state = i === current ? "current" : waiting.has(i) ? "waiting" : visited.has(i) ? "visited" : "";
      return `<g class="node ${state}" transform="translate(${p.x} ${p.y})"><circle r="${NODE_R}"/><text class="node-value" dy="4">${i}</text></g>`;
    });

    return `<div class="panel-block"><div class="block-title">Graph — ${escapeHtml(graph.variable)}</div>
      <div class="diagram"><svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" role="img" aria-label="graph">${lines.join("")}${nodes.join("")}</svg>
      <div class="footnote"><span class="dot current"></span> current <span class="dot waiting"></span> waiting in a queue or stack <span class="dot visited"></span> visited</div></div></div>`;
  }

  // ---- call stack and recursion tree ------------------------------------------------------------

  function callStack(frames, context) {
    if (frames.length <= 1 && !context.model.calls.recursive) return "";
    const shown = frames.slice(-8);
    const hidden = frames.length - shown.length;
    return `<div class="panel-block"><div class="block-title">Call stack · ${frames.length} deep</div>
      <div class="call-stack">${hidden > 0 ? `<div class="frame-card muted">… ${hidden} more below</div>` : ""}${shown.map((frame, i) => {
        const top = i === shown.length - 1;
        const vars = frame.variables.filter(v => v.ref == null).slice(0, 6)
          .map(v => `<span class="entry"><span class="key">${escapeHtml(v.name)}</span><span class="arrow">=</span><span class="val">${escapeHtml(v.display)}</span></span>`).join("");
        return `<div class="frame-card ${top ? "top" : ""} ${top && context.before && context.before.step.depth < context.step.depth ? "added" : ""}">
          <div class="frame-name">${escapeHtml(callLabel(frame.call))}</div><div class="entries">${vars}</div></div>`;
      }).reverse().join("")}</div></div>`;
  }

  function callLabel(call) {
    return `${call.method}(${call.args.map(a => a.display).join(", ")})`;
  }

  /** Every call so far as a tree, with what each returned. Shown when the code recurses. */
  function recursionTree(context) {
    const { model, index } = context;
    const { list, at, recursive } = model.calls;
    if (!recursive) return "";

    const current = at[index];
    const shown = list.filter(call => call.start <= index).slice(0, MAX_NODES);
    const ids = new Set(shown.map(c => c.id));

    // Leaves get consecutive columns; a parent sits over the middle of its children.
    const x = new Map();
    let column = 0;
    const place = call => {
      const kids = call.children.filter(id => ids.has(id)).map(id => list[id]);
      if (kids.length === 0) { x.set(call.id, column++); return; }
      kids.forEach(place);
      x.set(call.id, (x.get(kids[0].id) + x.get(kids[kids.length - 1].id)) / 2);
    };
    shown.filter(c => c.parent == null || !ids.has(c.parent)).forEach(place);

    // Wide enough for the longest label, so neighbouring calls never overlap.
    const longest = Math.max(...shown.map(call => callLabel(call).length));
    const W = Math.max(60, Math.min(200, longest * 7.4 + 18)), H = 58;
    const pos = call => ({ x: x.get(call.id) * W + W / 2, y: (call.depth - 1) * H + 22 });
    const width = column * W + 10, height = Math.max(...shown.map(c => c.depth)) * H + 20;

    const onStack = new Set();
    for (let id = current; id != null; id = list[id].parent) onStack.add(id);

    const edges = shown.filter(c => c.parent != null && ids.has(c.parent)).map(c => {
      const p = pos(list[c.parent]), q = pos(c);
      return `<line class="edge" x1="${p.x}" y1="${p.y + 10}" x2="${q.x}" y2="${q.y - 12}"/>`;
    });
    const nodes = shown.map(call => {
      const p = pos(call);
      const done = call.end != null && call.end <= index;
      const state = call.id === current ? "current" : onStack.has(call.id) ? "active" : done ? "returned" : "";
      const isNew = context.before && call.start === index && context.before.step.depth < context.step.depth;
      return `<g class="call ${state} ${isNew ? "added" : ""}" transform="translate(${p.x} ${p.y})">
        <rect x="${-W / 2 + 4}" y="-12" width="${W - 8}" height="22" rx="4"/>
        <text dy="4">${escapeHtml(callLabel(call))}</text>
        ${done && call.returned ? `<text class="returned" dy="24">→ ${escapeHtml(call.returned.text)}</text>` : ""}
      </g>`;
    });

    const more = list.filter(call => call.start <= index).length - shown.length;
    return `<div class="panel-block"><div class="block-title">Recursion tree · ${list.filter(c => c.start <= index).length} calls so far</div>
      <div class="diagram"><svg width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="recursion tree">${edges.join("")}${nodes.join("")}</svg>
      ${more > 0 ? `<div class="footnote">Showing the first ${MAX_NODES} calls.</div>` : ""}
      <div class="footnote"><span class="dot current"></span> running <span class="dot active"></span> waiting for a call it made <span class="dot returned"></span> returned</div></div></div>`;
  }

  // ---- memory ------------------------------------------------------------------------------------

  function memoryPanel(context) {
    const { model, index, sizes } = context;
    const steps = model.trace.steps;
    const allocated = steps[index].allocated || 0;
    const next = index + 1 < steps.length ? steps[index + 1].allocated : model.trace.totalAllocated;
    const thisStep = Math.max(0, (next || 0) - allocated);
    const total = model.trace.totalAllocated || (steps.length ? steps[steps.length - 1].allocated : 0);

    // At most 300 points across, whatever the length of the run.
    const stride = Math.max(1, Math.ceil(steps.length / 300));
    const points = [];
    for (let i = 0; i < steps.length; i += stride) points.push([i, steps[i].allocated || 0]);
    const W = 300, H = 48;
    const maxY = Math.max(1, total);
    const px = i => (steps.length <= 1 ? 0 : (i / (steps.length - 1)) * W);
    const py = v => H - (v / maxY) * (H - 4) - 2;
    const line = points.map(([i, v]) => `${px(i).toFixed(1)},${py(v).toFixed(1)}`).join(" ");

    const sizeList = sizes.map(({ names, object }) =>
      `<span class="entry"><span class="key">${escapeHtml(names.join(" = "))}</span><span class="arrow">≈</span><span class="val">${bytes(object.bytes)}</span></span>`).join("");

    return `<div class="panel-block memory"><div class="block-title">Memory</div>
      <div class="memory-figures">
        <div><div class="figure ${thisStep > 0 ? "hot" : ""}">${bytes(thisStep)}</div><div class="muted">this line allocates</div></div>
        <div><div class="figure">${bytes(allocated)}</div><div class="muted">allocated so far</div></div>
        <div><div class="figure">${bytes(total)}</div><div class="muted">in the whole run</div></div>
      </div>
      <svg class="sparkline" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="memory allocated over the run">
        <polyline points="${line}"/><line class="cursor" x1="${px(index)}" y1="0" x2="${px(index)}" y2="${H}"/>
      </svg>
      ${sizeList ? `<div class="entries sizes">${sizeList}</div>` : ""}
      <div class="footnote">${context.python
        ? "Approximate, from Python's tracemalloc: what your code allocated, not the visualizer. Python allocates even for integers above 256. Sizes are each structure itself (sys.getsizeof), not what its elements point to."
        : "Bytes allocated by your code, not by the visualizer. Sizes are approximate: each structure itself, not what its elements point to."}</div>
    </div>`;
  }

  window.DsaRun = { prepare, render, heapAt, framesAt, MAX_CELLS };
})();
