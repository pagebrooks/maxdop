**No formatting changes.** A file formatted by 0.1.3 comes out of 0.1.4 byte for byte the same.
This release moves the parser forward and cleans up the toolchain behind it.

## ScriptDom 180.140.0

maxdop parses with Microsoft's ScriptDom, and the pin moves from 180.102.0 to 180.140.0. The grammar
surface grew from 935 to 938 concrete node types, all three from one addition: the Fabric DW grammar's
scalar AI functions (`AI_SUMMARIZE`, `AI_CLASSIFY`, `AI_TRANSLATE` and the others) now take an
`ON ERROR` clause — `ERROR ON ERROR`, `NULL ON ERROR` or `DEFAULT <value> ON ERROR`.

Those functions exist only under the Fabric DW grammar; under every other grammar the same text is an
ordinary function call. maxdop has no layout rule for them and passes the whole expression through as
written, so the new clause needed no formatting work. What it got instead is a test: the clause
survives the round trip verbatim, inside a statement that still formats around it, and formatting the
result again is a fixed point.

The parser is the one thing in this release that can change what maxdop sees in a file, so it was
checked as such. The corpus sweep against 180.140.0 came back unchanged: nothing refused, nothing
crashed, nothing unstable.

## Toolchain

- **VS Code extension.** `ovsx`, the tool that publishes the extension to Open VSX, pulled in its own
  older copy of `@vscode/vsce` beneath the one the extension already builds with, and with it a
  dependency under a security advisory. An npm override now resolves `ovsx` to the top-level `vsce`,
  which removes the duplicate tree and the advisory with it. This is build tooling: the extension's
  own code is unchanged and it has no runtime npm dependencies.
- **Test toolchain.** Mutation testing runs on Stryker.NET 5.0, and the test projects move to
  Microsoft.NET.Test.Sdk 18.11, coverlet 10.1 and xunit.runner.visualstudio 4.0.1. Pinned, as before,
  so a mutation score or a coverage number stays comparable with last week's.
