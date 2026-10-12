"""DSA Visualizer's Python runner.

The C# engine drives Python files through this script: it sends one JSON request on
stdin and reads one JSON answer from stdout. Everything students see — input values,
curve fitting, explanations, the panel — is shared with C#; this script does only what
needs a Python interpreter:

  describe   the functions in a file, their parameters, examples written as calls, the
             comment lines, and a reading of the code for complexity
  run        one instrumented run: per-statement counts, hidden library work, memory
  step       one recorded run for the run view: every step's variables and objects

Instrumentation works like the C# engine's: the student's code is parsed, a counter call
is put before every statement, library calls with hidden cost are charged as they run,
and every function is wrapped to track call depth. Nothing outside the standard library
is used, and the student's code never leaves the machine.

Requires Python 3.9 or later.
"""

import ast
import builtins
import bisect
import collections
import heapq
import io
import json
import math
import sys
import threading
import time
import tokenize
import tracemalloc
import typing

MIN_VERSION = (3, 9)

# ---- limits ----------------------------------------------------------------------------

DEFAULT_BUDGET = 1_000_000
DEFAULT_DEPTH_LIMIT = 10_000
MAX_ITEMS = 1_000
MAX_OBJECTS = 300
MAX_PATCH = 8
MIN_PATCHABLE = 16
MAX_TEXT = 80
MAX_CHAR_ITEMS = 40
MAX_FORMAT_NODES = 10_000
SNIPPET_LENGTH = 80
THREAD_STACK = 512 * 1024 * 1024

SOLUTION_FILE = "<solution>"

# What LeetCode's Python environment provides without an import, so a solution pasted
# from LeetCode runs as it does there.
LEETCODE_NAMES = {
    "List": typing.List, "Optional": typing.Optional, "Dict": typing.Dict, "Set": typing.Set,
    "Tuple": typing.Tuple, "Deque": typing.Deque, "collections": collections, "heapq": heapq,
    "math": math, "bisect": bisect, "deque": collections.deque, "defaultdict": collections.defaultdict,
    "Counter": collections.Counter, "inf": math.inf,
}


class TreeNode:
    """LeetCode's TreeNode, used when the file does not define its own."""

    def __init__(self, val=0, left=None, right=None):
        self.val = val
        self.left = left
        self.right = right


class ListNode:
    """LeetCode's ListNode, used when the file does not define its own."""

    def __init__(self, val=0, next=None):
        self.val = val
        self.next = next


# ---- stopping a run --------------------------------------------------------------------
# BaseException, not Exception: a solution's own `except Exception` must not swallow them.

class BudgetExceeded(BaseException):
    pass


class DepthExceeded(BaseException):
    pass


# ---- the request ------------------------------------------------------------------------

def main():
    if sys.version_info < MIN_VERSION:
        answer({"error": "python-version", "message":
                f"Python {MIN_VERSION[0]}.{MIN_VERSION[1]} or later is needed; this is "
                f"{sys.version_info[0]}.{sys.version_info[1]}."})
        return

    request = json.loads(sys.stdin.read())
    handler = {"describe": describe, "run": run_measured, "step": run_stepped}.get(request.get("mode"))
    if handler is None:
        answer({"error": "request", "message": f"Unknown mode: {request.get('mode')!r}"})
        return
    answer(handler(request))


_stdout = sys.stdout


def answer(value):
    _stdout.write(json.dumps(value))
    _stdout.flush()


def parse(source):
    """The source's syntax tree, or the syntax error said the way a student needs it."""
    try:
        return ast.parse(source, SOLUTION_FILE), None
    except SyntaxError as error:
        return None, f"line {error.lineno}, column {error.offset or 1}: {error.msg}"


# ---- describe ------------------------------------------------------------------------------

def describe(request):
    source = request["source"]
    tree, error = parse(source)
    if error:
        return {"compileError": error, "entries": [], "comments": comment_lines(source)}

    calls_by_function = {}
    for call in example_calls(tree):
        calls_by_function.setdefault(call["function"], []).append(call)

    entries = list_entries(tree, calls_by_function)
    result = {"entries": entries, "comments": comment_lines(source), "calls": calls_by_function}

    entry_name = request.get("entry")
    if entry_name:
        target = find_function(tree, entry_name)
        if target:
            result["time"] = estimate_time(tree, target)
            result["space"] = {"class": "Unknown", "reasoning":
                               "Reading Python code for space is not supported; the measured figures are the answer.",
                               "linearLoopDepth": 0, "logLoopDepth": 0, "recursiveSelfCalls": 0}
            result["methods"] = [{"name": f.name, "parameters": [a.arg for a in f.args.args if a.arg != "self"]}
                                 for f in ast.walk(tree) if isinstance(f, (ast.FunctionDef, ast.AsyncFunctionDef))]
            result["stackLike"], result["heapLike"] = container_roles(tree)
    return result


def node_classes(tree):
    """Classes shaped like LeetCode's TreeNode or ListNode: names of the attributes they set."""
    found = {}
    for cls in [n for n in tree.body if isinstance(n, ast.ClassDef)]:
        attributes = set()
        for node in ast.walk(cls):
            if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "self":
                attributes.add(node.attr)
            if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
                attributes.add(node.target.id)
        if {"val", "left", "right"} <= attributes:
            found[cls.name] = "tree"
        elif {"val", "next"} <= attributes:
            found[cls.name] = "list"
    return found


def list_entries(tree, calls_by_function):
    nodes = node_classes(tree)
    entries = []

    def add(function, owner):
        if function.name.startswith("_"):
            return
        positional = [a for a in function.args.args]
        if owner is not None:
            if not positional or positional[0].arg != "self":
                return
            positional = positional[1:]
        # A program's entry point is where examples are called from, not a solution.
        if owner is None and function.name == "main" and not positional:
            return

        given = {p for call in calls_by_function.get(function.name, []) for p, v in call["values"].items() if v is not None}
        parameters = []
        reason = None
        for argument in positional:
            hint = ast.unparse(argument.annotation) if argument.annotation is not None else None
            supported = hint is not None or argument.arg in given
            parameters.append({"name": argument.arg, "type": hint or "", "supported": supported})
            if not supported and reason is None:
                reason = (f"Parameter '{argument.arg}' has no type hint, and no example in the file gives it a value. "
                          f"Add a hint the way LeetCode does, like '{argument.arg}: List[int]'.")
        if function.args.vararg or function.args.kwarg or function.args.kwonlyargs:
            reason = reason or "Functions taking *args, **kwargs or keyword-only parameters cannot be called with an example."
        if owner is not None and not constructible(owner):
            reason = reason or f"{owner.name} needs arguments to be created, so this method cannot be called."
        entries.append({
            "declaringType": owner.name if owner is not None else "",
            "name": function.name,
            "parameters": parameters,
            "reason": reason,
            "line": function.lineno,
        })

    for node in tree.body:
        if isinstance(node, ast.FunctionDef):
            add(node, None)
        elif isinstance(node, ast.ClassDef) and node.name not in nodes:
            for member in node.body:
                if isinstance(member, ast.FunctionDef) and not member.decorator_list:
                    add(member, node)

    entries.sort(key=lambda e: (e["reason"] is not None, e["line"]))
    return entries


def constructible(cls):
    for member in cls.body:
        if isinstance(member, ast.FunctionDef) and member.name == "__init__":
            required = len(member.args.args) - 1 - len(member.args.defaults)
            return required <= 0
    return True


def find_function(tree, entry):
    owner, _, name = entry.rpartition(".")
    for node in tree.body:
        if not owner and isinstance(node, ast.FunctionDef) and node.name == name:
            return node
        if isinstance(node, ast.ClassDef) and node.name == owner:
            for member in node.body:
                if isinstance(member, ast.FunctionDef) and member.name == name:
                    return member
    return None


# ---- examples written in the code ------------------------------------------------------------

def comment_lines(source):
    """Every comment and docstring line, with its line number, for the shared example reader."""
    lines = []
    try:
        for token in tokenize.generate_tokens(io.StringIO(source).readline):
            if token.type == tokenize.COMMENT:
                lines.append({"text": token.string.lstrip("#").strip(), "line": token.start[0]})
            elif token.type == tokenize.STRING and token.string.lstrip("rbuRBU").startswith(('"""', "'''")):
                for offset, text in enumerate(token.string.split("\n")):
                    lines.append({"text": text.strip().strip('"\'').strip(), "line": token.start[0] + offset})
    except (tokenize.TokenError, IndentationError, SyntaxError):
        pass
    return lines


def leetcode_text(value):
    """A Python literal written the way LeetCode writes values: JSON, with tuples as lists."""
    def convert(v):
        if isinstance(v, (list, tuple)):
            return [convert(x) for x in v]
        if isinstance(v, set):
            return sorted((convert(x) for x in v), key=repr)
        if isinstance(v, dict):
            return {str(k): convert(x) for k, x in v.items()}
        return v
    return json.dumps(convert(value), separators=(",", ":"))


def example_calls(tree):
    """
    Calls written with values, outside the function they call: in `if __name__ ==
    "__main__":`, at the top level, or in another function. A name passed in is followed
    back to where it was given a literal value, if nothing changed it since.
    """
    found = []
    functions = {n.name for n in ast.walk(tree) if isinstance(n, ast.FunctionDef)}
    parents = {}
    for parent in ast.walk(tree):
        for child in ast.iter_child_nodes(parent):
            parents[child] = parent

    def enclosing_function(node):
        while node in parents:
            node = parents[node]
            if isinstance(node, ast.FunctionDef):
                return node.name
        return None

    def resolve(expression, call):
        if isinstance(expression, ast.Name):
            assignment = last_assignment(expression.id, call, parents)
            if assignment is None:
                return None, f"`{expression.id}`, which is not given a value in the code before the call."
            if assignment == "changed":
                return None, f"`{expression.id}`, which changes between where it is set and the call."
            try:
                return leetcode_text(ast.literal_eval(assignment)), None
            except (ValueError, SyntaxError, TypeError):
                return None, f"`{expression.id}`, which is set to `{shorten(ast.unparse(assignment))}`, not a value written out."
        try:
            return leetcode_text(ast.literal_eval(expression)), None
        except (ValueError, SyntaxError, TypeError):
            return None, f"`{shorten(ast.unparse(expression))}`, which is not a value written out."

    for call in [n for n in ast.walk(tree) if isinstance(n, ast.Call)]:
        name = call.func.attr if isinstance(call.func, ast.Attribute) else call.func.id if isinstance(call.func, ast.Name) else None
        if name not in functions or enclosing_function(call) == name:
            continue
        function = next(f for f in ast.walk(tree) if isinstance(f, ast.FunctionDef) and f.name == name)
        parameters = [a.arg for a in function.args.args if a.arg != "self"]
        values = {p: None for p in parameters}
        notes = []
        positional = call.args
        if len(positional) > len(parameters):
            continue
        pairs = list(zip(parameters, positional)) + [(k.arg, k.value) for k in call.keywords if k.arg in values]
        for parameter, expression in pairs:
            value, reason = resolve(expression, call)
            if value is not None:
                values[parameter] = value
            else:
                notes.append({"parameter": parameter, "message": f"Line {call.lineno} passes {reason}"})
        found.append({"function": name, "line": call.lineno, "values": values, "notes": notes,
                      "complete": len(pairs) == len(parameters)})
    return found


def last_assignment(name, call, parents):
    """The literal last assigned to `name` before `call`, in the same block or an enclosing one."""
    scope = parents.get(call)
    while scope is not None and not isinstance(scope, (ast.Module, ast.FunctionDef)):
        scope = parents.get(scope)
    if scope is None:
        return None
    candidate = None
    for node in ast.walk(scope):
        if getattr(node, "lineno", 10 ** 9) >= call.lineno:
            continue
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == name for t in node.targets):
            if candidate is None or node.lineno > candidate.lineno:
                candidate = node
    if candidate is None:
        return None
    for node in ast.walk(scope):
        line = getattr(node, "lineno", 0)
        if not (candidate.lineno < line < call.lineno):
            continue
        if isinstance(node, ast.AugAssign) and isinstance(node.target, ast.Name) and node.target.id == name:
            return "changed"
        if isinstance(node, ast.Subscript) and isinstance(node.ctx, ast.Store) and \
                isinstance(node.value, ast.Name) and node.value.id == name:
            return "changed"
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and \
                isinstance(node.func.value, ast.Name) and node.func.value.id == name and \
                node.func.attr in ("append", "extend", "insert", "pop", "remove", "sort", "reverse", "clear", "add", "update"):
            return "changed"
    return candidate.value


def shorten(text, limit=40):
    text = " ".join(text.split())
    return text if len(text) <= limit else text[:limit - 1] + "…"


# ---- reading the code for time complexity -----------------------------------------------------

def estimate_time(tree, function):
    """
    A reading of the code, in the same terms as the C# engine's: loop nesting, halving
    loops, two-pointer loops that never reset, and the kinds of recursion. When the shape
    is not one it recognises, it says unknown and why rather than guessing.
    """
    name = function.name
    self_calls = [c for c in ast.walk(function) if isinstance(c, ast.Call) and called_name(c) == name]
    nested = {f.name: f for f in ast.walk(function) if isinstance(f, ast.FunctionDef) and f is not function}

    # A recursive helper defined inside — `def dfs(...)` — is where the work usually is.
    recursion = recursion_kind(function, self_calls)
    if recursion is None:
        for helper in nested.values():
            calls = [c for c in ast.walk(helper) if isinstance(c, ast.Call) and called_name(c) == helper.name]
            if calls:
                recursion = recursion_kind(helper, calls)
                break

    linear, log, because, worklist = loop_shape(function)
    loops = f"loop nesting depth {linear}" + (f", {log} of them halving or doubling" if log else "")
    cause = f" The deepest cost is {because}." if because else ""

    # A traversal's nested loops are bounded together, not multiplied: nesting would call
    # a BFS O(n³). The same judgement as the C# reader: withheld, with the reason.
    if worklist and linear >= 2 and recursion is None:
        return estimate("Unknown",
                        f"Contains a worklist loop, {worklist}: a BFS or DFS-style traversal. Each node goes through the "
                        "worklist once in total, so the loops around and inside it are amortised — usually O(V + E) — "
                        f"rather than multiplied. Loop nesting alone would have said depth {linear}, which overstates it.",
                        linear, log, 0)

    if recursion is not None:
        kind, detail, branches = recursion
        if kind == "exponential":
            return estimate("Exponential", detail, linear, log, branches)
        if kind == "unknown":
            return estimate("Unknown", detail, linear, log, branches)
        if kind == "amortised":
            # The loops visit every cell, and the recursion sinks each one once across all
            # of them: the total is the loops' work, not the loops times the recursion.
            if linear == 0:
                return estimate("Linear", detail, linear, log, branches)
            return estimate(ladder(linear), detail + f" The loops around it are {loops}, so that is the total.", linear, log, branches)
        if kind == "linear":
            if linear >= 1:
                return estimate(ladder(1 + linear), detail + f" Each visit also does work at {loops}, so the total is that multiplied by n.",
                                linear, log, branches)
            return estimate("Linear", detail, linear, log, branches)
        if kind == "linearithmic":
            return estimate("Linearithmic", detail, linear, log, branches)
        if kind == "logarithmic":
            return estimate("Logarithmic" if linear == 0 else "Unknown", detail, linear, log, branches)

    if linear == 0 and log == 0:
        return estimate("Constant", "No loops and no recursion: a fixed amount of work.", 0, 0, 0)
    if linear == 0:
        return estimate("Logarithmic", f"Found {loops}, and no recursion.{cause}", 0, log, 0)
    if log == 0:
        return estimate(ladder(linear), f"Found {loops}, and no recursion.{cause}", linear, 0, 0)
    if linear == 1 and log == 1:
        return estimate("Linearithmic", f"Found {loops}, and no recursion.{cause}", linear, log, 0)
    return estimate("Unknown", f"Found {loops}, which does not map onto a standard class.{cause}", linear, log, 0)


def ladder(depth):
    return {1: "Linear", 2: "Quadratic", 3: "Cubic"}.get(depth, "Unknown")


def estimate(cls, reasoning, linear, log, calls):
    return {"class": cls, "reasoning": reasoning, "linearLoopDepth": linear, "logLoopDepth": log, "recursiveSelfCalls": calls}


def called_name(call):
    if isinstance(call.func, ast.Name):
        return call.func.id
    if isinstance(call.func, ast.Attribute) and isinstance(call.func.value, ast.Name) and call.func.value.id == "self":
        return call.func.attr
    return None


def recursion_kind(function, self_calls):
    if not self_calls:
        return None
    name = function.name
    branches = max(len([c for c in ast.walk(stmt) if c in self_calls]) for stmt in function.body) if function.body else 0
    branches = max(branches, len(self_calls) if len(self_calls) <= 4 else branches)
    source = ast.unparse(function)

    memo = memo_table(function)
    if memo:
        return ("linear", f"'{name}' calls itself {times(len(self_calls))} but caches results in '{memo}', so each "
                          "distinct subproblem is solved once: the work is the number of subproblems.", len(self_calls))

    children = [a for c in self_calls for a in c.args if isinstance(a, ast.Attribute) and a.attr in ("left", "right", "next")]
    if children:
        return ("linear", f"'{name}' recurses into child nodes ({', '.join(sorted({ast.unparse(a) for a in children}))}), "
                          "so each node is visited once.", len(self_calls))

    visited = next((n for n in ("visited", "seen", "vis", "marked") if n in source), None)
    grid_marking = any(isinstance(t, ast.Subscript) and isinstance(t.ctx, ast.Store)
                       for n in ast.walk(function) if isinstance(n, ast.Assign) for t in n.targets)
    if visited or (grid_marking and len(self_calls) >= 2 and "return" in source):
        mark = visited or "the grid itself"
        return ("amortised", f"'{name}' marks {mark} as visited and stops on revisits, so every node or cell is "
                             "processed once in total, however many times the loops around it start it.", len(self_calls))

    halves = any(isinstance(n, ast.BinOp) and isinstance(n.op, (ast.FloorDiv, ast.RShift)) for c in self_calls for n in ast.walk(c)) \
        or any(isinstance(a, ast.Subscript) and isinstance(a.slice, ast.Slice) for c in self_calls for a in c.args) \
        or "mid" in {n.id for c in self_calls for n in ast.walk(c) if isinstance(n, ast.Name)}
    if len(self_calls) >= 2 and halves:
        return ("linearithmic", f"'{name}' calls itself {times(len(self_calls))} but splits the range each time — "
                                "divide and conquer: log n levels with linear work at each.", len(self_calls))
    if len(self_calls) >= 2:
        return ("exponential", f"'{name}' calls itself {times(len(self_calls))} per invocation with no memoisation, no "
                               "visited set, and no halving: the calls multiply at every level.", len(self_calls))
    if halves:
        return ("logarithmic", f"'{name}' calls itself once on half the range, so it recurses about log n deep.", 1)
    return ("linear", f"'{name}' calls itself once, on a slightly smaller input: about n calls deep.", 1)


def memo_table(function):
    for decorator in function.decorator_list:
        text = ast.unparse(decorator)
        if "cache" in text:
            return f"@{text}"
    # A dictionary or list checked for an answer that is then returned.
    for node in ast.walk(function):
        if isinstance(node, ast.If) and isinstance(node.test, ast.Compare) and \
                any(isinstance(op, ast.In) for op in node.test.ops):
            container = node.test.comparators[0]
            for stmt in node.body:
                if isinstance(stmt, ast.Return) and isinstance(stmt.value, ast.Subscript) and \
                        ast.unparse(stmt.value.value) == ast.unparse(container):
                    return ast.unparse(container)
    return None


def times(count):
    return "twice" if count == 2 else f"{count} times"


def loop_shape(function):
    """
    Deepest nesting of linear loops and of halving loops. Inner loops that only move a
    pointer forward are not multiplied; a list operation that scans the list (`in`,
    .index, .remove, pop(0), insert(0, …)) counts as one more level where it sits.
    Returns (linear, log, the deepest cost in words, the worklist loop if there is one).
    """
    lists = list_names(function)
    best = {"linear": 0, "log": 0, "because": None}
    worklist = [None]

    def record(linear, log, because):
        if linear * 10 + log > best["linear"] * 10 + best["log"]:
            best.update(linear=linear, log=log, because=because)

    def walk(node, linear, log, outer_pointers):
        for child in ast.iter_child_nodes(node):
            if isinstance(child, (ast.FunctionDef, ast.Lambda)):
                continue
            if isinstance(child, ast.For):
                record(linear + 1, log, f"`{shorten(ast.unparse(child).splitlines()[0], 50)}` on line {child.lineno}")
                walk(child, linear + 1, log, outer_pointers | assigned_names(child))
                continue
            if isinstance(child, ast.While):
                if worklist[0] is None and is_worklist(child):
                    worklist[0] = f"'while {shorten(ast.unparse(child.test), 30)}' on line {child.lineno}"
                if halving(child):
                    record(linear, log + 1, None)
                    walk(child, linear, log + 1, outer_pointers)
                elif linear > 0 and monotonic_pointer(child, outer_pointers):
                    # A two-pointer or sliding-window loop: its pointer only moves forward
                    # and is never reset, so across the whole outer loop it runs n times in
                    # total, not n times per pass.
                    walk(child, linear, log, outer_pointers)
                else:
                    record(linear + 1, log, f"`while {shorten(ast.unparse(child.test), 40)}` on line {child.lineno}")
                    walk(child, linear + 1, log, outer_pointers | assigned_names(child))
                continue
            if isinstance(child, (ast.ListComp, ast.SetComp, ast.DictComp, ast.GeneratorExp)):
                record(linear + len(child.generators), log, f"the comprehension on line {child.lineno}")
                walk(child, linear + len(child.generators), log, outer_pointers)
                continue
            scan = scans_list(child, lists)
            if scan:
                record(linear + 1, log, f"`{shorten(ast.unparse(child), 40)}` on line {child.lineno}, which {scan}")
            walk(child, linear, log, outer_pointers)
        record(linear, log, None)

    walk(function, 0, 0, frozenset())
    return best["linear"], best["log"], best["because"], worklist[0]


def list_names(function):
    """Names the function treats as lists: parameters hinted List[...], and names assigned a list."""
    names = set()
    for argument in function.args.args:
        if argument.annotation is not None and "List" in ast.unparse(argument.annotation).replace("list", "List"):
            names.add(argument.arg)
    for node in ast.walk(function):
        if isinstance(node, ast.Assign) and isinstance(node.value, (ast.List, ast.ListComp)) or \
                isinstance(node, ast.Assign) and isinstance(node.value, ast.BinOp) and isinstance(node.value.left, ast.List) or \
                isinstance(node, ast.Assign) and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Name) and \
                node.value.func.id in ("list", "sorted"):
            names.update(t.id for t in node.targets if isinstance(t, ast.Name))
    return names


def scans_list(node, lists):
    """What a list operation hidden in one expression costs, in words — or None."""
    if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], (ast.In, ast.NotIn)) and \
            isinstance(node.comparators[0], ast.Name) and node.comparators[0].id in lists:
        return f"scans the list `{node.comparators[0].id}`: O(n) each time"
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and isinstance(node.func.value, ast.Name) and \
            node.func.value.id in lists:
        name = node.func.attr
        if name in ("index", "remove", "count"):
            return f"scans the list `{node.func.value.id}`: O(n) each time"
        if name in ("pop", "insert") and node.args and isinstance(node.args[0], ast.Constant) and node.args[0].value == 0:
            return f"shifts every element of `{node.func.value.id}` along by one: O(n) each time"
    return None


def is_worklist(loop):
    """`while queue:` that takes from the front or top of the thing it tests: a BFS or DFS."""
    tested = {n.id for n in ast.walk(loop.test) if isinstance(n, ast.Name)}
    for node in ast.walk(loop):
        if isinstance(node, ast.Call):
            target = node.func
            if isinstance(target, ast.Attribute) and target.attr in ("popleft", "pop") and \
                    isinstance(target.value, ast.Name) and target.value.id in tested:
                return True
            if (isinstance(target, ast.Attribute) and target.attr == "heappop" or
                    isinstance(target, ast.Name) and target.id == "heappop") and node.args and \
                    isinstance(node.args[0], ast.Name) and node.args[0].id in tested:
                return True
    return False


def assigned_names(node):
    return frozenset(t.id for n in ast.walk(node) for t in getattr(n, "targets", [getattr(n, "target", None)])
                     if isinstance(t, ast.Name))


def halving(loop):
    for node in ast.walk(loop):
        if isinstance(node, ast.AugAssign) and isinstance(node.op, (ast.FloorDiv, ast.RShift, ast.Mult, ast.LShift)):
            return True
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("lo", "hi", "low", "high", "left", "right", "l", "r")
                                                for t in node.targets) and "mid" in ast.unparse(node.value):
            return True
    return False


def monotonic_pointer(loop, outer_assigned):
    """The loop's condition uses a variable the loop only ever increases, and the outer loop never resets."""
    condition_names = {n.id for n in ast.walk(loop.test) if isinstance(n, ast.Name)}
    increased = {n.target.id for n in ast.walk(loop) if isinstance(n, ast.AugAssign) and isinstance(n.target, ast.Name)
                 and isinstance(n.op, ast.Add)}
    candidates = condition_names & increased
    return any(name not in outer_assigned for name in candidates)


def container_roles(tree):
    """
    Lists used as stacks (append and pop() only) and lists used as heaps (passed to heapq),
    so the run view can draw them as what they are rather than as plain arrays.
    """
    heaps, stacks, indexed, popped, appended = set(), set(), set(), set(), set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            target = node.func
            module_call = isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name) and target.value.id == "heapq"
            bare_call = isinstance(target, ast.Name) and target.id in ("heappush", "heappop", "heapify", "heappushpop", "heapreplace")
            if (module_call or bare_call) and node.args and isinstance(node.args[0], (ast.Name, ast.Attribute)):
                heaps.add(ast.unparse(node.args[0]).replace("self.", ""))
            if isinstance(target, ast.Attribute) and isinstance(target.value, (ast.Name, ast.Attribute)):
                owner = ast.unparse(target.value).replace("self.", "")
                if target.attr == "pop" and not node.args:
                    popped.add(owner)
                if target.attr == "append":
                    appended.add(owner)
        if isinstance(node, ast.Subscript) and isinstance(node.value, (ast.Name, ast.Attribute)):
            index = node.slice
            if not (isinstance(index, ast.UnaryOp) and isinstance(index.op, ast.USub)):
                indexed.add(ast.unparse(node.value).replace("self.", ""))
    stacks = (popped & appended) - indexed - heaps
    return sorted(stacks), sorted(heaps)


# ---- instrumentation ------------------------------------------------------------------------

# Library calls whose cost is hidden inside one statement. Charged as they run, and only
# when the object really is what the cost assumes: `x in d` costs nothing extra on a
# dict, and a lot on a list.
METHOD_COSTS = {
    "insert": "O(n − index)", "pop": "O(n − index)", "remove": "O(n)", "index": "O(n)", "count": "O(n)",
    "sort": "O(n log n)", "reverse": "O(n)", "copy": "O(n)", "extend": "O(k)", "join": "O(n)",
    "heappush": "O(log n)", "heappop": "O(log n)", "heapify": "O(n)", "heappushpop": "O(log n)",
    "heapreplace": "O(log n)", "nlargest": "O(n log k)", "nsmallest": "O(n log k)",
    "bisect_left": "O(log n)", "bisect_right": "O(log n)", "bisect": "O(log n)", "insort": "O(n)",
    "insort_left": "O(n)", "insort_right": "O(n)", "find": "O(n)", "replace": "O(n)", "split": "O(n)",
}
BUILTIN_COSTS = {
    "sorted": "O(n log n)", "min": "O(n)", "max": "O(n)", "sum": "O(n)", "list": "O(n)", "set": "O(n)",
    "tuple": "O(n)", "dict": "O(n)", "any": "O(n)", "all": "O(n)",
    "heappush": "O(log n)", "heappop": "O(log n)", "heapify": "O(n)", "heappushpop": "O(log n)",
    "heapreplace": "O(log n)", "nlargest": "O(n log k)", "nsmallest": "O(n log k)",
    "bisect_left": "O(log n)", "bisect_right": "O(log n)", "insort": "O(n)",
}


class Instrumenter(ast.NodeTransformer):
    """Rewrites the student's code: a counter before each statement, charges, depth tracking."""

    def __init__(self, source, stepping):
        self.source = source
        self.stepping = stepping
        self.statements = []
        self.sites = []
        self.function_stack = []

    # -- statements --

    def statement_info(self, node):
        segment = ast.get_source_segment(self.source, node) or ""
        first_line = segment.split("\n")[0]
        snippet = " ".join(first_line.split())
        if isinstance(node, (ast.For, ast.While, ast.If, ast.With, ast.Try)) or \
                (hasattr(ast, "Match") and isinstance(node, ast.Match)):
            snippet = snippet.rstrip(":")
        if len(snippet) > SNIPPET_LENGTH:
            snippet = snippet[:SNIPPET_LENGTH - 1] + "…"
        statement_id = len(self.statements)
        self.statements.append({
            "id": statement_id, "line": node.lineno, "column": node.col_offset + 1,
            "length": len(first_line.strip()), "snippet": snippet,
            "method": self.function_stack[-1] if self.function_stack else "",
            "variables": [],
        })
        return statement_id

    def counted(self, body):
        """Puts a counter before every statement of a body that belongs to a function."""
        if not self.function_stack:
            return [self.visit(s) for s in body]
        result = []
        for statement in body:
            statement_id = self.statement_info(statement)
            if self.stepping:
                call = f"_dsaviz_.values(_dsaviz_.step({statement_id}), locals())"
            else:
                call = f"_dsaviz_.hit({statement_id})"
            counter = ast.parse(call).body[0]
            ast.copy_location(counter, statement)
            ast.fix_missing_locations(counter)
            result.append(counter)
            visited = self.visit(statement)
            if isinstance(visited, list):
                result.extend(visited)
            elif visited is not None:
                result.append(visited)
        return result

    def generic_visit(self, node):
        for field in ("body", "orelse", "finalbody"):
            if isinstance(getattr(node, field, None), list) and not isinstance(node, (ast.Module, ast.ClassDef)) and \
                    getattr(node, field) and isinstance(getattr(node, field)[0], ast.stmt):
                setattr(node, field, self.counted(getattr(node, field)))
        for field, value in ast.iter_fields(node):
            if field in ("body", "orelse", "finalbody") and isinstance(value, list) and value and \
                    isinstance(value[0], ast.stmt) and not isinstance(node, (ast.Module, ast.ClassDef)):
                continue
            if isinstance(value, list):
                new = []
                for item in value:
                    if isinstance(item, ast.AST):
                        item = self.visit(item)
                        if item is None:
                            continue
                        if isinstance(item, list):
                            new.extend(item)
                            continue
                    new.append(item)
                value[:] = new
            elif isinstance(value, ast.AST):
                replacement = self.visit(value)
                setattr(node, field, replacement)
        return node

    def visit_ExceptHandler(self, node):
        node.body = self.counted(node.body)
        return node

    def visit_match_case(self, node):
        node.body = self.counted(node.body)
        return node

    # -- functions: depth tracking and returned values --

    def visit_FunctionDef(self, node):
        self.function_stack.append(node.name)
        is_generator = any(isinstance(n, (ast.Yield, ast.YieldFrom)) for n in walk_own(node))
        node.body = self.counted(node.body)
        for field in ("args", "returns"):
            pass
        node.decorator_list = [self.visit(d) for d in node.decorator_list]
        self.function_stack.pop()
        if not is_generator:
            # Enter and a try/finally Exit: the one construct that runs on every way out.
            enter = ast.parse("_dsaviz_.enter()").body[0]
            leave = ast.parse("_dsaviz_.exit()").body[0]
            wrapped = ast.Try(body=node.body, handlers=[], orelse=[], finalbody=[leave])
            node.body = [enter, wrapped]
            ast.copy_location(enter, node)
            ast.copy_location(wrapped, node)
            ast.fix_missing_locations(node)
        return node

    def visit_AsyncFunctionDef(self, node):
        return node

    def visit_Lambda(self, node):
        return node

    def visit_Return(self, node):
        if node.value is not None:
            node.value = self.visit(node.value)
            if self.stepping and self.function_stack:
                node.value = self.call("ret", [node.value], node)
        return node

    # -- hidden costs, measurement only --

    def site(self, node, member, cost):
        self.sites.append({"site": len(self.sites), "line": node.lineno, "column": node.col_offset + 1,
                           "member": member, "cost": cost})
        return len(self.sites) - 1

    def call(self, method, arguments, node, keywords=()):
        call = ast.Call(func=ast.Attribute(value=ast.Name(id="_dsaviz_", ctx=ast.Load()), attr=method, ctx=ast.Load()),
                        args=list(arguments), keywords=list(keywords))
        return ast.fix_missing_locations(ast.copy_location(call, node))

    def charging(self):
        return not self.stepping and bool(self.function_stack)

    def visit_Compare(self, node):
        node = self.generic_visit(node)
        if self.charging() and len(node.ops) == 1 and isinstance(node.ops[0], (ast.In, ast.NotIn)):
            site = self.site(node, "in (on a list, tuple or string)", "O(n)")
            contains = self.call("contains", [node.left, node.comparators[0], ast.Constant(site)], node)
            if isinstance(node.ops[0], ast.NotIn):
                return ast.copy_location(ast.UnaryOp(op=ast.Not(), operand=contains), node)
            return contains
        return node

    def visit_Call(self, node):
        node = self.generic_visit(node)
        if not self.charging():
            return node
        if isinstance(node.func, ast.Attribute) and node.func.attr in METHOD_COSTS:
            site = self.site(node, node.func.attr, METHOD_COSTS[node.func.attr])
            return self.call("method", [node.func.value, ast.Constant(node.func.attr), ast.Constant(site)] + node.args,
                             node, node.keywords)
        if isinstance(node.func, ast.Name) and node.func.id in BUILTIN_COSTS:
            site = self.site(node, node.func.id, BUILTIN_COSTS[node.func.id])
            return self.call("builtin", [node.func, ast.Constant(site)] + node.args, node, node.keywords)
        return node

    def visit_Subscript(self, node):
        node = self.generic_visit(node)
        if self.charging() and isinstance(node.ctx, ast.Load) and isinstance(node.slice, ast.Slice):
            site = self.site(node, "slice", "O(k)")
            bounds = [s if s is not None else ast.Constant(None) for s in (node.slice.lower, node.slice.upper, node.slice.step)]
            return self.call("slice", [node.value, ast.Constant(site)] + bounds, node)
        return node

    def visit_AugAssign(self, node):
        node = self.generic_visit(node)
        if self.charging() and isinstance(node.op, ast.Add) and isinstance(node.target, ast.Name):
            site = self.site(node, "+= on a string", "O(n)")
            value = self.call("concat", [ast.Name(id=node.target.id, ctx=ast.Load()), node.value, ast.Constant(site)], node)
            return ast.copy_location(ast.Assign(targets=[node.target], value=value), node)
        return node

    def visit_BinOp(self, node):
        node = self.generic_visit(node)
        if self.charging() and isinstance(node.op, ast.Mult) and \
                (isinstance(node.left, (ast.List, ast.Constant)) or isinstance(node.right, (ast.List, ast.Constant))):
            site = self.site(node, "[…] * n", "O(n)")
            return self.call("repeat", [node.left, node.right, ast.Constant(site)], node)
        return node

    def visit_comprehension(self, node):
        node = self.generic_visit(node)
        if self.charging():
            site = self.site(node.iter, "comprehension", "O(n)")
            node.iter = self.call("each", [node.iter, ast.Constant(site)], node.iter)
        return node


def walk_own(function):
    """The nodes of a function, without descending into functions nested inside it."""
    stack = list(ast.iter_child_nodes(function))
    while stack:
        node = stack.pop()
        yield node
        if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda, ast.ClassDef)):
            stack.extend(ast.iter_child_nodes(node))


def instrument(source, stepping):
    tree, error = parse(source)
    if error:
        return None, None, None, error
    instrumenter = Instrumenter(source, stepping)
    tree = instrumenter.visit(tree)
    ast.fix_missing_locations(tree)
    return compile(tree, SOLUTION_FILE, "exec"), instrumenter.statements, instrumenter.sites, None


# ---- the tracer -----------------------------------------------------------------------------

class Tracer:
    """What instrumented code calls into. Counts statements, charges library work, tracks depth and memory."""

    def __init__(self, statement_count, site_count, budget, depth_limit, recorder=None):
        self.hits = [0] * statement_count
        self.bytes = [0] * statement_count
        self.largest = [0] * statement_count
        self.work = [0] * statement_count
        self.site_calls = [0] * site_count
        self.site_units = [0] * site_count
        self.budget = budget
        self.depth_limit = depth_limit
        self.operations = 0
        self.depth = 0
        self.max_depth = 0
        self.pending = -1
        self.callers = []
        self.recorder = recorder
        self.solution_bytes = 0
        self.base = 0
        self.start_level = 0
        self.peak_held = 0
        self.execution_bytes = 0
        # Off while the file is loaded and the inputs are built: a call at the bottom of the
        # file, or a TreeNode constructor, is setup, not the solution's work.
        self.active = False

    # Memory: tracemalloc's peak since the last reading, over where it stood then, is what
    # the student's code allocated in between — including a temporary freed again before
    # the statement ended, which the net change alone would miss.
    #
    # Every tracer method charges what came before it (pause) and resets the baseline when
    # it is done (resume), so the tracer's own bookkeeping — Python allocates even to add
    # one to a counter past 256 — is never charged to the student's statement. One
    # statement's allocation can arrive in several pieces around a charged library call;
    # they add up to its execution's total, which is what "largest" is measured on.
    def pause(self):
        current, peak = tracemalloc.get_traced_memory()
        # The most memory held at once above where the run started: Python's space
        # complexity, measured rather than inferred from what was allocated.
        if peak - self.start_level > self.peak_held:
            self.peak_held = peak - self.start_level
        grown = peak - self.base
        if grown > 0 and self.pending >= 0:
            self.bytes[self.pending] += grown
            self.solution_bytes += grown
            self.execution_bytes += grown

    def resume(self):
        # Read first, then reset: reading allocates a tuple, and reading after the reset
        # would leave that tuple in the peak, charged to the next statement. In this order
        # the reading is freed before the reset, and the base that replaces the old one is
        # the same size, so the peak starts exactly at the base.
        self.base = tracemalloc.get_traced_memory()[0]
        tracemalloc.reset_peak()

    def finish_execution(self):
        if self.pending >= 0 and self.execution_bytes > self.largest[self.pending]:
            self.largest[self.pending] = self.execution_bytes
        self.execution_bytes = 0

    def hit(self, statement_id):
        if not self.active:
            return statement_id
        self.pause()
        self.finish_execution()
        self.pending = statement_id
        self.hits[statement_id] += 1
        self.operations += 1
        if self.operations > self.budget:
            raise BudgetExceeded()
        self.resume()
        return statement_id

    def step(self, statement_id):
        """The first half of a recorded step: counts it. Instrumented as `values(step(id), locals())`."""
        return self.hit(statement_id)

    def values(self, statement_id, local_values):
        """
        The second half: hands the variables to the recorder. Only reading variables ran
        since step(), so everything allocated since — the locals() snapshot, the recording
        — is the tracer's, and the baseline is simply reset past it.
        """
        if not self.active:
            return
        if self.recorder is not None:
            self.recorder.step(statement_id, self.depth, local_values, self.solution_bytes)
        self.resume()

    def ret(self, value):
        if self.active and self.recorder is not None:
            self.pause()
            self.recorder.returned(self.depth, value)
            self.resume()
        return value

    def enter(self):
        if not self.active:
            return
        self.pause()
        self.depth += 1
        if self.depth > self.max_depth:
            self.max_depth = self.depth
        if self.depth > self.depth_limit:
            raise DepthExceeded()
        self.callers.append((self.pending, self.execution_bytes))
        self.execution_bytes = 0
        self.resume()

    def exit(self):
        if not self.active:
            return
        self.pause()
        self.finish_execution()
        self.depth -= 1
        if self.callers:
            # Back in the caller, part-way through the statement that made the call.
            self.pending, self.execution_bytes = self.callers.pop()
        self.resume()

    def charge(self, site, units):
        if not self.active:
            return
        self.pause()
        units = max(0, int(units))
        self.site_calls[site] += 1
        self.site_units[site] += units
        if self.pending >= 0:
            self.work[self.pending] += units
        self.operations += units
        if self.operations > self.budget:
            raise BudgetExceeded()
        self.resume()

    # -- charged operations: each does exactly what the original expression did --

    def contains(self, item, container, site):
        result = item in container
        if type(container) in (list, tuple):
            self.charge(site, container.index(item) + 1 if result else len(container))
        elif type(container) is str:
            self.charge(site, len(container))
        return result

    def method(self, owner, name, site, *args, **kwargs):
        bound = getattr(owner, name)
        kind = type(owner)
        if kind is list:
            size = len(owner)
            if name in ("insert", "pop"):
                index = args[0] if args else (size - 1 if name == "pop" else 0)
                if index < 0:
                    index += size
                self.charge(site, max(0, size - index - (1 if name == "pop" else 0)))
            elif name in ("remove", "index", "count", "reverse", "copy"):
                self.charge(site, size)
            elif name == "sort":
                self.charge(site, size * max(1, math.log2(size)) if size > 1 else size)
            elif name == "extend":
                self.charge(site, len(args[0]) if args and hasattr(args[0], "__len__") else 0)
        elif kind is str and name in ("join",):
            self.charge(site, len(args[0]) if args and hasattr(args[0], "__len__") else 0)
        elif kind is str and name in ("find", "replace", "split", "index", "count"):
            self.charge(site, len(owner))
        elif owner is heapq or owner is bisect:
            self.charge_module_call(name, site, args)
        return bound(*args, **kwargs)

    def charge_module_call(self, name, site, args):
        size = len(args[0]) if args and hasattr(args[0], "__len__") else 0
        if name in ("heappush", "heappop", "heappushpop", "heapreplace", "bisect_left", "bisect_right", "bisect"):
            self.charge(site, max(1, math.ceil(math.log2(size + 1))))
        elif name in ("heapify", "insort", "insort_left", "insort_right"):
            self.charge(site, size)
        elif name in ("nlargest", "nsmallest"):
            data = args[1] if len(args) > 1 else []
            n = len(data) if hasattr(data, "__len__") else 0
            k = args[0] if args else 1
            self.charge(site, n * max(1, math.log2(max(2, k))))

    def builtin(self, function, site, *args, **kwargs):
        result = function(*args, **kwargs)
        # Only the real built-in: a solution may define its own `max`.
        if function in (sorted, list, tuple, set, dict) and hasattr(result, "__len__"):
            size = len(result)
            self.charge(site, size * max(1, math.log2(size)) if function is sorted and size > 1 else size)
        elif function in (min, max, sum, any, all) and args and hasattr(args[0], "__len__"):
            self.charge(site, len(args[0]))
        elif getattr(function, "__module__", None) in ("_heapq", "heapq", "_bisect", "bisect"):
            self.charge_module_call(function.__name__, site, args)
        return result

    def slice(self, owner, site, lower, upper, step):
        result = owner[lower:upper:step]
        if hasattr(result, "__len__"):
            self.charge(site, len(result))
        return result

    def concat(self, left, right, site):
        result = left
        result += right
        if type(result) is str:
            self.charge(site, len(result))
        return result

    def repeat(self, left, right, site):
        result = left * right
        if type(result) in (list, str):
            self.charge(site, len(result))
        return result

    def each(self, iterable, site):
        for item in iterable:
            self.charge(site, 1)
            yield item


# ---- inputs ---------------------------------------------------------------------------------

def build_value(text, kind, namespace):
    """A LeetCode-format value as the Python object the function expects."""
    value = json.loads(text)
    if kind == "tree":
        return build_tree(value, namespace.get("TreeNode", TreeNode))
    if kind == "list":
        return build_list(value, namespace.get("ListNode", ListNode))
    return value


def build_tree(values, node_type):
    if not values or values[0] is None:
        return None
    root = node_type(values[0])
    waiting = collections.deque([root])
    index = 1
    while waiting and index < len(values):
        parent = waiting.popleft()
        for side in ("left", "right"):
            if index >= len(values):
                break
            item = values[index]
            index += 1
            if item is not None:
                child = node_type(item)
                setattr(parent, side, child)
                waiting.append(child)
    return root


def build_list(values, node_type):
    head = None
    for item in reversed(values):
        head = node_type(item, head)
    return head


def leetcode_format(value, budget=None):
    """A returned value written the way LeetCode prints it."""
    if budget is None:
        budget = [MAX_FORMAT_NODES]
    budget[0] -= 1
    if budget[0] < 0:
        return "…"
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(value) if isinstance(value, int) else (f"{value:.5f}".rstrip("0").rstrip(".") if value == value else "NaN")
    if isinstance(value, str):
        return json.dumps(value)
    if is_tree(value):
        parts, queue = [], collections.deque([value])
        while queue and budget[0] > 0:
            budget[0] -= 1
            node = queue.popleft()
            if node is None:
                parts.append("null")
                continue
            parts.append(leetcode_format(node.val, budget))
            queue.append(node.left)
            queue.append(node.right)
        while parts and parts[-1] == "null":
            parts.pop()
        return "[" + ",".join(parts) + "]"
    if is_list_node(value):
        parts, node = [], value
        while node is not None and budget[0] > 0:
            budget[0] -= 1
            parts.append(leetcode_format(node.val, budget))
            node = node.next
        return "[" + ",".join(parts) + (",…" if node is not None else "") + "]"
    if isinstance(value, dict):
        return json.dumps({str(k): v for k, v in value.items()}, default=str)
    if isinstance(value, (set, frozenset)):
        return "[" + ",".join(leetcode_format(v, budget) for v in sorted(value, key=repr)) + "]"
    if hasattr(value, "__iter__"):
        return "[" + ",".join(leetcode_format(v, budget) for v in value) + "]"
    return json.dumps(str(value))


def is_tree(value):
    return hasattr(value, "val") and hasattr(value, "left") and hasattr(value, "right")


def is_list_node(value):
    return hasattr(value, "val") and hasattr(value, "next") and not hasattr(value, "left")


# ---- running --------------------------------------------------------------------------------

def prepare(request, stepping):
    code, statements, sites, error = instrument(request["source"], stepping)
    if error:
        return None, {"status": "CompileError", "errors": [error]}
    # The tracer's handle has one leading underscore, not two: inside a class body a
    # double-underscore name is mangled to _Solution__name and would not be found.
    namespace = {"__name__": "dsaviz_solution", "__builtins__": builtins, "TreeNode": TreeNode, "ListNode": ListNode}
    namespace.update(LEETCODE_NAMES)
    return (code, statements, sites, namespace), None


def locate(namespace, entry):
    owner, _, name = entry.rpartition(".")
    if owner:
        return getattr(namespace[owner](), name)
    return namespace[name]


def execute(request, tracer, code, namespace):
    """Runs the entry on its own thread, with a deep stack for deep recursion."""
    outcome = {}
    captured = io.StringIO()

    namespace["_dsaviz_"] = tracer

    def target():
        sys.stdout = captured
        try:
            exec(code, namespace)                         # defines the solution; nothing is counted yet
            function = locate(namespace, request["entry"])
            try:
                arguments = [build_value(a["text"], a.get("kind", "value"), namespace) for a in request.get("args", [])]
            except Exception as error:
                # The inputs come from the visualizer, so failing to build them is its bug,
                # not the student's — said so, and not as "Your code threw".
                outcome["status"] = "RuntimeError"
                outcome["error"] = f"The visualizer could not build the input for your function ({type(error).__name__}: {error})."
                return
            outcome["arguments"] = arguments
            tracemalloc.start()
            tracer.resume()
            tracer.start_level = tracer.base
            tracer.active = True
            started = time.perf_counter()
            try:
                outcome["value"] = function(*arguments)
                outcome["status"] = "Completed"
            finally:
                outcome["elapsed"] = (time.perf_counter() - started) * 1000
                tracer.pause()
                tracer.finish_execution()
                tracer.active = False
                tracemalloc.stop()
        except BudgetExceeded:
            outcome["status"] = "BudgetExceeded"
            outcome["error"] = f"Execution budget of {tracer.budget:,} operations was exceeded."
        except (DepthExceeded, RecursionError):
            outcome["status"] = "RecursionLimitExceeded"
            outcome["error"] = (f"Recursion went deeper than {tracer.depth_limit:,} calls. That usually means a missing "
                                "or unreachable base case — or an input large enough that this recursion should be "
                                "rewritten as a loop.")
        except Exception as error:                        # the student's own exception, reported as theirs
            outcome["status"] = "RuntimeError"
            line = error_line(error)
            outcome["errorLine"] = line
            where = f" at line {line}" if line else ""
            outcome["error"] = f"Your code threw {type(error).__name__}{where}: {error}"
        finally:
            sys.stdout = _stdout

    # A deep stack for deep recursion — as deep as the platform allows. Windows caps a
    # thread's stack at 256 MB and refuses more with ValueError.
    for size in (THREAD_STACK, 256 * 1024 * 1024, 64 * 1024 * 1024):
        try:
            threading.stack_size(size)
            break
        except ValueError:
            continue
    sys.setrecursionlimit(tracer.depth_limit * 4 + 1000)
    worker = threading.Thread(target=target, daemon=True)
    worker.start()
    worker.join()
    outcome["output"] = captured.getvalue()
    return outcome


def error_line(error):
    line = None
    traceback = error.__traceback__
    while traceback is not None:
        if traceback.tb_frame.f_code.co_filename == SOLUTION_FILE:
            line = traceback.tb_lineno
        traceback = traceback.tb_next
    return line


def run_measured(request):
    prepared, failure = prepare(request, stepping=False)
    if failure:
        return failure
    code, statements, sites, namespace = prepared
    tracer = Tracer(len(statements), len(sites), request.get("budget", DEFAULT_BUDGET),
                    request.get("depthLimit", DEFAULT_DEPTH_LIMIT))
    outcome = execute(request, tracer, code, namespace)

    costs = []
    for info in statements:
        i = info["id"]
        if tracer.hits[i]:
            costs.append({"id": i, "line": info["line"], "column": info["column"], "length": info["length"],
                          "snippet": info["snippet"], "hits": tracer.hits[i], "bytesAllocated": tracer.bytes[i],
                          "libraryWork": tracer.work[i], "largestAllocation": tracer.largest[i]})
    calls = [{**site, "calls": tracer.site_calls[site["site"]], "work": tracer.site_units[site["site"]]}
             for site in sites if tracer.site_calls[site["site"]]]

    return {
        "status": outcome["status"],
        "statements": costs,
        "totalOperations": tracer.operations,
        "totalStatements": sum(tracer.hits),
        "totalBytesAllocated": sum(tracer.bytes),
        "maxRecursionDepth": tracer.max_depth,
        "peakBytes": tracer.peak_held,
        "elapsedMs": outcome.get("elapsed", 0),
        "errors": [outcome["error"]] if "error" in outcome else [],
        "errorLine": outcome.get("errorLine"),
        "returnValue": leetcode_format(outcome["value"]) if outcome["status"] == "Completed" else None,
        "libraryCalls": calls,
        "output": outcome["output"][:10_000],
    }


# ---- recording a run for the run view -----------------------------------------------------------

class Recorder:
    """
    The Python counterpart of the C# HeapRecorder: variables and the objects they lead to,
    with identity kept across steps and only changes sent. Lists used as stacks are sent
    top first, and lists used as heaps as binary heaps, so they are drawn as what they are.
    """

    def __init__(self, statements, max_steps, stack_names, heap_names):
        self.statements = statements
        self.max_steps = max_steps
        self.steps = []
        self.returns = []
        self.truncated = False
        self.ids = {}
        self.keep = []                 # ids of live objects are only unique while they are alive
        self.sent = {}
        self.stack_names = set(stack_names)
        self.heap_names = set(heap_names)
        self.roles = {}

    def step(self, statement_id, depth, local_values, allocated):
        if len(self.steps) >= self.max_steps:
            self.truncated = True
            return
        info = self.statements[statement_id]
        pending = []
        variables = []
        for name, value in list(local_values.items()):
            if name.startswith("__") or name == "self" or callable(value) and not hasattr(value, "__len__") \
                    or isinstance(value, type) or type(value).__name__ == "module":
                if name == "self" and hasattr(value, "__dict__"):
                    for field, field_value in vars(value).items():
                        if not field.startswith("_") and not callable(field_value):
                            variables.append(self.variable(field, field_value, pending))
                continue
            variables.append(self.variable(name, value, pending))
        changed = self.visit(pending)
        step = {"index": len(self.steps), "statementId": statement_id, "line": info["line"], "column": info["column"],
                "depth": depth, "method": info["method"], "variables": variables, "allocated": allocated}
        if changed:
            step["heap"] = changed
        self.steps.append(step)

    def returned(self, depth, value):
        if not self.truncated:
            self.returns.append({"afterStep": len(self.steps), "depth": depth, "value": self.value(value, None)})

    # -- values --

    def variable(self, name, value, pending):
        if value is None:
            return {"name": name, "display": "None", "kind": "null", "items": None, "number": None}
        if isinstance(value, bool):
            return {"name": name, "display": str(value), "kind": "scalar", "items": None, "number": None}
        if isinstance(value, int):
            return {"name": name, "display": str(value), "kind": "scalar", "items": None, "number": value}
        if isinstance(value, float):
            return {"name": name, "display": repr(value), "kind": "scalar", "items": None, "number": None}
        if isinstance(value, str):
            shown = value if len(value) <= MAX_TEXT else value[:MAX_TEXT] + "…"
            items = list(value) if len(value) <= MAX_CHAR_ITEMS else None
            return {"name": name, "display": json.dumps(shown, ensure_ascii=False), "kind": "string", "items": items, "number": None}
        if isinstance(value, tuple) and len(value) <= 6 and all(not self.is_object(v) for v in value):
            return {"name": name, "display": self.inline(value), "kind": "scalar", "items": None, "number": None}
        if name in self.stack_names and type(value) is list:
            self.roles[id(value)] = "stack"
        if name in self.heap_names and type(value) is list:
            self.roles[id(value)] = "heap"
        object_id = self.track(value, pending)
        return {"name": name, "display": self.summary(value), "kind": self.kind_of(value), "items": None,
                "number": None, "ref": object_id}

    def is_object(self, value):
        return not isinstance(value, (type(None), bool, int, float, str)) and not \
            (isinstance(value, tuple) and len(value) <= 6 and all(not self.is_object(v) for v in value))

    def value(self, value, pending):
        if value is None:
            return {"text": "None"}
        if isinstance(value, bool):
            return {"text": str(value)}
        if isinstance(value, int):
            return {"text": str(value), "number": value}
        if isinstance(value, float):
            return {"text": f"{value:.5g}"}
        if isinstance(value, str):
            text = value if len(value) <= MAX_TEXT else value[:MAX_TEXT] + "…"
            return {"text": json.dumps(text, ensure_ascii=False)}
        if not self.is_object(value):
            return {"text": self.inline(value)}
        return {"text": self.summary(value), "ref": self.track(value, pending)}

    def inline(self, value):
        if isinstance(value, tuple):
            return "(" + ", ".join(self.inline(v) for v in value) + ")"
        if isinstance(value, str):
            return json.dumps(value if len(value) <= 12 else value[:12] + "…", ensure_ascii=False)
        return str(value)

    def track(self, value, pending):
        key = id(value)
        if key not in self.ids:
            self.ids[key] = len(self.ids) + 1
            self.keep.append(value)
        if pending is not None:
            pending.append(value)
        return self.ids[key]

    def kind_of(self, value):
        role = self.roles.get(id(value))
        if role:
            return role
        if is_tree(value):
            return "tree"
        if is_list_node(value):
            return "listnode"
        if isinstance(value, collections.deque):
            return "queue"
        if isinstance(value, (list, tuple)):
            return "list"
        if isinstance(value, dict):
            return "map"
        if isinstance(value, (set, frozenset)):
            return "set"
        return "object"

    def summary(self, value):
        kind = self.kind_of(value)
        if kind in ("tree", "listnode"):
            return f"{type(value).__name__}({self.inline(getattr(value, 'val', '?'))})"
        if hasattr(value, "__len__"):
            return f"{type(value).__name__} ({len(value)})"
        return type(value).__name__

    # -- objects --

    def visit(self, pending):
        changed, seen = [], set()
        while pending and len(seen) < MAX_OBJECTS:
            value = pending.pop(0)
            if id(value) in seen:
                continue
            seen.add(id(value))
            current = self.describe(value, pending)
            previous = self.sent.get(current["id"])
            if previous is not None and previous == current:
                continue
            self.sent[current["id"]] = current
            changed.append(self.as_patch(previous, current) if previous else current)
        return changed

    def describe(self, value, pending):
        kind = self.kind_of(value)
        described = {"id": self.ids[id(value)], "kind": kind, "type": type(value).__name__,
                     "count": len(value) if hasattr(value, "__len__") else 0, "bytes": sys.getsizeof(value)}
        if kind == "tree":
            described["fields"] = [{"name": f, "value": self.value(getattr(value, f, None), pending)} for f in ("val", "left", "right")]
        elif kind == "listnode":
            described["fields"] = [{"name": f, "value": self.value(getattr(value, f, None), pending)} for f in ("val", "next")]
        elif kind == "map":
            described["entries"] = [{"key": self.value(k, pending), "value": self.value(v, pending)}
                                    for k, v in list(value.items())[:MAX_ITEMS]]
        elif kind == "heap":
            described["items"] = [self.value(v, pending) for v in value[:MAX_ITEMS]]
            described["arity"] = 2
        elif kind == "stack":
            described["items"] = [self.value(v, pending) for v in list(reversed(value))[:MAX_ITEMS]]
        elif kind == "set":
            described["items"] = [self.value(v, pending) for v in list(value)[:MAX_ITEMS]]
        elif kind in ("list", "queue"):
            described["items"] = [self.value(v, pending) for v in list(value)[:MAX_ITEMS]]
        else:
            fields = vars(value) if hasattr(value, "__dict__") else {}
            described["fields"] = [{"name": k, "value": self.value(v, pending)} for k, v in list(fields.items())[:8]]
        return described

    @staticmethod
    def as_patch(previous, current):
        if current["kind"] != "list" or "items" not in previous or "items" not in current:
            return current
        old, new = previous["items"], current["items"]
        if len(old) != len(new) or len(new) < MIN_PATCHABLE:
            return current
        patch = []
        for index, (a, b) in enumerate(zip(old, new)):
            if a != b:
                if len(patch) == MAX_PATCH:
                    return current
                patch.append({"index": index, "value": b})
        result = {k: v for k, v in current.items() if k != "items"}
        result["patch"] = patch
        return result


def run_stepped(request):
    prepared, failure = prepare(request, stepping=True)
    if failure:
        return {**failure, "steps": [], "statements": []}
    code, statements, sites, namespace = prepared
    recorder = Recorder(statements, request.get("maxSteps", 10_000), request.get("stackLike", []), request.get("heapLike", []))
    tracer = Tracer(len(statements), len(sites), request.get("budget", DEFAULT_BUDGET),
                    request.get("depthLimit", DEFAULT_DEPTH_LIMIT), recorder)
    outcome = execute(request, tracer, code, namespace)

    return {
        "status": outcome["status"],
        "steps": recorder.steps,
        "returns": recorder.returns,
        "statements": statements,
        "truncated": recorder.truncated,
        "totalAllocated": tracer.solution_bytes,
        "errors": [outcome["error"]] if "error" in outcome else [],
        "errorLine": outcome.get("errorLine"),
        "returnValue": leetcode_format(outcome["value"]) if outcome["status"] == "Completed" else None,
    }


if __name__ == "__main__":
    main()
