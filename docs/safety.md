# Safety

maxdop makes two promises about your files, and then checks them.

## The guarantees

**1. It won't break your code.** Every format re-parses its own output and compares its **significant
token stream** against the input's — type, text and order, token by token. Comments are trivia and
invisible to that comparison, so they get a second check of their own. On any mismatch you get your
original text back, untouched, and [exit code 2](cli.md#exit-codes).

Tokens rather than trees, deliberately. A parser is a deterministic function of its token stream, so
two inputs with identical significant tokens necessarily produce identical ASTs — comparing tokens
*delivers* tree equivalence and is strictly stronger, because different tokens can still yield the
same tree. It is also the only version that is implementable: ScriptDom exposes no structural
comparer, and a node-type fingerprint would miss the failures that matter most, since two
`BooleanComparisonExpression`s have the same shape whether the operator is `=` or `>`.

One exception, and it is the reason keyword casing works at all. T-SQL's non-reserved words —
`NVARCHAR`, `NOCOUNT`, `CAST` — lex as identifiers, so comparing every identifier case-sensitively
would mean keyword casing could never reach them. The printer may therefore *name specific token
positions* as keyword positions, and only those are compared case-insensitively. It is a per-token
permission, not a relaxed rule: the printer grants it only from regions whose grammar admits no object
name, every identifier it does not claim is still compared exactly, and a claim can relax case and
nothing else — the token must still exist, in the same place, with the same type.

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
| Comments lost | **0** of 11,385 |
| Comments that changed which code they sit between | **15**, across 8 files, in every variant |
| Token coverage, corpus-wide | **98.6%** under a formatter handler |
| Token coverage, First Responder Kit and Ola Hallengren | **100%** |
| Token coverage, AdventureWorks and WideWorldImporters | **99.6%** |
| Per-file median coverage | **100%** |

1,694 of the 2,215 files format. Of the remaining 521: 90 are ScriptDom's deliberate syntax-error
fixtures, 427 do not parse for reasons of their own — templated placeholders, sqlcmd directives, other
dialects — and 4 are empty. All of them pass through untouched, which is the second guarantee doing
its job.

Both comment numbers are printed by `maxdop-corpus` on every run rather than being measured once and
quoted afterwards — the denominator is comments in files that actually formatted, since a comment in
a file that never parsed was never at risk. The 11,385 moves with the corpus, which is fetched rather
than vendored.

### Comment placement

A comment that *moves* still round-trips, so none of the safety gates above can see it. Nothing is
lost when one does — it lands on the wrong side of a construct — but "the gates are clean" is not
evidence either way, so it is counted rather than assumed. The 15 are almost all commented-out
Extended Events DDL (`--ADD TARGET package0.ring_buffer(…)` inside a statement whose live form has no
handler yet), and the count is identical under all ten configuration variants.

It also gets its own harness.

A comment is inserted at **every token boundary** of a second, hand-written corpus — 8,460 tokens
covering every construct maxdop models — in both own-line and end-of-line positions. The result must
sit between the same code it was written between. That is 16,920 insertions; none of them can make the
formatter refuse its own output, and the number that land between different code than they were
written between is pinned by a ratchet, so it can fall but never rise unnoticed.

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
