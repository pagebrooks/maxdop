// Renders the terminal demo — docs/images/demo.gif and docs/images/demo.png —
// by driving the real maxdop binary through a real shell with VHS.
//
//   node render.mjs           re-record the GIF and the still
//   node render.mjs --check   fail if the recording would show stale output
//
// The split matters. Recording needs vhs, ttyd and ffmpeg, takes tens of seconds
// and produces a GIF whose bytes differ run to run — frame timing is not
// reproducible, so comparing the file against a committed copy would fail every
// time and teach everyone to ignore it.
//
// What *is* reproducible is the text the demo puts on screen. --check formats
// before.sql with the real binary and compares it to the committed snapshot, so
// a layout change fails CI in seconds with no recording tools installed, and the
// GIF can never quietly end up advertising output the formatter stopped
// producing. That is the same guarantee generate.mjs makes for the README image.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const check = process.argv.includes('--check');

const input = join(here, 'before.sql');
const snapshot = join(here, 'after.sql');
const tape = join(here, 'demo.tape');

/** The file name the tape types at, and so the one a viewer reads on screen. */
const ON_SCREEN_NAME = 'orders.sql';

function fail(message) {
  console.error(`demo: ${message}`);
  process.exit(1);
}

/**
 * The binary under test. MAXDOP_BINARY lets CI point at the artifact it just
 * published rather than building a second copy.
 */
function binary() {
  const configured = process.env.MAXDOP_BINARY;
  if (configured) {
    if (!existsSync(configured)) {
      fail(`MAXDOP_BINARY is set to ${configured}, which does not exist.`);
    }
    return configured;
  }

  const built = join(repo, 'src', 'Maxdop.Cli', 'bin', 'Release', 'net10.0', 'maxdop');
  if (!existsSync(built)) {
    fail(
      'no maxdop binary. Build one with\n' +
        '  mise exec -- dotnet build src/Maxdop.Cli -c Release\n' +
        'or point MAXDOP_BINARY at an existing one.',
    );
  }
  return built;
}

/** What the real formatter makes of before.sql, right now. */
function format(bin) {
  const run = spawnSync(bin, [input], { encoding: 'utf8' });

  if (run.error) {
    fail(`could not run ${bin}: ${run.error.message}`);
  }
  if (run.status !== 0) {
    fail(`maxdop exited ${run.status} on before.sql:\n${run.stderr}`);
  }
  return run.stdout;
}

function requireTool(name, hint) {
  // `which`, not `command -v` through a shell: node deprecates passing args to a
  // shelled child, and this needs no shell to begin with.
  if (spawnSync('which', [name], { stdio: 'ignore' }).status !== 0) {
    fail(`${name} is not installed. ${hint}`);
  }
}

const bin = binary();
const current = format(bin);

if (check) {
  let committed;
  try {
    committed = readFileSync(snapshot, 'utf8');
  } catch {
    fail(`no snapshot at ${snapshot}. Run "npm run terminal".`);
  }

  if (committed !== current) {
    fail(
      'the terminal demo would now show different output than the committed snapshot.\n' +
        'maxdop\'s layout changed, so docs/images/demo.gif and demo.png are stale.\n' +
        'Re-record them with "npm run terminal" (needs vhs, ttyd and ffmpeg).',
    );
  }

  console.log('demo: recording matches current formatter output');
  process.exit(0);
}

requireTool('vhs', 'See https://github.com/charmbracelet/vhs#installation.');
requireTool('ttyd', 'vhs needs it to drive a real terminal.');
requireTool('ffmpeg', 'vhs needs it to encode the GIF.');

// A scratch directory, so the demo types at `orders.sql` rather than at a path
// that would show a machine's directory layout on screen — and so --write in the
// second act cannot touch anything committed.
const stage = mkdtempSync(join(tmpdir(), 'maxdop-demo-'));

try {
  writeFileSync(join(stage, ON_SCREEN_NAME), readFileSync(input));

  // The binary goes on PATH under its plain name: the tape must type `maxdop`,
  // not an absolute path into someone's build directory.
  const bindir = join(stage, 'bin');
  mkdirSync(bindir);
  spawnSync('ln', ['-sf', bin, join(bindir, 'maxdop')]);

  const run = spawnSync('vhs', [tape], {
    cwd: repo,
    stdio: 'inherit',
    env: {
      ...process.env,
      DEMO_DIR: stage,
      PATH: `${bindir}:${process.env.PATH}`,
      // Keeps the recorded prompt to a bare `$`, so the GIF shows a command and
      // not a hostname, a git branch, or whatever the recorder's shell theme is.
      PS1: '\\$ ',
      BASH_SILENCE_DEPRECATION_WARNING: '1',
    },
  });

  if (run.status !== 0) {
    fail(`vhs exited ${run.status}.`);
  }

  writeFileSync(snapshot, current);

  for (const artifact of ['demo.gif', 'demo.png']) {
    const path = join(repo, 'docs', 'images', artifact);
    if (!existsSync(path)) {
      fail(`vhs did not produce ${artifact}.`);
    }
    const kb = Math.round(readFileSync(path).length / 1024);
    console.log(`demo: docs/images/${artifact}  ${kb} KB`);
  }
} finally {
  rmSync(stage, { recursive: true, force: true });
}
