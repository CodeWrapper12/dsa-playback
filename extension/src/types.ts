// Mirrors the engine's JSON contracts. Kept deliberately close to the C# records so a
// change on either side shows up as a type error rather than an undefined at runtime.

export type RunStatus =
  | "Completed" | "BudgetExceeded" | "CompileError" | "RuntimeError"
  | "RecursionLimitExceeded" | "TimedOut" | "InvalidInput";

export type ComplexityClass =
  | "Unknown" | "Constant" | "Logarithmic" | "Linear"
  | "Linearithmic" | "Quadratic" | "Cubic" | "Exponential";

export type GenerationStrategy =
  | "Random" | "Distinct" | "Sorted" | "ReverseSorted" | "AllDuplicates" | "NearlySorted"
  | "Balanced" | "Skewed" | "Path";

export type InputShape =
  | "IntArray" | "IntList" | "StringValue" | "Matrix" | "IntScalar" | "BoolScalar"
  | "LongScalar" | "CharArray" | "StringArray" | "NestedIntList" | "Tree" | "LinkedList"
  | "EdgeList" | "CharMatrix";

export interface InputSpec {
  shape: InputShape;
  strategy: GenerationStrategy;
  minValue: number;
  maxValue: number;
  scalesWithSize: boolean;
  fixedValue: number;
  seed: number;
}

export interface StatementCost {
  id: number;
  line: number;
  column: number;
  length: number;
  snippet: string;
  hits: number;
  bytesAllocated: number;
  /** Work inside library calls this statement made — the scan inside List.Contains. */
  libraryWork: number;
}

export interface LibraryCall {
  site: number;
  line: number;
  column: number;
  member: string;
  cost: string;
  calls: number;
  work: number;
}

export interface LineCost {
  line: number;
  hits: number;
  bytesAllocated: number;
  statementCount: number;
  libraryWork: number;
}

export interface TraceResult {
  status: RunStatus;
  statements: StatementCost[];
  lines: LineCost[];
  totalOperations: number;
  totalStatements: number;
  totalBytesAllocated: number;
  maxRecursionDepth: number;
  libraryCalls: LibraryCall[];
  elapsedMs: number;
  errors: string[];
  output: string;
  returnValue: string | null;
  memoryModel: string;
}

export interface SizePoint {
  size: number;
  operations: number;
  bytesAllocated: number;
  elapsedMs: number;
  statements: number;
  libraryWork: number;
  maxRecursionDepth: number;
}

export interface ClassFit {
  class: ComplexityClass;
  rSquared: number;
  coefficient: number;
}

export interface CurveFit {
  best: ComplexityClass;
  rSquared: number;
  ranked: ClassFit[];
  explanation: string;
}

export interface StaticEstimate {
  class: ComplexityClass;
  reasoning: string;
  linearLoopDepth: number;
  logLoopDepth: number;
  recursiveSelfCalls: number;
}

/** How one statement's cost grows with n, fitted from its own counts. */
export interface StatementGrowth {
  id: number;
  line: number;
  column: number;
  snippet: string;
  runs: ComplexityClass;
  runsFormula: string | null;
  cost: ComplexityClass;
  costFormula: string | null;
  memory: ComplexityClass;
  memoryFormula: string | null;
  largestAllocation: ComplexityClass;
  because: string | null;
  costs: number[];
}

/** Why the method is the class it is: which statements dominate, and by what formula. */
export interface Derivation {
  summary: string;
  dominant: number[];
  statements: StatementGrowth[];
}

export interface SpaceReading {
  allocation: CurveFit;
  largestAllocation: CurveFit;
  callDepth: CurveFit;
  likelyPeak: ComplexityClass;
  summary: string;
  static: StaticEstimate;
  disagreement: boolean;
  disagreementNote: string | null;
}

export interface MeasurementResult {
  status: RunStatus;
  points: SizePoint[];
  empirical: CurveFit;
  static: StaticEstimate;
  disagreement: boolean;
  disagreementNote: string | null;
  detail: TraceResult | null;
  derivation: Derivation | null;
  space: SpaceReading | null;
  inputs: InputSpec[];
  parameters: ParameterDescription[];
  errors: string[];
  sizeNote: string | null;
}

export interface ParameterDescription {
  name: string;
  type: string;
  shape: InputShape;
  applicableStrategies: GenerationStrategy[];
}

export interface JobResponse {
  primary: MeasurementResult | null;
  comparison: MeasurementResult | null;
  steps: StepTrace | null;
  /** Set when every example was asked for. */
  examples?: ExampleReport | null;
  source: string | null;
  comparisonSource: string | null;
  label: string | null;
  comparisonLabel: string | null;
  errors: string[];
  /** Set when nothing could run for a reason the editor can help with: python-missing or python-version. */
  errorCode?: string;
}

export interface EntryParameter { name: string; type: string; supported: boolean; }

export interface EntryPoint {
  declaringType: string;
  name: string;
  parameters: EntryParameter[];
  measurable: boolean;
  /** 1-based line of the method's name. */
  line: number;
  /** Why it cannot be run, in a sentence the user can act on. */
  reason: string | null;
  /** Every parameter can be generated, so growth can be measured; otherwise it runs only on examples. */
  generatable: boolean;
  /** A LeetCode design problem: a class run through its example's list of calls. */
  design: boolean;
  /** "Solution.TwoSum" — unambiguous even when classes share a method name. */
  qualified: string;
  signature: string;
}

export interface JobRequest {
  file: string;
  entry: string;
  sizes?: number[];
  budget?: number;
  inputs?: InputSpec[];
  strategy?: GenerationStrategy;
  stepSize?: number;
  maxSteps?: number;
  /** Typed values, one LeetCode-format text per parameter; null keeps that parameter's default source. */
  values?: (string | null)[];
  /** Which example found in the code to use; negative means none. */
  example?: number;
  /** Run every example found in the code instead of recording one run. */
  allExamples?: boolean;
  /** The Python interpreter for a Python file. */
  python?: string;
  /** The C++ compiler for a C++ file, when the student set one. */
  cppCompiler?: string;
  /** The rustc for a Rust file, when the student set one. */
  rustc?: string;
  compareFile?: string;
  compareEntry?: string;
}

// ---- step-through -------------------------------------------------------------

export interface StepVariable {
  name: string;
  display: string;
  /** scalar, string, null, function, or for an object its heap kind: array, list, map, set, stack, queue, heap, tree, listnode, sequence, object. */
  kind: string;
  items: string[] | null;
  number: number | null;
  /** The object this variable holds; the same id is the same object. */
  ref?: number;
}

export interface HeapValue {
  text: string;
  number?: number;
  ref?: number;
}

/** An object as it was at a step, sent only at steps where it changed. */
export interface HeapObject {
  id: number;
  kind: string;
  type: string;
  count: number;
  bytes: number;
  items?: HeapValue[];
  entries?: { key: HeapValue; value: HeapValue }[];
  fields?: { name: string; value: HeapValue }[];
  /** Instead of items: just the positions that changed. */
  patch?: { index: number; value: HeapValue }[];
}

export interface ReturnEvent {
  afterStep: number;
  depth: number;
  value: HeapValue;
}

export interface ExecutionStep {
  index: number;
  statementId: number;
  line: number;
  column: number;
  depth: number;
  method: string;
  variables: StepVariable[];
  heap?: HeapObject[];
  /** Bytes the code had allocated before this statement. */
  allocated: number;
}

export interface StatementInfo {
  id: number;
  line: number;
  column: number;
  length: number;
  snippet: string;
  method: string;
  variables: string[];
}

export interface StepTrace {
  status: RunStatus;
  size: number;
  steps: ExecutionStep[];
  statements: StatementInfo[];
  truncated: boolean;
  maxSteps: number;
  arguments: StepVariable[];
  returnValue: string | null;
  errors: string[];
  errorLine: number | null;
  inputs: StepInput[];
  examples: FoundExample[];
  exampleNotes: string[];
  selectedExample: number | null;
  check: OutputCheck | null;
  returns: ReturnEvent[];
  totalAllocated: number;
  methods: { name: string; parameters: string[] }[];
  graph: { kind: "adjacency" | "edges"; variable: string; nodeCount?: string } | null;
  /** A design problem's calls, each with the first step recorded after it began. */
  calls: { index: number; operation: string; step: number }[];
}

export type InputSource = "Example" | "Typed" | "Generated";

export interface StepInput {
  name: string;
  type: string;
  /** The value in LeetCode's format, as shown in the input box. */
  literal: string;
  source: InputSource;
  note: string | null;
  error: string | null;
}

export interface FoundExample {
  label: string;
  line: number;
  origin: "Comment" | "Call";
  values: (string | null)[];
  notes: { parameter: string; message: string }[];
  expected: string | null;
  anyOrder: boolean;
}

export interface OutputCheck {
  expected: string;
  actual: string;
  matches: boolean;
  anyOrder: boolean;
}

/** One example's result from "Run all examples". `matches` is null when there is nothing to compare. */
export interface ExampleRun {
  index: number;
  label: string;
  line: number;
  status: RunStatus;
  expected: string | null;
  actual: string | null;
  matches: boolean | null;
  anyOrder: boolean;
  error: string | null;
  errorLine: number | null;
}

export interface ExampleReport {
  status: RunStatus;
  runs: ExampleRun[];
  errors: string[];
}
