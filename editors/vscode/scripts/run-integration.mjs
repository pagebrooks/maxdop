// Launches the installed VS Code with this extension loaded and runs
// test/integration against it.
//
// Isolated `--user-data-dir` and `--extensions-dir` on purpose: the test must not
// read the developer's settings (a personal `editor.defaultFormatter` would
// change the result) and must not leave anything behind in their profile. It also
// forces a fresh instance rather than reusing a running window.

import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const extension = dirname(dirname(fileURLToPath(import.meta.url)));
const sandbox = mkdtempSync(join(tmpdir(), 'maxdop-vscode-it-'));
const workspace = join(sandbox, 'workspace');
const TIMEOUT_MS = 120_000;

spawnSync('mkdir', ['-p', workspace]);

writeFileSync(join(workspace, '.maxdop.json'), '{ "keywordCase": "lower" }');
writeFileSync(join(workspace, 'messy.sql'), 'select a,b from dbo.t where a=1\n');
writeFileSync(join(workspace, 'broken.sql'), 'SELECT FROM WHERE;\n');

// Formatted by the same binary the extension will use, and through the same
// --stdin-filepath, so it is a fixed point under *this workspace's* config. Built
// without the flag it would come back upper-case while the extension produces
// lower-case, and the "no edit when nothing would change" test would fail for a
// reason that has nothing to do with the extension.
const formatted = spawnSync(
  join(extension, 'bin', 'maxdop'),
  ['--stdin-filepath', join(workspace, 'already.sql')],
  {
    input: 'select a,b from dbo.t where a=1\n',
    encoding: 'utf8',
    cwd: workspace,
  },
);
writeFileSync(join(workspace, 'already.sql'), formatted.stdout);

// The Electron binary, NOT the `code` wrapper on PATH. The wrapper hands the
// arguments to a new window and exits 0 immediately, so a test run through it
// reports success before a single assertion has executed — verified by making an
// assertion that cannot hold and watching it "pass". Anything that can only ever
// return green is worse than no test at all.
const binary = process.env.VSCODE_BIN ?? '/usr/share/code/code';

const child = spawn(
  binary,
  [
    workspace,
    `--extensionDevelopmentPath=${extension}`,
    `--extensionTestsPath=${join(extension, 'out', 'test', 'integration', 'index.js')}`,
    `--user-data-dir=${join(sandbox, 'user-data')}`,
    `--extensions-dir=${join(sandbox, 'extensions')}`,
    // No --disable-extensions: it disables the *built-in* extensions too, and the
    // built-in SQL extension is what gives a .sql file the `sql` language id this
    // provider registers for. Without it the document opens as plaintext and no
    // formatter is ever consulted. The empty --extensions-dir above already
    // guarantees no third-party extension is loaded.
    '--disable-workspace-trust',
    '--disable-gpu',
    '--new-window',
  ],
  { stdio: 'inherit' },
);

const timer = setTimeout(() => {
  console.error(`run-integration: no result after ${TIMEOUT_MS / 1000}s, killing VS Code`);
  child.kill('SIGKILL');
  process.exit(1);
}, TIMEOUT_MS);

child.on('exit', (code) => {
  clearTimeout(timer);
  console.log(code === 0 ? 'integration tests passed' : `integration tests failed (exit ${code})`);
  process.exit(code ?? 1);
});
