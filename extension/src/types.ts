// Mirrors the engine's JSON contracts. Kept deliberately close to the C# records so a
// change on either side shows up as a type error rather than an undefined at runtime.

export type RunStatus =
  | "Completed" | "BudgetExceeded" | "CompileError" | "RuntimeError"
  | "RecursionLimitExceeded" | "TimedOut";

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
  source: string | null;
  comparisonSource: string | null;
  label: string | null;
  comparisonLabel: string | null;
  errors: string[];
}

export interface EntryParameter { name: string; type: string; supported: boolean; }

export interface EntryPoint {
  declaringType: string;
  name: string;
  parameters: EntryParameter[];
  measurable: boolean;
  /** Why it cannot be measured, in a sentence the user can act on. */
  reason: string | null;
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
  compareFile?: string;
  compareEntry?: string;
}

// ---- step-through -------------------------------------------------------------

export interface StepVariable {
  name: string;
  display: string;
  /** scalar, string, array, grid, collection, tree, list, null, object, function or error. */
  kind: string;
  items: string[] | null;
  number: number | null;
}

export interface ExecutionStep {
  index: number;
  statementId: number;
  line: number;
  column: number;
  depth: number;
  method: string;
  variables: StepVariable[];
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
}
