// Renders the Neovim demo — docs/images/nvim.gif and nvim.png — by driving the
// real editor, the real conform.nvim, and the real maxdop binary with VHS.
//
//   node render.mjs           re-record the GIF and the still
//   node render.mjs --check   fail if the recording would show stale output
//
// The same split as demos/terminal/render.mjs, for the same reason: recording
// needs vhs, ttyd, ffmpeg and nvim and produces bytes that differ run to run,
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

const CONFORM_REPO = 'https://github.com/stevearc/conform.nvim.git';

function fail(message) {
  console.error(`nvim demo: ${message}`);
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
      'the Neovim demo would now show different output than the committed snapshot.\n' +
        "maxdop's layout changed, so docs/images/nvim.gif and nvim.png are stale.\n" +
        'Re-record with "npm run nvim" (needs vhs, ttyd, ffmpeg and nvim).',
    );
  }

  console.log('nvim demo: recording matches current formatter output');
  process.exit(0);
}

requireTool('vhs', 'See https://github.com/charmbracelet/vhs#installation.');
requireTool('ttyd', 'vhs needs it to drive a real terminal.');
requireTool('ffmpeg', 'vhs needs it to encode the GIF.');
/**
 * The binary mise.toml pins, when mise can supply it.
 *
 * Preferred over PATH because the pin is the whole point. The documented command
 * is a bare `npm run nvim`, so resolving the editor against whatever happens to
 * be installed is exactly what the pin exists to prevent — and it is what put a
 * nightly's stack trace into this recording once already. Falls through to PATH
 * when mise is absent, so the script still runs without it.
 */
function pinned(tool) {
  const found = spawnSync('mise', ['which', tool], { encoding: 'utf8', cwd: repo });
  return found.status === 0 ? found.stdout.trim() : null;
}

/**
 * The editor to record.
 *
 * This demo is a compatibility test as much as a recording: conform's synchronous
 * format-on-save path calls vim.wait with a fractional timeout, and a Neovim
 * strict enough to reject that errors in BufWritePre and writes the file
 * unformatted. Recording against whatever nvim happens to be on PATH is how a GIF
 * ends up showing a formatter that did nothing, which is why mise.toml pins one
 * and why that pin is consulted before PATH. NVIM_BINARY overrides both.
 */
function nvimBinary() {
  const configured = process.env.NVIM_BINARY;
  if (configured) {
    if (!existsSync(configured)) {
      fail(`NVIM_BINARY is set to ${configured}, which does not exist.`);
    }
    return configured;
  }

  const fromMise = pinned('nvim');
  if (fromMise) {
    return fromMise;
  }

  requireTool('nvim', 'mise.toml pins one — run "mise install" — or set NVIM_BINARY.');
  return spawnSync('which', ['nvim'], { encoding: 'utf8' }).stdout.trim();
}
requireTool('git', 'Needed to fetch conform.nvim.');

/**
 * conform.nvim, the plugin docs/editors.md tells Neovim users to configure.
 *
 * Cloned rather than vendored: this repository should not carry a copy of
 * somebody else's plugin, and a recording made against a stale one would show a
 * setup that no longer matches the documentation. CONFORM_NVIM points at an
 * existing checkout when there is one, which also makes the demo recordable
 * offline.
 */
function conform(stage) {
  const existing = process.env.CONFORM_NVIM;
  if (existing) {
    if (!existsSync(existing)) {
      fail(`CONFORM_NVIM is set to ${existing}, which does not exist.`);
    }
    return existing;
  }

  const target = join(stage, 'conform.nvim');
  const clone = spawnSync('git', ['clone', '--depth', '1', '--quiet', CONFORM_REPO, target], {
    encoding: 'utf8',
  });
  if (clone.status !== 0) {
    fail(`could not clone conform.nvim: ${clone.stderr?.trim()}\nSet CONFORM_NVIM to a checkout to record offline.`);
  }
  return target;
}

/**
 * The visual-mode keymap, lifted out of docs/editors.md rather than copied here.
 *
 * The recording demonstrates a snippet readers are told to paste, so the two must
 * be the same text. Extracting it means a change to the documented code shows up
 * in the next recording, and a broken snippet fails this script instead of
 * producing a GIF of a keypress that does nothing.
 */
function selectionKeymap() {
  const doc = readFileSync(join(repo, 'docs', 'editors.md'), 'utf8');
  const heading = '### Formatting just the selection';
  const at = doc.indexOf(heading);
  if (at === -1) {
    fail(`docs/editors.md no longer has a "${heading}" section to take the keymap from.`);
  }
  const block = /```lua\n([\s\S]*?)```/.exec(doc.slice(at));
  if (!block) {
    fail(`no lua block under "${heading}" in docs/editors.md.`);
  }
  return block[1];
}

// A scratch directory, so the status line reads `orders.sql` rather than a path
// through somebody's home directory — and so the write cannot touch anything
// committed.
const stage = mkdtempSync(join(tmpdir(), 'maxdop-nvim-'));

try {
  writeFileSync(join(stage, ON_SCREEN_NAME), readFileSync(input));

  // maxdop goes on PATH under its plain name, because the conform config in the
  // recording is the one from the docs — `command = "maxdop"`, not a path into
  // somebody's build directory.
  const bindir = join(stage, 'bin');
  mkdirSync(bindir);
  spawnSync('ln', ['-sf', bin, join(bindir, 'maxdop')]);
  spawnSync('ln', ['-sf', nvimBinary(), join(bindir, 'nvim')]);

  const conformPath = conform(stage);

  // XDG_* into the staging directory so the recorder's own Neovim config, plugins
  // and shada file are not involved. Without this the recording shows whatever
  // colorscheme and statusline the person recording happens to run.
  const xdg = join(stage, 'xdg');
  for (const sub of ['config', 'data', 'state', 'cache']) {
    mkdirSync(join(xdg, sub), { recursive: true });
  }

  // The config is placed where a bare `nvim` will find it, rather than passed
  // with -u or VIMINIT, so the tape can type the command a viewer would type.
  mkdirSync(join(xdg, 'config', 'nvim'), { recursive: true });
  writeFileSync(
    join(xdg, 'config', 'nvim', 'init.lua'),
    `${readFileSync(join(here, 'init.lua'), 'utf8')}\n${selectionKeymap()}\n`,
  );

  const run = spawnSync('vhs', [tape], {
    cwd: repo,
    stdio: 'inherit',
    env: {
      ...process.env,
      DEMO_DIR: stage,
      PATH: `${bindir}:${process.env.PATH}`,
      PS1: '\\$ ',
      BASH_SILENCE_DEPRECATION_WARNING: '1',
      CONFORM_PATH: conformPath,
      XDG_CONFIG_HOME: join(xdg, 'config'),
      XDG_DATA_HOME: join(xdg, 'data'),
      XDG_STATE_HOME: join(xdg, 'state'),
      XDG_CACHE_HOME: join(xdg, 'cache'),
    },
  });

  if (run.status !== 0) {
    fail(`vhs exited ${run.status}.`);
  }

  for (const artifact of ['nvim.gif', 'nvim.png']) {
    const path = join(repo, 'docs', 'images', artifact);
    if (!existsSync(path)) {
      fail(`vhs did not produce ${artifact}.`);
    }
    const kb = Math.round(readFileSync(path).length / 1024);
    console.log(`nvim demo: docs/images/${artifact}  ${kb} KB`);
  }
} finally {
  rmSync(stage, { recursive: true, force: true });
}
