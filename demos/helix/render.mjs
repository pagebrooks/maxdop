// Renders the Helix demo — docs/images/helix.gif and helix.png — by driving the
// real editor and the real maxdop binary with VHS.
//
//   node render.mjs           re-record the GIF and the still
//   node render.mjs --check   fail if the recording would show stale output
//
// The same split as demos/terminal/render.mjs, for the same reason: recording
// needs vhs, ttyd, ffmpeg and hx and produces bytes that differ run to run,
// while the *text* on screen is reproducible and can be checked in seconds with
// none of those installed.
//
// The fixture is shared with the terminal demo rather than copied. Both images
// then show the same input becoming the same output, and there is one snapshot
// to keep current instead of two that can disagree.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const check = process.argv.includes('--check');

const input = join(here, '..', 'terminal', 'before.sql');
const snapshot = join(here, '..', 'terminal', 'after.sql');
const tape = join(here, 'demo.tape');

/** The name the tape opens, and so the one a viewer reads in the status line. */
const ON_SCREEN_NAME = 'orders.sql';


function fail(message) {
  console.error(`helix demo: ${message}`);
  process.exit(1);
}

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

/** What the real formatter makes of the shared fixture, right now. */
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
      'the Helix demo would now show different output than the committed snapshot.\n' +
        "maxdop's layout changed, so docs/images/helix.gif and helix.png are stale.\n" +
        'Re-record with "npm run helix" (needs vhs, ttyd, ffmpeg and hx).',
    );
  }

  console.log('helix demo: recording matches current formatter output');
  process.exit(0);
}

requireTool('vhs', 'See https://github.com/charmbracelet/vhs#installation.');
requireTool('ttyd', 'vhs needs it to drive a real terminal.');
requireTool('ffmpeg', 'vhs needs it to encode the GIF.');
/**
 * The binary mise.toml pins, when mise can supply it.
 *
 * Preferred over PATH because the pin is the whole point. The documented command
 * is a bare `npm run helix`, so resolving the editor against whatever happens to
 * be installed is exactly what the pin exists to prevent — and it is what put a
 * nightly's stack trace into this recording once already. Falls through to PATH
 * when mise is absent, so the script still runs without it.
 */
function pinned(tool) {
  const found = spawnSync('mise', ['which', tool], { encoding: 'utf8', cwd: repo });
  return found.status === 0 ? found.stdout.trim() : null;
}

/**
 * The editor to record. mise.toml pins a version, which is consulted before PATH
 * so the recording does not silently depend on the recorder's own Helix — a
 * different one renders a different theme and a different status line, and the
 * still is compared against its predecessor by eye. HELIX_BINARY overrides both.
 */
function helixBinary() {
  const configured = process.env.HELIX_BINARY;
  if (configured) {
    if (!existsSync(configured)) {
      fail(`HELIX_BINARY is set to ${configured}, which does not exist.`);
    }
    return configured;
  }

  const fromMise = pinned('hx');
  if (fromMise) {
    return fromMise;
  }

  requireTool('hx', 'mise.toml pins one — run "mise install" — or set HELIX_BINARY.');
  return spawnSync('which', ['hx'], { encoding: 'utf8' }).stdout.trim();
}

/**
 * The formatter configuration, lifted out of docs/editors.md rather than copied.
 *
 * The recording demonstrates config readers are told to paste, so the two must be
 * the same text. Extracting it means a change to the documented block shows up in
 * the next recording, and a broken block fails this script instead of producing a
 * GIF of an editor that formatted nothing.
 */
function languagesToml() {
  const doc = readFileSync(join(repo, 'docs', 'editors.md'), 'utf8');
  const heading = '## Helix';
  const at = doc.indexOf(heading);
  if (at === -1) {
    fail(`docs/editors.md no longer has a "${heading}" section to take the config from.`);
  }
  const block = /```toml\n([\s\S]*?)```/.exec(doc.slice(at));
  if (!block) {
    fail(`no toml block under "${heading}" in docs/editors.md.`);
  }
  return block[1];
}

// A scratch directory, so the status line reads `orders.sql` rather than a path
// through somebody's home directory — and so the write cannot touch anything
// committed.
const stage = mkdtempSync(join(tmpdir(), 'maxdop-helix-'));

try {
  writeFileSync(join(stage, ON_SCREEN_NAME), readFileSync(input));

  // maxdop goes on PATH under its plain name, because the languages.toml in the
  // recording is the one from the docs — `command = "maxdop"`, not a path into
  // somebody's build directory. hx joins it so the tape can type a bare `hx`.
  const bindir = join(stage, 'bin');
  mkdirSync(bindir);
  spawnSync('ln', ['-sf', bin, join(bindir, 'maxdop')]);
  spawnSync('ln', ['-sf', helixBinary(), join(bindir, 'hx')]);

  // XDG_* into the staging directory so the recorder's own Neovim config, plugins
  // and shada file are not involved. Without this the recording shows whatever
  // colorscheme and statusline the person recording happens to run.
  const xdg = join(stage, 'xdg');
  for (const sub of ['config', 'data', 'state', 'cache']) {
    mkdirSync(join(xdg, sub), { recursive: true });
  }

  // Placed where a bare `hx` will find it, so the tape types the command a
  // viewer would type and the recorder's own Helix config is not involved.
  mkdirSync(join(xdg, 'config', 'helix'), { recursive: true });
  writeFileSync(join(xdg, 'config', 'helix', 'config.toml'), readFileSync(join(here, 'config.toml')));
  writeFileSync(join(xdg, 'config', 'helix', 'languages.toml'), languagesToml());

  const run = spawnSync('vhs', [tape], {
    cwd: repo,
    stdio: 'inherit',
    env: {
      ...process.env,
      DEMO_DIR: stage,
      PATH: `${bindir}:${process.env.PATH}`,
      PS1: '\\$ ',
      BASH_SILENCE_DEPRECATION_WARNING: '1',
      XDG_CONFIG_HOME: join(xdg, 'config'),
      XDG_DATA_HOME: join(xdg, 'data'),
      XDG_STATE_HOME: join(xdg, 'state'),
      XDG_CACHE_HOME: join(xdg, 'cache'),
    },
  });

  if (run.status !== 0) {
    fail(`vhs exited ${run.status}.`);
  }

  for (const artifact of ['helix.gif', 'helix.png']) {
    const path = join(repo, 'docs', 'images', artifact);
    if (!existsSync(path)) {
      fail(`vhs did not produce ${artifact}.`);
    }
    const kb = Math.round(readFileSync(path).length / 1024);
    console.log(`helix demo: docs/images/${artifact}  ${kb} KB`);
  }
} finally {
  rmSync(stage, { recursive: true, force: true });
}
