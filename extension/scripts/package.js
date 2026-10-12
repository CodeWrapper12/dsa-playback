// Builds one platform's extension package: the engine published self-contained for that
// platform, so students need no .NET, packed with the compiled extension.
//
//   node scripts/package.js                 the platform this machine runs
//   node scripts/package.js linux-x64       any target in TARGETS
//
// Writes dist/dsa-visualizer-<target>.vsix.

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

// VS Code's platform names, and the .NET runtime each one needs.
const TARGETS = {
  "win32-x64": "win-x64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64",
  "linux-x64": "linux-x64"
};

const target = process.argv[2] || `${process.platform}-${process.arch}`;
const runtime = TARGETS[target];
if (!runtime) {
  console.error(`Unknown target '${target}'. Choose one of: ${Object.keys(TARGETS).join(", ")}.`);
  process.exit(2);
}

const extensionRoot = path.resolve(__dirname, "..");
const engineProject = path.resolve(extensionRoot, "..", "engine", "DsaViz.Engine", "DsaViz.Engine.csproj");
const engineOut = path.join(extensionRoot, "engine");
const distribution = path.join(extensionRoot, "dist");

function run(command, args, cwd = extensionRoot) {
  console.log(`> ${command} ${args.join(" ")}`);
  execFileSync(command, args, { cwd, stdio: "inherit", shell: process.platform === "win32" });
}

// A previous target's engine must not end up inside this one's package.
fs.rmSync(engineOut, { recursive: true, force: true });

run("dotnet", [
  "publish", engineProject,
  "-c", "Release",
  "-r", runtime,
  "--self-contained", "true",
  // Roslyn ships its messages in thirteen languages; the engine reports in English.
  "-p:SatelliteResourceLanguages=en",
  "-p:DebugType=none",
  // Precompiled, so starting the engine does not mean compiling Roslyn first: each run
  // starts about twice as fast, for about 7 MB more download.
  "-p:PublishReadyToRun=true",
  "-o", engineOut
]);

run("npx", ["tsc", "-p", "./"]);

fs.mkdirSync(distribution, { recursive: true });
const output = path.join(distribution, `dsa-visualizer-${target}.vsix`);
run("npx", ["vsce", "package", "--target", target, "--out", output,
  "--allow-missing-repository", "--skip-license"]);

const megabytes = fs.statSync(output).size / (1024 * 1024);
console.log(`\n${output}: ${megabytes.toFixed(1)} MB`);
