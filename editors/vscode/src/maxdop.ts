import { spawn } from 'node:child_process';

/**
 * Running the maxdop CLI. Deliberately free of any `vscode` import so it can be
 * exercised by `node --test` against the real binary — the interesting failures
 * here (a partial UTF-8 read, an exit code mapped to the wrong outcome, a
 * process left running after cancellation) are all invisible to a test that
 * stubs the process out, and all cost the user their document.
 */

export interface FormatRequest {
  /** Path to the maxdop executable. */
  readonly binary: string;
  /** The document's full text, exactly as the editor holds it. */
  readonly text: string;
  /**
   * Where the buffer came from, passed as `--stdin-filepath`. This is what lets
   * the CLI discover the repo's `.maxdop.json`; without it an unsaved buffer
   * silently formats with defaults instead of the team's settings.
   */
  readonly filePath?: string;
  /** Aborts the run and kills the process. */
  readonly signal?: AbortSignal;
}

export type FormatOutcome =
  /** maxdop formatted the buffer. `text` is the replacement. */
  | { readonly kind: 'formatted'; readonly text: string }
  /**
   * Exit 1: the input's problem — a parse error or SQLCMD syntax. The file is
   * fine and the CLI echoed it back unchanged; the editor must make no edit.
   */
  | { readonly kind: 'declined'; readonly message: string }
  /** Exit 2, or the process could not be run at all. maxdop's problem. */
  | { readonly kind: 'failed'; readonly message: string };

/** Exit codes are the CLI's contract with every caller. */
const enum Exit {
  Ok = 0,
  InputsProblem = 1,
}

export function format(request: FormatRequest): Promise<FormatOutcome> {
  const args = ['--stdin-filepath', request.filePath ?? 'stdin.sql'];

  return new Promise((resolve) => {
    const child = spawn(request.binary, args, { stdio: ['pipe', 'pipe', 'pipe'] });

    // stdout is collected as buffers and decoded once at the end. Decoding each
    // chunk as it arrives would corrupt any multi-byte character that happens to
    // straddle a chunk boundary — rare enough to pass every ASCII test and then
    // mangle the first document containing an em dash.
    const stdout: Buffer[] = [];
    let stderr = '';
    let settled = false;

    const finish = (outcome: FormatOutcome) => {
      if (!settled) {
        settled = true;
        request.signal?.removeEventListener('abort', abort);
        resolve(outcome);
      }
    };

    const abort = () => {
      child.kill();
      // No outcome: a cancelled format is not a failure and must not raise
      // anything at the user. The provider's promise is discarded by VS Code.
      settled = true;
    };

    if (request.signal?.aborted) {
      child.kill();
      return;
    }
    request.signal?.addEventListener('abort', abort, { once: true });

    child.stdout.on('data', (chunk: Buffer) => stdout.push(chunk));
    child.stderr.on('data', (chunk: Buffer) => (stderr += chunk.toString('utf8')));

    child.on('error', (error: NodeJS.ErrnoException) =>
      finish({
        kind: 'failed',
        message:
          error.code === 'ENOENT'
            ? `maxdop was not found at ${request.binary}. Set "maxdop.path" to point at the binary.`
            : `could not run ${request.binary}: ${error.message}`,
      }),
    );

    child.on('close', (code) => {
      if (code === Exit.Ok) {
        finish({ kind: 'formatted', text: Buffer.concat(stdout).toString('utf8') });
      } else if (code === Exit.InputsProblem) {
        finish({ kind: 'declined', message: clean(stderr) });
      } else {
        finish({ kind: 'failed', message: clean(stderr) || `maxdop exited with ${code}.` });
      }
    });

    // stdin is UTF-8 by contract: the editor hands over a decoded
    // buffer, so there are no original bytes to preserve on this path.
    child.stdin.on('error', () => {
      /* The process died before reading; `close` reports the real reason. */
    });
    child.stdin.end(request.text, 'utf8');
  });
}

/** Strips the CLI's `maxdop: ` prefixes, which the editor supplies for itself. */
function clean(stderr: string): string {
  return stderr
    .split('\n')
    .map((line) => line.replace(/^maxdop: /, '').trim())
    .filter((line) => line.length > 0)
    .join(' ');
}
