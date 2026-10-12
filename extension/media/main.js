// Webview UI. Also runnable outside VS Code through window.__render, which is how the
// views get looked at during development without launching an Extension Host.

(function () {
  "use strict";

  const vscode = typeof acquireVsCodeApi === "function" ? acquireVsCodeApi() : null;
  const app = document.getElementById("app");

  const STRATEGY_HELP = {
    Random: "Random values from a bounded range — repeats are likely",
    Distinct: "Every value unique — the worst case for code that stops at a duplicate",
    Sorted: "Ascending",
    ReverseSorted: "Descending",
    AllDuplicates: "Every value identical (for a grid: all land, one giant island)",
    NearlySorted: "Ascending with a few positions swapped",
    Balanced: "Perfectly balanced tree — height log n",
    Skewed: "Every node has only a right child — height n, the worst case for recursion depth",
    Path: "Nodes joined in a single line — the deepest possible traversal"
  };

  const CLASS_LABEL = {
    Constant: "O(1)", Logarithmic: "O(log n)", Linear: "O(n)",
    Linearithmic: "O(n log n)", Quadratic: "O(n²)", Cubic: "O(n³)",
    Exponential: "O(2ⁿ)", Unknown: "unknown"
  };

  // Matches the engine's basis functions so the drawn curve is the fitted one rather
  // than a smooth line that merely looks plausible.
  const BASIS = {
    Constant: () => 1,
    Logarithmic: n => (n < 2 ? 1 : Math.log2(n)),
    Linear: n => n,
    Linearithmic: n => (n < 2 ? 1 : n * Math.log2(n)),
    Quadratic: n => n * n,
    Cubic: n => n * n * n
  };

  let state = { response: null, request: null, tab: "cost", expanded: new Set() };

  const num = value => value.toLocaleString("en-US");
  const escapeHtml = text => String(text).replace(/[&<>"]/g,
    ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));

  function bytes(value) {
    if (!value) return "0";
    if (value < 1024) return value + " B";
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";
    return (value / (1024 * 1024)).toFixed(1) + " MB";
  }

  // ---- rendering ---------------------------------------------------------------

  function render() {
    const { response } = state;
    if (!response) { app.innerHTML = '<p class="placeholder">Measuring…</p>'; return; }

    const primary = response.primary;
    if (!primary || primary.status !== "Completed") {
      // The engine puts the reason in the errors when nothing was measured; the size note
      // is a fallback so an explanation can never be reduced to "no result".
      const problems = [...(response.errors || []), ...((primary && primary.errors) || [])];
      if (problems.length === 0 && primary && primary.sizeNote) problems.push(primary.sizeNote);
      app.innerHTML = header(response) + banner(
        "Nothing could be measured",
        problems.length ? problems.map(escapeHtml).join("<br>") : "The engine returned no result.",
        true);
      return;
    }

    const parts = [header(response), verdicts(primary)];

    if (primary.disagreement && primary.disagreementNote) {
      parts.push(banner("Time: the code and the measurement disagree", escapeHtml(primary.disagreementNote)));
    }
    if (primary.space && primary.space.disagreement && primary.space.disagreementNote) {
      parts.push(banner("Space: the code and the measurement disagree", escapeHtml(primary.space.disagreementNote)));
    }
    if (primary.sizeNote) parts.push(banner("Sizes adjusted", escapeHtml(primary.sizeNote)));
    if (primary.errors && primary.errors.length) {
      parts.push(banner("Some sizes did not run", primary.errors.map(escapeHtml).join("<br>")));
    }

    parts.push(controls(primary));

    const tabs = [["cost", "Statement cost"], ["growth", "Growth"]];
    if (response.comparison) tabs.push(["compare", "Comparison"]);
    if (!tabs.some(t => t[0] === state.tab)) state.tab = "cost";

    parts.push('<div class="tabs">' + tabs.map(([id, label]) =>
      `<button class="tab ${state.tab === id ? "active" : ""}" data-tab="${id}">${label}</button>`
    ).join("") + "</div>");

    parts.push('<div class="panel">' +
      (state.tab === "cost" ? costPanel(response, primary)
        : state.tab === "growth" ? growthPanel(primary)
        : comparePanel(response)) +
      "</div>");

    app.innerHTML = parts.join("");
    wire();
  }

  function header(response) {
    return `<div class="head">
      <h1>${escapeHtml(response.label || "Solution")}</h1>
      <span class="entry">${escapeHtml((state.request && state.request.entry) || "")}()</span>
    </div>`;
  }

  function banner(title, body, isError) {
    return `<div class="banner ${isError ? "error" : ""}"><strong>${escapeHtml(title)}</strong>${body}</div>`;
  }

  function card(label, value, note) {
    return `<div class="verdict">
      <div class="label">${label}</div>
      <div class="value ${value === "unknown" ? "unknown" : ""}">${value}</div>
      <div class="note">${escapeHtml(note)}</div>
    </div>`;
  }

  function verdicts(primary) {
    const label = value => CLASS_LABEL[value] || value;
    const parts = [`<div class="verdicts">
      ${card("Time — measured", label(primary.empirical.best), primary.empirical.explanation)}
      ${card("Time — read from the code", label(primary.static.class), primary.static.reasoning)}
    </div>`];

    // Space gets the same two readings as time: what the run actually did, and what the
    // code says it should do. The original request asked how memory is allocated;
    // this is that answer, at the same prominence as the time answer.
    const space = primary.space;
    if (space) {
      parts.push(`<div class="verdicts">
        ${card("Space — measured", label(space.likelyPeak), space.summary)}
        ${card("Space — read from the code", label(space.static.class), space.static.reasoning)}
      </div>`);
    }
    return parts.join("");
  }

  function controls(primary) {
    const params = primary.parameters || [];
    const sizes = primary.points.map(p => p.size).join(", ");

    const inputs = primary.inputs.map((spec, index) => {
      const param = params[index] || { name: "argument " + (index + 1), type: spec.shape, applicableStrategies: [] };
      const label = `<label for="in-${index}">${escapeHtml(param.name)} <span class="type">${escapeHtml(param.type)}</span></label>`;

      if (param.applicableStrategies.length > 0) {
        // Only arrangements that make sense for this parameter: Skewed for a tree,
        // never for an array. The engine decides, so the two can never disagree.
        return `<div class="control">${label}
          <select id="in-${index}" data-index="${index}" data-kind="strategy">
            ${param.applicableStrategies.map(value =>
              `<option value="${value}" ${value === spec.strategy ? "selected" : ""} title="${escapeHtml(STRATEGY_HELP[value] || "")}">${value}</option>`
            ).join("")}
          </select></div>`;
      }

      if (spec.shape === "BoolScalar") {
        return `<div class="control">${label}
          <select id="in-${index}" data-index="${index}" data-kind="bool">
            <option value="0" ${spec.fixedValue === 0 ? "selected" : ""}>false</option>
            <option value="1" ${spec.fixedValue !== 0 ? "selected" : ""}>true</option>
          </select></div>`;
      }

      return `<div class="control">${label}
        <div class="scalar">
          <label class="inline"><input type="checkbox" data-index="${index}" data-kind="scales" ${spec.scalesWithSize ? "checked" : ""}> grows with n</label>
          <input type="number" id="in-${index}" data-index="${index}" data-kind="value" value="${spec.fixedValue}" ${spec.scalesWithSize ? "disabled" : ""}>
        </div></div>`;
    }).join("");

    return `<div class="controls">
      ${inputs}
      <div class="control">
        <label for="sizes">Sizes</label>
        <input type="text" id="sizes" value="${escapeHtml(sizes)}" title="Leave unchanged to let the engine choose and adapt">
      </div>
      <button id="rerun">Re-measure</button>
    </div>`;
  }

  // ---- cost panel --------------------------------------------------------------

  function costPanel(response, primary) {
    const detail = primary.detail;
    if (!detail) return '<p class="placeholder">No per-statement detail was captured.</p>';

    const largest = primary.points.length ? primary.points[primary.points.length - 1].size : null;
    const cost = s => s.hits + (s.libraryWork || 0);

    // Ranked by total cost, not by how often a statement ran. Ranked by runs, a
    // List.Contains line that runs 1,000 times sits below a loop header that runs 1,001 —
    // and its 499,500 elements of scanning, the real cost, would be invisible.
    const ranked = [...detail.statements].sort((a, b) => cost(b) - cost(a));
    const maxCost = Math.max(1, ...ranked.map(cost));
    const shown = ranked.slice(0, 40);
    const anyLibraryWork = ranked.some(s => s.libraryWork > 0);

    const growthById = new Map(((primary.derivation && primary.derivation.statements) || []).map(g => [g.id, g]));
    const dominant = new Set((primary.derivation && primary.derivation.dominant) || []);

    const rows = shown.map((statement, index) => {
      // Logarithmic, so a statement that ran once stays visible beside one that cost
      // half a million. On a linear scale everything but the top row vanishes.
      const width = Math.max(2, Math.round(110 * Math.log(cost(statement) + 1) / Math.log(maxCost + 1)));
      return `<tr class="hot-row" data-line="${statement.line}" title="Go to line ${statement.line}">
        <td class="rank">${index + 1}</td>
        <td class="hot-bar"><span class="bar" style="width:${width}px"></span></td>
        <td>${num(statement.hits)}</td>
        ${anyLibraryWork ? `<td class="${statement.libraryWork ? "hidden-work" : "muted"}">${statement.libraryWork ? "+" + num(statement.libraryWork) : "—"}</td>` : ""}
        <td class="${statement.bytesAllocated ? "" : "muted"}">${bytes(statement.bytesAllocated)}</td>
        <td class="growth ${dominant.has(statement.id) ? "dominant" : ""}">${growthCell(growthById.get(statement.id))}</td>
        <td class="snippet-cell">${escapeHtml(statement.snippet)}</td>
        <td class="muted">${statement.line}:${statement.column}</td>
      </tr>`;
    }).join("");

    const calls = (detail.libraryCalls || []).slice().sort((a, b) => b.work - a.work);
    const library = calls.length === 0 ? "" : `
      <h3>Library calls doing hidden work</h3>
      <p class="footnote">Each of these is one statement on the page and a loop inside the library.
        The work is the number of elements it touched in total.</p>
      <table class="hot">
        <thead><tr><th class="snippet-cell">call</th><th>cost per call</th><th>calls</th><th>work</th><th>at</th></tr></thead>
        <tbody>${calls.map(call => `<tr class="hot-row" data-line="${call.line}" title="Go to line ${call.line}">
          <td class="snippet-cell">${escapeHtml(call.member)}</td>
          <td>${escapeHtml(call.cost)}</td>
          <td>${num(call.calls)}</td>
          <td class="hidden-work">${num(call.work)}</td>
          <td class="muted">${call.line}:${call.column}</td>
        </tr>`).join("")}</tbody>
      </table>`;

    const why = primary.derivation
      ? `<div class="derivation"><div class="label">Derivation</div>
           <p>${formatInlineCode(primary.derivation.summary)}</p></div>`
      : "";

    return `${why}<div class="banner info"><strong>The costs are on your code</strong>
        Every executed line in ${escapeHtml(response.label || "the file")} is annotated in the
        editor with how often it ran, any work hidden inside library calls it made, and the
        memory it allocated. Hover a line for the breakdown. Editing the file clears the
        annotations, because once a line moves the numbers beside it are no longer about that line.</div>

      <table class="hot">
        <thead><tr>
          <th class="rank">#</th><th></th><th>executions</th>
          ${anyLibraryWork ? "<th>library work</th>" : ""}
          <th>memory</th><th>grows as</th><th class="snippet-cell">statement</th><th>at</th>
        </tr></thead>
        <tbody>${rows}</tbody>
      </table>
      ${library}
      <div class="footnote">
        Measured at n = ${largest === null ? "\u2014" : num(largest)}:
        ${num(detail.totalStatements)} statements and ${num(detail.totalOperations - detail.totalStatements)}
        units of library work, ${bytes(detail.totalBytesAllocated)} allocated,
        call depth ${num(detail.maxRecursionDepth)}.
        ${ranked.length > shown.length ? `Showing the ${shown.length} costliest of ${num(ranked.length)} executed statements. ` : ""}
        Click a row to jump to it.
        <br>${escapeHtml(detail.memoryModel)}
      </div>`;
  }

  /** The statement's own class and formula; the dominant ones are what set the method's class. */
  function growthCell(growth) {
    if (!growth) return '<span class="muted">—</span>';
    const label = CLASS_LABEL[growth.cost];
    if (!label) return '<span class="muted">irregular</span>';
    const title = [
      growth.runsFormula ? `runs ${growth.runsFormula} times` : null,
      growth.cost !== growth.runs && growth.costFormula ? `costs ${growth.costFormula}` : null,
      growth.because,
      growth.memoryFormula ? `allocates ${growth.memoryFormula} in total` : null
    ].filter(Boolean).join("; ");
    return `<span title="${escapeHtml(title)}">${label}</span>` +
      (growth.costFormula ? ` <span class="formula">${escapeHtml(growth.costFormula)}</span>` : "");
  }

  /** `code` in the derivation's prose becomes inline code, everything else stays escaped. */
  function formatInlineCode(text) {
    return escapeHtml(text).replace(/`([^`]+)`/g, "<code>$1</code>");
  }

  // ---- charts ------------------------------------------------------------------

  function chart(series, useLog, axisTitle = "operations") {
    const width = 760, height = 380;
    const left = 76, right = 20, top = 18, bottom = 46;
    const plotWidth = width - left - right;
    const plotHeight = height - top - bottom;

    const all = series.flatMap(s => s.points);
    if (all.length === 0) return "";

    const tx = value => useLog ? Math.log10(Math.max(value, 1)) : value;
    const ty = value => useLog ? Math.log10(Math.max(value, 1)) : value;

    const xs = all.map(p => tx(p.size));
    const ys = all.map(p => ty(p.operations));
    const minX = Math.min(...xs), maxX = Math.max(...xs);
    const minY = useLog ? Math.min(...ys) : 0, maxY = Math.max(...ys);

    const spanX = maxX - minX || 1;
    const spanY = maxY - minY || 1;

    const px = value => left + (tx(value) - minX) / spanX * plotWidth;
    const py = value => top + plotHeight - (ty(value) - minY) / spanY * plotHeight;

    const parts = [];

    // Gridlines and ticks
    for (let i = 0; i <= 4; i++) {
      const y = top + plotHeight - (i / 4) * plotHeight;
      const value = minY + (i / 4) * spanY;
      const label = formatTick(useLog ? Math.pow(10, value) : value);
      parts.push(`<line class="grid" x1="${left}" y1="${y}" x2="${left + plotWidth}" y2="${y}"/>`);
      parts.push(`<text class="tick" x="${left - 8}" y="${y + 4}" text-anchor="end">${label}</text>`);
    }

    const xTicks = series[0].points.map(p => p.size);
    for (const size of xTicks) {
      const x = px(size);
      parts.push(`<line class="grid" x1="${x}" y1="${top}" x2="${x}" y2="${top + plotHeight}"/>`);
      parts.push(`<text class="tick" x="${x}" y="${top + plotHeight + 16}" text-anchor="middle">${size}</text>`);
    }

    parts.push(`<line class="axis" x1="${left}" y1="${top + plotHeight}" x2="${left + plotWidth}" y2="${top + plotHeight}"/>`);
    parts.push(`<line class="axis" x1="${left}" y1="${top}" x2="${left}" y2="${top + plotHeight}"/>`);
    parts.push(`<text class="axis-title" x="${left + plotWidth / 2}" y="${height - 8}" text-anchor="middle">input size n</text>`);
    parts.push(`<text class="axis-title" x="14" y="${top + plotHeight / 2}" text-anchor="middle" transform="rotate(-90 14 ${top + plotHeight / 2})">${escapeHtml(axisTitle)}</text>`);

    for (const one of series) {
      if (one.fit) {
        const curve = fitCurve(one.fit, one.points, one.intercept);
        if (curve.length) {
          parts.push(`<polyline class="fitline" stroke="${one.color}" points="${
            curve.map(p => `${px(p.size)},${py(p.operations)}`).join(" ")}"/>`);
        }
      }

      parts.push(`<polyline class="series" stroke="${one.color}" points="${
        one.points.map(p => `${px(p.size)},${py(p.operations)}`).join(" ")}"/>`);

      for (const point of one.points) {
        parts.push(`<circle cx="${px(point.size)}" cy="${py(point.operations)}" r="3.5" fill="${one.color}">
          <title>n=${num(point.size)} — ${num(point.operations)} operations, ${bytes(point.bytesAllocated)}</title>
        </circle>`);
      }
    }

    return `<div class="chart-wrap"><svg class="chart" viewBox="0 0 ${width} ${height}"
      width="${width}" height="${height}" role="img"
      aria-label="Statements executed against input size">${parts.join("")}</svg></div>`;
  }

  function formatTick(value) {
    if (value >= 1e6) return (value / 1e6).toFixed(1) + "M";
    if (value >= 1e3) return (value / 1e3).toFixed(1) + "k";
    return Math.round(value).toString();
  }

  /** Samples the fitted curve densely so a quadratic reads as a curve, not a chord. */
  function fitCurve(fit, points, intercept) {
    if (!fit || fit.best === "Unknown" || points.length === 0) return [];

    const top = (fit.ranked || []).find(f => f.class === fit.best);
    if (!top) return [];

    // Call depth is fitted with an intercept (log n + 1 cannot pass through zero), and
    // the engine reports only the slope, so the offset is recovered from the points.
    let offset = 0;
    if (intercept && BASIS[fit.best]) {
      const meanY = points.reduce((sum, p) => sum + p.operations, 0) / points.length;
      const meanB = points.reduce((sum, p) => sum + BASIS[fit.best](p.size), 0) / points.length;
      offset = meanY - top.coefficient * meanB;
    }

    const first = points[0].size;
    const last = points[points.length - 1].size;
    const samples = [];

    for (let i = 0; i <= 40; i++) {
      const size = first + (last - first) * (i / 40);
      let value;

      if (fit.best === "Exponential") {
        // The engine reports the growth base here rather than a multiplier, so the
        // scale is recovered from the last measured point.
        const base = top.coefficient;
        const anchor = points[points.length - 1];
        const scale = anchor.operations / Math.pow(base, anchor.size);
        value = scale * Math.pow(base, size);
      } else {
        const basis = BASIS[fit.best];
        if (!basis) return [];
        value = offset + top.coefficient * basis(size);
      }

      if (!isFinite(value)) return [];
      samples.push({ size, operations: value });
    }

    return samples;
  }

  // Time, memory, and stack depth are plotted the same way, so the same chart answers
  // "how does it grow" for all three.
  const METRICS = {
    time: { label: "Time", axis: "operations", value: p => p.operations, fit: r => r.empirical, intercept: false },
    memory: { label: "Memory allocated", axis: "bytes allocated", value: p => p.bytesAllocated,
      fit: r => r.space && r.space.allocation, intercept: false },
    depth: { label: "Call depth", axis: "call depth", value: p => p.maxRecursionDepth,
      fit: r => r.space && r.space.callDepth, intercept: true }
  };

  function metricBar(results) {
    const available = Object.keys(METRICS).filter(key =>
      key === "time" || results.some(r => r.points.some(p => METRICS[key].value(p) > (key === "depth" ? 1 : 0))));
    if (!available.includes(state.metric)) state.metric = "time";
    if (available.length < 2) return "";
    return `<div class="metrics">${available.map(key =>
      `<button class="metric ${state.metric === key ? "active" : ""}" data-metric="${key}">${METRICS[key].label}</button>`).join("")}</div>`;
  }

  function seriesFor(result, label, color) {
    const metric = METRICS[state.metric || "time"];
    return {
      label, color,
      points: result.points.map(p => ({ size: p.size, operations: metric.value(p), bytesAllocated: p.bytesAllocated })),
      fit: metric.fit(result),
      intercept: metric.intercept
    };
  }

  function growthPanel(primary) {
    const useLog = state.log === true;
    const bar = metricBar([primary]);
    const metric = METRICS[state.metric || "time"];
    const series = [seriesFor(primary, "measured", "var(--hot)")];
    const fitted = series[0].fit;

    return `${bar}<div class="legend">
        <span class="key"><span class="swatch" style="background:var(--hot)"></span>measured</span>
        <span class="key"><span class="swatch" style="background:var(--hot);opacity:.6"></span>fitted ${
          fitted ? (CLASS_LABEL[fitted.best] || fitted.best) : "—"}</span>
        <button class="ghost" id="toggle-log">${useLog ? "Linear axes" : "Log-log axes"}</button>
      </div>
      ${chart(series, useLog, metric.axis)}
      ${pointsTable([{ label: "measured", result: primary }])}
      <div class="footnote">
        On log-log axes every complexity class is a straight line and the slope is the
        exponent — O(n) climbs at 45°, O(n²) twice as steeply — which
        makes neighbouring classes far easier to tell apart than on linear axes.
        ${primary.empirical.ranked && primary.empirical.ranked.length > 1
          ? "Runner-up: " + CLASS_LABEL[primary.empirical.ranked[1].class]
            + " at R²=" + primary.empirical.ranked[1].rSquared.toFixed(4) + "."
          : ""}
      </div>`;
  }

  function comparePanel(response) {
    const primary = response.primary, other = response.comparison;
    if (!other || other.status !== "Completed") {
      return banner("The second solution could not be measured",
        ((other && other.errors) || []).map(escapeHtml).join("<br>") || "No result.", true);
    }

    const useLog = state.log === true;
    const bar = metricBar([primary, other]);
    const metric = METRICS[state.metric || "time"];
    const series = [
      seriesFor(primary, response.label, "var(--hot)"),
      seriesFor(other, response.comparisonLabel, "var(--compare)")
    ];
    const classOf = one => one.fit ? (CLASS_LABEL[one.fit.best] || one.fit.best) : "—";

    const at = result => {
      const last = result.points[result.points.length - 1];
      return last ? last : null;
    };
    const a = at(primary), b = at(other);
    const verdict = a && b && a.size === b.size
      ? `At n=${num(a.size)} the difference is ${num(a.operations)} against ${num(b.operations)} statements — `
        + (b.operations === 0 ? "" : `${(a.operations / b.operations).toFixed(1)}×.`)
      : "";

    return `${bar}<div class="legend">
        <span class="key"><span class="swatch" style="background:var(--hot)"></span>${
          escapeHtml(response.label || "A")} — ${classOf(series[0])}</span>
        <span class="key"><span class="swatch" style="background:var(--compare)"></span>${
          escapeHtml(response.comparisonLabel || "B")} — ${classOf(series[1])}</span>
        <button class="ghost" id="toggle-log">${useLog ? "Linear axes" : "Log-log axes"}</button>
      </div>
      ${chart(series, useLog, metric.axis)}
      ${pointsTable([
        { label: response.label, result: primary },
        { label: response.comparisonLabel, result: other }])}
      <div class="footnote">${escapeHtml(verdict)}
        Both solutions were measured over the same sizes and the same generated data, so the
        gap between the curves is the algorithms and nothing else.</div>`;
  }

  function pointsTable(entries) {
    const sizes = [...new Set(entries.flatMap(e => e.result.points.map(p => p.size)))].sort((a, b) => a - b);

    if (entries.length > 1) {
      const head = entries.map(e => `<th>${escapeHtml(e.label || "")}</th>`).join("");
      const rows = sizes.map(size => `<tr><td>n = ${num(size)}</td>${entries.map(e => {
        const point = e.result.points.find(p => p.size === size);
        return `<td>${point ? num(point.operations) : "\u2014"}</td>`;
      }).join("")}</tr>`).join("");
      return `<table><thead><tr><th>size</th>${head}</tr></thead><tbody>${rows}</tbody></table>`;
    }

    // Operations split into what is on the page (statements) and what is hidden inside
    // library calls, plus the stack depth reached — the raw material for both time and
    // space complexity.
    const points = entries[0].result.points;
    const hasLibrary = points.some(p => p.libraryWork > 0);
    const hasDepth = points.some(p => p.maxRecursionDepth > 1);

    const rows = sizes.map(size => {
      const p = points.find(point => point.size === size);
      if (!p) return "";
      return `<tr><td>n = ${num(size)}</td>
        <td>${num(p.operations)}</td>
        ${hasLibrary ? `<td>${num(p.statements)}</td><td class="hidden-work">${num(p.libraryWork)}</td>` : ""}
        ${hasDepth ? `<td>${num(p.maxRecursionDepth)}</td>` : ""}
        <td>${bytes(p.bytesAllocated)}</td></tr>`;
    }).join("");

    return `<table><thead><tr><th>size</th><th>operations</th>
      ${hasLibrary ? "<th>statements</th><th>library work</th>" : ""}
      ${hasDepth ? "<th>call depth</th>" : ""}
      <th>memory allocated</th></tr></thead><tbody>${rows}</tbody></table>`;
  }

  // ---- events ------------------------------------------------------------------

  function wire() {
    app.querySelectorAll(".tab").forEach(button =>
      button.addEventListener("click", () => { state.tab = button.dataset.tab; render(); }));

    app.querySelectorAll(".expander").forEach(span =>
      span.addEventListener("click", () => {
        const line = Number(span.dataset.line);
        state.expanded.has(line) ? state.expanded.delete(line) : state.expanded.add(line);
        render();
      }));

    app.querySelectorAll(".hot-row").forEach(row =>
      row.addEventListener("click", () => {
        if (vscode) vscode.postMessage({ type: "reveal", line: Number(row.dataset.line) });
      }));

    app.querySelectorAll(".metric").forEach(button =>
      button.addEventListener("click", () => { state.metric = button.dataset.metric; render(); }));

    const toggle = document.getElementById("toggle-log");
    if (toggle) toggle.addEventListener("click", () => { state.log = !state.log; render(); });

    const sizesField = document.getElementById("sizes");
    if (sizesField) sizesField.addEventListener("input", () => { state.sizesEdited = true; });

    app.querySelectorAll('input[data-kind="scales"]').forEach(box =>
      box.addEventListener("change", () => {
        const value = app.querySelector(`input[data-kind="value"][data-index="${box.dataset.index}"]`);
        if (value) value.disabled = box.checked;
      }));

    const rerun = document.getElementById("rerun");
    if (rerun) rerun.addEventListener("click", () => {
      // Start from the engine's own resolved inputs and apply only what the user changed,
      // so nothing here has to reproduce the engine's rules for inferring them.
      const inputs = state.response.primary.inputs.map(spec => ({ ...spec }));
      app.querySelectorAll("[data-kind]").forEach(control => {
        const spec = inputs[Number(control.dataset.index)];
        if (!spec) return;
        switch (control.dataset.kind) {
          case "strategy": spec.strategy = control.value; break;
          case "bool": spec.fixedValue = Number(control.value); break;
          case "scales": spec.scalesWithSize = control.checked; break;
          case "value": spec.fixedValue = parseInt(control.value, 10) || 0; break;
        }
      });

      // Sizes are only sent when typed: left alone, the engine keeps choosing them,
      // including dropping to small sizes for an expensive algorithm.
      let sizes;
      if (state.sizesEdited) {
        sizes = sizesField.value.split(",").map(part => parseInt(part.trim(), 10)).filter(n => Number.isFinite(n) && n >= 0);
        if (sizes.length === 0) sizes = undefined;
      }

      if (!vscode) return;
      app.innerHTML = '<p class="placeholder">Measuring…</p>';
      vscode.postMessage({ type: "rerun", inputs, sizes });
    });
  }

  window.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "loading") { state.response = null; render(); }
    else if (message.type === "result") {
      state = { ...state, response: message.response, request: message.request, expanded: new Set(), sizesEdited: false };
      render();
    } else if (message.type === "error") {
      app.innerHTML = banner("The engine could not be run", escapeHtml(message.message), true);
    }
  });

  // Development entry point: feed a recorded engine response straight in.
  window.__render = (response, request) => {
    state = { ...state, response, request, expanded: new Set() };
    render();
  };
})();
