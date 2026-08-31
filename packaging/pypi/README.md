# maxdop

**Max Degree of Prettiness for your T-SQL.** A T-SQL formatter that runs in CI,
understands the whole language, and checks its own work.

```sh
maxdop query.sql       # formatted SQL to stdout
maxdop --write src/    # format every .sql under src/, in place
maxdop --check src/    # fail the build if anything would change
```

This package ships the native `maxdop` executable for your platform. It has no
Python dependencies and is not importable — installing it puts the `maxdop`
command on your PATH, nothing more. There is no interpreter between you and the
binary, so cold start stays around 3.5 ms.

## Why

Most SQL formatters tokenise and guess, and lose the shape of your code when it
matters. maxdop is built on [ScriptDom](https://github.com/microsoft/sqlscriptdom),
Microsoft's own T-SQL parser — twelve grammars, SQL Server 2000 to 2025 plus
Fabric DW. It reads stored procedures, `GO` batches and 2000-era syntax the way
the server does.

It also verifies itself. Every result is re-parsed and compared against your
input — token stream, tree and comments. If anything differs you get your
original file back, untouched, and a distinct exit code. Measured over 2,215
real-world files: zero refusals, zero comments lost.

## Use it with pre-commit

```yaml
repos:
  - repo: local
    hooks:
      - id: maxdop
        name: maxdop
        entry: maxdop --write
        language: python
        additional_dependencies: [maxdop==0.1.2]
        types: [sql]
```

pre-commit builds a virtualenv, pip resolves the wheel for that machine, and the
binary lands on PATH — nothing to install first.

## Configuration

One `.maxdop.json` at your repo root, committed next to the code it formats, so
a repo formats the same way whoever opens it.

```json
{
  "maxWidth": 100,
  "indentSize": 4,
  "keywordCase": "upper",
  "parserVersion": "2022"
}
```

Full documentation, other install methods, and the comparison against the other
ten T-SQL formatters: <https://github.com/pagebrooks/maxdop>

MIT licensed.
