import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runTests } from "@vscode/test-electron";

// Launches VS Code with this extension loaded from source and the repository open as the
// workspace, as the F5 launch configuration does. On a developer's Mac it uses the
// installed VS Code, so nothing is downloaded; set VSCODE_PATH to use another. Anywhere
// else — CI on Windows and Linux — the current stable VS Code is downloaded.
const MAC_INSTALL = "/Applications/Visual Studio Code.app/Contents/MacOS/Code";

async function main() {
  const extensionDevelopmentPath = path.resolve(__dirname, "../../..");
  const repository = path.resolve(extensionDevelopmentPath, "..");
  const extensionTestsPath = path.resolve(__dirname, "./suite");
  const vscodeExecutablePath = process.env.VSCODE_PATH ?? (fs.existsSync(MAC_INSTALL) ? MAC_INSTALL : undefined);

  if (process.env.VSCODE_PATH && !fs.existsSync(process.env.VSCODE_PATH)) {
    throw new Error(`VSCODE_PATH points at ${process.env.VSCODE_PATH}, which does not exist.`);
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
