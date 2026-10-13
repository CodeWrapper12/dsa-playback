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
                        "python/two_sum_sorted.py", "python/level_order.py", "python/climb_stairs.py",
                        "cpp/two_sum_sorted.cpp", "cpp/level_order.cpp", "cpp/climb_stairs.cpp",
                        "rust/two_sum_sorted.rs", "rust/level_order.rs", "rust/climb_stairs.rs",
                        "java/TwoSumSorted.java", "java/LevelOrder.java", "java/ClimbStairs.java",
                        "javascript/two_sum_sorted.js", "javascript/level_order.js", "javascript/climb_stairs.js",
                        "typescript/two_sum_sorted.ts", "typescript/level_order.ts", "typescript/climb_stairs.ts"]) {
    check(`walkthrough sample ${sample} is shipped`, files.includes(`samples/${sample}`));
  }
  check("the Python runner is shipped beside the engine", files.includes("engine/python/dsaviz_py.py"));
  check("the C++ runtime header is shipped beside the engine", files.includes("engine/cpp/dsaviz.hpp"));
  check("the Rust runtime is shipped beside the engine", files.includes("engine/rust/dsaviz.rs"));
  check("the Java runtime is shipped beside the engine", files.includes("engine/java/Dsaviz.java"));
  check("the JavaScript reader, runtime and TypeScript compiler are shipped beside the engine",
    ["engine/js/dsaviz_js.js", "engine/js/dsaviz_rt.js", "engine/js/typescript.js"].every(f => files.includes(f)));
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

  // The listing is what search reads: the name, the keywords, and the README's opening.
  const manifest = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  check("the name says what it is", manifest.displayName === "DSA Playback \u2014 Algorithm Visualizer & Big-O Explainer",
    manifest.displayName);
  const wanted = ["algorithm visualizer", "time complexity", "space complexity", "recursion tree", "linked list", "step by step"];
  check("30 keywords, including the ones students search for",
    manifest.keywords.length === 30 && new Set(manifest.keywords).size === 30 && wanted.every(k => manifest.keywords.includes(k)),
    `${manifest.keywords.length} keywords`);
  check("questions go to Discussions", manifest.qna === "https://github.com/CodeWrapper12/dsa-playback/discussions");
  const opening = readme.split("![")[0];
  const promises = ["Runs on your machine", "No sign-in", "Your code never leaves your computer", "line by line"];
  check("the README opens with running locally, no sign-in, privacy and the why",
    promises.every(p => opening.includes(p)), promises.filter(p => !opening.includes(p)).join(", "));
  const competitors = ["algolens", "python tutor", "pythontutor", "visualgo", "algorithm visualizer pro"];
  check("no other product is named", !competitors.some(name => readme.toLowerCase().includes(name)));

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

  // The C++ compiler is the student's own, as Python is.
  const onPath = name => {
    const found = spawnSync(process.platform === "win32" ? "where" : "which", [name], { encoding: "utf8" });
    return found.status === 0 ? found.stdout.split(/\r?\n/)[0].trim() : "";
  };
  const cppCompiler = process.env.DSAVIZ_CXX || onPath("clang++") || onPath("g++");
  check("a C++ compiler is available to check with", cppCompiler.length > 0, cppCompiler || "set DSAVIZ_CXX");
  const cppSample = path.join(root, "samples", "cpp", "two_sum_sorted.cpp");
  const cppRun = job({ file: cppSample, entry: "Solution.twoSum", stepSize: 8, cppCompiler });
  const cppSteps = cppRun.stdout ? JSON.parse(cppRun.stdout).steps : null;
  check("records a C++ run with the bundled engine", cppSteps?.status === "Completed" && cppSteps.check?.matches === true,
    cppSteps ? `${cppSteps.steps.length} steps${cppSteps.errors.length ? ": " + cppSteps.errors.join("; ") : ""}`
             : (cppRun.stdout || cppRun.stderr).slice(0, 300));

  // Rust is the student's own too: rustc on the path, or rustup's.
  const rustc = process.env.DSAVIZ_RUSTC || onPath("rustc") ||
    [path.join(os.homedir(), ".cargo", "bin", process.platform === "win32" ? "rustc.exe" : "rustc")].find(fs.existsSync) || "";
  check("Rust is available to check with", rustc.length > 0, rustc || "set DSAVIZ_RUSTC");
  const rustSample = path.join(root, "samples", "rust", "two_sum_sorted.rs");
  // rustup's rustc finds its toolchain through the home folder, and rustc finds the system's
  // linker through PATH: both are the student's own, so the real environment is used. .NET is
  // still kept out.
  const rustRun = spawnSync(engine, ["--job", "-"], {
    input: JSON.stringify({ file: rustSample, entry: "Solution.two_sum", stepSize: 8, rustc }),
    env: { ...process.env, DOTNET_ROOT: bare.DOTNET_ROOT }, encoding: "utf8", timeout: 300_000
  });
  const rustSteps = rustRun.stdout ? JSON.parse(rustRun.stdout).steps : null;
  check("records a Rust run with the bundled engine", rustSteps?.status === "Completed" && rustSteps.check?.matches === true,
    rustSteps ? `${rustSteps.steps.length} steps${rustSteps.errors.length ? ": " + rustSteps.errors.join("; ") : ""}`
              : (rustRun.stdout || rustRun.stderr).slice(0, 300));

  // The JDK is the student's own: JAVA_HOME, or javac on the path.
  const javaHome = process.env.DSAVIZ_JAVA_HOME || process.env.JAVA_HOME || "";
  check("a JDK is available to check with", javaHome.length > 0 || onPath("javac").length > 0, javaHome || "set JAVA_HOME");
  const javaSample = path.join(root, "samples", "java", "TwoSumSorted.java");
  const javaRun = spawnSync(engine, ["--job", "-"], {
    input: JSON.stringify({ file: javaSample, entry: "Solution.twoSum", stepSize: 8, javaHome: javaHome || undefined }),
    env: { ...process.env, DOTNET_ROOT: bare.DOTNET_ROOT }, encoding: "utf8", timeout: 300_000
  });
  const javaSteps = javaRun.stdout ? JSON.parse(javaRun.stdout).steps : null;
  check("records a Java run with the bundled engine", javaSteps?.status === "Completed" && javaSteps.check?.matches === true,
    javaSteps ? `${javaSteps.steps.length} steps${javaSteps.errors.length ? ": " + javaSteps.errors.join("; ") : ""}`
              : (javaRun.stdout || javaRun.stderr).slice(0, 300));

  // Node is the student's own; TypeScript's compiler comes with the extension.
  check("Node.js is available to check with", onPath("node").length > 0, onPath("node") || "node not on the path");
  for (const [language, file] of [["JavaScript", "javascript/two_sum_sorted.js"], ["TypeScript", "typescript/two_sum_sorted.ts"]]) {
    const scriptRun = spawnSync(engine, ["--job", "-"], {
      input: JSON.stringify({ file: path.join(root, "samples", file), entry: "twoSum", stepSize: 8 }),
      env: { ...process.env, DOTNET_ROOT: bare.DOTNET_ROOT }, encoding: "utf8", timeout: 300_000
    });
    const scriptSteps = scriptRun.stdout ? JSON.parse(scriptRun.stdout).steps : null;
    check(`records a ${language} run with the bundled engine`, scriptSteps?.status === "Completed" && scriptSteps.check?.matches === true,
      scriptSteps ? `${scriptSteps.steps.length} steps${scriptSteps.errors.length ? ": " + scriptSteps.errors.join("; ") : ""}`
                  : (scriptRun.stdout || scriptRun.stderr).slice(0, 300));
  }

  const measured = job({ file: sample, entry: "Solution.TwoSum" });
  const primary = measured.stdout ? JSON.parse(measured.stdout).primary : null;
  check("measures complexity with no .NET installed", primary?.status === "Completed" && primary.empirical.best === "Linear",
    primary ? `${primary.status}, ${primary.empirical.best}` : measured.stderr.slice(0, 300));
} finally {
  fs.rmSync(unpacked, { recursive: true, force: true });
}

console.log(failures.length === 0 ? "\nPackage verified." : `\n${failures.length} check(s) failed.`);
process.exit(failures.length === 0 ? 0 : 1);
