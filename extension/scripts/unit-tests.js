// Runs the unit tests: every out/test/*.test.js, and nothing under out/test/integration,
// which needs a running VS Code. Listed here rather than by a shell glob, which the
// Windows shell does not expand.
const { spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const folder = path.join(__dirname, "..", "out", "test");
const files = fs.readdirSync(folder).filter(name => name.endsWith(".test.js")).map(name => path.join(folder, name));
if (files.length === 0) {
  console.error(`No unit tests found in ${folder}. Compile first: npx tsc -p ./`);
  process.exit(1);
}
const result = spawnSync(process.execPath, ["--test", ...files], { stdio: "inherit" });
process.exit(result.status ?? 1);
