// Renders the before/after image used in the READMEs.
//
// The image is a build artifact, not an asset someone remembered to retake: it
// is produced by running the real CLI over demos/sample.sql, so it cannot show
// layout the formatter no longer produces. `--check` asserts that in CI without
// rendering anything.
//
//   node generate.mjs           regenerate the snapshots, SVGs and PNGs
//   node generate.mjs --check   fail if a committed snapshot is stale
//
// Two variants are rendered, because one image cannot serve both readers. The
// width of either is set by its longest line, and GitHub scales a README image
// down to fit its content column — ~890px on a desktop, but only ~350px on a
// phone. The wide image's 95-column `after` panel therefore arrives on a phone
// at roughly 0.42×, putting 14px code in front of the reader at under 6px. The
// narrow variant exists to be picked up by a `max-width` source in the README's
// <picture>: same formatter, shorter sample, so it survives the scale-down.
//
// Colours come from shiki using VS Code's own Dark+/Light+ themes and TextMate
// SQL grammar, so the image matches what the editor shows rather than
// approximating it.

import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Resvg } from '@resvg/resvg-js';
import { codeToTokens } from 'shiki';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..');
const check = process.argv.includes('--check');

/** Layout, in pixels. Column positions are pinned per token (see toSvg). */
const FONT = 'DejaVu Sans Mono, Menlo, Consolas, monospace';
const FONT_SIZE = 14;
const ADVANCE = FONT_SIZE * 0.602; // DejaVu Sans Mono's advance width, in em.
const LINE_HEIGHT = 21;
const PADDING = 18;
const HEADER = 34;
const GAP = 20;
const RADIUS = 10;

const THEMES = {
  dark: { theme: 'dark-plus', chrome: '#252526', label: '#9d9d9d', border: '#333333' },
  light: { theme: 'light-plus', chrome: '#f3f3f3', label: '#616161', border: '#e0e0e0' },
};

/**
 * The two renderings, widest first. `suffix` lands in the file name; the README
 * picks the narrow one with a `max-width` media query.
 */
const VARIANTS = [
  { sample: 'sample.sql', snapshot: 'expected.sql', suffix: '' },
  { sample: 'sample-narrow.sql', snapshot: 'expected-narrow.sql', suffix: '-narrow', maxColumns: 50 },
];

/** The widest line in some text, which is what sets a panel's width. */
function columnsOf(text) {
  return Math.max(...text.replace(/\n$/, '').split('\n').map((line) => line.length));
}

/**
 * A phone gives the README about 350 CSS px. At 50 columns the image is ~433px
 * wide, so 14px code still reaches the reader above 11px — monospace stops being
 * readable around 10px. Nothing enforces that on the sample itself, so a later
 * edit lengthening one line would quietly undo the whole variant.
 *
 * Asserted before anything is written, not during rendering: the snapshot and
 * the four images are written in a loop, so failing partway would leave an
 * updated expected-narrow.sql beside a stale PNG — and `--check` compares only
 * the snapshot, so it would then pass while the image it guards was wrong.
 */
function assertBudget(variant, columns) {
  if (variant.maxColumns !== undefined && columns > variant.maxColumns) {
    fail(
      `${variant.sample} renders ${columns} columns, over the ${variant.maxColumns}-column budget.\n` +
        'That image is the one phones get; past this width the text arrives too small to read.\n' +
        'Shorten the longest line in the sample, or in what maxdop makes of it.',
    );
  }
}

const binary =
  process.env.MAXDOP_BINARY ??
  join(repo, 'editors', 'vscode', 'bin', process.platform === 'win32' ? 'maxdop.exe' : 'maxdop');

const renders = VARIANTS.map((variant) => {
  const samplePath = join(here, variant.sample);
  const before = readFileSync(samplePath, 'utf8');
  const after = format(samplePath);
  return {
    ...variant,
    snapshotPath: join(here, variant.snapshot),
    before,
    after,
    // Both panels are drawn to one width, so the wider of the two is the image's.
    columns: Math.max(columnsOf(before), columnsOf(after)),
  };
});

if (check) {
  for (const render of renders) {
    let snapshot;
    try {
      snapshot = readFileSync(render.snapshotPath, 'utf8');
    } catch {
      fail(`no snapshot at ${render.snapshotPath}. Run "npm run generate".`);
    }

    if (snapshot !== render.after) {
      fail(
        `the committed demo output for ${render.sample} no longer matches what maxdop produces.\n` +
          'The README image is therefore showing layout the formatter does not.\n' +
          'Run "npm run generate" in demos/ and commit the result.',
      );
    }
  }

  console.log(`demo: ${renders.length} snapshots match current formatter output`);
  process.exit(0);
}

const images = join(repo, 'docs', 'images');
mkdirSync(images, { recursive: true });

for (const render of renders) {
  assertBudget(render, render.columns);
}

for (const render of renders) {
  writeFileSync(render.snapshotPath, render.after);

  for (const [name, theme] of Object.entries(THEMES)) {
    const svg = await toSvg(render.before, render.after, theme);
    writeFileSync(join(images, `before-after-${name}${render.suffix}.svg`), svg);

    const png = new Resvg(svg, {
      // 2× so the image stays crisp on a HiDPI screen after the README scales it
      // down. GitHub serves it at half size; the extra pixels are what stop the
      // text going soft.
      fitTo: { mode: 'zoom', value: 2 },
      font: { loadSystemFonts: true, defaultFontFamily: 'DejaVu Sans Mono' },
    })
      .render()
      .asPng();

    writeFileSync(join(images, `before-after-${name}${render.suffix}.png`), png);
    console.log(
      `docs/images/before-after-${name}${render.suffix}.png  ${render.columns} cols  ${(png.length / 1024).toFixed(0)} KB`,
    );
  }
}

/** Formats a file with the real CLI, failing loudly rather than rendering something stale. */
function format(path) {
  const result = spawnSync(binary, [path], { encoding: 'utf8' });

  if (result.error?.code === 'ENOENT') {
    fail(`no maxdop binary at ${binary}.\nBuild one with "npm run build:binary" in editors/vscode, or set MAXDOP_BINARY.`);
  }
  if (result.status !== 0) {
    fail(`maxdop exited ${result.status}: ${result.stderr?.trim()}`);
  }

  return result.stdout;
}

/** One panel of highlighted code, plus the width it needs. */
async function panel(code, label, themeName) {
  const { tokens, fg, bg } = await codeToTokens(code.replace(/\n$/, ''), { lang: 'sql', theme: themeName });
  const columns = Math.max(...tokens.map((line) => line.reduce((n, t) => n + t.content.length, 0)));
  return { tokens, fg, bg, label, columns, lines: tokens.length };
}

async function toSvg(beforeCode, afterCode, theme) {
  const panels = [
    await panel(beforeCode, 'before', theme.theme),
    await panel(afterCode, 'after · maxdop', theme.theme),
  ];

  // Stacked, not side by side. Side by side the image is twice as wide as one
  // panel, and GitHub scales a README image down to its ~890px content column —
  // so 14px code was arriving at the reader around 7px. Stacking makes the image
  // one panel wide, which roughly doubles the rendered text for the same source.
  //
  // Both panels still get the same width so the pair reads as a comparison rather
  // than as two unrelated screenshots; heights follow each panel's own content,
  // since stacking gives no reason to pad the shorter one.
  const columns = Math.max(...panels.map((p) => p.columns));
  const panelWidth = Math.ceil(columns * ADVANCE) + PADDING * 2;
  const heights = panels.map((p) => HEADER + p.lines * LINE_HEIGHT + PADDING);
  const width = panelWidth;
  const height = heights.reduce((total, h) => total + h, 0) + GAP;

  const parts = [
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">`,
    `<rect width="${width}" height="${height}" fill="none"/>`,
  ];

  panels.forEach((p, index) => {
    const y0 = index === 0 ? 0 : heights[0] + GAP;
    const panelHeight = heights[index];
    parts.push(
      `<g transform="translate(0 ${y0})">`,
      `<rect width="${panelWidth}" height="${panelHeight}" rx="${RADIUS}" fill="${p.bg}" stroke="${theme.border}"/>`,
      // Header strip: a rect with the same radius, its bottom half covered by a
      // square one, so only the top corners are rounded.
      `<path d="M0 ${RADIUS} a ${RADIUS} ${RADIUS} 0 0 1 ${RADIUS} ${-RADIUS} h ${panelWidth - RADIUS * 2} a ${RADIUS} ${RADIUS} 0 0 1 ${RADIUS} ${RADIUS} v ${HEADER - RADIUS} h ${-panelWidth} Z" fill="${theme.chrome}"/>`,
      `<line x1="0" y1="${HEADER}" x2="${panelWidth}" y2="${HEADER}" stroke="${theme.border}"/>`,
      `<text x="${PADDING}" y="${HEADER - 12}" font-family="${FONT}" font-size="12" fill="${theme.label}">${escape(p.label)}</text>`,
    );

    p.tokens.forEach((line, row) => {
      const y = HEADER + PADDING + row * LINE_HEIGHT;
      let column = 0;
      const spans = [];

      for (const token of line) {
        if (token.content.trim().length > 0) {
          // Every token is positioned at its own absolute column rather than
          // flowing after the previous one. If the rendering machine lacks the
          // exact font, only glyph shapes change — columns still line up, which
          // is the whole point of a monospace screenshot.
          spans.push(
            `<tspan x="${(PADDING + column * ADVANCE).toFixed(2)}" fill="${token.color ?? p.fg}">${escape(token.content)}</tspan>`,
          );
        }
        column += token.content.length;
      }

      if (spans.length > 0) {
        parts.push(
          `<text y="${y}" font-family="${FONT}" font-size="${FONT_SIZE}" xml:space="preserve">${spans.join('')}</text>`,
        );
      }
    });

    parts.push('</g>');
  });

  parts.push('</svg>');
  return parts.join('\n');
}

function escape(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function fail(message) {
  console.error(`demo: ${message}`);
  process.exit(1);
}
