import assert from 'node:assert/strict';
import * as vscode from 'vscode';

/**
 * Runs inside a real VS Code extension host, which is the only place the editor
 * glue actually executes. The unit tests cover the CLI seam; this covers the seam
 * that costs a document if it is wrong — whether the provider is registered for
 * `sql` at all, and whether what it returns is an edit VS Code will apply.
 */
export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder, 'the test workspace did not open');

  // Activation is asynchronous, and `executeFormatDocumentProvider` does not wait
  // for it: opening a .sql document starts `onLanguage:sql` but the command can
  // reach the formatter registry first and find it empty. Awaiting activation
  // here is the difference between testing the provider and testing a race.
  // Found by the suite reporting no edits while the extension was demonstrably
  // loaded and the document demonstrably `sql`.
  const extension = vscode.extensions.all.find((candidate) => candidate.packageJSON?.name === 'maxdop');
  assert.ok(extension, 'the maxdop extension was not loaded');
  await extension.activate();

  await formatsADocumentUsingTheRepoConfig(folder.uri);
  await leavesAnUnparseableDocumentAlone(folder.uri);
  await makesNoEditWhenNothingWouldChange(folder.uri);
  await theCommandFormatsWithoutBeingTheDefaultFormatter(folder.uri);
}

interface Formatting {
  /** The raw edits VS Code would apply. */
  readonly edits: vscode.TextEdit[];
  /** The document's text after applying them. */
  readonly text: string;
  /** Its text beforehand. */
  readonly before: string;
}

/**
 * Asks the editor for the same edits Format Document would apply, then applies
 * them and reverts.
 *
 * Assertions are written against the resulting text rather than the number of
 * edits: VS Code narrows a whole-document replacement to the range that actually
 * differs — one `TextEdit` from the provider came back as three — so an edit
 * count tests VS Code's diffing, not this extension.
 */
async function format(uri: vscode.Uri): Promise<Formatting> {
  const document = await vscode.workspace.openTextDocument(uri);
  await vscode.window.showTextDocument(document);

  // Asserted rather than assumed: the provider is registered for `sql`, so if
  // the document opens as anything else, every later assertion fails for a
  // reason that has nothing to do with the formatter.
  assert.equal(document.languageId, 'sql', `opened as "${document.languageId}", not sql`);

  const before = document.getText();
  const edits =
    (await vscode.commands.executeCommand<vscode.TextEdit[] | undefined>(
      'vscode.executeFormatDocumentProvider',
      uri,
      { tabSize: 4, insertSpaces: true },
    )) ?? [];

  if (edits.length === 0) {
    return { edits, text: before, before };
  }

  const workspaceEdit = new vscode.WorkspaceEdit();
  workspaceEdit.set(uri, edits);
  assert.ok(await vscode.workspace.applyEdit(workspaceEdit), 'the edits did not apply');

  const text = document.getText();
  await vscode.commands.executeCommand('workbench.action.files.revert');
  return { edits, text, before };
}

async function formatsADocumentUsingTheRepoConfig(folder: vscode.Uri): Promise<void> {
  const result = await format(vscode.Uri.joinPath(folder, 'messy.sql'));

  assert.notEqual(result.text, result.before, 'nothing was reformatted');

  // The workspace's .maxdop.json asks for lower-case keywords. If
  // --stdin-filepath were not passed, this would come back upper-case: exactly
  // the silent failure that flag exists to prevent, and one no unit test on this
  // side of the process boundary can see.
  assert.match(result.text, /select/, 'the repo config was not discovered');
  assert.doesNotMatch(result.text, /SELECT/, 'keywords came back upper-case');
  console.log('ok — formats a document using the repo config');
}

async function leavesAnUnparseableDocumentAlone(folder: vscode.Uri): Promise<void> {
  const result = await format(vscode.Uri.joinPath(folder, 'broken.sql'));

  // The file's problem, not maxdop's. No edit at all is the only safe answer:
  // an edit replacing the document with itself would still mark it dirty.
  assert.equal(result.edits.length, 0, 'an unparseable file produced an edit');
  console.log('ok — leaves an unparseable document alone');
}

async function makesNoEditWhenNothingWouldChange(folder: vscode.Uri): Promise<void> {
  const result = await format(vscode.Uri.joinPath(folder, 'already.sql'));

  assert.equal(result.edits.length, 0, 'a formatted file was edited anyway');
  console.log('ok — no edit when nothing would change');
}

/**
 * The escape hatch for a workspace that has another SQL formatter set as the
 * default — the mssql extension's, in practice. `editor.defaultFormatter` decides
 * who Format Document reaches, but the command must format regardless, so it is
 * exercised here through `executeCommand` rather than through the provider.
 */
async function theCommandFormatsWithoutBeingTheDefaultFormatter(folder: vscode.Uri): Promise<void> {
  const uri = vscode.Uri.joinPath(folder, 'messy.sql');
  const document = await vscode.workspace.openTextDocument(uri);
  const editor = await vscode.window.showTextDocument(document);
  const before = document.getText();

  await vscode.commands.executeCommand('maxdop.formatDocument');

  // The command edits the document itself rather than returning edits, so unlike
  // `format` above there is nothing to apply — only to check and undo.
  assert.notEqual(editor.document.getText(), before, 'the command made no edit');
  assert.match(editor.document.getText(), /select/, 'the repo config was not discovered');
  await vscode.commands.executeCommand('workbench.action.files.revert');
  console.log('ok — the command formats without being the default formatter');
}
