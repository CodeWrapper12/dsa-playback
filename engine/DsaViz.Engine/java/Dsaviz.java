// Dsaviz.java — compiled once per JDK by DSA Playback and linked into a student's Java solution.
//
// The engine rewrites the solution so every statement first calls in here: Dsaviz.hit(id)
// when measuring, Dsaviz.s(id).v("x", x)...go() when recording a run to step through.
// Everything the run view and the complexity verdicts need comes from here: statement
// counts, memory (the JVM's own exact count of bytes this thread allocated), call depth,
// the variables at every step and the objects they lead to, and what each call returned.
//
// It speaks the same line protocol as the C++ and Rust runtimes. Nothing it does while
// recording is counted as the student's: what it allocates is subtracted.

import java.lang.management.ManagementFactory;
import java.lang.reflect.Array;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;
import java.nio.charset.StandardCharsets;
import java.util.*;

public final class Dsaviz {
    private Dsaviz() {}

    // ---- state -----------------------------------------------------------------------------

    private static final com.sun.management.ThreadMXBean MX =
        (com.sun.management.ThreadMXBean) ManagementFactory.getThreadMXBean();

    static long budget = 50_000_000L, operations, statementsRun;
    static int depth, maxDepth, depthLimit = 20_000;
    static long[] hits = new long[1], bytes = new long[1], largest = new long[1], work = new long[1];
    static long[] siteCalls = new long[0], siteWork = new long[0];
    static int current = -1;
    static boolean active, stepping, truncated;
    static long maxSteps = 10_000, steps;
    static long allocBase, allocLast, since;
    /** What the runtime itself allocated while recording: never the student's. */
    static long own;
    static String programFile = "Solution.java";
    static final StringBuilder out = new StringBuilder(1 << 16);

    private static long allocated() { return MX.getCurrentThreadAllocatedBytes(); }

    /** Bytes the student's code allocated since the run began. */
    private static long allocatedSoFar() { return allocated() - allocBase - own; }

    /** Leaves out what the runtime allocated since `before`. */
    private static void excludeSince(long before) {
        long mine = allocated() - before;
        own += mine;
        allocLast += mine;
    }

    // ---- counting ----------------------------------------------------------------------------

    /** Charges what was allocated since the last statement began to that statement. */
    private static void flush(long now) {
        if (current >= 0) {
            long delta = now - allocLast;
            bytes[current] += delta;
            since += delta;
            if (since > largest[current]) largest[current] = since;
        }
        allocLast = now;
    }

    /** One statement about to run. */
    public static void hit(int id) {
        if (!active) return;
        long now = allocated();
        flush(now);
        since = 0;
        current = id;
        hits[id]++;
        statementsRun++;
        if (++operations > budget) finish("BudgetExceeded", "Execution budget of " + budget + " operations was exceeded.", -1);
    }

    /** Work done inside a library call the current statement made, at call site `site`. */
    static void charge(int site, long amount) {
        if (!active) return;
        if (site >= 0 && site < siteCalls.length) { siteCalls[site]++; siteWork[site] += Math.max(0, amount); }
        if (amount <= 0) return;
        if (current >= 0) work[current] += amount;
        operations += amount;
        if (operations > budget) finish("BudgetExceeded", "Execution budget of " + budget + " operations was exceeded.", -1);
    }

    /** A call begins: counts depth, and stops runaway recursion before the stack runs out. */
    public static void enter() {
        if (!active) return;
        if (++depth > maxDepth) maxDepth = depth;
        if (depth > depthLimit)
            finish("RecursionLimitExceeded", "Recursion went deeper than " + depthLimit +
                   " calls. That usually means a missing or unreachable base case.", -1);
    }

    public static void leave() { if (active) depth--; }

    // ---- library costs -------------------------------------------------------------------------

    private static long size(Object o) {
        if (o == null) return 0;
        if (o instanceof Collection) return ((Collection<?>) o).size();
        if (o instanceof Map) return ((Map<?, ?>) o).size();
        if (o instanceof CharSequence) return ((CharSequence) o).length();
        if (o.getClass().isArray()) return Array.getLength(o);
        return 0;
    }

    /** Sequences a search walks: a list, a deque, a string — never a set or a map. */
    private static boolean sequence(Object o) {
        return o instanceof List || o instanceof Deque || o instanceof CharSequence || (o != null && o.getClass().isArray());
    }

    /** A search through `o` — contains, indexOf — charged by what `o` really is. */
    public static <T> T scan(int site, T o) { charge(site, sequence(o) ? size(o) : 0); return o; }

    /** A whole collection or array copied, sorted or streamed. */
    public static <T> T copied(int site, T o) { charge(site, size(o)); return o; }

    public static <T> T sorted(int site, T o) {
        long n = size(o);
        charge(site, n > 1 ? (long) (n * (Math.log(n) / Math.log(2))) : 0);
        return o;
    }

    /** A value made by copying — a substring, a char array: charged its length. */
    public static <T> T made(int site, T o) { charge(site, size(o)); return o; }

    /** Inserting or removing at an index moves what is after it in a list or a StringBuilder. */
    public static int at(int site, Object list, int index) {
        long after = list instanceof List || list instanceof StringBuilder ? size(list) - index : 0;
        charge(site, list instanceof LinkedList ? Math.min(index, size(list)) : after);
        return index;
    }

    // A char, short or byte passes through as itself: widened to int, it would choose a
    // different overload — set.remove(c) would look for an Integer and never find it.
    public static char at(int site, Object list, char index) { at(site, list, (int) index); return index; }
    public static short at(int site, Object list, short index) { at(site, list, (int) index); return index; }
    public static byte at(int site, Object list, byte index) { at(site, list, (int) index); return index; }

    /** remove(Object) on a list searches it; on a set or a map it does not. */
    public static <T> T at(int site, Object list, T value) {
        charge(site, list instanceof List ? size(list) : 0);
        return value;
    }

    /** s += x: a new string as long as both. */
    public static <T> T plus(int site, String left, T right) {
        charge(site, (left == null ? 4 : left.length()) + String.valueOf(right).length());
        return right;
    }

    // ---- recording a step ------------------------------------------------------------------------

    /** The variables of the step being recorded, kept without allocating: names, kinds and values. */
    public static final class Step {
        final String[] names = new String[256];
        final byte[] kinds = new byte[256];       // 0 object, 1 long, 2 double, 3 boolean, 4 char, 5 gone
        final byte[] roles = new byte[256];
        final long[] longs = new long[256];
        final double[] doubles = new double[256];
        final Object[] objects = new Object[256];
        int count;
        boolean recording;

        private Step put(String name, byte kind, int role) {
            if (count < names.length) { names[count] = name; kinds[count] = kind; roles[count] = (byte) role; }
            return this;
        }

        public Step v(String name, int value) { if (recording && count < names.length) longs[count] = value; return put(name, (byte) 1, 0).next(); }
        public Step v(String name, long value) { if (recording && count < names.length) longs[count] = value; return put(name, (byte) 1, 0).next(); }
        public Step v(String name, double value) { if (recording && count < names.length) doubles[count] = value; return put(name, (byte) 2, 0).next(); }
        public Step v(String name, boolean value) { if (recording && count < names.length) longs[count] = value ? 1 : 0; return put(name, (byte) 3, 0).next(); }
        public Step v(String name, char value) { if (recording && count < names.length) longs[count] = value; return put(name, (byte) 4, 0).next(); }
        public Step v(String name, Object value) { return v(name, value, 0); }
        public Step v(String name, Object value, int role) { if (recording && count < names.length) objects[count] = value; return put(name, (byte) 0, role).next(); }
        /** A variable the compiler would not let be read here, and why. */
        public Step gone(String name, String why) { if (recording && count < names.length) objects[count] = why; return put(name, (byte) 5, 0).next(); }

        private Step next() { if (recording) count++; return this; }

        /** The step is complete: written out, if recording. */
        public void go() {
            if (!recording) return;
            recording = false;
            long before = allocated();
            long student = before - allocBase - own;      // read first: recording allocates
            try { record(this, student); } finally { for (int i = 0; i < count; i++) objects[i] = null; excludeSince(before); }
        }
    }

    private static final Step STEP = new Step();

    /** A statement about to run, whose variables follow. */
    public static Step s(int id) {
        hit(id);
        STEP.count = 0;
        STEP.recording = active && stepping && !warming && steps < maxSteps;
        if (active && stepping && steps >= maxSteps) truncated = true;
        return STEP;
    }

    // ---- writing values --------------------------------------------------------------------------

    static final int MAX_ITEMS = 1000, MAX_OBJECTS = 300, MAX_TEXT = 200;
    private static final IdentityHashMap<Object, Long> IDS = new IdentityHashMap<>();
    private static long nextId = 1;

    private static long id(Object o) {
        Long known = IDS.get(o);
        if (known != null) return known;
        long fresh = nextId++;
        IDS.put(o, fresh);
        return fresh;
    }

    private static void quote(StringBuilder j, String text) {
        j.append('"');
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '"': j.append("\\\""); break;
                case '\\': j.append("\\\\"); break;
                case '\n': j.append("\\n"); break;
                case '\r': j.append("\\r"); break;
                case '\t': j.append("\\t"); break;
                default:
                    if (c < 0x20) j.append(String.format("\\u%04x", (int) c)); else j.append(c);
            }
        }
        j.append('"');
    }

    static String decimal(double d) {
        if (Double.isNaN(d)) return "NaN";
        if (Double.isInfinite(d)) return d > 0 ? "Infinity" : "-Infinity";
        String s = String.format(Locale.ROOT, "%.5f", d);
        while (s.endsWith("0")) s = s.substring(0, s.length() - 1);
        if (s.endsWith(".")) s += "0";
        return s;
    }

    private static String clip(String s) { return s.length() > MAX_TEXT ? s.substring(0, MAX_TEXT) + "…" : s; }

    private static void scalar(StringBuilder j, String text) { j.append("{\"x\":"); quote(j, text); j.append('}'); }

    private static void number(StringBuilder j, long n) { j.append("{\"x\":\"").append(n).append("\",\"num\":").append(n).append('}'); }

    /** A class shaped like LeetCode's TreeNode or ListNode: its val, left, right or next fields. */
    private static final Map<Class<?>, Field[]> SHAPES = new HashMap<>();

    private static Field[] shape(Class<?> c) {
        return SHAPES.computeIfAbsent(c, k -> {
            Field val = field(k, "val"), left = field(k, "left"), right = field(k, "right"), next = field(k, "next");
            if (val != null && left != null && right != null) return new Field[] { val, left, right };
            if (val != null && next != null) return new Field[] { val, next };
            return new Field[0];
        });
    }

    private static Field field(Class<?> c, String name) {
        try { Field f = c.getDeclaredField(name); f.setAccessible(true); return f; }
        catch (ReflectiveOperationException | RuntimeException e) { return null; }
    }

    private static Object get(Field f, Object o) {
        try { return f.get(o); } catch (IllegalAccessException e) { return null; }
    }

    private static boolean ownClass(Class<?> c) {
        String n = c.getName();
        return !(n.startsWith("java.") || n.startsWith("javax.") || n.startsWith("jdk.") || n.startsWith("sun."));
    }

    private static String typeName(Object o) {
        Class<?> c = o.getClass();
        return c.isArray() ? c.getComponentType().getSimpleName() + "[]" : c.getSimpleName();
    }

    /** Every object reachable from one step's variables, written once each. */
    private static final class Shot {
        final StringBuilder objects = new StringBuilder();
        final Set<Long> written = new HashSet<>();

        boolean begin(long id) { return written.size() < MAX_OBJECTS && written.add(id); }

        void add(StringBuilder object) {
            if (objects.length() > 0) objects.append(',');
            objects.append(object);
        }
    }

    private static void reference(StringBuilder j, long id, String text) {
        j.append("{\"r\":").append(id).append(",\"x\":");
        quote(j, text);
        j.append('}');
    }

    private static String text(Object o) {
        if (o == null) return "null";
        if (o instanceof String) return "\"" + clip((String) o) + "\"";
        if (o instanceof Character) return "'" + o + "'";
        if (o instanceof Double || o instanceof Float) return decimal(((Number) o).doubleValue());
        if (o instanceof Number || o instanceof Boolean) return o.toString();
        Field[] s = shape(o.getClass());
        if (s.length > 0) return o.getClass().getSimpleName() + "(" + text(get(s[0], o)) + ")";
        long n = size(o);
        return "(" + n + ")";
    }

    private static void value(StringBuilder j, Object o, int role, Shot shot) {
        if (o == null) { scalar(j, "null"); return; }
        if (o instanceof Integer || o instanceof Long || o instanceof Short || o instanceof Byte) { number(j, ((Number) o).longValue()); return; }
        if (o instanceof Double || o instanceof Float) { scalar(j, decimal(((Number) o).doubleValue())); return; }
        if (o instanceof Boolean) { scalar(j, o.toString()); return; }
        if (o instanceof Character) { scalar(j, o.toString()); return; }
        if (o instanceof String) { scalar(j, "\"" + clip((String) o) + "\""); return; }
        if (o instanceof StringBuilder || o instanceof StringBuffer) { scalar(j, "\"" + clip(o.toString()) + "\""); return; }

        Class<?> c = o.getClass();
        long id = id(o);
        Field[] s = shape(c);
        boolean drawn = s.length > 0 || c.isArray() || o instanceof Collection || o instanceof Map || ownClass(c);
        if (!drawn) { scalar(j, clip(String.valueOf(o))); return; }
        reference(j, id, text(o));
        if (!shot.begin(id)) return;

        StringBuilder w = new StringBuilder();
        w.append("{\"id\":").append(id);
        if (s.length == 3) {
            w.append(",\"k\":\"tree\",\"t\":"); quote(w, c.getSimpleName());
            w.append(",\"b\":32,\"f\":[[\"val\",");
            value(w, get(s[0], o), 0, shot);
            w.append("],[\"left\",");
            value(w, get(s[1], o), 0, shot);
            w.append("],[\"right\",");
            value(w, get(s[2], o), 0, shot);
            w.append("]]}");
        } else if (s.length == 2) {
            w.append(",\"k\":\"listnode\",\"t\":"); quote(w, c.getSimpleName());
            w.append(",\"b\":24,\"f\":[[\"val\",");
            value(w, get(s[0], o), 0, shot);
            w.append("],[\"next\",");
            value(w, get(s[1], o), 0, shot);
            w.append("]]}");
        } else if (c.isArray()) {
            int n = Array.getLength(o);
            w.append(",\"k\":\"array\",\"t\":"); quote(w, typeName(o));
            w.append(",\"c\":").append(n).append(",\"b\":").append(16 + n * elementSize(c.getComponentType())).append(",\"i\":[");
            for (int i = 0; i < n && i < MAX_ITEMS; i++) { if (i > 0) w.append(','); value(w, Array.get(o, i), 0, shot); }
            w.append("]}");
        } else if (o instanceof Map) {
            Map<?, ?> m = (Map<?, ?>) o;
            w.append(",\"k\":\"map\",\"t\":"); quote(w, c.getSimpleName());
            w.append(",\"c\":").append(m.size()).append(",\"b\":").append(48 + m.size() * 48L).append(",\"e\":[");
            int i = 0;
            for (Map.Entry<?, ?> e : m.entrySet()) {
                if (i >= MAX_ITEMS) break;
                if (i++ > 0) w.append(',');
                w.append('['); value(w, e.getKey(), 0, shot); w.append(','); value(w, e.getValue(), 0, shot); w.append(']');
            }
            w.append("]}");
        } else if (o instanceof Collection) {
            Collection<?> items = (Collection<?>) o;
            String kind;
            List<Object> order = new ArrayList<>(Math.min(items.size(), MAX_ITEMS));
            if (o instanceof Stack) {
                kind = "stack";
                List<?> list = (List<?>) o;
                for (int i = list.size() - 1; i >= 0 && order.size() < MAX_ITEMS; i--) order.add(list.get(i));
            } else {
                if (o instanceof PriorityQueue) kind = "heap";
                else if (role == 1) kind = "stack";
                else if (role == 2 || (o instanceof Queue && !(o instanceof List))) kind = "queue";
                else if (o instanceof Set) kind = "set";
                else kind = "array";
                Object[] all = o instanceof PriorityQueue ? ((PriorityQueue<?>) o).toArray() : null;
                if (all != null) { for (Object x : all) { if (order.size() >= MAX_ITEMS) break; order.add(x); } }
                else for (Object x : items) { if (order.size() >= MAX_ITEMS) break; order.add(x); }
            }
            w.append(",\"k\":\"").append(kind).append('"');
            if (kind.equals("heap")) w.append(",\"ar\":2");
            w.append(",\"t\":"); quote(w, c.getSimpleName());
            w.append(",\"c\":").append(items.size()).append(",\"b\":").append(40 + items.size() * 16L).append(",\"i\":[");
            for (int i = 0; i < order.size(); i++) { if (i > 0) w.append(','); value(w, order.get(i), 0, shot); }
            w.append("]}");
        } else {
            // The student's own object: its fields — a design problem's state, most often.
            w.append(",\"k\":\"object\",\"t\":"); quote(w, c.getSimpleName());
            List<Field> fields = new ArrayList<>();
            for (Class<?> k = c; k != null && ownClass(k); k = k.getSuperclass())
                for (Field f : k.getDeclaredFields())
                    if (!Modifier.isStatic(f.getModifiers()) && !f.isSynthetic()) { try { f.setAccessible(true); fields.add(f); } catch (RuntimeException ignored) { } }
            w.append(",\"b\":").append(16 + fields.size() * 8L).append(",\"f\":[");
            for (int i = 0; i < fields.size() && i < 30; i++) {
                if (i > 0) w.append(',');
                w.append('['); quote(w, fields.get(i).getName()); w.append(','); value(w, get(fields.get(i), o), 0, shot); w.append(']');
            }
            w.append("]}");
        }
        shot.add(w);
    }

    private static int elementSize(Class<?> c) {
        if (c == long.class || c == double.class) return 8;
        if (c == int.class || c == float.class) return 4;
        if (c == char.class || c == short.class) return 2;
        if (c == byte.class || c == boolean.class) return 1;
        return 4;
    }

    private static void record(Step step, long studentAllocated) {
        steps++;
        Shot shot = new Shot();
        StringBuilder j = new StringBuilder(256);
        j.append("{\"s\":").append(current).append(",\"d\":").append(depth).append(",\"a\":").append(studentAllocated).append(",\"v\":[");
        for (int i = 0; i < step.count && i < step.names.length; i++) {
            if (i > 0) j.append(',');
            j.append("{\"n\":"); quote(j, step.names[i]); j.append(",\"t\":\"\",\"v\":");
            switch (step.kinds[i]) {
                case 1: number(j, step.longs[i]); break;
                case 2: scalar(j, decimal(step.doubles[i])); break;
                case 3: scalar(j, step.longs[i] != 0 ? "true" : "false"); break;
                case 4: scalar(j, String.valueOf((char) step.longs[i])); break;
                case 5: scalar(j, "(" + step.objects[i] + ")"); break;
                default: value(j, step.objects[i], step.roles[i], shot);
            }
            j.append('}');
        }
        j.append("],\"o\":[").append(shot.objects).append("]}");
        out.append(j).append('\n');
    }

    // ---- returns -----------------------------------------------------------------------------------

    private static void returned(Object value, boolean primitive, long n) {
        if (!active || !stepping || steps >= maxSteps) return;
        long before = allocated();
        try {
            StringBuilder j = new StringBuilder("{\"ret\":").append(depth).append(",\"after\":").append(steps).append(",\"v\":");
            if (primitive) number(j, n); else value(j, value, 0, new Shot());
            out.append(j).append("}\n");
        } finally { excludeSince(before); }
    }

    public static int ret(int v) { returned(null, true, v); return v; }
    public static long ret(long v) { returned(null, true, v); return v; }
    public static short ret(short v) { returned(null, true, v); return v; }
    public static byte ret(byte v) { returned(null, true, v); return v; }
    public static char ret(char v) { returned(String.valueOf(v), false, 0); return v; }
    public static boolean ret(boolean v) { returned(v, false, 0); return v; }
    public static double ret(double v) { returned(v, false, 0); return v; }
    public static float ret(float v) { returned(v, false, 0); return v; }
    public static <T> T ret(T v) { returned(v, false, 0); return v; }

    // ---- reading inputs (LeetCode's format) ----------------------------------------------------------

    private static final class Parser {
        final String s; int i;
        Parser(String s) { this.s = s; }

        void space() { while (i < s.length() && Character.isWhitespace(s.charAt(i))) i++; }

        Object parse() {
            space();
            if (i >= s.length()) return null;
            char c = s.charAt(i);
            if (c == '[') {
                i++;
                List<Object> items = new ArrayList<>();
                while (true) {
                    space();
                    if (i >= s.length()) break;
                    if (s.charAt(i) == ']') { i++; break; }
                    items.add(parse());
                    space();
                    if (i < s.length() && s.charAt(i) == ',') i++;
                }
                return items;
            }
            if (c == '"') {
                i++;
                StringBuilder t = new StringBuilder();
                while (i < s.length() && s.charAt(i) != '"') {
                    char x = s.charAt(i);
                    if (x == '\\' && i + 1 < s.length()) { char e = s.charAt(++i); t.append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e); }
                    else t.append(x);
                    i++;
                }
                i++;
                return t.toString();
            }
            int start = i;
            while (i < s.length() && s.charAt(i) != ',' && s.charAt(i) != ']' && !Character.isWhitespace(s.charAt(i))) i++;
            String word = s.substring(start, i);
            if (word.equals("true")) return Boolean.TRUE;
            if (word.equals("false")) return Boolean.FALSE;
            if (word.equals("null")) return null;
            return new java.math.BigDecimal(word);
        }
    }

    static Object parse(String text) { return new Parser(text).parse(); }

    /** Builds a parsed value as the type written in the parameter: "int[]", "List<List<Integer>>", "TreeNode". */
    @SuppressWarnings("unchecked")
    public static <T> T read(String[] inputs, int index, String type) {
        try {
            return (T) build(parse(index < inputs.length ? inputs[index] : ""), type.replace(" ", ""));
        } catch (RuntimeException e) {
            finish("InputError", e.toString(), -1);
            return null;
        }
    }

    static Object build(Object v, String type) {
        switch (type) {
            case "int": case "Integer": return ((java.math.BigDecimal) v).intValueExact();
            case "long": case "Long": return ((java.math.BigDecimal) v).longValueExact();
            case "short": case "Short": return ((java.math.BigDecimal) v).shortValueExact();
            case "byte": case "Byte": return ((java.math.BigDecimal) v).byteValueExact();
            case "double": case "Double": return ((java.math.BigDecimal) v).doubleValue();
            case "float": case "Float": return ((java.math.BigDecimal) v).floatValue();
            case "boolean": case "Boolean": return (Boolean) v;
            case "char": case "Character": return ((String) v).charAt(0);
            case "String": case "CharSequence": return (String) v;
        }
        if (type.endsWith("[]")) {
            String element = type.substring(0, type.length() - 2);
            List<?> items = v instanceof String && element.equals("char") ? chars((String) v) : (List<?>) v;
            Object array = Array.newInstance(arrayClass(element), items.size());
            for (int i = 0; i < items.size(); i++) Array.set(array, i, build(items.get(i), element));
            return array;
        }
        int open = type.indexOf('<');
        if (open > 0) {
            String outer = type.substring(0, open);
            String inner = type.substring(open + 1, type.length() - 1);
            List<Object> list = outer.equals("LinkedList") ? new LinkedList<>() : new ArrayList<>();
            for (Object item : (List<?>) v) list.add(build(item, inner));
            return outer.equals("Set") || outer.equals("HashSet") ? new HashSet<>(list) : list;
        }
        return node(v, type);
    }

    private static List<String> chars(String s) {
        List<String> out = new ArrayList<>();
        for (char c : s.toCharArray()) out.add(String.valueOf(c));
        return out;
    }

    private static Class<?> arrayClass(String element) {
        switch (element) {
            case "int": return int.class;
            case "long": return long.class;
            case "short": return short.class;
            case "byte": return byte.class;
            case "double": return double.class;
            case "float": return float.class;
            case "boolean": return boolean.class;
            case "char": return char.class;
            case "String": return String.class;
            case "Integer": return Integer.class;
            case "Long": return Long.class;
            case "Character": return Character.class;
        }
        if (element.endsWith("[]")) return Array.newInstance(arrayClass(element.substring(0, element.length() - 2)), 0).getClass();
        try { return Class.forName(element); } catch (ClassNotFoundException e) { return Object.class; }
    }

    /** A TreeNode in level order with nulls, or a ListNode, as LeetCode writes them. */
    private static Object node(Object v, String type) {
        try {
            Class<?> c = Class.forName(type);
            Field[] s = shape(c);
            if (s.length == 0) throw new IllegalArgumentException(type + " is not a TreeNode or ListNode");
            List<?> items = v == null ? new ArrayList<>() : (List<?>) v;
            if (items.isEmpty() || items.get(0) == null) return null;
            Constructor<?> make = c.getDeclaredConstructor();
            make.setAccessible(true);
            java.util.function.Function<Object, Object> create = x -> {
                try { Object n = make.newInstance(); s[0].set(n, ((java.math.BigDecimal) x).intValueExact()); return n; }
                catch (ReflectiveOperationException e) { throw new IllegalStateException(e); }
            };
            if (s.length == 2) {
                Object head = null;
                for (int i = items.size() - 1; i >= 0; i--) { Object n = create.apply(items.get(i)); s[1].set(n, head); head = n; }
                return head;
            }
            Object root = create.apply(items.get(0));
            ArrayDeque<Object> waiting = new ArrayDeque<>();
            waiting.add(root);
            int i = 1;
            while (!waiting.isEmpty() && i < items.size()) {
                Object n = waiting.poll();
                if (i < items.size() && items.get(i) != null) { Object l = create.apply(items.get(i)); s[1].set(n, l); waiting.add(l); }
                i++;
                if (i < items.size() && items.get(i) != null) { Object r = create.apply(items.get(i)); s[2].set(n, r); waiting.add(r); }
                i++;
            }
            return root;
        } catch (ReflectiveOperationException e) {
            throw new IllegalStateException(e);
        }
    }

    // ---- writing results (LeetCode's format) ---------------------------------------------------------

    static String format(Object o) {
        StringBuilder j = new StringBuilder();
        format(j, o);
        return j.toString();
    }

    private static void format(StringBuilder j, Object o) {
        if (o == null) { j.append("null"); return; }
        if (o instanceof String || o instanceof CharSequence) { quote(j, o.toString()); return; }
        if (o instanceof Character) { quote(j, o.toString()); return; }
        if (o instanceof Double || o instanceof Float) { j.append(decimal(((Number) o).doubleValue())); return; }
        if (o instanceof Number || o instanceof Boolean) { j.append(o); return; }
        Class<?> c = o.getClass();
        if (c.isArray()) {
            j.append('[');
            for (int i = 0; i < Array.getLength(o); i++) { if (i > 0) j.append(','); format(j, Array.get(o, i)); }
            j.append(']');
            return;
        }
        if (o instanceof Collection) {
            j.append('[');
            int i = 0;
            for (Object x : (Collection<?>) o) { if (i++ > 0) j.append(','); format(j, x); }
            j.append(']');
            return;
        }
        Field[] s = shape(c);
        if (s.length == 2) {
            j.append('[');
            int i = 0;
            for (Object n = o; n != null && i < 100_000; n = get(s[1], n)) { if (i++ > 0) j.append(','); format(j, get(s[0], n)); }
            j.append(']');
            return;
        }
        if (s.length == 3) {
            List<Object> order = new ArrayList<>();
            ArrayDeque<Object> waiting = new ArrayDeque<>();
            List<Object> level = new ArrayList<>();
            level.add(o);
            while (!level.isEmpty() && order.size() < 100_000) {
                List<Object> next = new ArrayList<>();
                for (Object n : level) {
                    order.add(n);
                    if (n != null) { next.add(get(s[1], n)); next.add(get(s[2], n)); }
                }
                boolean any = false;
                for (Object n : next) if (n != null) any = true;
                level = any ? next : new ArrayList<>();
            }
            while (!order.isEmpty() && order.get(order.size() - 1) == null) order.remove(order.size() - 1);
            j.append('[');
            for (int i = 0; i < order.size(); i++) { if (i > 0) j.append(','); Object n = order.get(i); if (n == null) j.append("null"); else format(j, get(s[0], n)); }
            j.append(']');
            return;
        }
        quote(j, String.valueOf(o));
    }

    // ---- design problems -------------------------------------------------------------------------------

    /**
     * LeetCode's design problems: the first operation creates the object, every later one calls
     * a method on it, and each result is collected, null for the constructor and void methods.
     */
    public static List<Object> design(Class<?> type, String operationsText, String argumentsText) throws Throwable {
        List<?> operations = (List<?>) parse(operationsText);
        List<?> arguments = (List<?>) parse(argumentsText);
        if (operations == null || operations.isEmpty())
            throw new DesignInputError("The list of operations is empty. The first one creates the object, like \"" + type.getSimpleName() + "\".");
        if (arguments == null || operations.size() != arguments.size())
            throw new DesignInputError("There are " + operations.size() + " operations but " + (arguments == null ? 0 : arguments.size()) +
                                       " argument lists. Each operation needs its own list, [] when it takes none.");
        if (!type.getSimpleName().equals(operations.get(0)))
            throw new DesignInputError("The first operation creates the object, so it must be \"" + type.getSimpleName() + "\", not \"" + operations.get(0) + "\".");

        List<Object> results = new ArrayList<>();
        Object target = null;
        for (int i = 0; i < operations.size(); i++) {
            List<?> args = (List<?>) arguments.get(i);
            out.append("{\"call\":").append(i).append(",\"step\":").append(steps).append("}\n");
            try {
                if (i == 0) {
                    Constructor<?> make = null;
                    for (Constructor<?> k : type.getDeclaredConstructors()) if (k.getParameterCount() == args.size()) make = k;
                    if (make == null) throw new DesignInputError(type.getSimpleName() + " has no constructor taking " + args.size() + " argument(s).");
                    make.setAccessible(true);
                    target = make.newInstance(convert(make.getGenericParameterTypes(), args));
                    results.add(null);
                } else {
                    String name = String.valueOf(operations.get(i));
                    Method method = null;
                    boolean named = false;
                    for (Method m : type.getDeclaredMethods()) {
                        if (!m.getName().equals(name)) continue;
                        named = true;
                        if (m.getParameterCount() == args.size()) method = m;
                    }
                    if (!named) throw new DesignInputError("Operation " + (i + 1) + " is \"" + name + "\", but " + type.getSimpleName() + " has no method by that name.");
                    if (method == null) throw new DesignInputError("Operation " + (i + 1) + " calls " + name + " with " + args.size() + " argument(s), which it does not take.");
                    method.setAccessible(true);
                    results.add(method.invoke(target, convert(method.getGenericParameterTypes(), args)));
                }
            } catch (InvocationTargetException e) {
                throw e.getCause();
            }
        }
        return results;
    }

    private static Object[] convert(Type[] types, List<?> args) {
        Object[] out = new Object[types.length];
        for (int i = 0; i < types.length; i++) out[i] = build(args.get(i), written(types[i]));
        return out;
    }

    private static String written(Type t) {
        if (t instanceof Class) {
            Class<?> c = (Class<?>) t;
            return c.isArray() ? written(c.getComponentType()) + "[]" : c.getSimpleName();
        }
        if (t instanceof ParameterizedType) {
            ParameterizedType p = (ParameterizedType) t;
            return ((Class<?>) p.getRawType()).getSimpleName() + "<" + written(p.getActualTypeArguments()[0]) + ">";
        }
        return t.getTypeName();
    }

    /** Calls that do not fit the class: the input's fault, not the code's. */
    public static final class DesignInputError extends RuntimeException {
        private static final long serialVersionUID = 1L;
        public DesignInputError(String message) { super(message); }
    }

    // ---- the run ---------------------------------------------------------------------------------------

    /** Reads the run's settings — run|step, budget, depth limit, steps kept — and prepares to count. */
    public static void configure(String[] args, int statements, int sites, String file) {
        hits = new long[statements + 1];
        bytes = new long[statements + 1];
        largest = new long[statements + 1];
        work = new long[statements + 1];
        siteCalls = new long[sites];
        siteWork = new long[sites];
        programFile = file;
        if (args.length > 0) stepping = args[0].equals("step");
        if (args.length > 1) budget = Long.parseLong(args[1]);
        if (args.length > 2) depthLimit = Integer.parseInt(args[2]);
        if (args.length > 3) maxSteps = Long.parseLong(args[3]);
        preload();
    }

    /**
     * Loads and uses the collections solutions use, before anything is counted: the first use
     * of a class allocates for the JVM's own bookkeeping, and that is not the solution's.
     */
    private static void preload() {
        List<Integer> list = new ArrayList<>(List.of(3, 1, 2));
        Collections.sort(list);
        list.sort(Comparator.reverseOrder());
        new LinkedList<>(list).poll();
        ArrayDeque<Integer> deque = new ArrayDeque<>(list);
        deque.push(4); deque.pop(); deque.offer(5); deque.poll(); deque.peekLast();
        new Stack<Integer>().push(1);
        PriorityQueue<Integer> heap = new PriorityQueue<>(list);
        heap.offer(0); heap.poll();
        new PriorityQueue<Integer>(Comparator.reverseOrder()).offer(1);
        Map<Integer, Integer> map = new HashMap<>();
        map.put(1, 1); map.merge(1, 1, Integer::sum); map.getOrDefault(2, 0); map.computeIfAbsent(3, k -> 0);
        new TreeMap<>(map).firstKey();
        new LinkedHashMap<>(map).keySet().iterator().next();
        new HashSet<>(list).contains(1);
        new TreeSet<>(list).first();
        int[] array = { 3, 1, 2 };
        Arrays.sort(array);
        Arrays.fill(array, 0);
        Arrays.copyOf(array, 4);
        Arrays.stream(array).sum();
        list.stream().map(x -> x + 1).collect(java.util.stream.Collectors.toList());
        StringBuilder text = new StringBuilder("ab");
        text.append('c').append(1).insert(0, 'x').reverse().deleteCharAt(0).toString().toCharArray();
        "abc".substring(1).indexOf('c');
        String.valueOf(new char[] { 'a' });
        Integer.valueOf(1000).hashCode();
        Math.max(1, 2);
    }

    public static String[] inputs() {
        try {
            String text = new String(System.in.readAllBytes(), StandardCharsets.UTF_8);
            return text.isEmpty() ? new String[0] : text.replace("\r", "").split("\n", -1);
        } catch (java.io.IOException e) {
            return new String[0];
        }
    }

    /**
     * The warm-up run: the solution runs once, counted only so a runaway loop is still stopped,
     * and nothing recorded. The JVM loads classes and links lambdas the first time code runs,
     * and what that allocates is not the solution's — the measured run that follows is clean.
     */
    public static void warm() {
        begin();
        warming = true;
    }

    static boolean warming;

    /** Whether this run measures (rather than records a run to step through). */
    public static boolean measuring() { return !stepping; }

    /** The solution has returned: nothing after this is its work. */
    public static void stop() {
        if (!active) return;
        flush(allocated());
        active = false;
    }

    /** Starts counting: everything from here is the student's code. */
    public static void begin() {
        warming = false;
        Arrays.fill(hits, 0); Arrays.fill(bytes, 0); Arrays.fill(largest, 0); Arrays.fill(work, 0);
        Arrays.fill(siteCalls, 0); Arrays.fill(siteWork, 0);
        steps = 0;
        truncated = false;
        operations = statementsRun = 0;
        depth = maxDepth = 0;
        current = -1;
        allocBase = allocLast = allocated();
        since = 0;
        own = 0;
        active = true;
    }

    /** Ends a run that completed, with what it returned; `voidResult` when it returns nothing. */
    public static void succeed(Object result, boolean voidResult) {
        stop();
        StringBuilder j = new StringBuilder("{\"end\":\"Completed\",\"result\":");
        if (voidResult) j.append("null"); else quote(j, format(result));
        end(j, -1);
    }

    /** The student's exception, said the way they would want to hear it, with the line it was thrown at. */
    public static void threw(Throwable t) {
        flush(allocated());
        active = false;
        if (t instanceof DesignInputError) {
            StringBuilder j = new StringBuilder("{\"end\":\"InvalidInput\",\"error\":");
            quote(j, t.getMessage());
            end(j, -1);
            return;
        }
        int line = -1;
        for (StackTraceElement e : t.getStackTrace())
            if (programFile.equals(e.getFileName()) && e.getLineNumber() > 0 && !e.getClassName().startsWith("Dsaviz")) { line = e.getLineNumber(); break; }
        String message = t.getMessage();
        String text = t instanceof StackOverflowError
            ? "Your code threw StackOverflowError: the recursion went too deep"
            : "Your code threw " + t.getClass().getSimpleName() + (message != null ? ": " + message : "");
        StringBuilder j = new StringBuilder("{\"end\":\"RuntimeError\",\"error\":");
        quote(j, text);
        end(j, line);
    }

    static void finish(String status, String error, int line) {
        flush(allocated());
        active = false;
        StringBuilder j = new StringBuilder("{\"end\":");
        quote(j, status);
        j.append(",\"error\":");
        quote(j, error);
        end(j, line);
    }

    private static void end(StringBuilder j, int line) {
        if (current >= 0 && since > largest[current]) largest[current] = since;
        j.append(",\"statement\":").append(current);
        if (line > 0) j.append(",\"line\":").append(line);
        j.append(",\"ops\":").append(operations).append(",\"statements\":").append(statementsRun)
         .append(",\"allocated\":").append(sum(bytes)).append(",\"peak\":-1")
         .append(",\"maxDepth\":").append(maxDepth).append(",\"truncated\":").append(truncated);
        list(j, "hits", hits); list(j, "bytes", bytes); list(j, "largest", largest); list(j, "work", work);
        j.append(",\"sites\":[");
        for (int i = 0; i < siteCalls.length; i++) { if (i > 0) j.append(','); j.append('[').append(siteCalls[i]).append(',').append(siteWork[i]).append(']'); }
        j.append("]}");
        out.append(j).append('\n');
        System.out.flush();
        // A line of the student's own output may not have ended: the protocol starts afresh.
        byte[] text = ("\n" + out).getBytes(StandardCharsets.UTF_8);
        System.out.write(text, 0, text.length);
        System.out.flush();
        Runtime.getRuntime().halt(0);
    }

    private static long sum(long[] values) { long total = 0; for (long v : values) total += v; return total; }

    private static void list(StringBuilder j, String name, long[] values) {
        j.append(",\"").append(name).append("\":[");
        for (int i = 0; i < values.length; i++) { if (i > 0) j.append(','); j.append(values[i]); }
        j.append(']');
    }

    /** Runs `body` on a thread with a deep stack, so deep recursion reaches the depth limit first. */
    public static void deep(Runnable body) throws InterruptedException {
        Thread worker = new Thread(null, body, "solution", 1L << 29);
        worker.setUncaughtExceptionHandler((thread, e) ->
            finish("RuntimeError", "The visualizer's runtime failed (" + e + "). This is a bug in the visualizer, not in your code.", -1));
        worker.start();
        worker.join();
    }
}
