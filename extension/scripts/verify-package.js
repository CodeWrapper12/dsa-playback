// Checks a built package the way a student would meet it: unpacked, on a machine with no
// .NET at all. Runs the bundled engine with an environment stripped of every .NET
// location, on a walkthrough sample, both recording a run and measuring complexity.
//
//   node scripts/verify-package.js dist/dsa-playback-darwin-arm64.vsix

const { execFileSync, spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const MAX_PACKAGE_MEGABYTES = 60;

const vsix = path.resolve(process.argv[2] || "");
if (!process.argv[2] || !fs.existsSync(vsix)) {
  console.error("Usage: node scripts/verify-package.js <package.vsix>");
  process.exit(2);
}

const failures = [];
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? ` — ${detail}` : ""}`);
  if (!ok) failures.push(name);
};

const megabytes = fs.statSync(vsix).size / (1024 * 1024);
check(`package is at most ${MAX_PACKAGE_MEGABYTES} MB`, megabytes <= MAX_PACKAGE_MEGABYTES, `${megabytes.toFixed(1)} MB`);

const unpacked = fs.mkdtempSync(path.join(os.tmpdir(), "dsaviz-package-"));
try {
  // A .vsix is a zip. Windows' own tar reads zips; elsewhere unzip is standard.
  // Windows' own tar (bsdtar) by full path: under Git Bash, plain "tar" is GNU tar, which
  // reads the "D:" of a Windows path as a remote host.
  if (process.platform === "win32") execFileSync(path.join(process.env.SystemRoot, "System32", "tar.exe"), ["-xf", vsix, "-C", unpacked]);
  else execFileSync("unzip", ["-q", vsix, "-d", unpacked]);
  const root = path.join(unpacked, "extension");
  const files = fs.readdirSync(root, { recursive: true, withFileTypes: true })
    .filter(entry => entry.isFile())
    .map(entry => path.relative(root, path.join(entry.parentPath ?? entry.path, entry.name)).split(path.sep).join("/"));

  for (const script of ["media/main.js", "media/run.js", "media/styles.css"]) {
    check(`the panel's ${script} is shipped`, files.includes(script));
  }
  check("no TypeScript sources, tests or dev files are shipped",
    !files.some(f => f.startsWith("src/") || f.startsWith("out/test/") || f.startsWith(".devharness/") || f.endsWith(".map")));
  for (const sample of ["csharp/TwoSumSorted.cs", "csharp/LevelOrder.cs", "csharp/ClimbStairs.cs",
                        "python/two_sum_sorted.py", "python/level_order.py", "python/climb_stairs.py"]) {
    check(`walkthrough sample ${sample} is shipped`, files.includes(`samples/${sample}`));
  }
  check("the Python runner is shipped beside the engine", files.includes("engine/python/dsaviz_py.py"));
  check("the icon, licence and changelog are shipped",
    files.includes("images/icon.png") && files.includes("LICENSE.txt") && files.includes("changelog.md"));

  // Every image the Marketplace page shows must point at a file that is in the repository.
  const readme = fs.readFileSync(path.join(root, "readme.md"), "utf8");
  const images = [...readme.matchAll(/!\[[^\]]*\]\(([^)]+)\)/g)].map(m => m[1]);
  const prefix = "https://github.com/CodeWrapper12/dsa-playback/raw/HEAD/extension/";
  const repositoryRoot = path.resolve(__dirname, "..");
  const broken = images.filter(url => !url.startsWith(prefix) || !fs.existsSync(path.join(repositoryRoot, url.slice(prefix.length))));
  check("every README image links to a file in the repository", images.length > 0 && broken.length === 0,
    broken.length ? broken.join(", ") : `${images.length} images`);

  const engine = path.join(root, "engine", process.platform === "win32" ? "DsaViz.Engine.exe" : "DsaViz.Engine");
  check("the engine is bundled", fs.existsSync(engine));

  // No PATH entry, DOTNET_ROOT or install location: if anything outside the package
  // were needed, this is where it would fail.
  const bare = {
    PATH: process.platform === "win32" ? process.env.SystemRoot + "\\System32" : "/usr/bin:/bin",
    HOME: unpacked,
    DOTNET_ROOT: path.join(unpacked, "no-dotnet-here"),
    TMPDIR: os.tmpdir()
  };
  if (process.platform === "win32") bare.SystemRoot = process.env.SystemRoot;

  const sample = path.join(root, "samples", "csharp", "TwoSumSorted.cs");
  const job = request => spawnSync(engine, ["--job", "-"], {
    input: JSON.stringify(request), env: bare, encoding: "utf8", timeout: 120_000
  });

  const recorded = job({ file: sample, entry: "Solution.TwoSum", stepSize: 8 });
  const steps = recorded.stdout ? JSON.parse(recorded.stdout).steps : null;
  check("records a run with no .NET installed", steps?.status === "Completed" && steps.steps.length > 0,
    steps ? `${steps.steps.length} steps` : recorded.stderr.slice(0, 300));
  check("uses the example from the code and checks its output", steps?.check?.matches === true,
    steps?.check ? `${steps.check.actual} vs ${steps.check.expected}` : "no check");

  // Python is the student's own; only .NET is kept out of the environment.
  const python = (process.env.DSAPLAYBACK_PYTHON ||
    execFileSync(process.platform === "win32" ? "where" : "which", [process.platform === "win32" ? "python" : "python3"])
      .toString().split(/\r?\n/)[0]).trim();
  const pythonSample = path.join(root, "samples", "python", "two_sum_sorted.py");
  const pythonRun = job({ file: pythonSample, entry: "Solution.twoSum", stepSize: 8, python });
  const pythonSteps = pythonRun.stdout ? JSON.parse(pythonRun.stdout).steps : null;
  check("records a Python run with the bundled engine", pythonSteps?.status === "Completed" && pythonSteps.check?.matches === true,
    pythonSteps ? `${pythonSteps.steps.length} steps` : pythonRun.stderr.slice(0, 300));

  const measured = job({ file: sample, entry: "Solution.TwoSum" });
  const primary = measured.stdout ? JSON.parse(measured.stdout).primary : null;
  check("measures complexity with no .NET installed", primary?.status === "Completed" && primary.empirical.best === "Linear",
    primary ? `${primary.status}, ${primary.empirical.best}` : measured.stderr.slice(0, 300));
} finally {
  fs.rmSync(unpacked, { recursive: true, force: true });
}

console.log(failures.length === 0 ? "\nPackage verified." : `\n${failures.length} check(s) failed.`);
process.exit(failures.length === 0 ? 0 : 1);
