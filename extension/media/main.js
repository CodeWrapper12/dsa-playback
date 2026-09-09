// Webview UI. Also runnable outside VS Code through window.__render, which is how the
// views get looked at during development without launching an Extension Host.

(function () {
  "use strict";

  const vscode = typeof acquireVsCodeApi === "function" ? acquireVsCodeApi() : null;
  const app = document.getElementById("app");

  const STRATEGIES = [
    ["Random", "Random values from a bounded range — repeats are likely"],
    ["Distinct", "Every value unique — the worst case for duplicate-sensitive code"],
    ["Sorted", "Ascending"],
    ["ReverseSorted", "Descending"],
    ["AllDuplicates", "Every value identical"],
    ["NearlySorted", "Ascending with a few positions swapped"]
  ];

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
      const problems = [...(response.errors || []), ...((primary && primary.errors) || [])];
      app.innerHTML = header(response) + banner(
        "Nothing could be measured",
        problems.length ? problems.map(escapeHtml).join("<br>") : "The engine returned no result.",
        true);
      return;
    }

    const parts = [header(response), verdicts(primary)];

    if (primary.disagreement && primary.disagreementNote) {
      parts.push(banner("The code and the measurement disagree", escapeHtml(primary.disagreementNote)));
    }
    if (primary.sizeNote) parts.push(banner("Sizes adjusted", escapeHtml(primary.sizeNote)));
    if (primary.errors && primary.errors.length) {
      parts.push(banner("Some sizes did not run", primary.errors.map(escapeHtml).join("<br>")));
    }

    parts.push(controls(primary));

    const tabs = [["cost", "Cost per line"], ["growth", "Growth"]];
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

  function verdicts(primary) {
    const measured = primary.empirical;
    const estimate = primary.static;

    const measuredValue = CLASS_LABEL[measured.best] || measured.best;
    const staticValue = CLASS_LABEL[estimate.class] || estimate.class;

    return `<div class="verdicts">
      <div class="verdict">
        <div class="label">Measured</div>
        <div class="value ${measured.best === "Unknown" ? "unknown" : ""}">${measuredValue}</div>
        <div class="note">${escapeHtml(measured.explanation)}</div>
      </div>
      <div class="verdict">
        <div class="label">Read from the code</div>
        <div class="value ${estimate.class === "Unknown" ? "unknown" : ""}">${staticValue}</div>
        <div class="note">${escapeHtml(estimate.reasoning)}</div>
      </div>
    </div>`;
  }

  function controls(primary) {
    const current = (primary.inputs.find(i => i.shape !== "IntScalar" && i.shape !== "BoolScalar")
      || {}).strategy || "Random";
    const sizes = primary.points.map(p => p.size).join(", ");

    const hasCollection = primary.inputs.some(
      i => i.shape !== "IntScalar" && i.shape !== "BoolScalar");

    return `<div class="controls">
      <div class="control">
        <label for="strategy">Input arrangement</label>
        <select id="strategy" ${hasCollection ? "" : "disabled"}>
          ${STRATEGIES.map(([value, description]) =>
            `<option value="${value}" ${value === current ? "selected" : ""} title="${escapeHtml(description)}">${value}</option>`
          ).join("")}
        </select>
      </div>
      <div class="control">
        <label for="sizes">Sizes</label>
        <input type="text" id="sizes" value="${escapeHtml(sizes)}">
      </div>
      <button id="rerun">Re-measure</button>
    </div>`;
  }

  // ---- cost panel --------------------------------------------------------------

  function costPanel(response, primary) {
    const detail = primary.detail;
    if (!detail) return '<p class="placeholder">No per-statement detail was captured.</p>';

    const source = (response.source || "").split(/\r?\n/);
    const byLine = new Map(detail.lines.map(l => [l.line, l]));
    const statementsByLine = new Map();
    for (const statement of detail.statements) {
      if (!statementsByLine.has(statement.line)) statementsByLine.set(statement.line, []);
      statementsByLine.get(statement.line).push(statement);
    }

    const maxHits = Math.max(1, ...detail.lines.map(l => l.hits));
    const largest = primary.points.length ? primary.points[primary.points.length - 1].size : null;

    const rows = source.map((text, index) => {
      const lineNumber = index + 1;
      const cost = byLine.get(lineNumber);
      const executed = Boolean(cost);

      // A log scale keeps a line that ran once visible beside one that ran 500,000
      // times; on a linear scale everything but the hottest line collapses to nothing.
      const width = executed
        ? Math.max(2, Math.round(112 * Math.log(cost.hits + 1) / Math.log(maxHits + 1)))
        : 0;

      const expandable = cost && cost.statementCount > 1;
      const isOpen = state.expanded.has(lineNumber);

      let html = `<div class="row line ${executed ? "" : "dead"}">
        <span class="num">${lineNumber}</span>
        <span class="cost">${executed
          ? `<span class="bar" style="width:${width}px"></span><span class="count">${num(cost.hits)}</span>`
          + (expandable ? ` <span class="expander" data-line="${lineNumber}">${isOpen ? "▾" : "▸"}${cost.statementCount}</span>` : "")
          : ""}</span>
        <span class="bytes ${executed && cost.bytesAllocated ? "" : "zero"}">${executed ? bytes(cost.bytesAllocated) : ""}</span>
        <span class="src">${escapeHtml(text)}</span>
      </div>`;

      if (expandable && isOpen) {
        html += statementsByLine.get(lineNumber).map(statement =>
          `<div class="substatement">
            <span class="marker">col ${statement.column}</span>
            <span class="count">${num(statement.hits)}</span>
            <span class="bytes ${statement.bytesAllocated ? "" : "zero"}">${bytes(statement.bytesAllocated)}</span>
            <span class="snippet">${escapeHtml(statement.snippet)}</span>
          </div>`).join("");
      }

      return html;
    }).join("");

    return `<div class="code-scroll">
        <div class="row header">
          <span class="num">line</span><span>executions</span>
          <span class="bytes">memory</span><span>source</span>
        </div>
        <div class="code">${rows}</div>
      </div>
      <div class="footnote">
        Measured at n = ${largest === null ? "—" : num(largest)}.
        Bars use a logarithmic scale so a line that ran once stays visible next to one that
        ran ${num(maxHits)} times; the numbers beside them are exact.
        A line holding more than one statement shows a count you can expand — the total is
        a sum, and the breakdown is usually the interesting part.
        Dimmed lines never executed.
        <br>${escapeHtml(detail.memoryModel)}
      </div>`;
  }

  // ---- charts ------------------------------------------------------------------

  function chart(series, useLog) {
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
    parts.push(`<text class="axis-title" x="14" y="${top + plotHeight / 2}" text-anchor="middle" transform="rotate(-90 14 ${top + plotHeight / 2})">statements executed</text>`);

    for (const one of series) {
      if (one.fit) {
        const curve = fitCurve(one.fit, one.points);
        if (curve.length) {
          parts.push(`<polyline class="fitline" stroke="${one.color}" points="${
            curve.map(p => `${px(p.size)},${py(p.operations)}`).join(" ")}"/>`);
        }
      }

      parts.push(`<polyline class="series" stroke="${one.color}" points="${
        one.points.map(p => `${px(p.size)},${py(p.operations)}`).join(" ")}"/>`);

      for (const point of one.points) {
        parts.push(`<circle cx="${px(point.size)}" cy="${py(point.operations)}" r="3.5" fill="${one.color}">
          <title>n=${num(point.size)} — ${num(point.operations)} statements, ${bytes(point.bytesAllocated)}</title>
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
  function fitCurve(fit, points) {
    if (!fit || fit.best === "Unknown" || points.length === 0) return [];

    const top = fit.ranked && fit.ranked[0];
    if (!top) return [];

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
        value = top.coefficient * basis(size);
      }

      if (!isFinite(value)) return [];
      samples.push({ size, operations: value });
    }

    return samples;
  }

  function growthPanel(primary) {
    const useLog = state.log === true;
    const series = [{
      label: "measured", color: "var(--hot)", points: primary.points, fit: primary.empirical
    }];

    return `<div class="legend">
        <span class="key"><span class="swatch" style="background:var(--hot)"></span>measured</span>
        <span class="key"><span class="swatch" style="background:var(--hot);opacity:.6"></span>fitted ${
          CLASS_LABEL[primary.empirical.best] || primary.empirical.best}</span>
        <button class="ghost" id="toggle-log">${useLog ? "Linear axes" : "Log-log axes"}</button>
      </div>
      ${chart(series, useLog)}
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
    const series = [
      { label: response.label, color: "var(--hot)", points: primary.points, fit: primary.empirical },
      { label: response.comparisonLabel, color: "var(--compare)", points: other.points, fit: other.empirical }
    ];

    const at = result => {
      const last = result.points[result.points.length - 1];
      return last ? last : null;
    };
    const a = at(primary), b = at(other);
    const verdict = a && b && a.size === b.size
      ? `At n=${num(a.size)} the difference is ${num(a.operations)} against ${num(b.operations)} statements — `
        + (b.operations === 0 ? "" : `${(a.operations / b.operations).toFixed(1)}×.`)
      : "";

    return `<div class="legend">
        <span class="key"><span class="swatch" style="background:var(--hot)"></span>${
          escapeHtml(response.label || "A")} — ${CLASS_LABEL[primary.empirical.best]}</span>
        <span class="key"><span class="swatch" style="background:var(--compare)"></span>${
          escapeHtml(response.comparisonLabel || "B")} — ${CLASS_LABEL[other.empirical.best]}</span>
        <button class="ghost" id="toggle-log">${useLog ? "Linear axes" : "Log-log axes"}</button>
      </div>
      ${chart(series, useLog)}
      ${pointsTable([
        { label: response.label, result: primary },
        { label: response.comparisonLabel, result: other }])}
      <div class="footnote">${escapeHtml(verdict)}
        Both solutions were measured over the same sizes and the same generated data, so the
        gap between the curves is the algorithms and nothing else.</div>`;
  }

  function pointsTable(entries) {
    const sizes = [...new Set(entries.flatMap(e => e.result.points.map(p => p.size)))]
      .sort((a, b) => a - b);

    const head = entries.length > 1
      ? entries.map(e => `<th>${escapeHtml(e.label || "")}</th>`).join("")
      : "<th>statements</th><th>memory</th>";

    const rows = sizes.map(size => {
      const cells = entries.length > 1
        ? entries.map(e => {
            const point = e.result.points.find(p => p.size === size);
            return `<td>${point ? num(point.operations) : "—"}</td>`;
          }).join("")
        : (() => {
            const point = entries[0].result.points.find(p => p.size === size);
            return `<td>${point ? num(point.operations) : "—"}</td>
                    <td>${point ? bytes(point.bytesAllocated) : "—"}</td>`;
          })();
      return `<tr><td>n = ${num(size)}</td>${cells}</tr>`;
    }).join("");

    return `<table><thead><tr><th>size</th>${head}</tr></thead><tbody>${rows}</tbody></table>`;
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

    const toggle = document.getElementById("toggle-log");
    if (toggle) toggle.addEventListener("click", () => { state.log = !state.log; render(); });

    const rerun = document.getElementById("rerun");
    if (rerun) rerun.addEventListener("click", () => {
      const strategy = document.getElementById("strategy").value;
      const sizes = document.getElementById("sizes").value
        .split(",").map(part => parseInt(part.trim(), 10)).filter(n => Number.isFinite(n) && n > 0);

      if (!vscode) return;
      app.innerHTML = '<p class="placeholder">Measuring…</p>';
      vscode.postMessage({ type: "rerun", strategy, sizes: sizes.length ? sizes : undefined });
    });
  }

  window.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "loading") { state.response = null; render(); }
    else if (message.type === "result") {
      state = { ...state, response: message.response, request: message.request, expanded: new Set() };
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
