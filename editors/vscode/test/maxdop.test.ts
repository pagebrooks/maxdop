import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, describe, it } from 'node:test';
import { format } from '../src/maxdop';

/**
 * Runs against the real CLI, not a stub. `npm test` builds it first (see the
 * `pretest` script); `MAXDOP_BINARY` overrides for a binary from elsewhere, such
 * as a release-matrix artifact.
 */
// __dirname is out/test once compiled, so the extension root is two levels up.
const binary =
  process.env.MAXDOP_BINARY ??
  join(__dirname, '..', '..', 'bin', process.platform === 'win32' ? 'maxdop.exe' : 'maxdop');

describe('format', () => {
  before(() => {
    assert.ok(
      existsSync(binary),
      `no maxdop binary at ${binary} — run "npm run build:binary", or set MAXDOP_BINARY.`,
    );
  });

  it('formats a statement', async () => {
    const outcome = await format({ binary, text: 'select a,b from dbo.t where a=1' });

    assert.equal(outcome.kind, 'formatted');
    assert.match(outcome.kind === 'formatted' ? outcome.text : '', /^SELECT\b/);
  });

  it('is a fixed point on its own output', async () => {
    const once = await format({ binary, text: 'select a,b from dbo.t where a=1' });
    assert.equal(once.kind, 'formatted');

    const twice = await format({ binary, text: once.kind === 'formatted' ? once.text : '' });
    assert.equal(twice.kind, 'formatted');
    assert.equal(twice.kind === 'formatted' ? twice.text : '', once.kind === 'formatted' ? once.text : '');
  });

  it('keeps CRLF documents on CRLF', async () => {
    // The editor hands over whatever the document holds. A formatter that
    // normalised line endings would turn every format on a Windows-authored file
    // into a whole-file diff.
    const outcome = await format({ binary, text: 'SELECT 1;\r\nSELECT 2;\r\n' });

    assert.equal(outcome.kind, 'formatted');
    const text = outcome.kind === 'formatted' ? outcome.text : '';
    assert.ok(text.includes('\r\n'), 'CRLF was lost');
    assert.ok(!/(?<!\r)\n/.test(text), 'a bare LF crept in');
  });

  it('survives multi-byte characters split across reads', async () => {
    // A long run of non-ASCII text makes stdout arrive in several chunks, which
    // is where per-chunk decoding corrupts a character straddling a boundary.
    const comment = `-- ${'é—😀 '.repeat(4000)}\n`;
    const outcome = await format({ binary, text: `${comment}SELECT 1;\n` });

    assert.equal(outcome.kind, 'formatted');
    const text = outcome.kind === 'formatted' ? outcome.text : '';
    assert.ok(!text.includes('\uFFFD'), 'a replacement character appeared');
    assert.ok(text.includes('😀'), 'the astral character did not survive');
  });

  it('declines an unparseable buffer instead of editing it', async () => {
    const outcome = await format({ binary, text: 'SELECT FROM WHERE;' });

    assert.equal(outcome.kind, 'declined');
    assert.ok(outcome.kind === 'declined' && outcome.message.length > 0, 'no reason given');
    assert.ok(outcome.kind === 'declined' && !outcome.message.startsWith('maxdop: '), 'prefix not stripped');
  });

  it('names SQLCMD syntax rather than calling it a parse error', async () => {
    const outcome = await format({ binary, text: ':setvar Path "C:\\temp"\nSELECT 1;\n' });

    assert.equal(outcome.kind, 'declined');
    assert.match(outcome.kind === 'declined' ? outcome.message : '', /SQLCMD/i);
  });

  it('reports a missing binary in a way that says what to do', async () => {
    const outcome = await format({ binary: join(tmpdir(), 'not-maxdop-at-all'), text: 'SELECT 1;' });

    assert.equal(outcome.kind, 'failed');
    assert.match(outcome.kind === 'failed' ? outcome.message : '', /maxdop\.path/);
  });

  it('never settles once cancelled', async () => {
    const controller = new AbortController();
    controller.abort();

    const settled = await Promise.race([
      format({ binary, text: 'SELECT 1;', signal: controller.signal }),
      new Promise((resolve) => setTimeout(() => resolve('still pending'), 250)),
    ]);

    // An aborted format is not a failure: VS Code has already moved on, and
    // resolving would race an edit against whatever the user typed next.
    assert.equal(settled, 'still pending');
  });
});

describe('config discovery', () => {
  let workspace: string;

  before(async () => {
    workspace = await mkdtemp(join(tmpdir(), 'maxdop-vscode-'));
    await writeFile(join(workspace, '.maxdop.json'), '{ "keywordCase": "lower" }');
  });

  after(() => {
    /* mkdtemp lives under the OS temp directory; leaving it is harmless. */
  });

  it('finds the repo config through --stdin-filepath', async () => {
    // The whole point of passing the path: without it an unsaved buffer would
    // format with defaults and quietly ignore the team's settings.
    const outcome = await format({
      binary,
      text: 'SELECT 1;',
      filePath: join(workspace, 'query.sql'),
    });

    assert.equal(outcome.kind, 'formatted');
    assert.match(outcome.kind === 'formatted' ? outcome.text : '', /^select\b/);
  });

  it('formats with defaults when there is no path to discover from', async () => {
    const outcome = await format({ binary, text: 'select 1;' });

    assert.equal(outcome.kind, 'formatted');
    assert.match(outcome.kind === 'formatted' ? outcome.text : '', /^SELECT\b/);
  });
});

/**
 * The Marketplace renders README.md outside the repo, so the screenshot has to be an
 * absolute URL pinned to a tag — which means the tag is a second copy of the version in
 * package.json, free to drift the moment someone bumps one and not the other. Nothing
 * catches that: a bad pin is a broken image on the Marketplace listing, long after the
 * release. So the two are asserted equal here rather than remembered.
 */
describe('marketplace README', () => {
  const root = join(__dirname, '..', '..');
  const readme = readFileSync(join(root, 'README.md'), 'utf8');
  const { version } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as {
    version: string;
  };

  it('pins images to the tag matching package.json', () => {
    const pins = [...readme.matchAll(/raw\.githubusercontent\.com\/[^/]+\/[^/]+\/([^/]+)\//g)];

    assert.ok(pins.length > 0, 'no absolute image URL found — the Marketplace needs one');

    for (const [url, ref] of pins) {
      assert.equal(ref, `v${version}`, `${url} is pinned to ${ref}, not v${version}`);
    }
  });

  it('uses no relative image paths, which the Marketplace cannot resolve', () => {
    assert.equal(/!\[[^\]]*\]\((?!https?:)/.test(readme), false);
  });

  /**
   * The publisher id is written twice: once as the identity the extension is published
   * under, and once inside `configurationDefaults`, where it names the formatter VS Code
   * should hand .sql files to. If those two drift, the extension installs and then formats
   * nothing, because the default formatter it points at does not exist — a failure that
   * produces no error anywhere. (That the id is not still the placeholder is checked at
   * release time instead, so day-to-day CI is not red for a registration that has not
   * happened yet.)
   */
  it('names itself as the default formatter, consistently', () => {
    const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as {
      publisher: string;
      name: string;
      contributes?: { configurationDefaults?: Record<string, unknown> };
    };
    const defaults = pkg.contributes?.configurationDefaults ?? {};
    let checked = 0;

    for (const [scope, settings] of Object.entries(defaults)) {
      const formatter = (settings as Record<string, unknown>)['editor.defaultFormatter'];
      if (typeof formatter === 'string') {
        assert.equal(formatter, `${pkg.publisher}.${pkg.name}`, `${scope} points elsewhere`);
        checked++;
      }
    }

    assert.ok(checked > 0, 'no default formatter declared — the extension would not be picked');
  });
});
