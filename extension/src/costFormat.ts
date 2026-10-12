// Pure formatting and scaling for the editor annotations. Kept free of any vscode
// import so it can be tested directly — this is the part where an off-by-one or a
// wrong scale would quietly mislead the reader, and it is not worth trusting to
// inspection.

export function formatCount(value: number): string {
  return value.toLocaleString("en-US");
}

export function formatBytes(value: number): string {
  if (!value) return "";
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * The text that sits at the end of an annotated line: how often it ran, the work done
 * inside library calls it made, and what it allocated.
 *
 * The library work is shown separately rather than folded into the count. "1,000×" next
 * to a List.Contains line is true and misleading; "1,000×  ·  +499,500 library work" is
 * the whole story, and the gap between the two numbers is the lesson.
 */
export function annotationText(hits: number, bytesAllocated: number, libraryWork = 0): string {
  const parts = [`${formatCount(hits)}×`];
  if (libraryWork > 0) parts.push(`+${formatCount(libraryWork)} library work`);
  const bytes = formatBytes(bytesAllocated);
  if (bytes) parts.push(bytes);
  return parts.join("   ·   ");
}

/**
 * How strongly to shade a line, from 0 to 1.
 *
 * Logarithmic on purpose. Bubble sort's inner line runs 749,625 times while its outer
 * loop runs once; on a linear scale the outer loop would be shaded 0.0000013 and read
 * as unexecuted, which is exactly the distinction the shading exists to make.
 */
export function heatIntensity(hits: number, maxHits: number): number {
  if (hits <= 0) return 0;
  if (maxHits <= 1) return 1;
  return Math.log(hits + 1) / Math.log(maxHits + 1);
}
