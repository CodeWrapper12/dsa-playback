import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runTests } from "@vscode/test-electron";

// Launches the installed VS Code — no download — with this extension loaded from source
// and the repository open as the workspace, exactly as the F5 launch configuration does.
// The workspace matters: the extension finds the engine relative to it.
async function main() {
  const extensionDevelopmentPath = path.resolve(__dirname, "../../..");
  const repository = path.resolve(extensionDevelopmentPath, "..");
  const extensionTestsPath = path.resolve(__dirname, "./suite");
  const vscodeExecutablePath = process.env.VSCODE_PATH ?? "/Applications/Visual Studio Code.app/Contents/MacOS/Code";

  if (!fs.existsSync(vscodeExecutablePath)) {
    throw new Error(`VS Code not found at ${vscodeExecutablePath}. Set VSCODE_PATH to its executable.`);
  }

  const userData = fs.mkdtempSync(path.join(os.tmpdir(), "dsaviz-vscode-"));
  await runTests({
    vscodeExecutablePath,
    extensionDevelopmentPath,
    extensionTestsPath,
    launchArgs: [repository, "--disable-extensions", "--user-data-dir", userData, "--skip-welcome", "--skip-release-notes"]
  });
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
