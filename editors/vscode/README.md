# maxdop for VS Code

**Max Degree of Prettiness for your T-SQL.**

Formats T-SQL with [maxdop](https://github.com/pagebrooks/maxdop) — an opinionated formatter
built on Microsoft's own parser (`Microsoft.SqlServer.TransactSql.ScriptDom`), so it handles the
things other SQL formatters choke on: stored procedures, multi-batch scripts, `GO`, non-`;`
delimiters, and 2000-era legacy syntax.

<!--
  Absolute URL, and pinned to a tag rather than a branch: the Marketplace renders this README
  outside the repo, so relative paths break, and a branch URL would let a published version's
  screenshot change under it. `<picture>` and prefers-color-scheme are not supported there either,
  hence one dark image rather than a pair. The tag is stamped from the release tag at
  packaging time, alongside the version in package.json — don't hand-edit it.
-->
![A stored procedure before and after maxdop](https://raw.githubusercontent.com/pagebrooks/maxdop/v0.1.0/docs/images/before-after-dark.png)

## Safety Measures

1. **It cannot break your code.** Every format re-parses its own output and compares its significant
   token stream against the input's, with a second check for the comments. Identical tokens mean an
   identical parse tree. On any mismatch you get your original text back, untouched.
2. **It never destroys a file.** Constructs the formatter doesn't handle are emitted verbatim. A
   file that fails to parse is left exactly as it was, with the reason in the **maxdop** output
   channel.

## Use

Format a `.sql` file with **Format Document**, or turn on `editor.formatOnSave`. To make maxdop the
default for SQL when other formatters are installed:

```jsonc
"[sql]": {
  "editor.defaultFormatter": "pbrooks.maxdop",
  "editor.formatOnSave": true
}
```

The CLI is bundled — there is nothing to install separately, and nothing is downloaded on first
activation.

maxdop formats whole files. It registers no range formatter on purpose: asking a formatter for a
range and getting the whole document back is how **Format Selection** quietly reformats work outside
the selection.

## Configuration lives in your repo, not your editor

There is exactly one extension setting, `maxdop.path`, for pointing at a different binary.

Everything about *how* code is formatted goes in a `.maxdop.json` file at your repo root, so the
whole team formats identically whether they use VS Code, the CLI, or CI:

```jsonc
{
  "maxWidth": 100,
  "indentSize": 4,
  "keywordCase": "upper",
  "leadingCommas": false,
  "recaseBuiltInFunctions": true,
  "alwaysBreakWhere": false,
  "parserVersion": "2019",
  "exclude": ["db/generated/**", "*.gen.sql"]
}
```

The nearest `.maxdop.json` at or above the file being formatted wins. `editor.tabSize` and friends
are deliberately ignored — a file should not format differently because of who opened it.

`parserVersion` pins the grammar, so a 2019-target codebase is not silently reformatted under 2025
rules. The [full option list](https://github.com/pagebrooks/maxdop#configuration) is in the project
README.

## The same formatter runs in your pipeline

The binary inside this extension is the one you can put in CI. `maxdop --check src/` exits nonzero
if anything would change, which is what turns a shared style into an enforced one — an editor
formatter alone can only ask nicely.

```sh
maxdop --check src/                                   # fail the build if anything would change
git diff --name-only -z | maxdop --check --files-from -   # or only what changed
```

Download it from [releases](https://github.com/pagebrooks/maxdop/releases). It is one static
executable with no .NET runtime, for Windows, macOS, Linux and Alpine, x64 and arm64.

## Using this alongside the mssql extension

They are built to sit side by side. This extension contributes a document formatter and nothing
else — no language server, no connection handling, no IntelliSense — so
[mssql](https://marketplace.visualstudio.com/items?itemName=ms-mssql.mssql) keeps everything it does
today and only formatting changes hands.

Because both register a formatter for SQL, VS Code needs to know which one **Format Document** should
reach. Installing this extension makes it the default for `[sql]`, and an
`editor.defaultFormatter` you set yourself always wins over that — so to hand formatting back to
mssql, set it explicitly:

```jsonc
"[sql]": { "editor.defaultFormatter": "ms-mssql.mssql" }
```

Either way, **maxdop: Format Document** in the Command Palette always formats with maxdop, so you can
keep mssql on format-on-save and still reach for this one deliberately (it takes a keybinding).

Selections are the one thing to know about: because maxdop ships no range formatter, **Format
Selection** falls through to mssql's, whichever default you pick.
