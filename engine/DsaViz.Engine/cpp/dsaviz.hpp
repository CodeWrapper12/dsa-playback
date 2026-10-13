// dsaviz.hpp — compiled into a student's C++ solution by DSA Playback.
//
// The engine rewrites the solution so every statement first calls into this header:
// dsaviz::hit(id) when measuring, dsaviz::step(id, variables...) when recording a run to
// step through. Everything the run view and the complexity verdicts need comes from here:
// statement counts, memory (operator new and delete are replaced), call depth, the
// variables at every step and the objects they lead to, and what each call returned.
//
// Everything this header declares is in namespace dsaviz, or starts with dsaviz_, so it
// cannot meet a name in the student's code. Its own bookkeeping never counts as theirs:
// allocations made while recording are made with counting switched off.
#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <bitset>
#include <cassert>
#include <cctype>
#include <cfloat>
#include <climits>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <exception>
#include <functional>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <limits>
#include <list>
#include <map>
#include <memory>
#include <new>
#include <numeric>
#include <optional>
#include <queue>
#include <random>
#include <set>
#include <sstream>
#include <stack>
#include <stdexcept>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#if __has_include(<cxxabi.h>)
#include <cxxabi.h>
#define DSAVIZ_DEMANGLE 1
#endif

#if defined(_WIN32)
#include <io.h>
#else
#include <pthread.h>
#include <unistd.h>
#endif

using namespace std;

namespace dsaviz {

// ---- state ---------------------------------------------------------------------------------

/// Everything counted about one run. Sized before the run starts, so counting never allocates.
struct State {
    long long budget = 50000000;
    long long operations = 0;          // statements run, plus work charged to library calls
    long long statements = 0;
    int depth = 0, maxDepth = 0, depthLimit = 20000;
    int current = -1;                  // the statement running now
    int count = 0;                     // statements in the program
    long long* hits = nullptr;
    long long* bytes = nullptr;
    long long* largest = nullptr;      // most one execution of a statement allocated
    long long* work = nullptr;         // work inside library calls a statement made
    int sites = 0;                     // library calls that are charged, by where they are written
    long long* siteCalls = nullptr;
    long long* siteWork = nullptr;
    long long since = 0;               // allocated since the current statement began
    long long allocated = 0, held = 0, peak = 0;
    bool active = false;               // counting the student's code
    bool quiet = false;                // the header's own work: never counted
    bool stepping = false;
    long long maxSteps = 10000, steps = 0;
    bool truncated = false;
    long long* callStarts = nullptr;   // a design problem's calls, measured: operations when each began
    long long calls = 0, callRoom = 0;
    FILE* out = stdout;
};

inline State& state() { static State s; return s; }

/// Sets the header's own work apart for as long as it lives: nothing allocated is counted.
struct Quiet {
    bool was;
    Quiet() : was(state().quiet) { state().quiet = true; }
    ~Quiet() { state().quiet = was; }
};

// ---- memory ---------------------------------------------------------------------------------

// Every block carries its size and whether it was counted, just before the address the
// program sees: freeing a block counted during the run lowers what is held; freeing one
// made before the run — an input — does not, as it was never added.
constexpr std::size_t Header = 16;
constexpr std::size_t Counted = 0x5EED;

inline void* allocate(std::size_t n) {
    void* raw = std::malloc(n + Header);
    if (!raw) throw std::bad_alloc();
    auto* meta = static_cast<std::size_t*>(raw);
    State& s = state();
    bool count = s.active && !s.quiet;
    meta[0] = n;
    meta[1] = count ? Counted : 0;
    if (count) {
        s.allocated += (long long)n;
        s.held += (long long)n;
        if (s.held > s.peak) s.peak = s.held;
        s.since += (long long)n;
        if (s.current >= 0) s.bytes[s.current] += (long long)n;
    }
    return static_cast<char*>(raw) + Header;
}

inline void release(void* p) noexcept {
    if (!p) return;
    void* raw = static_cast<char*>(p) - Header;
    auto* meta = static_cast<std::size_t*>(raw);
    if (meta[1] == Counted) state().held -= (long long)meta[0];
    std::free(raw);
}

// ---- output ---------------------------------------------------------------------------------

/// JSON written into a buffer of its own, which the caller writes out in one piece.
struct Json {
    std::string text;
    void raw(const char* s) { text += s; }
    void raw(const std::string& s) { text += s; }
    void string(std::string_view s) {
        text += '"';
        for (unsigned char c : s) {
            switch (c) {
                case '"': text += "\\\""; break;
                case '\\': text += "\\\\"; break;
                case '\n': text += "\\n"; break;
                case '\r': text += "\\r"; break;
                case '\t': text += "\\t"; break;
                default:
                    if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); text += b; }
                    else text += (char)c;
            }
        }
        text += '"';
    }
    void number(long long v) { text += std::to_string(v); }
};

inline void emit(const std::string& line) {
    std::fwrite(line.data(), 1, line.size(), state().out);
    std::fputc('\n', state().out);
}

// ---- reading values (LeetCode's format) ----------------------------------------------------

/// A parsed value: a number, text, true, false, null, or a list of values.
struct Value {
    enum Kind { Null, Number, Text, Bool, List } kind = Null;
    std::string text;              // a number as written, or the text itself
    bool flag = false;
    std::vector<Value> items;
};

struct Parser {
    const std::string& s;
    std::size_t i = 0;
    void space() { while (i < s.size() && std::isspace((unsigned char)s[i])) ++i; }
    Value parse() {
        space();
        Value v;
        if (i >= s.size()) return v;
        char c = s[i];
        if (c == '[') {
            ++i; v.kind = Value::List; space();
            if (i < s.size() && s[i] == ']') { ++i; return v; }
            for (;;) {
                v.items.push_back(parse()); space();
                if (i < s.size() && s[i] == ',') { ++i; continue; }
                if (i < s.size() && s[i] == ']') { ++i; break; }
                break;
            }
            return v;
        }
        if (c == '"') {
            ++i; v.kind = Value::Text;
            while (i < s.size() && s[i] != '"') {
                if (s[i] == '\\' && i + 1 < s.size()) {
                    char e = s[++i];
                    v.text += e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e;
                    ++i;
                } else v.text += s[i++];
            }
            ++i;
            return v;
        }
        if (s.compare(i, 4, "true") == 0) { i += 4; v.kind = Value::Bool; v.flag = true; return v; }
        if (s.compare(i, 5, "false") == 0) { i += 5; v.kind = Value::Bool; return v; }
        if (s.compare(i, 4, "null") == 0) { i += 4; return v; }
        v.kind = Value::Number;
        while (i < s.size() && (std::isdigit((unsigned char)s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            v.text += s[i++];
        return v;
    }
};

// ---- what a type is ------------------------------------------------------------------------

template <class T, class = void> struct IsTree : std::false_type {};
template <class T> struct IsTree<T, std::void_t<decltype(std::declval<T&>().val), decltype(std::declval<T&>().left),
                                                 decltype(std::declval<T&>().right)>> : std::true_type {};
template <class T, class = void> struct IsChain : std::false_type {};
template <class T> struct IsChain<T, std::void_t<decltype(std::declval<T&>().val), decltype(std::declval<T&>().next)>>
    : std::true_type {};

template <class T, class = void> struct Iterable : std::false_type {};
template <class T> struct Iterable<T, std::void_t<decltype(std::begin(std::declval<const T&>())),
                                                   decltype(std::end(std::declval<const T&>()))>> : std::true_type {};

template <class T, class = void> struct IsIterator : std::false_type {};
template <class T> struct IsIterator<T, std::void_t<typename std::iterator_traits<T>::iterator_category>> : std::true_type {};

template <class T> struct IsPair : std::false_type {};
template <class A, class B> struct IsPair<std::pair<A, B>> : std::true_type {};

template <class T> struct Reader;

template <class T> T build(const Value& v) { return Reader<T>::read(v); }

template <class T> struct Reader {
    static T read(const Value& v) {
        if constexpr (std::is_same_v<T, bool>) return v.flag;
        else if constexpr (std::is_same_v<T, char>) return v.text.empty() ? '\0' : v.text[0];
        else if constexpr (std::is_integral_v<T>) return (T)std::stoll(v.text);
        else if constexpr (std::is_floating_point_v<T>) return (T)std::stod(v.text);
        else if constexpr (std::is_same_v<T, std::string>) return v.text;
        else static_assert(sizeof(T) == 0, "DSA Playback cannot read this parameter type");
    }
};

/// A tree in level order with nulls, or a linked list, as LeetCode writes them.
template <class N> struct Reader<N*> {
    static N* read(const Value& v) {
        if constexpr (IsTree<N>::value) {
            if (v.items.empty() || v.items[0].kind == Value::Null) return nullptr;
            N* root = new N(build<decltype(N::val)>(v.items[0]));
            std::queue<N*> waiting;
            waiting.push(root);
            std::size_t i = 1;
            while (!waiting.empty() && i < v.items.size()) {
                N* node = waiting.front(); waiting.pop();
                if (i < v.items.size() && v.items[i].kind != Value::Null) { node->left = new N(build<decltype(N::val)>(v.items[i])); waiting.push(node->left); }
                ++i;
                if (i < v.items.size() && v.items[i].kind != Value::Null) { node->right = new N(build<decltype(N::val)>(v.items[i])); waiting.push(node->right); }
                ++i;
            }
            return root;
        } else if constexpr (IsChain<N>::value) {
            N* head = nullptr;
            N** tail = &head;
            for (const Value& item : v.items) { *tail = new N(build<decltype(N::val)>(item)); tail = &(*tail)->next; }
            return head;
        } else {
            static_assert(sizeof(N) == 0, "DSA Playback can read TreeNode* and ListNode*, not other pointers");
        }
    }
};

template <class T, class A> struct Reader<std::vector<T, A>> {
    static std::vector<T, A> read(const Value& v) {
        std::vector<T, A> out;
        // LeetCode writes a vector<char> both ways: "abc" and ["a","b","c"].
        if constexpr (std::is_same_v<T, char>) {
            if (v.kind == Value::Text) return std::vector<T, A>(v.text.begin(), v.text.end());
        }
        out.reserve(v.items.size());
        for (const Value& item : v.items) out.push_back(build<T>(item));
        return out;
    }
};

// ---- writing values (LeetCode's format) ----------------------------------------------------

inline std::string decimal(double d) {
    if (std::isnan(d)) return "NaN";
    if (std::isinf(d)) return d > 0 ? "Infinity" : "-Infinity";
    char b[64];
    std::snprintf(b, sizeof b, "%.5f", d);
    std::string s = b;
    while (!s.empty() && s.back() == '0') s.pop_back();
    if (!s.empty() && s.back() == '.') s += '0';
    return s;
}

template <class T> void format(Json& j, const T& v);

template <class Node> void formatTree(Json& j, Node* root) {
    std::vector<Node*> order;
    std::queue<Node*> q;
    if (root) q.push(root);
    std::vector<Node*> levelOrder;
    while (!q.empty()) {
        Node* n = q.front(); q.pop();
        levelOrder.push_back(n);
        if (n) { q.push(n->left); q.push(n->right); }
        if (levelOrder.size() > 100000) break;
    }
    while (!levelOrder.empty() && !levelOrder.back()) levelOrder.pop_back();
    j.raw("[");
    for (std::size_t i = 0; i < levelOrder.size(); ++i) {
        if (i) j.raw(",");
        if (levelOrder[i]) format(j, levelOrder[i]->val); else j.raw("null");
    }
    j.raw("]");
}

template <class T> void format(Json& j, const T& v) {
    using D = std::decay_t<T>;
    if constexpr (std::is_same_v<D, bool>) j.raw(v ? "true" : "false");
    else if constexpr (std::is_same_v<D, char>) { char s[2] = {v, 0}; j.string(s); }
    else if constexpr (std::is_integral_v<D>) j.raw(std::to_string(v));
    else if constexpr (std::is_floating_point_v<D>) j.raw(decimal((double)v));
    else if constexpr (std::is_same_v<D, std::string>) j.string(v);
    else if constexpr (std::is_pointer_v<D>) {
        using N = std::remove_pointer_t<D>;
        if constexpr (IsTree<N>::value) formatTree(j, v);
        else if constexpr (IsChain<N>::value) {
            j.raw("[");
            int k = 0;
            for (N* n = v; n && k < 100000; n = n->next, ++k) { if (k) j.raw(","); format(j, n->val); }
            j.raw("]");
        } else j.raw(v ? "\"<pointer>\"" : "null");
    }
    else if constexpr (IsPair<D>::value) { j.raw("["); format(j, v.first); j.raw(","); format(j, v.second); j.raw("]"); }
    else if constexpr (Iterable<D>::value) {
        j.raw("[");
        bool first = true;
        for (const auto& item : v) { if (!first) j.raw(","); first = false; format(j, item); }
        j.raw("]");
    }
    else j.raw("\"<value>\"");
}

// ---- counting --------------------------------------------------------------------------------

[[noreturn]] void finish(const char* status, const std::string& error, int statement);

/// One statement about to run.
inline void hit(int id) {
    State& s = state();
    if (!s.active) return;
    if (s.current >= 0 && s.since > s.largest[s.current]) s.largest[s.current] = s.since;
    s.since = 0;
    s.current = id;
    ++s.hits[id];
    ++s.statements;
    if (++s.operations > s.budget) {
        char b[128];
        std::snprintf(b, sizeof b, "Execution budget of %lld operations was exceeded.", s.budget);
        finish("BudgetExceeded", b, id);
    }
}

/// Work done inside a library call the current statement made, at call site `site`.
inline void charge(int site, long long work) {
    State& s = state();
    if (!s.active) return;
    if (site >= 0 && site < s.sites) { ++s.siteCalls[site]; s.siteWork[site] += work > 0 ? work : 0; }
    if (work <= 0) return;
    if (s.current >= 0) s.work[s.current] += work;
    s.operations += work;
    if (s.operations > s.budget) {
        char b[128];
        std::snprintf(b, sizeof b, "Execution budget of %lld operations was exceeded.", s.budget);
        finish("BudgetExceeded", b, s.current);
    }
}

/// One call's frame: counts depth, and stops runaway recursion before the stack runs out.
struct Frame {
    Frame() {
        State& s = state();
        if (!s.active) return;
        if (++s.depth > s.maxDepth) s.maxDepth = s.depth;
        if (s.depth > s.depthLimit) {
            char b[200];
            std::snprintf(b, sizeof b, "Recursion went deeper than %d calls. That usually means a missing or unreachable "
                                       "base case.", s.depthLimit);
            finish("RecursionLimitExceeded", b, s.current);
        }
    }
    ~Frame() { State& s = state(); if (s.active) --s.depth; }
};

// ---- library costs --------------------------------------------------------------------------

template <class A, class B> long long span(const A& first, const B& last) {
    if constexpr (std::is_base_of_v<std::random_access_iterator_tag, typename std::iterator_traits<A>::iterator_category>)
        return (long long)(last - first);
    else return (long long)std::distance(first, A(last));
}

/// A scan over [first, last) — find, count, accumulate, min_element, max_element, reverse,
/// copy — charged, then made. `call` is the student's own call, with the range passed in,
/// so every argument is evaluated exactly once, as it was written.
template <class A, class B, class F> decltype(auto) scanned(int site, A&& first, B&& last, F&& call) {
    charge(site, span(first, last));
    return call(std::forward<A>(first), std::forward<B>(last));
}

/// A comparison sort over [first, last): about n·log₂n comparisons.
template <class A, class B, class F> decltype(auto) sorted(int site, A&& first, B&& last, F&& call) {
    long long n = span(first, last);
    charge(site, n > 1 ? (long long)(n * std::log2((double)n)) : 0);
    return call(std::forward<A>(first), std::forward<B>(last));
}

template <class C, class = void> struct HasIterators : std::false_type {};
template <class C> struct HasIterators<C, std::void_t<typename C::iterator, typename C::const_iterator>> : std::true_type {};

/// How much an insert or erase at `at` moves: everything after it, when the container is
/// contiguous — a vector, a string, a deque. A key, a node-based container or a class of the
/// student's own moves nothing that is charged.
template <class C, class P> long long shifted(const C& c, const P& at) {
    using D = std::decay_t<C>;
    if constexpr (HasIterators<D>::value) {
        using It = typename D::const_iterator;
        if constexpr ((std::is_same_v<std::decay_t<P>, typename D::iterator> || std::is_same_v<std::decay_t<P>, It>) &&
                      std::is_base_of_v<std::random_access_iterator_tag, typename std::iterator_traits<It>::iterator_category>)
            return (long long)(c.cend() - It(at));
        else if constexpr (std::is_same_v<D, std::string> && std::is_integral_v<std::decay_t<P>>)
            return (long long)c.size() - (long long)at;
    }
    return 0;
}

/// c.insert(at, ...), charged for what it shifts. Each argument is evaluated once.
template <class C, class P, class... R> decltype(auto) inserted(int site, C&& c, P&& at, R&&... rest) {
    charge(site, shifted(c, at));
    return c.insert(std::forward<P>(at), std::forward<R>(rest)...);
}

/// c.erase(at, ...), charged for what it shifts. Each argument is evaluated once.
template <class C, class P, class... R> decltype(auto) erased(int site, C&& c, P&& at, R&&... rest) {
    charge(site, shifted(c, at));
    return c.erase(std::forward<P>(at), std::forward<R>(rest)...);
}

/// string::find: a scan of the string. A set's or map's find is not a scan, and is not charged.
template <class C> C& searching(int site, C& c) {
    if constexpr (std::is_same_v<std::decay_t<C>, std::string>) charge(site, (long long)c.size());
    else charge(site, 0);
    return c;
}

/// A result built by copying — a substring: charged its length.
template <class T> T copied(int site, T&& value) {
    if constexpr (std::is_same_v<std::decay_t<T>, std::string>) charge(site, (long long)value.size());
    return std::forward<T>(value);
}

// ---- recording a step ------------------------------------------------------------------------

/// A variable as the step build passes it: its name, the type it was declared with, and a reference to it.
template <class T> struct Var { const char* name; const char* type; const T& value; };
template <class T> Var<T> var(const char* name, const char* type, const T& value) { return {name, type, value}; }

constexpr std::size_t MaxItems = 1000;
constexpr std::size_t MaxObjects = 300;
constexpr std::size_t MaxText = 200;

/// Every object reachable from one step's variables, written once each, by address.
struct Snapshot {
    Json variables, objects;
    std::set<const void*> written;
    std::size_t count = 0;
    bool firstVariable = true, firstObject = true;

    template <class T> void value(Json& j, const T& v, const char* type = "");
    template <class T> void object(const T& v, const char* type);
    template <class T> void variable(const Var<T>& v);
};

template <class T> std::string scalarText(const T& v) {
    Json j; format(j, v); return j.text;
}

inline std::string address(const void* p) { char b[32]; std::snprintf(b, sizeof b, "%llu", (unsigned long long)(std::uintptr_t)p); return b; }

template <class T> constexpr bool scalar() {
    using D = std::decay_t<T>;
    return std::is_arithmetic_v<D> || std::is_enum_v<D>;
}

template <class T> struct Underlying : T {
    static const typename T::container_type& of(const T& adapter) { return adapter.*(&Underlying::c); }
};

template <class C> struct ContainerKind { static constexpr const char* kind = "list"; };
template <class K, class C, class A> struct ContainerKind<std::set<K, C, A>> { static constexpr const char* kind = "set"; };
template <class K, class C, class A> struct ContainerKind<std::multiset<K, C, A>> { static constexpr const char* kind = "set"; };
template <class K, class H, class E, class A> struct ContainerKind<std::unordered_set<K, H, E, A>> { static constexpr const char* kind = "set"; };
template <class K, class H, class E, class A> struct ContainerKind<std::unordered_multiset<K, H, E, A>> { static constexpr const char* kind = "set"; };

template <class T> struct IsMap : std::false_type {};
template <class K, class V, class C, class A> struct IsMap<std::map<K, V, C, A>> : std::true_type {};
template <class K, class V, class C, class A> struct IsMap<std::multimap<K, V, C, A>> : std::true_type {};
template <class K, class V, class H, class E, class A> struct IsMap<std::unordered_map<K, V, H, E, A>> : std::true_type {};
template <class T> struct IsAdapter : std::false_type {};
template <class T, class C> struct IsAdapter<std::stack<T, C>> : std::true_type {};
template <class T, class C> struct IsAdapter<std::queue<T, C>> : std::true_type {};
template <class T, class C, class P> struct IsAdapter<std::priority_queue<T, C, P>> : std::true_type {};
template <class T> struct IsVector : std::false_type {};
template <class T, class A> struct IsVector<std::vector<T, A>> : std::true_type {};
template <class T, std::size_t N> struct IsVector<std::array<T, N>> : std::true_type {};

template <class T> void Snapshot::value(Json& j, const T& v, const char* type) {
    using D = std::decay_t<T>;
    if constexpr (scalar<D>() && !std::is_same_v<D, char> && !std::is_same_v<D, bool> && std::is_integral_v<D>) {
        j.raw("{\"x\":"); j.string(std::to_string(v)); j.raw(",\"num\":"); j.raw(std::to_string((long long)v)); j.raw("}");
    }
    else if constexpr (std::is_same_v<D, char>) { char s[2] = {v, 0}; j.raw("{\"x\":"); j.string(s); j.raw("}"); }
    else if constexpr (scalar<D>()) { j.raw("{\"x\":"); j.string(scalarText(v)); j.raw("}"); }
    else if constexpr (std::is_same_v<D, std::string>) {
        j.raw("{\"x\":");
        j.string("\"" + (v.size() > MaxText ? v.substr(0, MaxText) + "…" : v) + "\"");
        j.raw("}");
    }
    else if constexpr (std::is_pointer_v<D>) {
        if (!v) { j.raw("{\"x\":\"null\"}"); return; }
        using N = std::remove_pointer_t<D>;
        if constexpr (IsTree<N>::value || IsChain<N>::value) {
            j.raw("{\"r\":"); j.raw(address(v)); j.raw(",\"x\":");
            j.string(std::string(IsTree<N>::value ? "TreeNode(" : "ListNode(") + scalarText(v->val) + ")");
            j.raw("}");
            object(v, "");
        } else { j.raw("{\"x\":\"<pointer>\"}"); }
    }
    else if constexpr (IsPair<D>::value) {
        j.raw("{\"x\":"); j.string("(" + scalarText(v.first) + ", " + scalarText(v.second) + ")"); j.raw("}");
    }
    else if constexpr (Iterable<D>::value || IsAdapter<D>::value) {
        j.raw("{\"r\":"); j.raw(address(&v)); j.raw(",\"x\":"); j.string("(" + std::to_string(v.size()) + ")"); j.raw("}");
        object(v, type);
    }
    else if constexpr (IsIterator<D>::value) { j.raw("{\"x\":\"(iterator)\"}"); }
    else { j.raw("{\"x\":\"{…}\"}"); }
}

template <class T> void Snapshot::object(const T& v, const char* type) {
    using D = std::decay_t<T>;
    const void* key;
    if constexpr (std::is_pointer_v<D>) key = v; else key = &v;
    if (!written.insert(key).second || count >= MaxObjects) return;
    ++count;

    Json j;
    j.raw("{\"id\":"); j.raw(address(key));
    if constexpr (std::is_pointer_v<D>) {
        using N = std::remove_pointer_t<D>;
        j.raw(IsTree<N>::value ? ",\"k\":\"tree\",\"t\":\"TreeNode\",\"b\":" : ",\"k\":\"listnode\",\"t\":\"ListNode\",\"b\":");
        j.raw(std::to_string(sizeof(N)));
        j.raw(",\"f\":[[\"val\",");
        value(j, v->val);
        if constexpr (IsTree<N>::value) {
            j.raw("],[\"left\","); value(j, v->left); j.raw("],[\"right\","); value(j, v->right); j.raw("]]");
        } else {
            j.raw("],[\"next\","); value(j, v->next); j.raw("]]");
        }
    }
    else if constexpr (IsMap<D>::value) {
        j.raw(",\"k\":\"map\",\"t\":"); j.string(type[0] ? type : "map");
        j.raw(",\"c\":"); j.raw(std::to_string(v.size()));
        j.raw(",\"b\":"); j.raw(std::to_string(sizeof(D) + v.size() * (sizeof(typename D::value_type) + 32)));
        j.raw(",\"e\":[");
        std::size_t n = 0;
        for (const auto& [k, val] : v) {
            if (n >= MaxItems) break;
            if (n++) j.raw(",");
            j.raw("["); value(j, k); j.raw(","); value(j, val); j.raw("]");
        }
        j.raw("]");
    }
    else if constexpr (IsAdapter<D>::value) {
        const auto& inside = Underlying<D>::of(v);
        bool isStack = std::is_same_v<D, std::stack<typename D::value_type, typename D::container_type>>;
        bool isQueue = std::is_same_v<D, std::queue<typename D::value_type, typename D::container_type>>;
        j.raw(isStack ? ",\"k\":\"stack\"" : isQueue ? ",\"k\":\"queue\"" : ",\"k\":\"heap\",\"ar\":2");
        j.raw(",\"t\":"); j.string(type[0] ? type : isStack ? "stack" : isQueue ? "queue" : "priority_queue");
        j.raw(",\"c\":"); j.raw(std::to_string(v.size()));
        j.raw(",\"b\":"); j.raw(std::to_string(sizeof(D) + v.size() * sizeof(typename D::value_type)));
        j.raw(",\"i\":[");
        std::size_t n = 0;
        // A stack is drawn top first, as the run view expects.
        if (isStack) {
            for (auto it = inside.rbegin(); it != inside.rend() && n < MaxItems; ++it) { if (n++) j.raw(","); value(j, *it); }
        } else {
            for (auto it = inside.begin(); it != inside.end() && n < MaxItems; ++it) { if (n++) j.raw(","); value(j, *it); }
        }
        j.raw("]");
    }
    else if constexpr (Iterable<D>::value) {
        j.raw(",\"k\":\""); j.raw(IsVector<D>::value ? "array" : ContainerKind<D>::kind); j.raw("\"");
        j.raw(",\"t\":"); j.string(type[0] ? type : IsVector<D>::value ? "vector" : "container");
        j.raw(",\"c\":"); j.raw(std::to_string(v.size()));
        long long bytes = (long long)sizeof(D);
        if constexpr (IsVector<D>::value) bytes += (long long)(v.size() * sizeof(typename D::value_type));
        else bytes += (long long)(v.size() * (sizeof(typename D::value_type) + 32));
        j.raw(",\"b\":"); j.raw(std::to_string(bytes));
        j.raw(",\"i\":[");
        std::size_t n = 0;
        for (const auto& item : v) {
            if (n >= MaxItems) break;
            if (n++) j.raw(",");
            value(j, item);
        }
        j.raw("]");
    }
    j.raw("}");
    if (!firstObject) objects.raw(",");
    firstObject = false;
    objects.raw(j.text);
}

template <class T> void Snapshot::variable(const Var<T>& v) {
    if (!firstVariable) variables.raw(",");
    firstVariable = false;
    variables.raw("{\"n\":"); variables.string(v.name);
    variables.raw(",\"t\":"); variables.string(v.type);
    variables.raw(",\"v\":");
    // A container is described with the type the student declared it as.
    value(variables, v.value, v.type);
    variables.raw("}");
}

/// One statement about to run, with the variables it can see, when recording.
template <class... V> void step(int id, const V&... vars) {
    hit(id);
    State& s = state();
    if (!s.active || !s.stepping) return;
    if (s.steps >= s.maxSteps) { s.truncated = true; return; }
    ++s.steps;
    Quiet quiet;
    Snapshot shot;
    (shot.variable(vars), ...);
    std::string line = "{\"s\":" + std::to_string(id) + ",\"d\":" + std::to_string(s.depth) +
                       ",\"a\":" + std::to_string(s.allocated) + ",\"v\":[" + shot.variables.text +
                       "],\"o\":[" + shot.objects.text + "]}";
    emit(line);
}

/// A value being returned, noted for the recursion tree; the value itself is passed on.
template <class T> T&& returned(T&& value) {
    State& s = state();
    if (s.active && s.stepping && s.steps < s.maxSteps) {
        Quiet quiet;
        Snapshot shot;
        Json j;
        shot.value(j, value);
        emit("{\"ret\":" + std::to_string(s.depth) + ",\"after\":" + std::to_string(s.steps) + ",\"v\":" + j.text + "}");
    }
    return std::forward<T>(value);
}

// ---- the run ---------------------------------------------------------------------------------

inline void writeCounts(Json& j) {
    State& s = state();
    j.raw(",\"ops\":"); j.number(s.operations);
    j.raw(",\"statements\":"); j.number(s.statements);
    j.raw(",\"allocated\":"); j.number(s.allocated);
    j.raw(",\"peak\":"); j.number(s.peak);
    j.raw(",\"maxDepth\":"); j.number(s.maxDepth);
    j.raw(",\"truncated\":"); j.raw(s.truncated ? "true" : "false");
    j.raw(",\"hits\":[");
    for (int i = 0; i < s.count; ++i) { if (i) j.raw(","); j.number(s.hits[i]); }
    j.raw("],\"bytes\":[");
    for (int i = 0; i < s.count; ++i) { if (i) j.raw(","); j.number(s.bytes[i]); }
    j.raw("],\"largest\":[");
    for (int i = 0; i < s.count; ++i) { if (i) j.raw(","); j.number(std::max(s.largest[i], i == s.current ? s.since : 0LL)); }
    j.raw("],\"work\":[");
    for (int i = 0; i < s.count; ++i) { if (i) j.raw(","); j.number(s.work[i]); }
    j.raw("],\"sites\":[");
    for (int i = 0; i < s.sites; ++i) { if (i) j.raw(","); j.raw("["); j.number(s.siteCalls[i]); j.raw(","); j.number(s.siteWork[i]); j.raw("]"); }
    j.raw("]");
    if (s.calls > 0) {
        // What each call cost: the operations between its start and the next one's.
        j.raw(",\"callCosts\":[");
        for (long long i = 0; i < s.calls; ++i) {
            if (i) j.raw(",");
            j.number((i + 1 < s.calls ? s.callStarts[i + 1] : s.operations) - s.callStarts[i]);
        }
        j.raw("]");
    }
}

[[noreturn]] inline void finish(const char* status, const std::string& error, int statement) {
    State& s = state();
    s.active = false;
    Quiet quiet;
    Json j;
    j.raw("{\"end\":"); j.string(status);
    j.raw(",\"error\":"); j.string(error);
    j.raw(",\"statement\":"); j.number(statement);
    writeCounts(j);
    j.raw("}");
    emit(j.text);
    std::fflush(s.out);
    std::_Exit(0);
}

inline void crashed(int signal) {
    const char* what = signal == SIGSEGV ? "a segmentation fault"
                     : signal == SIGFPE ? "an arithmetic error (division by zero?)"
                     : signal == SIGABRT ? "an abort"
                     : "a crash";
    State& s = state();
    std::string text = std::string("Your code crashed with ") + what;
    finish("Crashed", text, s.current);
}

/// Starts counting: everything from here is the student's code.
inline void begin() {
    State& s = state();
    s.operations = s.statements = 0;
    s.allocated = s.held = s.peak = s.since = 0;
    s.depth = s.maxDepth = 0;
    s.current = -1;
    s.active = true;
}

template <class T> [[noreturn]] void succeed(const T* result) {
    State& s = state();
    s.active = false;
    Quiet quiet;
    Json j;
    j.raw("{\"end\":\"Completed\",\"result\":");
    if (result) { Json r; format(r, *result); j.string(r.text); } else j.raw("null");
    writeCounts(j);
    j.raw("}");
    emit(j.text);
    std::fflush(s.out);
    std::_Exit(0);
}

inline std::vector<std::string> readInputs() {
    std::vector<std::string> lines;
    std::string line;
    while (std::getline(std::cin, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        lines.push_back(line);
    }
    return lines;
}

/// Runs `body` on a thread with a deep stack, so deep recursion reaches the depth limit
/// rather than the end of the stack.
inline void deep(void (*body)()) {
#if defined(_WIN32)
    body();   // the stack size is set when linking
#else
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, (std::size_t)512 * 1024 * 1024);
    pthread_t thread;
    auto trampoline = [](void* f) -> void* { reinterpret_cast<void (*)()>(f)(); return nullptr; };
    if (pthread_create(&thread, &attr, trampoline, reinterpret_cast<void*>(body)) != 0) body();
    else pthread_join(thread, nullptr);
#endif
}

/// The student's exception, escaping their code, said the way they would want to hear it.
[[noreturn]] inline void threw(const std::exception& e) {
    std::string type = typeid(e).name();
#ifdef DSAVIZ_DEMANGLE
    int status = 0;
    if (char* plain = abi::__cxa_demangle(type.c_str(), nullptr, nullptr, &status)) { if (status == 0) type = plain; std::free(plain); }
#endif
    if (type.rfind("std::", 0) == 0) type = type.substr(5);
    if (type.rfind("__1::", 0) == 0) type = type.substr(5);
    finish("RuntimeError", "Your code threw " + type + ": " + e.what(), state().current);
}

[[noreturn]] inline void threwSomething() { finish("RuntimeError", "Your code threw an exception", state().current); }

// ---- design problems ---------------------------------------------------------------------------

/// A design problem's calls that do not fit its class: the input's fault, so nothing more runs.
[[noreturn]] inline void invalid(const std::string& message) { finish("InvalidInput", message, -1); }

inline std::string count(std::size_t n) { return n == 1 ? "1 argument" : std::to_string(n) + " arguments"; }

/// The operations and their arguments, checked against the class before anything runs.
inline void design_check(const std::vector<std::string>& operations, const Value& arguments, const char* type) {
    if (operations.empty())
        invalid(std::string("The list of operations is empty. The first one creates the object, like \"") + type + "\".");
    if (arguments.kind != Value::List || arguments.items.size() != operations.size())
        invalid("There are " + std::to_string(operations.size()) + " operations but " + std::to_string(arguments.items.size()) +
                " argument lists. Each operation needs its own list, [] when it takes none.");
    for (const Value& list : arguments.items)
        if (list.kind != Value::List) invalid("Each operation's arguments are a list, [] when it takes none.");
    if (operations[0] != type)
        invalid(std::string("The first operation creates the object, so it must be \"") + type + "\", not \"" + operations[0] + "\".");
}

[[noreturn]] inline void invalid_operation(std::size_t index, const std::string& name, const char* type) {
    invalid("Operation " + std::to_string(index + 1) + " is \"" + name + "\", but " + type + " has no public method by that name.");
}

[[noreturn]] inline void invalid_arity(std::size_t index, const char* name, std::size_t given, std::size_t takes) {
    invalid("Operation " + std::to_string(index + 1) + " calls " + name + " with " + count(given) + ", but it takes " + count(takes) + ".");
}

[[noreturn]] inline void invalid_constructor(const char* type, std::size_t given) {
    invalid(std::string(type) + " has no public constructor taking " + count(given) + ".");
}

/// Every call checked against the class before any is made: a name it does not have, or a
/// count of arguments none of its overloads takes, stops the run before anything happens.
inline void design_validate(const std::vector<std::string>& operations, const Value& arguments, const char* type,
                            const std::vector<std::size_t>& constructors,
                            const std::vector<std::pair<std::string, std::vector<std::size_t>>>& methods) {
    auto has = [](const std::vector<std::size_t>& counts, std::size_t n) { return std::find(counts.begin(), counts.end(), n) != counts.end(); };
    std::size_t made = arguments.items[0].items.size();
    if (!(constructors.empty() ? made == 0 : has(constructors, made))) invalid_constructor(type, made);
    for (std::size_t i = 1; i < operations.size(); i++) {
        auto found = std::find_if(methods.begin(), methods.end(), [&](const auto& m) { return m.first == operations[i]; });
        if (found == methods.end()) invalid_operation(i, operations[i], type);
        std::size_t given = arguments.items[i].items.size();
        if (!has(found->second, given)) invalid_arity(i, found->first.c_str(), given, found->second.front());
    }
}

/// Marks where call `index` begins, so the run view can tell one call from the next.
inline void call_starting(std::size_t index) {
    State& s = state();
    if (s.active && !s.stepping) {
        // Measuring: the count so far, kept outside the student's memory in a plain block.
        if (s.calls == s.callRoom) {
            s.callRoom = s.callRoom ? s.callRoom * 2 : 1024;
            s.callStarts = static_cast<long long*>(std::realloc(s.callStarts, (std::size_t)s.callRoom * sizeof(long long)));
        }
        s.callStarts[s.calls++] = s.operations;
        return;
    }
    if (!state().active || !state().stepping) return;
    Quiet quiet;
    emit("{\"call\":" + std::to_string(index) + ",\"step\":" + std::to_string(state().steps) + "}");
}

/// An argument built as the type the method declares. Building it is not the solution's work.
template <class T> T arg(const Value& value) {
    Quiet quiet;
    try { return build<T>(value); }
    catch (const std::exception& e) { invalid(std::string("An argument could not be read: ") + e.what()); }
}

/// Each call's result, in LeetCode's format, collected as the calls are made.
struct Results {
    std::string text = "[";
    bool first = true;
    void add(const std::string& item) { Quiet quiet; if (!first) text += ','; first = false; text += item; }
    void none() { add("null"); }
    template <class T> void value(const T& v) { Quiet quiet; Json j; format(j, v); add(j.text); }
};

/// Ends a design run that completed, with the list of results already written out.
[[noreturn]] inline void succeed_text(const std::string& result) {
    State& s = state();
    s.active = false;
    Quiet quiet;
    Json j;
    j.raw("{\"end\":\"Completed\",\"result\":");
    j.string(result + "]");
    writeCounts(j);
    j.raw("}");
    emit(j.text);
    std::fflush(s.out);
    std::_Exit(0);
}

/// Reads the run's settings from the command line: run|step, budget, depth limit, steps kept.
inline void configure(int argc, char** argv, int statements, int sites) {
    State& s = state();
    s.count = statements;
    s.sites = sites;
    s.siteCalls = static_cast<long long*>(std::calloc((std::size_t)sites + 1, sizeof(long long)));
    s.siteWork = static_cast<long long*>(std::calloc((std::size_t)sites + 1, sizeof(long long)));
    s.hits = static_cast<long long*>(std::calloc((std::size_t)statements + 1, sizeof(long long)));
    s.bytes = static_cast<long long*>(std::calloc((std::size_t)statements + 1, sizeof(long long)));
    s.largest = static_cast<long long*>(std::calloc((std::size_t)statements + 1, sizeof(long long)));
    s.work = static_cast<long long*>(std::calloc((std::size_t)statements + 1, sizeof(long long)));
    if (argc > 1) s.stepping = std::string(argv[1]) == "step";
    if (argc > 2) s.budget = std::atoll(argv[2]);
    if (argc > 3) s.depthLimit = std::atoi(argv[3]);
    if (argc > 4) s.maxSteps = std::atoll(argv[4]);
    static char buffer[1 << 16];
    std::setvbuf(s.out, buffer, _IOFBF, sizeof buffer);
    std::signal(SIGSEGV, crashed);
    std::signal(SIGFPE, crashed);
    std::signal(SIGABRT, crashed);
}

}  // namespace dsaviz

// The student's program allocates through these, so memory is counted exactly.
void* operator new(std::size_t n) { return dsaviz::allocate(n); }
void* operator new[](std::size_t n) { return dsaviz::allocate(n); }
void* operator new(std::size_t n, const std::nothrow_t&) noexcept { try { return dsaviz::allocate(n); } catch (...) { return nullptr; } }
void* operator new[](std::size_t n, const std::nothrow_t&) noexcept { try { return dsaviz::allocate(n); } catch (...) { return nullptr; } }
void operator delete(void* p) noexcept { dsaviz::release(p); }
void operator delete[](void* p) noexcept { dsaviz::release(p); }
void operator delete(void* p, std::size_t) noexcept { dsaviz::release(p); }
void operator delete[](void* p, std::size_t) noexcept { dsaviz::release(p); }
void operator delete(void* p, const std::nothrow_t&) noexcept { dsaviz::release(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { dsaviz::release(p); }
