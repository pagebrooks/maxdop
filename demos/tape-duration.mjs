// Guards the recordings against failing without failing.
//
// A GIF that stops a third of the way through, and one that captured a Lua stack
// trace instead of an editor, are both still valid GIFs. The `--check` halves
// compare the formatter's *text* output, which is byte-identical in both cases,
// so they are structurally incapable of noticing; the only thing that has ever
// caught either is somebody watching the frames go by.
//
// What is cheap to assert is that a recording lasts as long as its tape says it
// should. A truncated one is short by a wide margin — the recording that
// prompted this ran 9.1s against a 21.6s tape — so a generous tolerance still
// catches it with room to spare for VHS's own startup and encoding jitter.
//
// Both halves are deliberately dependency-free. Frame delays are read out of the
// GIF's own bytes rather than shelled out to ffprobe, so `--check` keeps its
// promise of needing none of the rendering tools installed, and can therefore run
// this in CI against the committed artifact.

/** VHS's own default, for a tape that never sets one. */
const DEFAULT_TYPING_SPEED_MS = 50;

/**
 * How far a recording may drift from its tape before this complains.
 *
 * The three tapes currently land within 2.5% of their recordings, so this is
 * roughly six times the drift ever observed. It is deliberately not tighter: the
 * estimate models the tape's own timings but not VHS's startup, the shell's
 * first prompt, or the encoder's rounding, and a guard that cries wolf is a
 * guard everyone learns to re-run until it passes. The failures it exists to
 * catch are not marginal — the one that prompted it was 58% short.
 */
const TOLERANCE = 0.15;

/** Milliseconds for a `Sleep`/`Set TypingSpeed` argument, whose unit is optional. */
function millis(value, unit) {
  const n = Number(value);
  return unit === 's' || unit === undefined ? n * 1000 : n;
}

/**
 * Roughly how many seconds of *recorded* screen time a tape describes.
 *
 * Commands between `Hide` and `Show` are excluded rather than counted: they run,
 * but VHS captures no frames for them, which is the whole point of staging a
 * fixture that way.
 */
export function tapeSeconds(tape) {
  // Only whole-line comments are stripped. A `#` mid-line is far more likely to
  // be a colour — `Set MarginFill "#1e1e2e"` — than a trailing remark.
  const source = tape
    .split('\n')
    .filter((line) => !/^\s*#/.test(line))
    .join('\n');

  // Alternation order matters: `Type "..."` claims its own string first, so the
  // quoted arguments of `Set Theme` and friends match nothing and are ignored.
  const token =
    /Type(?:@[\d.]+m?s)?\s+"([^"]*)"|Set\s+TypingSpeed\s+([\d.]+)(ms|s)?|Sleep\s+([\d.]+)(ms|s)?|\b(Hide|Show)\b|\b(?:Enter|Escape|Tab|Backspace|Space|Up|Down|Left|Right)\b(?:\s+(\d+))?/g;

  let ms = 0;
  let typing = DEFAULT_TYPING_SPEED_MS;
  let recording = true;

  for (const [, typed, speedValue, speedUnit, sleepValue, sleepUnit, visibility, keyCount] of source.matchAll(token)) {
    if (visibility) {
      recording = visibility === 'Show';
      continue;
    }
    if (speedValue !== undefined) {
      typing = millis(speedValue, speedUnit);
      continue;
    }

    // Everything below costs time; only what VHS is filming counts toward it.
    const cost =
      sleepValue !== undefined
        ? millis(sleepValue, sleepUnit)
        : typed !== undefined
          ? typed.length * typing
          : Number(keyCount ?? 1) * typing; // a bare key press, e.g. `Enter` or `Escape`

    if (recording) {
      ms += cost;
    }
  }

  return ms / 1000;
}

/**
 * The playing time of a GIF, summed from the per-frame delays in its Graphic
 * Control Extensions.
 *
 * The block structure is walked properly rather than scanned for the extension's
 * signature, because LZW-compressed image data is entropic enough to contain
 * that byte sequence by chance, and a guard that silently over-counts frames is
 * worse than no guard.
 */
export function gifSeconds(bytes) {
  const colourTableBytes = (packed) => (packed & 0x80 ? 3 * 2 ** ((packed & 0x07) + 1) : 0);

  let at = 6; // past "GIF89a"
  at += 7 + colourTableBytes(bytes[at + 4]); // logical screen descriptor, then any global table

  /** Advances past a chain of length-prefixed sub-blocks, which a zero length ends. */
  const skipSubBlocks = () => {
    while (at < bytes.length && bytes[at] !== 0x00) {
      at += bytes[at] + 1;
    }
    at += 1;
  };

  let centiseconds = 0;

  while (at < bytes.length && bytes[at] !== 0x3b) {
    const block = bytes[at++];

    if (block === 0x21) {
      const label = bytes[at++];
      if (label === 0xf9) {
        // size(1) packed(1) delay(2, little-endian) transparent(1), then terminator
        centiseconds += bytes[at + 2] | (bytes[at + 3] << 8);
      }
      skipSubBlocks();
    } else if (block === 0x2c) {
      at += 9; // image descriptor
      at += colourTableBytes(bytes[at - 1]); // any local colour table
      at += 1; // LZW minimum code size
      skipSubBlocks();
    } else {
      break; // not a structure this understands; better to say nothing than to guess
    }
  }

  return centiseconds / 100;
}

/**
 * Fails when a recording is not as long as its tape describes.
 *
 * Reports both numbers, because the useful question on a failure is always which
 * way it drifted: short means the capture stopped early, long means the tape and
 * the artifact are no longer the same demo.
 */
export function checkRecordingLength({ tape, gif, artifact, fail }) {
  const expected = tapeSeconds(tape);
  const actual = gifSeconds(gif);

  if (expected === 0) {
    fail('the tape describes no recorded time — is it still a VHS tape?');
  }

  const drift = Math.abs(actual - expected) / expected;
  if (drift > TOLERANCE) {
    fail(
      `${artifact} runs ${actual.toFixed(1)}s, but its tape describes ${expected.toFixed(1)}s.\n` +
        (actual < expected
          ? 'The capture stopped early, so the GIF loops back part-way through the demo.\n' +
            'Re-record it; this has happened when two recordings ran back to back.'
          : 'The recording is longer than the tape, so the two have gone out of step.'),
    );
  }

  return { expected, actual };
}
