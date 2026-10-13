// Reads and instruments a JavaScript or TypeScript solution for DSA Playback, with the
// TypeScript compiler's own parser, so both languages are read exactly as Node and tsc
// read them. One JSON request in on stdin, one JSON answer out on stdout:
//
//   describe — the functions, classes and parameter types, the comment lines examples are
//              read from, and a reading of the entry's time complexity from its shape;
//   build    — the program to run: every statement counted, a step-through build also
//              capturing the variables in scope, calls tracked for depth, library calls
//              that scan or copy charged for their work. Everything inserted stays on the
//              line it belongs to, so the tables of statements carry the student's lines.
"use strict";

const fs = require("fs");

const MAX_SNIPPET = 80;
const NODE_CLASSES = new Set(["TreeNode", "ListNode"]);
const PROVIDED = ["TreeNode", "ListNode", "MinPriorityQueue", "MaxPriorityQueue", "PriorityQueue", "Queue", "Deque"];

function main() {
  const request = JSON.parse(fs.readFileSync(0, "utf8"));
  const ts = require(request.typescript);
  let answer;
  try {
    answer = request.mode === "build" ? build(ts, request) : describe(ts, request);
  } catch (error) {
    answer = { internal: String(error && error.stack || error) };
  }
  process.stdout.write(JSON.stringify(answer));
}

// ---- reading ------------------------------------------------------------------------------------

function parse(ts, request) {
  const typescript = request.language === "ts";
  const file = ts.createSourceFile(typescript ? "solution.ts" : "solution.js", request.source,
    ts.ScriptTarget.Latest, true, typescript ? ts.ScriptKind.TS : ts.ScriptKind.JS);
  const diagnostic = (file.parseDiagnostics || [])[0];
  const problem = diagnostic
    ? `line ${file.getLineAndCharacterOfPosition(diagnostic.start).line + 1}: ${ts.flattenDiagnosticMessageText(diagnostic.messageText, " ")}`
    : null;
  return { file, problem, typescript };
}

const lineOf = (file, position) => file.getLineAndCharacterOfPosition(position).line + 1;

/** Every comment line, cleaned of its markers, with its line number: the shared example reader's input. */
function commentLines(ts, file) {
  const text = file.text;
  const seen = new Set();
  const lines = [];
  const take = ranges => {
    for (const range of ranges || []) {
      if (seen.has(range.pos)) continue;
      seen.add(range.pos);
      const first = lineOf(file, range.pos);
      text.slice(range.pos, range.end).split("\n").forEach((raw, offset) => {
        const cleaned = raw.trim().replace(/^\/\/+/, "").replace(/^\/\*+/, "").replace(/\*+\/$/, "").replace(/^\*+/, "").trim();
        lines.push({ text: cleaned, line: first + offset });
      });
    }
  };
  const visit = node => {
    take(ts.getLeadingCommentRanges(text, node.pos));
    take(ts.getTrailingCommentRanges(text, node.end));
    ts.forEachChild(node, visit);
  };
  visit(file);
  take(ts.getLeadingCommentRanges(text, file.endOfFileToken.pos));
  return lines.sort((a, b) => a.line - b.line);
}

/** A parameter's type: its annotation in TypeScript, its JSDoc @param type in JavaScript. */
function typeOf(ts, file, parameter) {
  if (parameter.type) return parameter.type.getText(file);
  const tag = ts.getJSDocParameterTags(parameter)[0];
  return tag && tag.typeExpression ? tag.typeExpression.type.getText(file) : "";
}

function returnTypeOf(ts, file, fn) {
  if (fn.type) return fn.type.getText(file);
  const tag = ts.getJSDocReturnTag ? ts.getJSDocReturnTag(fn) : null;
  return tag && tag.typeExpression ? tag.typeExpression.type.getText(file) : "";
}

const isFunctionValue = (ts, node) => node && (ts.isFunctionExpression(node) || ts.isArrowFunction(node));
const unwrap = (ts, node) => { while (node && ts.isParenthesizedExpression(node)) node = node.expression; return node; };

function parameters(ts, file, fn) {
  return fn.parameters.map(p => ({ name: p.name.getText(file), type: typeOf(ts, file, p) }));
}

/**
 * The functions and classes at the top level: function declarations, `var f = function`,
 * classes, and the prototype style LeetCode's JavaScript uses for design problems —
 * `var LRUCache = function(capacity)` with `LRUCache.prototype.get = function(key)`.
 */
function members(ts, file) {
  const functions = [];
  const classes = [];
  const declared = new Set();
  const prototypes = new Map();

  for (const statement of file.statements) {
    if (ts.isExpressionStatement(statement) && ts.isBinaryExpression(statement.expression) &&
        statement.expression.operatorToken.kind === ts.SyntaxKind.EqualsToken) {
      const left = statement.expression.left;
      const right = unwrap(ts, statement.expression.right);
      if (ts.isPropertyAccessExpression(left) && ts.isPropertyAccessExpression(left.expression) &&
          left.expression.name.text === "prototype" && ts.isIdentifier(left.expression.expression) && isFunctionValue(ts, right)) {
        const owner = left.expression.expression.text;
        if (!prototypes.has(owner)) prototypes.set(owner, []);
        prototypes.get(owner).push({ name: left.name.text, node: right, line: lineOf(file, statement.getStart(file)) });
      }
    }
  }

  const addFunction = (name, node, line) => {
    declared.add(name);
    if (prototypes.has(name)) {
      classes.push({
        name, line, style: "prototype", constructor: parameters(ts, file, node),
        methods: prototypes.get(name).map(m => ({ name: m.name, line: m.line, parameters: parameters(ts, file, m.node), returns: returnTypeOf(ts, file, m.node) }))
      });
      return;
    }
    functions.push({ name, line, parameters: parameters(ts, file, node), returns: returnTypeOf(ts, file, node) });
  };

  for (const statement of file.statements) {
    if (ts.isFunctionDeclaration(statement) && statement.name && statement.body) {
      addFunction(statement.name.text, statement, lineOf(file, statement.name.getStart(file)));
    } else if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        const value = unwrap(ts, declaration.initializer);
        if (ts.isIdentifier(declaration.name)) declared.add(declaration.name.text);
        if (ts.isIdentifier(declaration.name) && isFunctionValue(ts, value))
          addFunction(declaration.name.text, value, lineOf(file, declaration.name.getStart(file)));
      }
    } else if (ts.isClassDeclaration(statement) && statement.name) {
      declared.add(statement.name.text);
      const constructor = statement.members.find(m => ts.isConstructorDeclaration(m) && m.body);
      const methods = statement.members.filter(m => ts.isMethodDeclaration(m) && m.body && ts.isIdentifier(m.name) &&
        !(ts.getCombinedModifierFlags(m) & (ts.ModifierFlags.Private | ts.ModifierFlags.Static | ts.ModifierFlags.Protected)));
      classes.push({
        name: statement.name.text,
        line: lineOf(file, statement.name.getStart(file)),
        style: "class",
        constructor: constructor ? parameters(ts, file, constructor) : null,
        methods: methods.map(m => ({ name: m.name.text, line: lineOf(file, m.name.getStart(file)), parameters: parameters(ts, file, m), returns: returnTypeOf(ts, file, m) }))
      });
    } else if (ts.isEnumDeclaration(statement) && statement.name) {
      declared.add(statement.name.text);
    }
  }
  return { functions, classes: classes.filter(c => !NODE_CLASSES.has(c.name)), declared: [...declared] };
}

function describe(ts, request) {
  const { file, problem } = parse(ts, request);
  const { functions, classes } = members(ts, file);
  const answer = {
    problem,
    comments: commentLines(ts, file),
    functions: functions.filter(f => !NODE_CLASSES.has(f.name)),
    classes,
    lineCount: file.getLineStarts().length
  };
  if (request.entry && !problem) {
    const target = findFunction(ts, file, request.entry);
    if (target) answer.time = estimate(ts, file, target, request.entry);
  }
  return answer;
}

function findFunction(ts, file, name) {
  for (const statement of file.statements) {
    if (ts.isFunctionDeclaration(statement) && statement.name && statement.name.text === name) return statement;
    if (ts.isVariableStatement(statement))
      for (const d of statement.declarationList.declarations)
        if (ts.isIdentifier(d.name) && d.name.text === name && isFunctionValue(ts, unwrap(ts, d.initializer))) return unwrap(ts, d.initializer);
  }
  return null;
}

// ---- reading the time complexity from the code's shape ----------------------------------------

const SCANS = new Set(["includes", "indexOf", "lastIndexOf", "shift", "unshift", "splice", "slice", "concat", "reverse", "join", "find", "findIndex", "filter", "map", "some", "every"]);

function estimate(ts, file, fn, name) {
  const own = shape(ts, file, fn, name);
  const body = fn.body ? fn.body.getText(file) : "";
  // A helper the entry calls — a dfs declared inside it or beside it — is where the work often is.
  const helpers = [];
  const visit = node => {
    if ((ts.isFunctionDeclaration(node) || ts.isVariableDeclaration(node)) && node.name && ts.isIdentifier(node.name) && node.name.text !== name) {
      const value = ts.isFunctionDeclaration(node) ? node : unwrap(ts, node.initializer);
      if (value && (ts.isFunctionDeclaration(value) || isFunctionValue(ts, value)) && new RegExp(`\\b${node.name.text}\\s*\\(`).test(body))
        helpers.push(shape(ts, file, value, node.name.text));
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  const deeper = helpers.filter(h => rank(h.class) > rank(own.class)).sort((a, b) => rank(b.class) - rank(a.class))[0];
  return deeper ? { ...deeper, reasoning: `In a helper it calls: ${deeper.reasoning}` } : own;
}

const RANKS = ["Constant", "Logarithmic", "Linear", "Linearithmic", "Quadratic", "Cubic", "Exponential"];
const rank = c => RANKS.indexOf(c);

function shape(ts, file, fn, name) {
  const { linear, log } = depth(ts, file, fn.body);
  const body = fn.body ? fn.body.getText(file) : "";
  const selfCalls = (body.match(new RegExp(`(?<![\\w.])${name}\\s*\\(`, "g")) || []).length;
  const make = (cls, reasoning) => ({ class: cls, reasoning, linearLoopDepth: linear, logLoopDepth: log, recursiveSelfCalls: selfCalls });
  const withLoop = linear > 0 ? "Quadratic" : "Linear";

  if (selfCalls >= 2 && new RegExp(`${name}\\s*\\([^)]*\\.(left|right|next)\\b`).test(body))
    return make(withLoop, `${name} calls itself on a node's children, so each node is visited once.`);
  if (selfCalls >= 2 && /\w+(\[[^\]]+\])+\s*=[^=]|\b(visited|seen)\w*\s*\.\s*add\s*\(/.test(body))
    return make(withLoop, `${name} marks what it has visited before calling itself, so each one is visited once.`);
  if (selfCalls >= 2 && /\b(memo|cache|dp|seen)\w*\s*(\.(get|has|set)\b|\[)/.test(body))
    return make(withLoop, `${name} calls itself ${selfCalls} times, but stores each answer and returns it when asked again, so each value is worked out once.`);
  if (selfCalls >= 2)
    return make("Exponential", `${name} calls itself ${selfCalls} times in its body; without memoisation that branches at every call.`);
  if (selfCalls === 1)
    return make(withLoop, `${name} calls itself once per call${linear > 0 ? ", with a loop in each call" : ""}.`);

  const cls = linear === 0 && log === 0 ? "Constant" : linear === 0 ? "Logarithmic" : linear === 1 && log === 0 ? "Linear"
    : linear === 1 ? "Linearithmic" : linear === 2 ? "Quadratic" : "Cubic";
  return make(cls, linear === 0 && log === 0 ? "No loops or recursion."
    : `Loops nest ${linear} deep over the input${log > 0 ? `, and ${log} loop${log === 1 ? "" : "s"} halve what is left` : ""}.`);
}

/** Whether a name is made a Set or Map in the file — whose has and get are not scans. */
const hashed = (file, name) => new RegExp(`\\b${name.replace(/[^\w$]/g, "")}\\s*=\\s*new\\s+(Set|Map|WeakMap|WeakSet)\\b`).test(file.text);

function depth(ts, file, node) {
  if (!node) return { linear: 0, log: 0 };
  let best = { linear: 0, log: 0 };
  ts.forEachChild(node, child => {
    // A function inside is a helper, read on its own when it is called.
    if (ts.isFunctionLike(child)) return;
    const d = depth(ts, file, child);
    if (d.linear * 10 + d.log > best.linear * 10 + best.log) best = d;
  });
  let { linear, log } = best;
  if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression) && SCANS.has(node.expression.name.text) &&
      !hashed(file, node.expression.expression.getText(file).split(".").pop()))
    linear = Math.max(linear, 1);
  const loop = ts.isForStatement(node) || ts.isWhileStatement(node) || ts.isDoStatement(node) || ts.isForOfStatement(node) || ts.isForInStatement(node);
  if (!loop) return { linear, log };
  const header = ts.isForStatement(node) ? file.text.slice(node.getStart(file), node.statement.getStart(file))
    : ts.isWhileStatement(node) || ts.isDoStatement(node) ? node.expression.getText(file) : "";
  // Halving: a header that divides or shifts, or a loop that works out a middle — `left < right`
  // alone is as often two pointers walking inward, which is linear.
  const bodyText = node.statement ? node.statement.getText(file) : "";
  let halves = /(\/=\s*2\b|\*=\s*2\b|>>=\s*1\b|<<=\s*1\b)/.test(header) ||
    (/\bmid\b|\/\s*2\b|>>\s*1\b/.test(header + bodyText) && /\b(lo|left|l|low|start)\s*<=?\s*(hi|right|r|high|end)\b/.test(header));
  return halves ? { linear, log: log + 1 } : { linear: linear + 1, log };
}

// ---- building -----------------------------------------------------------------------------------

/** Library calls charged for their work, by what they do to the object they are called on. */
const CHARGED = {
  includes: "scan", indexOf: "scan", lastIndexOf: "scan",
  shift: "front", unshift: "front", splice: "splice",
  slice: "copy", concat: "copy", join: "copy", reverse: "copy", fill: "copy", toString: "copy", split: "copy",
  repeat: "result", substring: "copy", substr: "copy", toReversed: "copy", toSorted: "sort", flat: "copy",
  sort: "sort"
};

/** Calls that hand back something new — an array, a string — whose size is charged to the line. */
const MAKES = new Set(["slice", "map", "filter", "concat", "split", "join", "repeat", "substring", "substr", "toUpperCase",
  "toLowerCase", "trim", "flat", "flatMap", "toSorted", "toReversed", "toSpliced", "padStart", "padEnd", "replace", "replaceAll",
  "keys", "values", "entries", "from", "of", "fromCharCode", "toString"]);

/** Calls that grow the collection they are made on, charged for what they add. */
const GROWS = new Set(["push", "unshift", "set", "add", "splice", "enqueue", "pushBack", "pushFront"]);

const COST = { scan: "O(n)", front: "O(n)", splice: "O(n − index)", copy: "O(n)", result: "O(result)", sort: "O(n log n)", spread: "O(n)", plus: "O(n)" };

function build(ts, request) {
  const { file, problem, typescript } = parse(ts, request);
  if (problem) return { problem };
  const { declared } = members(ts, file);
  const stepping = !!request.stepping;
  const text = file.text;

  // Insertions, each an opening or a closing at a position, ranked by how far out it nests:
  // a statement's counter (0) holds a function body's (1), which holds a return's value (2),
  // which holds a charged library call (3). Opened outermost first, closed innermost first.
  const edits = [];
  const statements = [];
  const variables = [];      // per statement: [names, roles]
  const sites = [];
  const open = (position, value, rank, remove = 0) => edits.push({ position, value, rank, close: false, seq: edits.length, remove });
  const close = (position, value, rank) => edits.push({ position, value, rank, close: true, seq: edits.length, remove: 0 });

  const isCallback = fn => fn.parent && (ts.isCallExpression(fn.parent) || ts.isNewExpression(fn.parent)) &&
    (fn.parent.arguments || []).includes(fn);
  const isFrame = fn => (ts.isFunctionDeclaration(fn) || ts.isMethodDeclaration(fn) || ts.isConstructorDeclaration(fn) ||
    ts.isFunctionExpression(fn) || ts.isArrowFunction(fn) || ts.isGetAccessor(fn) || ts.isSetAccessor(fn)) && !isCallback(fn);
  const enclosingFunction = node => { for (let p = node.parent; p; p = p.parent) if (ts.isFunctionLike(p) && p.body) return p; return null; };
  const frameOf = node => { for (let p = enclosingFunction(node); p; p = enclosingFunction(p)) if (isFrame(p)) return p; return null; };
  const functionName = fn => {
    if (!fn) return "";
    if (fn.name && ts.isIdentifier(fn.name)) return fn.name.text;
    if (ts.isConstructorDeclaration(fn)) return fn.parent.name ? fn.parent.name.text : "constructor";
    const p = fn.parent;
    if (p && ts.isVariableDeclaration(p) && ts.isIdentifier(p.name)) return p.name.text;
    if (p && ts.isPropertyAssignment(p)) return p.name.getText(file);
    if (p && ts.isBinaryExpression(p) && ts.isPropertyAccessExpression(p.left)) {
      // LRUCache.prototype.get = function — named get; the constructor function is named after its class.
      return p.left.name.text;
    }
    return isCallback(fn) ? functionName(frameOf(fn)) : "(anonymous)";
  };
  const usesThis = fn => ts.isMethodDeclaration(fn) || ts.isConstructorDeclaration(fn) ||
    (ts.isFunctionExpression(fn) && fn.parent && ts.isBinaryExpression(fn.parent) && /\.prototype\./.test(fn.parent.left.getText(file))) ||
    (ts.isFunctionExpression(fn) && request.design && functionName(fn) === request.design && fn.parent && ts.isVariableDeclaration(fn.parent));

  // ---- the variables in scope at a statement, declared before it ----
  const bindingNames = (name, out) => {
    if (ts.isIdentifier(name)) out.push(name);
    else if (ts.isObjectBindingPattern(name) || ts.isArrayBindingPattern(name))
      for (const element of name.elements) if (!ts.isOmittedExpression(element)) bindingNames(element.name, out);
  };
  const declaredIn = node => {
    const found = [];
    const list = l => { for (const d of l.declarations) if (!(d.initializer && isFunctionValue(ts, unwrap(ts, d.initializer)))) bindingNames(d.name, found); };
    if (ts.isFunctionLike(node)) for (const p of node.parameters) bindingNames(p.name, found);
    if ((ts.isForStatement(node) && node.initializer && ts.isVariableDeclarationList(node.initializer)) ||
        ((ts.isForOfStatement(node) || ts.isForInStatement(node)) && ts.isVariableDeclarationList(node.initializer)))
      list(node.initializer);
    if (ts.isCatchClause(node) && node.variableDeclaration) bindingNames(node.variableDeclaration.name, found);
    const body = ts.isBlock(node) || ts.isSourceFile(node) || ts.isCaseClause(node) || ts.isDefaultClause(node) ? node.statements : null;
    if (body) for (const s of body) if (ts.isVariableStatement(s)) list(s.declarationList);
    return found;
  };
  const roleOf = (fn, name) => {
    const body = fn && fn.body ? fn.body.getText(file) : "";
    const call = m => new RegExp(`(?<![\\w.])${name}\\s*\\.\\s*${m}\\s*\\(`).test(body);
    if (call("shift")) return 2;
    if (call("push") && call("pop")) return 1;
    return 0;
  };
  function scope(statement) {
    // Innermost scope first, so a name shadows the same name further out; shown outermost
    // first, each scope's names in the order they are declared.
    const levels = [];
    const seen = new Set();
    const frame = frameOf(statement);
    for (let node = statement.parent; node && !ts.isSourceFile(node); node = node.parent) {
      const level = [];
      for (const id of declaredIn(node)) {
        if (id.end > statement.getStart(file) || seen.has(id.text) || id.text.startsWith("__d")) continue;
        seen.add(id.text);
        level.push(id.text);
      }
      levels.push(level);
    }
    const names = levels.reverse().flat();
    if (frame && usesThis(frame)) names.unshift("this");
    return { names, roles: names.map(n => roleOf(frame, n)) };
  }

  // ---- a counter before every statement in a function ----
  const counted = statement => !(ts.isFunctionDeclaration(statement) || ts.isClassDeclaration(statement) || ts.isBlock(statement) ||
    ts.isEmptyStatement(statement) || ts.isInterfaceDeclaration(statement) || ts.isTypeAliasDeclaration(statement) ||
    ts.isEnumDeclaration(statement) || ts.isModuleDeclaration(statement) || ts.isImportDeclaration(statement));
  const single = statement => {
    const p = statement.parent;
    return p && !ts.isBlock(p) && !ts.isSourceFile(p) && !ts.isCaseClause(p) && !ts.isDefaultClause(p) && !ts.isModuleBlock(p);
  };

  function statementInfo(node, snippetNode) {
    const id = statements.length;
    const start = snippetNode.getStart(file);
    const position = file.getLineAndCharacterOfPosition(start);
    const firstLine = text.slice(start, snippetNode.end).split("\n")[0].trim();
    let snippet = firstLine.replace(/\s+/g, " ").replace(/\{$/, "").trim();
    if (snippet.length > MAX_SNIPPET) snippet = snippet.slice(0, MAX_SNIPPET - 1) + "…";
    const frame = enclosingFunction(node);
    const variables_ = stepping ? scope(node) : { names: [], roles: [] };
    statements.push({ id, line: position.line + 1, column: position.character + 1, length: firstLine.length, snippet,
      method: functionName(frame), variables: variables_.names });
    variables.push([variables_.names, variables_.roles]);
    return id;
  }

  const counter = (id, names) => stepping
    ? `__d.s(${id})&&__d.r(()=>[${names.join(",")}]);`
    : `__d.h(${id});`;

  function countStatement(statement) {
    const id = statementInfo(statement, statement);
    const start = statement.getStart(file);
    const wrap = single(statement);
    open(start, (wrap ? "{" : "") + counter(id, variables[id][0]), 0);
    if (wrap) close(statement.end, "}", 0);
  }

  // ---- frames: depth for recursion, and what each call returned ----
  function frame(fn) {
    if (!fn.body) return;
    if (ts.isBlock(fn.body)) {
      // An empty body has nothing to track, and its opening and closing would share a place.
      if (!isFrame(fn) || fn.body.statements.length === 0) return;
      open(fn.body.getStart(file) + 1, "__d.e();try{", -1);
      close(fn.body.end - 1, "}finally{__d.x();}", -1);
      return;
    }
    // An expression body — x => x * 2 — is one statement, and a call when it is not a callback.
    const expression = fn.body;
    const id = statementInfo(expression, expression);
    const value = stepping && isFrame(fn) ? ["__d.ret(", ")"] : ["", ""];
    const enter = isFrame(fn) ? "__d.e();try{" : "";
    const leave = isFrame(fn) ? "}finally{__d.x();}" : "";
    open(expression.getStart(file), `{${enter}${counter(id, variables[id][0])}return ${value[0]}`, 1);
    close(expression.end, `${value[1]};${leave}}`, 1);
  }

  // ---- memory: an expression that creates an array, object, map, set or string ----
  function creates(node) {
    if ((ts.isArrayLiteralExpression(node) || ts.isObjectLiteralExpression(node)) && !isPattern(ts, node)) return true;
    if (ts.isNewExpression(node) || ts.isTemplateExpression(node)) return true;
    if (!ts.isCallExpression(node)) return false;
    const callee = node.expression;
    if (ts.isIdentifier(callee)) return /^(Array|String)$/.test(callee.text);
    return ts.isPropertyAccessExpression(callee) && MAKES.has(callee.name.text);
  }

  // ---- library calls charged for their work ----
  function site(node, kind, member) {
    const id = sites.length;
    const position = file.getLineAndCharacterOfPosition(node.getStart(file));
    sites.push({ site: id, line: position.line + 1, column: position.character + 1, member, cost: COST[kind], kind });
    return id;
  }

  function visit(node) {
    if (ts.isFunctionLike(node) && node.body) frame(node);

    if (node.kind >= ts.SyntaxKind.FirstStatement && node.kind <= ts.SyntaxKind.LastStatement &&
        !(node.parent && ts.isLabeledStatement(node.parent))) {
      // A labelled loop is counted at its label: braces between the two would break `continue outer`.
      const fn = enclosingFunction(node);
      if (fn && counted(node)) countStatement(node);
    }

    if (ts.isReturnStatement(node) && node.expression && stepping) {
      const fn = enclosingFunction(node);
      if (fn && isFrame(fn)) {
        open(node.expression.getStart(file), "__d.ret(", 2);
        close(node.expression.end, ")", 2);
      }
    }

    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression) && enclosingFunction(node)) {
      const method = node.expression.name.text;
      const kind = CHARGED[method];
      // sort with a comparator: the comparisons are counted where they run, in the comparator.
      if (kind && !((method === "sort" || method === "toSorted") && node.arguments.length > 0)) {
        const receiver = node.expression.expression;
        const id = site(node.expression.name, kind, `.${method}()`);
        open(receiver.getStart(file), `__d.k(${id},`, 3);
        close(receiver.end, ")", 3);
        if (kind === "splice" && node.arguments.length > 0) {
          open(node.arguments[0].getStart(file), `__d.i(${id},`, 3);
          close(node.arguments[0].end, ")", 3);
        }
      }
    }

    if (ts.isSpreadElement(node) && enclosingFunction(node)) {
      const id = site(node, "spread", "...spread");
      open(node.expression.getStart(file), `__d.k(${id},`, 3);
      close(node.expression.end, ")", 3);
    }

    if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && /^(Set|Map)$/.test(node.expression.text) &&
        node.arguments && node.arguments.length > 0 && enclosingFunction(node)) {
      const id = site(node, "spread", `new ${node.expression.text}(…)`);
      open(node.arguments[0].getStart(file), `__d.k(${id},`, 3);
      close(node.arguments[0].end, ")", 3);
    }

    // ---- memory: what each line creates ----
    if (enclosingFunction(node) && creates(node)) {
      open(node.getStart(file), "__d.a(", 2.4);
      close(node.end, ")", 2.4);
    }
    if (enclosingFunction(node) && ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression) &&
        GROWS.has(node.expression.name.text)) {
      const receiver = node.expression.expression;
      open(node.getStart(file), "__d.d(", 2.6);
      close(node.end, ")", 2.6);
      open(receiver.getStart(file), "__d.b(", 2.8);
      close(receiver.end, ")", 2.8);
    }

    if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusEqualsToken && enclosingFunction(node) &&
        (ts.isIdentifier(node.left) || (ts.isPropertyAccessExpression(node.left) && /^[\w.$]+$/.test(node.left.getText(file))))) {
      // s += x: a new string as long as both, when it is a string.
      const id = site(node.operatorToken, "plus", "string +=");
      const left = node.left.getText(file);
      open(node.operatorToken.getStart(file), `=__d.p(${id},${left},`, 3, 2);
      close(node.right.end, ")", 3);
    }

    ts.forEachChild(node, visit);
  }
  visit(file);

  // Applied from the end, so earlier positions stay valid. At one position, what is applied
  // last ends up first: openings go in before closings (a closing belongs to what came
  // before), innermost openings first, outermost closings first.
  let source = text;
  edits.sort((a, b) => b.position - a.position ||
    (a.close === b.close ? 0 : a.close ? 1 : -1) ||
    (a.close ? a.rank - b.rank || a.seq - b.seq : b.rank - a.rank || b.seq - a.seq));
  for (const edit of edits)
    source = source.slice(0, edit.position) + edit.value + source.slice(edit.position + edit.remove);

  const provided = PROVIDED.filter(name => !declared.includes(name)).map(name => `var ${name}=__d.${name};`).join("");
  const setup = `__d.setup(${JSON.stringify({ count: statements.length, sites: sites.map(s => s.kind), variables })});`;
  // On the student's first line, so every line after it keeps its number.
  source = `const __d=require(${JSON.stringify(request.runtime)});${provided}${setup}` + source;

  source += "\n" + driver(ts, file, request);
  // `raw` keeps TypeScript as TypeScript, to see what was inserted.
  if (typescript && !request.raw) {
    source = ts.transpileModule(source, {
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, removeComments: false }
    }).outputText;
  }
  return { program: source, statements, sites: sites.map(({ kind, ...rest }) => rest) };
}

/** An array or object written on the left of `=`, or a for-of's target: not created, but assigned to. */
function isPattern(ts, node) {
  let n = node;
  while (n.parent && (ts.isArrayLiteralExpression(n.parent) || ts.isObjectLiteralExpression(n.parent) || ts.isSpreadElement(n.parent) ||
         ts.isPropertyAssignment(n.parent) || ts.isShorthandPropertyAssignment(n.parent) || ts.isParenthesizedExpression(n.parent))) n = n.parent;
  const p = n.parent;
  return !!p && ((ts.isBinaryExpression(p) && p.operatorToken.kind === ts.SyntaxKind.EqualsToken && p.left === n) ||
    ((ts.isForOfStatement(p) || ts.isForInStatement(p)) && p.initializer === n));
}

/** What runs the entry: one function on the inputs, or a design problem's calls on its class. */
function driver(ts, file, request) {
  const { functions, classes } = members(ts, file);
  if (request.design) {
    const type = classes.find(c => c.name === request.design);
    if (!type) throw new Error(`No class ${request.design}`);
    const methods = type.methods.map(m => [m.name, m.parameters.length]);
    return `__d.design(${type.name},${JSON.stringify(type.name)},${(type.constructor || []).length},${JSON.stringify(methods)});`;
  }
  const entry = functions.find(f => f.name === request.entry);
  if (!entry) throw new Error(`No function ${request.entry}`);
  const kinds = entry.parameters.map(p => /\bTreeNode\b/.test(p.type) ? "tree" : /\bListNode\b/.test(p.type) ? "list" : "value");
  const isVoid = /^(void|undefined)$/.test(entry.returns.trim());
  return `__d.run(${JSON.stringify(kinds)},(a)=>${entry.name}(...a),${isVoid});`;
}

main();
