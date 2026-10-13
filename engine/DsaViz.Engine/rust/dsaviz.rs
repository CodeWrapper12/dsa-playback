//! dsaviz — linked into a student's Rust solution by DSA Playback.
//!
//! The engine rewrites the solution so every statement first calls into this crate:
//! `dsaviz::hit(id)` when measuring, `dsaviz::step(id, &[...])` when recording a run to
//! step through. Everything the run view and the complexity verdicts need comes from here:
//! statement counts, memory (a counting global allocator), call depth, the variables at
//! every step and the objects they lead to, and what each call returned.
//!
//! It speaks the same line protocol as the C++ runtime, so the engine reads both the same
//! way. Its own bookkeeping is never counted as the student's: allocations it makes while
//! recording happen with counting switched off.
#![allow(dead_code, static_mut_refs, clippy::all)]

use std::alloc::{GlobalAlloc, Layout, System};
use std::any::type_name;
use std::cell::RefCell;
use std::cmp::Reverse;
use std::collections::{BTreeMap, BTreeSet, BinaryHeap, HashMap, HashSet, VecDeque};
use std::fmt::Debug;
use std::io::{Read, Write};
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicPtr, Ordering::Relaxed};

// ---- memory ------------------------------------------------------------------------------------

static ACTIVE: AtomicBool = AtomicBool::new(false);
static QUIET: AtomicBool = AtomicBool::new(false);
static ALLOCATED: AtomicI64 = AtomicI64::new(0);
static HELD: AtomicI64 = AtomicI64::new(0);
static PEAK: AtomicI64 = AtomicI64::new(0);
static SINCE: AtomicI64 = AtomicI64::new(0);
static CURRENT: AtomicI64 = AtomicI64::new(-1);
static BYTES: AtomicPtr<i64> = AtomicPtr::new(std::ptr::null_mut());

/// Every block carries its size and whether it was counted, just before the address the
/// program sees: freeing a block counted during the run lowers what is held; freeing one
/// made before the run — an input — does not, as it was never added.
const COUNTED: usize = 0x5EED;

pub struct Counting;

#[inline]
fn header(align: usize) -> usize {
    if align > 16 { align } else { 16 }
}

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let h = header(layout.align());
        let raw = System.alloc(Layout::from_size_align_unchecked(layout.size() + h, h));
        if raw.is_null() {
            return raw;
        }
        let user = raw.add(h);
        let meta = user.sub(16) as *mut usize;
        let count = ACTIVE.load(Relaxed) && !QUIET.load(Relaxed);
        *meta = layout.size();
        *meta.add(1) = if count { COUNTED } else { 0 };
        if count {
            let n = layout.size() as i64;
            ALLOCATED.fetch_add(n, Relaxed);
            let held = HELD.fetch_add(n, Relaxed) + n;
            if held > PEAK.load(Relaxed) {
                PEAK.store(held, Relaxed);
            }
            SINCE.fetch_add(n, Relaxed);
            let current = CURRENT.load(Relaxed);
            let bytes = BYTES.load(Relaxed);
            if current >= 0 && !bytes.is_null() {
                *bytes.add(current as usize) += n;
            }
        }
        user
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        let h = header(layout.align());
        let meta = ptr.sub(16) as *mut usize;
        if *meta.add(1) == COUNTED {
            HELD.fetch_sub(*meta as i64, Relaxed);
        }
        System.dealloc(ptr.sub(h), Layout::from_size_align_unchecked(layout.size() + h, h));
    }
}

#[global_allocator]
static GLOBAL: Counting = Counting;

/// Sets the runtime's own work apart for as long as it lives: nothing allocated is counted.
pub struct Quiet(bool);
impl Quiet {
    pub fn new() -> Quiet {
        Quiet(QUIET.swap(true, Relaxed))
    }
}
impl Drop for Quiet {
    fn drop(&mut self) {
        QUIET.store(self.0, Relaxed);
    }
}

// ---- state -------------------------------------------------------------------------------------

struct State {
    budget: i64,
    operations: i64,
    statements: i64,
    depth: i64,
    max_depth: i64,
    depth_limit: i64,
    hits: Vec<i64>,
    bytes: Vec<i64>,
    largest: Vec<i64>,
    work: Vec<i64>,
    site_calls: Vec<i64>,
    site_work: Vec<i64>,
    stepping: bool,
    max_steps: i64,
    steps: i64,
    truncated: bool,
    out: Vec<u8>,
    program: String,
    panic_line: i64,
    panic_message: String,
}

static mut STATE: State = State {
    budget: 50_000_000,
    operations: 0,
    statements: 0,
    depth: 0,
    max_depth: 0,
    depth_limit: 20_000,
    hits: Vec::new(),
    bytes: Vec::new(),
    largest: Vec::new(),
    work: Vec::new(),
    site_calls: Vec::new(),
    site_work: Vec::new(),
    stepping: false,
    max_steps: 10_000,
    steps: 0,
    truncated: false,
    out: Vec::new(),
    program: String::new(),
    panic_line: -1,
    panic_message: String::new(),
};

// The student's code runs on one thread, and the thread that started it only waits.
fn st() -> &'static mut State {
    unsafe { &mut *std::ptr::addr_of_mut!(STATE) }
}

// ---- output ------------------------------------------------------------------------------------

fn escape(text: &str, out: &mut String) {
    out.push('"');
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
}

fn emit(line: &str) {
    let s = st();
    s.out.extend_from_slice(line.as_bytes());
    s.out.push(b'\n');
}

fn numbers(out: &mut String, name: &str, values: &[i64]) {
    out.push_str(",\"");
    out.push_str(name);
    out.push_str("\":[");
    for (i, v) in values.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        out.push_str(&v.to_string());
    }
    out.push(']');
}

fn counts(out: &mut String) {
    let s = st();
    let current = CURRENT.load(Relaxed);
    if current >= 0 {
        let since = SINCE.load(Relaxed);
        if since > s.largest[current as usize] {
            s.largest[current as usize] = since;
        }
    }
    out.push_str(&format!(
        ",\"ops\":{},\"statements\":{},\"allocated\":{},\"peak\":{},\"maxDepth\":{},\"truncated\":{}",
        s.operations,
        s.statements,
        ALLOCATED.load(Relaxed),
        PEAK.load(Relaxed),
        s.max_depth,
        s.truncated
    ));
    numbers(out, "hits", &s.hits);
    numbers(out, "bytes", &s.bytes);
    numbers(out, "largest", &s.largest);
    numbers(out, "work", &s.work);
    out.push_str(",\"sites\":[");
    for i in 0..s.site_calls.len() {
        if i > 0 {
            out.push(',');
        }
        out.push_str(&format!("[{},{}]", s.site_calls[i], s.site_work[i]));
    }
    out.push(']');
}

/// Ends the run: everything recorded, then how it ended, written out at once.
pub fn finish(status: &str, error: &str, line: i64) -> ! {
    ACTIVE.store(false, Relaxed);
    QUIET.store(true, Relaxed);
    let mut j = String::from("{\"end\":");
    escape(status, &mut j);
    j.push_str(",\"error\":");
    escape(error, &mut j);
    j.push_str(&format!(",\"statement\":{}", CURRENT.load(Relaxed)));
    if line > 0 {
        j.push_str(&format!(",\"line\":{}", line));
    }
    counts(&mut j);
    j.push('}');
    emit(&j);
    write_out();
    std::process::exit(0)
}

fn write_out() {
    let s = st();
    let stdout = std::io::stdout();
    let mut lock = stdout.lock();
    // A line of the student's own output may not have ended: the protocol starts afresh.
    let _ = lock.write_all(b"\n");
    let _ = lock.write_all(&s.out);
    let _ = lock.flush();
}

// ---- counting ----------------------------------------------------------------------------------

/// One statement about to run.
#[inline]
pub fn hit(id: usize) {
    if !ACTIVE.load(Relaxed) {
        return;
    }
    let s = st();
    let current = CURRENT.load(Relaxed);
    if current >= 0 {
        let since = SINCE.load(Relaxed);
        if since > s.largest[current as usize] {
            s.largest[current as usize] = since;
        }
    }
    SINCE.store(0, Relaxed);
    CURRENT.store(id as i64, Relaxed);
    s.hits[id] += 1;
    s.statements += 1;
    s.operations += 1;
    if s.operations > s.budget {
        let message = format!("Execution budget of {} operations was exceeded.", s.budget);
        finish("BudgetExceeded", &message, 0);
    }
}

/// Work done inside a library call the current statement made, at call site `site`.
pub fn charge(site: usize, work: i64) {
    if !ACTIVE.load(Relaxed) {
        return;
    }
    let s = st();
    if site < s.site_calls.len() {
        s.site_calls[site] += 1;
    }
    add_work(site, work);
}

fn add_work(site: usize, work: i64) {
    if work <= 0 || !ACTIVE.load(Relaxed) {
        return;
    }
    let s = st();
    if site < s.site_work.len() {
        s.site_work[site] += work;
    }
    let current = CURRENT.load(Relaxed);
    if current >= 0 {
        s.work[current as usize] += work;
    }
    s.operations += work;
    if s.operations > s.budget {
        let message = format!("Execution budget of {} operations was exceeded.", s.budget);
        finish("BudgetExceeded", &message, 0);
    }
}

/// Passes `value` on, after `charged` — the charge — has been made.
#[inline]
pub fn after<T>(_charged: (), value: T) -> T {
    value
}

/// One call's frame: counts depth, and stops runaway recursion before the stack runs out.
pub struct Frame(bool);
impl Frame {
    pub fn enter() -> Frame {
        if !ACTIVE.load(Relaxed) {
            return Frame(false);
        }
        let s = st();
        s.depth += 1;
        if s.depth > s.max_depth {
            s.max_depth = s.depth;
        }
        if s.depth > s.depth_limit {
            let message = format!(
                "Recursion went deeper than {} calls. That usually means a missing or unreachable base case.",
                s.depth_limit
            );
            finish("RecursionLimitExceeded", &message, 0);
        }
        Frame(true)
    }
}
impl Drop for Frame {
    fn drop(&mut self) {
        if self.0 && ACTIVE.load(Relaxed) {
            st().depth -= 1;
        }
    }
}

// ---- library costs -----------------------------------------------------------------------------

/// The capture of a value, to choose by its type what to do with it: `(&&&W(&x)).method()`
/// finds the most specific method that applies — the autoref trick, which lets one line of
/// generated code read any type without knowing it.
pub struct W<'a, T: ?Sized>(pub &'a T);

/// Collections a library call scans or copies whole, and how many elements that is.
pub trait Lengthy {
    fn length(&self) -> usize;
}
macro_rules! lengthy {
    ($($t:ty => $($g:ident),*);* $(;)?) => { $( impl<$($g),*> Lengthy for $t { fn length(&self) -> usize { self.len() } } )* };
}
lengthy! { Vec<T> => T; VecDeque<T> => T; [T] => T; HashSet<T> => T; BTreeSet<T> => T; BinaryHeap<T> => T;
           HashMap<K, V> => K, V; BTreeMap<K, V> => K, V }
impl Lengthy for String { fn length(&self) -> usize { self.len() } }
impl Lengthy for str { fn length(&self) -> usize { self.len() } }
impl<T, const N: usize> Lengthy for [T; N] { fn length(&self) -> usize { N } }
impl<T: Lengthy + ?Sized> Lengthy for &T { fn length(&self) -> usize { (**self).length() } }
impl<T: Lengthy + ?Sized> Lengthy for &mut T { fn length(&self) -> usize { (**self).length() } }
impl<T: Lengthy + ?Sized> Lengthy for Box<T> { fn length(&self) -> usize { (**self).length() } }

/// Sequences a search walks: `contains` and `find` scan these, but not a set or a map.
pub trait Sequence {
    fn elements(&self) -> usize;
}
impl<T> Sequence for Vec<T> { fn elements(&self) -> usize { self.len() } }
impl<T> Sequence for VecDeque<T> { fn elements(&self) -> usize { self.len() } }
impl<T> Sequence for [T] { fn elements(&self) -> usize { self.len() } }
impl<T, const N: usize> Sequence for [T; N] { fn elements(&self) -> usize { N } }
impl Sequence for String { fn elements(&self) -> usize { self.len() } }
impl Sequence for str { fn elements(&self) -> usize { self.len() } }
impl<T: Sequence + ?Sized> Sequence for &T { fn elements(&self) -> usize { (**self).elements() } }
impl<T: Sequence + ?Sized> Sequence for &mut T { fn elements(&self) -> usize { (**self).elements() } }

pub trait ScanCost { fn scan_cost(&self) -> i64; }
impl<'a, T: Sequence + ?Sized> ScanCost for &&W<'a, T> { fn scan_cost(&self) -> i64 { self.0.elements() as i64 } }
pub trait ScanFree { fn scan_cost(&self) -> i64; }
impl<'a, T: ?Sized> ScanFree for W<'a, T> { fn scan_cost(&self) -> i64 { 0 } }

pub trait CopyCost { fn copy_cost(&self) -> i64; }
impl<'a, T: Lengthy + ?Sized> CopyCost for &&W<'a, T> { fn copy_cost(&self) -> i64 { self.0.length() as i64 } }
pub trait CopyFree { fn copy_cost(&self) -> i64; }
impl<'a, T: ?Sized> CopyFree for W<'a, T> { fn copy_cost(&self) -> i64 { 0 } }

pub trait SortCost { fn sort_cost(&self) -> i64; }
impl<'a, T: Sequence + ?Sized> SortCost for &&W<'a, T> {
    fn sort_cost(&self) -> i64 {
        let n = self.0.elements() as f64;
        if n > 1.0 { (n * n.log2()) as i64 } else { 0 }
    }
}
pub trait SortFree { fn sort_cost(&self) -> i64; }
impl<'a, T: ?Sized> SortFree for W<'a, T> { fn sort_cost(&self) -> i64 { 0 } }

/// Inserting or removing at a position moves what is after it: in a Vec or String, all of
/// it; in a VecDeque, the shorter side. `shifter()` looks at the collection once, and the
/// index passes through `at`, so it is evaluated exactly once, where it was written.
pub struct Shift { len: usize, deque: bool }
impl Shift {
    pub fn at(&self, site: usize, index: usize) -> usize {
        let after = self.len.saturating_sub(index);
        charge(site, if self.deque { after.min(index) as i64 } else { after as i64 });
        index
    }
}
pub struct NoShift;
impl NoShift {
    pub fn at<I>(&self, _site: usize, index: I) -> I { index }
}
pub trait Shifts { fn shift_len(&self) -> (usize, bool); }
impl<T> Shifts for Vec<T> { fn shift_len(&self) -> (usize, bool) { (self.len(), false) } }
impl<T> Shifts for VecDeque<T> { fn shift_len(&self) -> (usize, bool) { (self.len(), true) } }
impl Shifts for String { fn shift_len(&self) -> (usize, bool) { (self.len(), false) } }
impl<T: Shifts + ?Sized> Shifts for &T { fn shift_len(&self) -> (usize, bool) { (**self).shift_len() } }
impl<T: Shifts + ?Sized> Shifts for &mut T { fn shift_len(&self) -> (usize, bool) { (**self).shift_len() } }
pub trait ShiftOf { fn shifter(&self) -> Shift; }
impl<'a, T: Shifts + ?Sized> ShiftOf for &&W<'a, T> {
    fn shifter(&self) -> Shift { let (len, deque) = self.0.shift_len(); Shift { len, deque } }
}
pub trait ShiftNone { fn shifter(&self) -> NoShift; }
impl<'a, T: ?Sized> ShiftNone for W<'a, T> { fn shifter(&self) -> NoShift { NoShift } }

/// An iterator that charges every element it hands on: the work of `iter().sum()` or
/// `position`, however the chain goes on.
pub struct Counted<I> { site: usize, inner: I }
pub fn counted<I: Iterator>(site: usize, inner: I) -> Counted<I> {
    charge(site, 0);
    Counted { site, inner }
}
impl<I: Iterator> Iterator for Counted<I> {
    type Item = I::Item;
    fn next(&mut self) -> Option<I::Item> {
        let item = self.inner.next();
        if item.is_some() { add_work(self.site, 1); }
        item
    }
    fn size_hint(&self) -> (usize, Option<usize>) { self.inner.size_hint() }
}
impl<I: DoubleEndedIterator> DoubleEndedIterator for Counted<I> {
    fn next_back(&mut self) -> Option<I::Item> {
        let item = self.inner.next_back();
        if item.is_some() { add_work(self.site, 1); }
        item
    }
}
impl<I: ExactSizeIterator> ExactSizeIterator for Counted<I> {}
impl<I: std::iter::FusedIterator> std::iter::FusedIterator for Counted<I> {}

// ---- reading values (LeetCode's format) --------------------------------------------------------

/// A parsed value: a number, text, true, false, null, or a list of values.
#[derive(Debug, Clone)]
pub enum Value { Null, Number(String), Text(String), Bool(bool), List(Vec<Value>) }

pub fn parse(text: &str) -> Value {
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    parse_at(&chars, &mut i)
}

fn parse_at(s: &[char], i: &mut usize) -> Value {
    while *i < s.len() && s[*i].is_whitespace() { *i += 1; }
    if *i >= s.len() { return Value::Null; }
    match s[*i] {
        '[' => {
            *i += 1;
            let mut items = Vec::new();
            loop {
                while *i < s.len() && s[*i].is_whitespace() { *i += 1; }
                if *i >= s.len() { break; }
                if s[*i] == ']' { *i += 1; break; }
                items.push(parse_at(s, i));
                while *i < s.len() && s[*i].is_whitespace() { *i += 1; }
                if *i < s.len() && s[*i] == ',' { *i += 1; }
            }
            Value::List(items)
        }
        '"' => {
            *i += 1;
            let mut text = String::new();
            while *i < s.len() && s[*i] != '"' {
                if s[*i] == '\\' && *i + 1 < s.len() {
                    *i += 1;
                    text.push(match s[*i] { 'n' => '\n', 't' => '\t', 'r' => '\r', c => c });
                } else {
                    text.push(s[*i]);
                }
                *i += 1;
            }
            *i += 1;
            Value::Text(text)
        }
        _ => {
            let start = *i;
            while *i < s.len() && !matches!(s[*i], ',' | ']') && !s[*i].is_whitespace() { *i += 1; }
            let word: String = s[start..*i].iter().collect();
            match word.as_str() {
                "true" => Value::Bool(true),
                "false" => Value::Bool(false),
                "null" => Value::Null,
                _ => Value::Number(word),
            }
        }
    }
}

/// A type an input can be built as.
pub trait FromLeet: Sized {
    fn from_leet(v: &Value) -> Result<Self, String>;
}
macro_rules! from_number {
    ($($t:ty),*) => { $( impl FromLeet for $t {
        fn from_leet(v: &Value) -> Result<Self, String> {
            match v {
                Value::Number(n) => n.parse::<$t>().map_err(|e| format!("'{}' is not a {}: {}", n, stringify!($t), e)),
                other => Err(format!("expected a number, found {:?}", other)),
            }
        }
    } )* };
}
from_number!(i8, i16, i32, i64, i128, isize, u8, u16, u32, u64, u128, usize, f32, f64);
impl FromLeet for bool {
    fn from_leet(v: &Value) -> Result<Self, String> {
        match v { Value::Bool(b) => Ok(*b), other => Err(format!("expected true or false, found {:?}", other)) }
    }
}
impl FromLeet for char {
    fn from_leet(v: &Value) -> Result<Self, String> {
        match v {
            Value::Text(t) => t.chars().next().ok_or_else(|| "expected a character, found \"\"".to_string()),
            other => Err(format!("expected a character, found {:?}", other)),
        }
    }
}
impl FromLeet for String {
    fn from_leet(v: &Value) -> Result<Self, String> {
        match v { Value::Text(t) => Ok(t.clone()), other => Err(format!("expected text, found {:?}", other)) }
    }
}
impl<T: FromLeet> FromLeet for Vec<T> {
    fn from_leet(v: &Value) -> Result<Self, String> {
        match v {
            Value::List(items) => items.iter().map(T::from_leet).collect(),
            // LeetCode writes a list of characters both ways: "abc" and ["a","b","c"].
            Value::Text(t) => t.chars().map(|c| T::from_leet(&Value::Text(c.to_string()))).collect(),
            other => Err(format!("expected a list, found {:?}", other)),
        }
    }
}

/// LeetCode's binary tree node, as the student's file or the engine defines it.
pub trait TreeShape: Sized {
    fn make(val: i32) -> Self;
    fn val(&self) -> i32;
    fn left(&self) -> &Option<Rc<RefCell<Self>>>;
    fn right(&self) -> &Option<Rc<RefCell<Self>>>;
    fn set_left(&mut self, node: Option<Rc<RefCell<Self>>>);
    fn set_right(&mut self, node: Option<Rc<RefCell<Self>>>);
}

/// LeetCode's linked list node.
pub trait ListShape: Sized {
    fn make(val: i32) -> Self;
    fn val(&self) -> i32;
    fn next(&self) -> &Option<Box<Self>>;
    fn set_next(&mut self, node: Option<Box<Self>>);
}

impl<T: TreeShape> FromLeet for Option<Rc<RefCell<T>>> {
    fn from_leet(v: &Value) -> Result<Self, String> {
        let items = match v { Value::List(items) => items, Value::Null => return Ok(None), other => return Err(format!("expected a tree, found {:?}", other)) };
        let number = |v: &Value| i32::from_leet(v);
        let first = match items.first() { None | Some(Value::Null) => return Ok(None), Some(first) => first };
        let root = Rc::new(RefCell::new(T::make(number(first)?)));
        let mut waiting = VecDeque::new();
        waiting.push_back(root.clone());
        let mut i = 1;
        while let Some(node) = waiting.pop_front() {
            if i >= items.len() { break; }
            if !matches!(items[i], Value::Null) {
                let child = Rc::new(RefCell::new(T::make(number(&items[i])?)));
                node.borrow_mut().set_left(Some(child.clone()));
                waiting.push_back(child);
            }
            i += 1;
            if i < items.len() && !matches!(items[i], Value::Null) {
                let child = Rc::new(RefCell::new(T::make(number(&items[i])?)));
                node.borrow_mut().set_right(Some(child.clone()));
                waiting.push_back(child);
            }
            i += 1;
        }
        Ok(Some(root))
    }
}

impl<T: ListShape> FromLeet for Option<Box<T>> {
    fn from_leet(v: &Value) -> Result<Self, String> {
        let items = match v { Value::List(items) => items, Value::Null => return Ok(None), other => return Err(format!("expected a list, found {:?}", other)) };
        let mut head: Option<Box<T>> = None;
        for item in items.iter().rev() {
            let mut node = Box::new(T::make(i32::from_leet(item)?));
            node.set_next(head.take());
            head = Some(node);
        }
        Ok(head)
    }
}

/// Builds input `index` as `T`. A failure is the visualizer's, said so.
pub fn input<T: FromLeet>(inputs: &[String], index: usize) -> T {
    let text = inputs.get(index).map(String::as_str).unwrap_or("");
    match T::from_leet(&parse(text)) {
        Ok(value) => value,
        Err(error) => finish("InputError", &error, 0),
    }
}

// ---- writing values (LeetCode's format) --------------------------------------------------------

fn decimal(d: f64) -> String {
    if d.is_nan() { return "NaN".into(); }
    if d.is_infinite() { return if d > 0.0 { "Infinity".into() } else { "-Infinity".into() }; }
    let mut s = format!("{:.5}", d);
    while s.ends_with('0') { s.pop(); }
    if s.ends_with('.') { s.push('0'); }
    s
}

/// A value as LeetCode prints a result.
pub trait ToLeet {
    fn to_leet(&self, out: &mut String);
    /// What None is printed as: an empty tree or list is [], anything else null.
    fn none_text() -> &'static str where Self: Sized { "null" }
}
macro_rules! to_number {
    ($($t:ty),*) => { $( impl ToLeet for $t { fn to_leet(&self, out: &mut String) { out.push_str(&self.to_string()); } } )* };
}
to_number!(i8, i16, i32, i64, i128, isize, u8, u16, u32, u64, u128, usize);
impl ToLeet for f64 { fn to_leet(&self, out: &mut String) { out.push_str(&decimal(*self)); } }
impl ToLeet for f32 { fn to_leet(&self, out: &mut String) { out.push_str(&decimal(*self as f64)); } }
impl ToLeet for bool { fn to_leet(&self, out: &mut String) { out.push_str(if *self { "true" } else { "false" }); } }
impl ToLeet for char { fn to_leet(&self, out: &mut String) { escape(&self.to_string(), out); } }
impl ToLeet for String { fn to_leet(&self, out: &mut String) { escape(self, out); } }
impl ToLeet for str { fn to_leet(&self, out: &mut String) { escape(self, out); } }
impl<T: ToLeet + ?Sized> ToLeet for &T { fn to_leet(&self, out: &mut String) { (**self).to_leet(out) } }
fn leet_list<'a, T: ToLeet + 'a>(items: impl Iterator<Item = &'a T>, out: &mut String) {
    out.push('[');
    for (i, item) in items.enumerate() {
        if i > 0 { out.push(','); }
        item.to_leet(out);
    }
    out.push(']');
}
impl<T: ToLeet> ToLeet for Vec<T> { fn to_leet(&self, out: &mut String) { leet_list(self.iter(), out) } }
impl<T: ToLeet> ToLeet for [T] { fn to_leet(&self, out: &mut String) { leet_list(self.iter(), out) } }
impl<T: ToLeet> ToLeet for VecDeque<T> { fn to_leet(&self, out: &mut String) { leet_list(self.iter(), out) } }
impl<T: ToLeet, const N: usize> ToLeet for [T; N] { fn to_leet(&self, out: &mut String) { leet_list(self.iter(), out) } }
impl<A: ToLeet, B: ToLeet> ToLeet for (A, B) {
    fn to_leet(&self, out: &mut String) { out.push('['); self.0.to_leet(out); out.push(','); self.1.to_leet(out); out.push(']'); }
}
impl<T: ToLeet> ToLeet for Option<T> {
    fn to_leet(&self, out: &mut String) {
        match self { Some(v) => v.to_leet(out), None => out.push_str(T::none_text()) }
    }
}
impl<T: TreeShape> ToLeet for Rc<RefCell<T>> {
    fn to_leet(&self, out: &mut String) {
        let mut order: Vec<Option<Rc<RefCell<T>>>> = Vec::new();
        let mut waiting = VecDeque::new();
        waiting.push_back(Some(self.clone()));
        while let Some(node) = waiting.pop_front() {
            if order.len() > 100_000 { break; }
            if let Some(n) = &node {
                let b = n.borrow();
                waiting.push_back(b.left().clone());
                waiting.push_back(b.right().clone());
            }
            order.push(node);
        }
        while matches!(order.last(), Some(None)) { order.pop(); }
        out.push('[');
        for (i, node) in order.iter().enumerate() {
            if i > 0 { out.push(','); }
            match node { Some(n) => out.push_str(&n.borrow().val().to_string()), None => out.push_str("null") }
        }
        out.push(']');
    }
    fn none_text() -> &'static str { "[]" }
}
impl<T: ListShape> ToLeet for Box<T> {
    fn to_leet(&self, out: &mut String) {
        out.push('[');
        let mut node: Option<&T> = Some(&**self);
        let mut i = 0;
        while let Some(n) = node {
            if i > 0 { out.push(','); }
            out.push_str(&n.val().to_string());
            node = n.next().as_deref();
            i += 1;
            if i > 100_000 { break; }
        }
        out.push(']');
    }
    fn none_text() -> &'static str { "[]" }
}

/// A result, as text: LeetCode's format when the type has one, else its Debug form.
pub trait ResultLeet { fn result_text(&self) -> String; }
impl<'a, T: ToLeet> ResultLeet for &&W<'a, T> {
    fn result_text(&self) -> String { let mut s = String::new(); self.0.to_leet(&mut s); s }
}
pub trait ResultDebug { fn result_text(&self) -> String; }
impl<'a, T: Debug> ResultDebug for &W<'a, T> { fn result_text(&self) -> String { format!("{:?}", self.0) } }
pub trait ResultOpaque { fn result_text(&self) -> String; }
impl<'a, T> ResultOpaque for W<'a, T> { fn result_text(&self) -> String { format!("<{}>", short_type(type_name::<T>())) } }

// ---- recording a step --------------------------------------------------------------------------

const MAX_ITEMS: usize = 1000;
const MAX_OBJECTS: usize = 300;
const MAX_TEXT: usize = 200;

/// "alloc::vec::Vec<std::collections::hash::map::HashMap<i32, usize>>" → "Vec<HashMap<i32, usize>>".
pub fn short_type(full: &str) -> String {
    let mut out = String::new();
    let mut word = String::new();
    for c in full.chars() {
        if c.is_alphanumeric() || c == '_' || c == ':' {
            word.push(c);
        } else {
            out.push_str(word.rsplit("::").next().unwrap_or(""));
            word.clear();
            out.push(c);
        }
    }
    out.push_str(word.rsplit("::").next().unwrap_or(""));
    out
}

/// Every object reachable from one step's variables, written once each, by address.
pub struct Shot {
    written: HashSet<usize>,
    objects: String,
    first_object: bool,
}

/// A value the run view can draw.
pub trait Snap {
    /// Writes the value as `{"x": text}` or, for an object, `{"r": address, "x": text}`,
    /// writing the object itself into the shot.
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8);
    /// A short text for the value inside another: a tuple's parts, a map's keys.
    fn text(&self) -> String;
    /// Whether None of this type is an empty tree or list, drawn as null.
    fn is_node() -> bool where Self: Sized { false }
}

fn scalar(j: &mut String, text: &str) {
    j.push_str("{\"x\":");
    escape(text, j);
    j.push('}');
}

macro_rules! snap_integer {
    ($($t:ty),*) => { $( impl Snap for $t {
        fn value(&self, j: &mut String, _: &mut Shot, _: u8) {
            j.push_str("{\"x\":");
            escape(&self.to_string(), j);
            if let Ok(n) = i64::try_from(*self) { j.push_str(&format!(",\"num\":{}", n)); }
            j.push('}');
        }
        fn text(&self) -> String { self.to_string() }
    } )* };
}
snap_integer!(i8, i16, i32, i64, i128, isize, u8, u16, u32, u64, u128, usize);
impl Snap for f64 {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &decimal(*self)) }
    fn text(&self) -> String { decimal(*self) }
}
impl Snap for f32 {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &decimal(*self as f64)) }
    fn text(&self) -> String { decimal(*self as f64) }
}
impl Snap for bool {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, if *self { "true" } else { "false" }) }
    fn text(&self) -> String { self.to_string() }
}
impl Snap for char {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &self.to_string()) }
    fn text(&self) -> String { format!("'{}'", self) }
}
fn quoted(s: &str) -> String {
    if s.chars().count() > MAX_TEXT { format!("\"{}…\"", s.chars().take(MAX_TEXT).collect::<String>()) } else { format!("\"{}\"", s) }
}
impl Snap for str {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &quoted(self)) }
    fn text(&self) -> String { quoted(self) }
}
impl Snap for String {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) { self.as_str().value(j, shot, role) }
    fn text(&self) -> String { quoted(self) }
}
impl<T: Snap + ?Sized> Snap for &T {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) { (**self).value(j, shot, role) }
    fn text(&self) -> String { (**self).text() }
}
impl<T: Snap + ?Sized> Snap for &mut T {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) { (**self).value(j, shot, role) }
    fn text(&self) -> String { (**self).text() }
}
impl<T: Snap> Snap for Reverse<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) { self.0.value(j, shot, role) }
    fn text(&self) -> String { self.0.text() }
}
impl<A: Snap, B: Snap> Snap for (A, B) {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &self.text()) }
    fn text(&self) -> String { format!("({}, {})", self.0.text(), self.1.text()) }
}
impl<A: Snap, B: Snap, C: Snap> Snap for (A, B, C) {
    fn value(&self, j: &mut String, _: &mut Shot, _: u8) { scalar(j, &self.text()) }
    fn text(&self) -> String { format!("({}, {}, {})", self.0.text(), self.1.text(), self.2.text()) }
}
impl<T: Snap> Snap for Option<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) {
        match self {
            None => scalar(j, if T::is_node() { "null" } else { "None" }),
            Some(v) if T::is_node() => v.value(j, shot, role),
            Some(_) => scalar(j, &self.text()),
        }
    }
    fn text(&self) -> String {
        match self { None => "None".into(), Some(v) if T::is_node() => v.text(), Some(v) => format!("Some({})", v.text()) }
    }
}

impl Shot {
    pub fn new() -> Shot { Shot { written: HashSet::new(), objects: String::new(), first_object: true } }

    /// Starts writing the object at `address`; false when it is already written or there are enough.
    fn begin(&mut self, address: usize) -> bool {
        self.written.len() < MAX_OBJECTS && self.written.insert(address)
    }

    fn add(&mut self, object: String) {
        if !self.first_object { self.objects.push(','); }
        self.first_object = false;
        self.objects.push_str(&object);
    }
}

fn reference(j: &mut String, address: usize, text: &str) {
    j.push_str(&format!("{{\"r\":{},\"x\":", address));
    escape(text, j);
    j.push('}');
}

/// A sequence drawn as an array, a stack (top first) or a queue.
fn sequence<'a, T: Snap + 'a>(address: usize, kind: &str, type_name: &str, len: usize, bytes: usize,
                               items: impl DoubleEndedIterator<Item = &'a T>, top_first: bool,
                               j: &mut String, shot: &mut Shot) {
    reference(j, address, &format!("({})", len));
    if !shot.begin(address) { return; }
    let mut o = format!("{{\"id\":{},\"k\":\"{}\",\"t\":", address, kind);
    escape(type_name, &mut o);
    o.push_str(&format!(",\"c\":{},\"b\":{},\"i\":[", len, bytes));
    let write = |i: usize, item: &T, o: &mut String, shot: &mut Shot| {
        if i > 0 { o.push(','); }
        item.value(o, shot, 0);
    };
    if top_first {
        for (i, item) in items.rev().take(MAX_ITEMS).enumerate() { write(i, item, &mut o, shot); }
    } else {
        for (i, item) in items.take(MAX_ITEMS).enumerate() { write(i, item, &mut o, shot); }
    }
    o.push_str("]}");
    shot.add(o);
}

/// Role 1: a Vec the code only pushes onto and pops from, drawn as a stack.
pub const STACK: u8 = 1;

impl<T: Snap> Snap for Vec<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) {
        let kind = if role == STACK { "stack" } else { "array" };
        sequence(self as *const _ as usize, kind, &short_type(type_name::<Self>()), self.len(),
                 24 + self.capacity() * std::mem::size_of::<T>(), self.iter(), role == STACK, j, shot);
    }
    fn text(&self) -> String { format!("[{}]", self.len()) }
}
impl<T: Snap> Snap for [T] {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) {
        sequence(self.as_ptr() as usize, if role == STACK { "stack" } else { "array" }, &format!("[{}]", short_type(type_name::<T>())),
                 self.len(), self.len() * std::mem::size_of::<T>(), self.iter(), role == STACK, j, shot);
    }
    fn text(&self) -> String { format!("[{}]", self.len()) }
}
impl<T: Snap, const N: usize> Snap for [T; N] {
    fn value(&self, j: &mut String, shot: &mut Shot, role: u8) { self[..].value(j, shot, role) }
    fn text(&self) -> String { format!("[{}]", N) }
}
impl<T: Snap> Snap for VecDeque<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        sequence(self as *const _ as usize, "queue", &short_type(type_name::<Self>()), self.len(),
                 32 + self.capacity() * std::mem::size_of::<T>(), self.iter(), false, j, shot);
    }
    fn text(&self) -> String { format!("[{}]", self.len()) }
}

fn entries<'a, K: Snap + 'a, V: Snap + 'a>(address: usize, type_name: &str, len: usize, bytes: usize,
                                          items: impl Iterator<Item = (&'a K, &'a V)>, j: &mut String, shot: &mut Shot) {
    reference(j, address, &format!("({})", len));
    if !shot.begin(address) { return; }
    let mut o = format!("{{\"id\":{},\"k\":\"map\",\"t\":", address);
    escape(type_name, &mut o);
    o.push_str(&format!(",\"c\":{},\"b\":{},\"e\":[", len, bytes));
    for (i, (k, v)) in items.take(MAX_ITEMS).enumerate() {
        if i > 0 { o.push(','); }
        o.push('[');
        k.value(&mut o, shot, 0);
        o.push(',');
        v.value(&mut o, shot, 0);
        o.push(']');
    }
    o.push_str("]}");
    shot.add(o);
}

fn members<'a, T: Snap + 'a>(address: usize, kind: &str, arity: u8, type_name: &str, len: usize, bytes: usize,
                             items: impl Iterator<Item = &'a T>, j: &mut String, shot: &mut Shot) {
    reference(j, address, &format!("({})", len));
    if !shot.begin(address) { return; }
    let mut o = format!("{{\"id\":{},\"k\":\"{}\",\"t\":", address, kind);
    escape(type_name, &mut o);
    if arity > 0 { o.push_str(&format!(",\"ar\":{}", arity)); }
    o.push_str(&format!(",\"c\":{},\"b\":{},\"i\":[", len, bytes));
    for (i, item) in items.take(MAX_ITEMS).enumerate() {
        if i > 0 { o.push(','); }
        item.value(&mut o, shot, 0);
    }
    o.push_str("]}");
    shot.add(o);
}

impl<K: Snap, V: Snap, S> Snap for HashMap<K, V, S> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        entries(self as *const _ as usize, &short_type(type_name::<HashMap<K, V>>()), self.len(),
                48 + self.capacity() * (std::mem::size_of::<(K, V)>() + 1), self.iter(), j, shot)
    }
    fn text(&self) -> String { format!("{{{}}}", self.len()) }
}
impl<K: Snap, V: Snap> Snap for BTreeMap<K, V> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        entries(self as *const _ as usize, &short_type(type_name::<Self>()), self.len(),
                24 + self.len() * (std::mem::size_of::<(K, V)>() + 16), self.iter(), j, shot)
    }
    fn text(&self) -> String { format!("{{{}}}", self.len()) }
}
impl<T: Snap, S> Snap for HashSet<T, S> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        members(self as *const _ as usize, "set", 0, &short_type(type_name::<HashSet<T>>()), self.len(),
                48 + self.capacity() * (std::mem::size_of::<T>() + 1), self.iter(), j, shot)
    }
    fn text(&self) -> String { format!("{{{}}}", self.len()) }
}
impl<T: Snap> Snap for BTreeSet<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        members(self as *const _ as usize, "set", 0, &short_type(type_name::<Self>()), self.len(),
                24 + self.len() * (std::mem::size_of::<T>() + 16), self.iter(), j, shot)
    }
    fn text(&self) -> String { format!("{{{}}}", self.len()) }
}
impl<T: Snap + Ord> Snap for BinaryHeap<T> {
    // iter() visits the heap's own array in order, so position i's children are at 2i+1 and 2i+2.
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        members(self as *const _ as usize, "heap", 2, &short_type(type_name::<Self>()), self.len(),
                24 + self.capacity() * std::mem::size_of::<T>(), self.iter(), j, shot)
    }
    fn text(&self) -> String { format!("[{}]", self.len()) }
}

impl<T: TreeShape> Snap for Rc<RefCell<T>> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        let address = Rc::as_ptr(self) as usize;
        reference(j, address, &self.text());
        if !shot.begin(address) { return; }
        let mut o = format!("{{\"id\":{},\"k\":\"tree\",\"t\":\"TreeNode\",\"b\":{},\"f\":[", address,
                            std::mem::size_of::<T>() + 24);
        match self.try_borrow() {
            Ok(node) => {
                o.push_str("[\"val\",");
                node.val().value(&mut o, shot, 0);
                o.push_str("],[\"left\",");
                node.left().value(&mut o, shot, 0);
                o.push_str("],[\"right\",");
                node.right().value(&mut o, shot, 0);
                o.push(']');
            }
            // Borrowed mutably at this moment, by the code being stepped through.
            Err(_) => o.push_str("[\"val\",{\"x\":\"(borrowed)\"}]"),
        }
        o.push_str("]}");
        shot.add(o);
    }
    fn text(&self) -> String {
        match self.try_borrow() { Ok(node) => format!("TreeNode({})", node.val()), Err(_) => "TreeNode(borrowed)".into() }
    }
    fn is_node() -> bool { true }
}

impl<T: ListShape> Snap for Box<T> {
    fn value(&self, j: &mut String, shot: &mut Shot, _: u8) {
        let address = &**self as *const T as usize;
        reference(j, address, &self.text());
        if !shot.begin(address) { return; }
        let mut o = format!("{{\"id\":{},\"k\":\"listnode\",\"t\":\"ListNode\",\"b\":{},\"f\":[[\"val\",", address,
                            std::mem::size_of::<T>());
        self.val().value(&mut o, shot, 0);
        o.push_str("],[\"next\",");
        self.next().value(&mut o, shot, 0);
        o.push_str("]]}");
        shot.add(o);
    }
    fn text(&self) -> String { format!("ListNode({})", self.val()) }
    fn is_node() -> bool { true }
}

/// A variable at a step: what can be read of it, or why it cannot be.
pub enum Cap<'a> {
    Snap(&'a dyn Snap),
    Debug(&'a dyn Debug, &'static str),
    Opaque(&'static str),
    /// The compiler would not let the variable be read here: moved, borrowed, or not set yet.
    Gone(&'static str),
}

pub trait CapSnap<'a> { fn cap(&self) -> Cap<'a>; }
impl<'a, T: Snap> CapSnap<'a> for &&W<'a, T> { fn cap(&self) -> Cap<'a> { Cap::Snap(self.0) } }
pub trait CapDebug<'a> { fn cap(&self) -> Cap<'a>; }
impl<'a, T: Debug> CapDebug<'a> for &W<'a, T> { fn cap(&self) -> Cap<'a> { Cap::Debug(self.0, type_name::<T>()) } }
pub trait CapOpaque<'a> { fn cap(&self) -> Cap<'a>; }
impl<'a, T: ?Sized> CapOpaque<'a> for W<'a, T> { fn cap(&self) -> Cap<'a> { Cap::Opaque(type_name::<T>()) } }

pub struct Var<'a> { name: &'static str, role: u8, cap: Cap<'a> }
pub fn v<'a>(name: &'static str, role: u8, cap: Cap<'a>) -> Var<'a> { Var { name, role, cap } }

fn write_cap(j: &mut String, cap: &Cap, role: u8, shot: &mut Shot) -> String {
    match cap {
        Cap::Snap(value) => { value.value(j, shot, role); String::new() }
        Cap::Debug(value, type_name) => {
            let text = format!("{:?}", value);
            let text = if text.chars().count() > MAX_TEXT { format!("{}…", text.chars().take(MAX_TEXT).collect::<String>()) } else { text };
            scalar(j, &text);
            short_type(type_name)
        }
        Cap::Opaque(type_name) => { scalar(j, &format!("<{}>", short_type(type_name))); short_type(type_name) }
        Cap::Gone(why) => { scalar(j, &format!("({})", why)); String::new() }
    }
}

/// One statement about to run, with the variables it can see, when recording.
pub fn step(id: usize, vars: &[Var]) {
    hit(id);
    if !ACTIVE.load(Relaxed) || !st().stepping { return; }
    let s = st();
    if s.steps >= s.max_steps { s.truncated = true; return; }
    s.steps += 1;
    let _quiet = Quiet::new();
    let mut shot = Shot::new();
    let mut j = format!("{{\"s\":{},\"d\":{},\"a\":{},\"v\":[", id, s.depth, ALLOCATED.load(Relaxed));
    for (i, var) in vars.iter().enumerate() {
        if i > 0 { j.push(','); }
        j.push_str("{\"n\":");
        escape(var.name, &mut j);
        j.push_str(",\"v\":");
        let type_name = write_cap(&mut j, &var.cap, var.role, &mut shot);
        j.push_str(",\"t\":");
        escape(&type_name, &mut j);
        j.push('}');
    }
    j.push_str("],\"o\":[");
    j.push_str(&shot.objects);
    j.push_str("]}");
    emit(&j);
}

/// A value being returned, noted for the recursion tree.
pub fn returned(cap: Cap) {
    let s = st();
    if !ACTIVE.load(Relaxed) || !s.stepping || s.steps >= s.max_steps { return; }
    let _quiet = Quiet::new();
    let mut shot = Shot::new();
    let mut j = String::new();
    write_cap(&mut j, &cap, 0, &mut shot);
    emit(&format!("{{\"ret\":{},\"after\":{},\"v\":{}}}", s.depth, s.steps, j));
}

// ---- the run -----------------------------------------------------------------------------------

/// Reads the run's settings — run|step, budget, depth limit, steps kept — and prepares to count.
pub fn configure(statements: usize, sites: usize, program: &str) {
    let args: Vec<String> = std::env::args().collect();
    let s = st();
    s.hits = vec![0; statements + 1];
    s.bytes = vec![0; statements + 1];
    s.largest = vec![0; statements + 1];
    s.work = vec![0; statements + 1];
    s.site_calls = vec![0; sites];
    s.site_work = vec![0; sites];
    s.out = Vec::with_capacity(1 << 16);
    s.program = program.to_string();
    BYTES.store(s.bytes.as_mut_ptr(), Relaxed);
    if let Some(mode) = args.get(1) { s.stepping = mode == "step"; }
    if let Some(budget) = args.get(2).and_then(|a| a.parse().ok()) { s.budget = budget; }
    if let Some(limit) = args.get(3).and_then(|a| a.parse().ok()) { s.depth_limit = limit; }
    if let Some(steps) = args.get(4).and_then(|a| a.parse().ok()) { s.max_steps = steps; }

    // A panic is noted, not printed: where it happened and what it said.
    std::panic::set_hook(Box::new(|info| {
        let _quiet = Quiet::new();
        let s = st();
        s.panic_message = if let Some(text) = info.payload().downcast_ref::<&str>() {
            text.to_string()
        } else if let Some(text) = info.payload().downcast_ref::<String>() {
            text.clone()
        } else {
            "a panic".to_string()
        };
        s.panic_line = match info.location() {
            Some(at) if at.file().ends_with(s.program.as_str()) => at.line() as i64,
            _ => -1,
        };
    }));
}

pub fn read_inputs() -> Vec<String> {
    let mut text = String::new();
    let _ = std::io::stdin().read_to_string(&mut text);
    text.lines().map(|l| l.trim_end_matches('\r').to_string()).collect()
}

/// Starts counting: everything from here is the student's code.
pub fn begin() {
    let s = st();
    s.operations = 0;
    s.statements = 0;
    s.depth = 0;
    s.max_depth = 0;
    ALLOCATED.store(0, Relaxed);
    HELD.store(0, Relaxed);
    PEAK.store(0, Relaxed);
    SINCE.store(0, Relaxed);
    CURRENT.store(-1, Relaxed);
    ACTIVE.store(true, Relaxed);
}

/// Ends a run that completed, with what it returned.
pub fn succeed(result: Option<String>) -> ! {
    ACTIVE.store(false, Relaxed);
    QUIET.store(true, Relaxed);
    let mut j = String::from("{\"end\":\"Completed\",\"result\":");
    match result { Some(text) => escape(&text, &mut j), None => j.push_str("null") }
    j.push_str(&format!(",\"statement\":{}", CURRENT.load(Relaxed)));
    counts(&mut j);
    j.push('}');
    emit(&j);
    write_out();
    std::process::exit(0)
}

/// Ends a run whose code panicked: what it said, and the line, when the panic was in the student's code.
pub fn panicked() -> ! {
    let s = st();
    let message = format!("Your code panicked: {}", s.panic_message);
    let line = s.panic_line;
    finish("RuntimeError", &message, line)
}

/// Runs `body` on a thread with a deep stack, so deep recursion reaches the depth limit
/// rather than the end of the stack.
pub fn deep<F: FnOnce() + Send + 'static>(body: F) {
    let worker = std::thread::Builder::new().stack_size(512 << 20).spawn(body);
    match worker {
        Ok(handle) => {
            if handle.join().is_err() {
                finish("RuntimeError", "The visualizer's runtime failed. This is a bug in the visualizer, not in your code.", 0);
            }
        }
        Err(_) => finish("RuntimeError", "The visualizer could not start a thread to run your code.", 0),
    }
}

/// Everything generated code refers to, in one import.
pub mod prelude {
    pub use super::{CapDebug as _, CapOpaque as _, CapSnap as _, CopyCost as _, CopyFree as _, ResultDebug as _,
                    ResultLeet as _, ResultOpaque as _, ScanCost as _, ScanFree as _, ShiftNone as _, ShiftOf as _,
                    SortCost as _, SortFree as _};
}
