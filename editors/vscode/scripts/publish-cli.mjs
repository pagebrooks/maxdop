// Puts a maxdop binary in `bin/`, where the extension looks for it.
//
// Two ways in, because there are two situations:
//
//   node scripts/publish-cli.mjs
//       Builds the CLI for this machine. The development path.
//
//   node scripts/publish-cli.mjs --target darwin-arm64 --binary <path>
//       Takes an already-built binary and stages it for that VSIX target. The
//       release path: NativeAOT does not cross-compile between operating
//       systems, so the seven platform binaries can only come from the seven
//       release-matrix runners, never from one packaging machine.

import { spawnSync } from 'node:child_process';
import { chmodSync, copyFileSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/** VSIX target → .NET runtime identifier. */
const RUNTIME_IDS = {
  'win32-x64': 'win-x64',
  'win32-arm64': 'win-arm64',
  'linux-x64': 'linux-x64',
  'linux-arm64': 'linux-arm64',
  'alpine-x64': 'linux-musl-x64',
  'darwin-x64': 'osx-x64',
  'darwin-arm64': 'osx-arm64',
};

const here = dirname(dirname(fileURLToPath(import.meta.url)));
const repo = resolve(here, '..', '..');
const binDir = join(here, 'bin');

const args = process.argv.slice(2);
const target = value('--target') ?? hostTarget();
const prebuilt = value('--binary');

const runtimeId = RUNTIME_IDS[target];
if (!runtimeId) {
  fail(`unknown target "${target}". Expected one of: ${Object.keys(RUNTIME_IDS).join(', ')}`);
}

const executable = target.startsWith('win32-') ? 'maxdop.exe' : 'maxdop';

rmSync(binDir, { recursive: true, force: true });
mkdirSync(binDir, { recursive: true });

if (prebuilt) {
  copyFileSync(prebuilt, join(binDir, executable));
} else {
  if (target !== hostTarget()) {
    fail(
      `cannot build ${target} on this machine — NativeAOT does not cross-compile between ` +
        `operating systems. Pass --binary <path> with the artifact from the ${runtimeId} ` +
        `release-matrix job.`,
    );
  }

  const staging = join(binDir, '.publish');
  const result = spawnSync(
    'dotnet',
    [
      'publish',
      join(repo, 'src', 'Maxdop.Cli', 'Maxdop.Cli.csproj'),
      '-c', 'Release',
      '-r', runtimeId,
      '-p:PublishAot=true',
      '-o', staging,
      '--nologo',
    ],
    { stdio: 'inherit' },
  );

  if (result.error ?? result.status !== 0) {
    fail(`dotnet publish failed${result.error ? `: ${result.error.message}` : ''}`);
  }

  copyFileSync(join(staging, executable), join(binDir, executable));
  rmSync(staging, { recursive: true, force: true });
}

if (!target.startsWith('win32-')) {
  chmodSync(join(binDir, executable), 0o755);
}

console.log(`bin/${executable} ready for ${target} (${runtimeId})`);

function value(flag) {
  const index = args.indexOf(flag);
  return index >= 0 ? args[index + 1] : undefined;
}

function hostTarget() {
  const architecture = process.arch === 'arm64' ? 'arm64' : 'x64';
  return `${process.platform}-${architecture}`;
}

function fail(message) {
  console.error(`publish-cli: ${message}`);
  process.exit(1);
}
