# Safety

maxdop makes two promises about your files, and then checks them.

## The guarantees

**1. It won't break your code.** Every format re-parses its own output and compares the token
stream, the AST and the comments against the input. On any mismatch you get your original text back,
untouched, and [exit code 2](cli.md#exit-codes).

**2. It never destroys a file.** Constructs the formatter doesn't model are emitted verbatim from the
token stream. A file that fails to parse passes through unchanged, with a notice on stderr.

## How that is verified

Both are claims, so they are checked against real T-SQL rather than trusted.

The corpus is 2,215 files:
[sql-server-samples](https://github.com/microsoft/sql-server-samples) (AdventureWorks and
WideWorldImporters), the [First Responder Kit](https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit),
[Ola Hallengren's Maintenance Solution](https://github.com/olahallengren/sql-server-maintenance-solution),
[sp_WhoIsActive](https://github.com/amachanic/sp_whoisactive), and ScriptDom's own parser test suite.
`tools/corpus/fetch.sh` is the definition of it; it is fetched, never vendored, so none of it is in
this repo.

Every file is formatted **once per configuration option** — ten variants, including leading commas,
tabs, lower-case keywords and a 60-column width.

| | |
| --- | --- |
| Refused, crashed, or non-idempotent | **0**, in every variant |
| Comments lost | **0** of 11,261 |
| Comments that changed which code they sit between | **15**, across 8 files |
| Token coverage, corpus-wide | **98.6%** under a formatter handler |
| Token coverage, First Responder Kit and Ola Hallengren | **100%** |
| Token coverage, AdventureWorks and WideWorldImporters | **99.6%** |
| Per-file median coverage | **100%** |

1,694 of the 2,215 files format. Of the rest, 90 are ScriptDom's deliberate syntax-error fixtures and
427 do not parse for reasons of their own — templated placeholders, sqlcmd directives, other
dialects. All of them pass through untouched, which is the second guarantee doing its job.

### Comment placement

A comment that *moves* still round-trips, so none of the safety gates above can see it. It gets its
own harness.

A comment is inserted at **every token boundary** of a second, hand-written corpus — 8,460 tokens
covering every construct maxdop models — in both own-line and end-of-line positions. The result must
sit between the same code it was written between. That is roughly 16,900 insertions, and none of them
can make the formatter refuse its own output.

### Grammar coverage

ScriptDom's AST is a closed set, so coverage is enumerated rather than sampled. A test asserts the
full list of 935 concrete node types against a committed baseline; a parser upgrade fails it with the
names of whatever appeared or vanished, which is the shortlist of new syntax worth a handler.

## What it will not do to your files

Below the formatter there is a byte-level layer with its own rules.

- **Encoding survives.** A UTF-16-LE-with-BOM file — SSMS's default — is written back as UTF-16 LE
  with its BOM. A trailing newline is preserved or absent exactly as it was, and CRLF stays CRLF.
- **If the bytes cannot round-trip, nothing is written.** A legacy code page such as a Windows-1252
  `é` would decode to a replacement character and corrupt the file on write, so maxdop declines the
  file and says so rather than "succeeding".
- **Literals are never touched.** `'it''s'` at any depth of escaping, `]]` inside a quoted
  identifier, and multi-line strings keep every byte. The continuation lines of a multi-line literal
  are never re-indented, because that would change the value.
