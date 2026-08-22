import { chmod } from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { format } from './maxdop';

let output: vscode.OutputChannel;

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel('maxdop');
  context.subscriptions.push(output);

  void ensureExecutable(context);

  context.subscriptions.push(
    vscode.languages.registerDocumentFormattingEditProvider({ language: 'sql' }, {
      provideDocumentFormattingEdits: (document, _options, token) =>
        provideEdits(context, document, token),
    }),
  );

  // An escape hatch for the two-formatter case. Only one formatter can be the
  // default for a language, so a user who keeps the mssql extension's formatter
  // on `editor.formatOnSave` has no way to reach this one; the command gives them
  // one, and is bindable to a key. It is also what makes deferring to mssql a
  // real choice rather than a loss of functionality.
  context.subscriptions.push(
    vscode.commands.registerCommand('maxdop.formatDocument', () => formatActiveEditor(context)),
  );

  // No range-formatting provider on purpose. The CLI rejects `--range` rather
  // than ignoring it, because formatting the whole file when the
  // caller asked for a selection is how "Format Selection" destroys work outside
  // the selection. Registering a provider that quietly did the whole document
  // would reintroduce exactly that. Better that VS Code reports no range
  // formatter than that it silently reformats more than was asked.
}

async function provideEdits(
  context: vscode.ExtensionContext,
  document: vscode.TextDocument,
  token: vscode.CancellationToken,
): Promise<vscode.TextEdit[]> {
  const controller = new AbortController();
  token.onCancellationRequested(() => controller.abort());

  const text = document.getText();
  const outcome = await format({
    binary: binaryPath(context),
    text,
    // An untitled buffer has no path to discover a config from, so it formats
    // with defaults — correct, and the alternative (guessing a workspace folder)
    // would apply one project's settings to a scratch buffer.
    filePath: document.isUntitled ? undefined : document.fileName,
    signal: controller.signal,
  });

  switch (outcome.kind) {
    case 'formatted':
      // Editor settings deliberately do not feed in here: `.maxdop.json` is the
      // whole configuration surface, so a repo formats the same
      // way regardless of whose editor is open. tabSize/insertSpaces are ignored
      // for the same reason Prettier ignores them.
      //
      // An unchanged document returns no edits at all, which keeps VS Code from
      // marking a clean file dirty on format-on-save.
      return outcome.text === text ? [] : [vscode.TextEdit.replace(fullRange(document), outcome.text)];

    case 'declined':
      // The input's problem, and the file is untouched. A modal error on every
      // keystroke-triggered format-on-save would be intolerable, so this goes to
      // the output channel and the status bar's formatter notice.
      output.appendLine(`${document.fileName}: ${outcome.message}`);
      return [];

    case 'failed':
      output.appendLine(`${document.fileName}: ${outcome.message}`);
      void vscode.window.showErrorMessage(`maxdop: ${outcome.message}`, 'Show Output').then((choice) => {
        if (choice === 'Show Output') {
          output.show(true);
        }
      });
      return [];
  }
}

/** Formats the active editor with maxdop, whichever formatter is the default. */
async function formatActiveEditor(context: vscode.ExtensionContext): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return;
  }

  const versionAtRequest = editor.document.version;
  const source = new vscode.CancellationTokenSource();
  try {
    const edits = await provideEdits(context, editor.document, source.token);

    // The document may have been edited while the CLI ran. Applying a whole-file
    // replacement computed from stale text would discard those keystrokes, so the
    // edit is abandoned instead — the user can simply run it again.
    if (edits.length > 0 && editor.document.version === versionAtRequest) {
      await editor.edit((builder) => {
        for (const edit of edits) {
          builder.replace(edit.range, edit.newText);
        }
      }, { undoStopBefore: true, undoStopAfter: true });
    }
  } finally {
    source.dispose();
  }
}

function fullRange(document: vscode.TextDocument): vscode.Range {
  return new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length));
}

/** The configured binary, or the one bundled in this platform's VSIX. */
function binaryPath(context: vscode.ExtensionContext): string {
  const configured = vscode.workspace.getConfiguration('maxdop').get<string>('path')?.trim();
  if (configured) {
    return configured;
  }

  const name = process.platform === 'win32' ? 'maxdop.exe' : 'maxdop';
  return path.join(context.extensionPath, 'bin', name);
}

/**
 * Restores the executable bit on the bundled binary.
 *
 * VSIXs are zip files, and the zip format's permission bits do not survive every
 * packaging and install path — a binary that is `rw-` instead of `rwx` fails with
 * a bare EACCES the first time anyone on Linux or macOS formats anything. Cheap
 * to do on activation, and it costs nothing when the bit is already right.
 */
async function ensureExecutable(context: vscode.ExtensionContext): Promise<void> {
  if (process.platform === 'win32') {
    return;
  }

  try {
    await chmod(path.join(context.extensionPath, 'bin', 'maxdop'), 0o755);
  } catch {
    // Missing binary is the developer-checkout case (`npm run build:binary` puts
    // one there) and is reported properly by the ENOENT path on first format.
  }
}

export function deactivate(): void {
  /* The CLI is spawned per format; nothing outlives a call. */
}
