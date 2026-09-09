// Mirrors the engine's JSON contracts. Kept deliberately close to the C# records so a
// change on either side shows up as a type error rather than an undefined at runtime.

export type RunStatus = "Completed" | "BudgetExceeded" | "CompileError" | "RuntimeError";

export type ComplexityClass =
  | "Unknown" | "Constant" | "Logarithmic" | "Linear"
  | "Linearithmic" | "Quadratic" | "Cubic" | "Exponential";

export type GenerationStrategy =
  | "Random" | "Distinct" | "Sorted" | "ReverseSorted" | "AllDuplicates" | "NearlySorted";

export type InputShape =
  | "IntArray" | "IntList" | "StringValue" | "Matrix" | "IntScalar" | "BoolScalar";

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
}

export interface LineCost {
  line: number;
  hits: number;
  bytesAllocated: number;
  statementCount: number;
}

export interface TraceResult {
  status: RunStatus;
  statements: StatementCost[];
  lines: LineCost[];
  totalOperations: number;
  totalBytesAllocated: number;
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

export interface MeasurementResult {
  status: RunStatus;
  points: SizePoint[];
  empirical: CurveFit;
  static: StaticEstimate;
  disagreement: boolean;
  disagreementNote: string | null;
  detail: TraceResult | null;
  inputs: InputSpec[];
  errors: string[];
  sizeNote: string | null;
}

export interface JobResponse {
  primary: MeasurementResult | null;
  comparison: MeasurementResult | null;
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
  signature: string;
}

export interface JobRequest {
  file: string;
  entry: string;
  sizes?: number[];
  budget?: number;
  inputs?: InputSpec[];
  compareFile?: string;
  compareEntry?: string;
}
