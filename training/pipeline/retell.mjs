// Puts a storyboard's words again over screens already captured: the narration (segments.json) and the cards
// (capture.json) come from the storyboard as it is now (narration.mjs), the screens stay those of the capture. For a
// change of wording, a check question or the ending: a change to what a step does needs a new capture (make.sh), and
// this refuses when the steps no longer match the screens.
//
//   node training/pipeline/retell.mjs <storyboard.mjs> <out-dir>      (retell.sh runs it, then narrate, compose, qa)

import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { actionOf, ending, narration } from './narration.mjs';

const [storyPath, out] = process.argv.slice(2);
if (!storyPath || !out) {
  console.error('usage: node retell.mjs <storyboard.mjs> <out-dir>');
  process.exit(2);
}
const story = (await import(pathToFileURL(resolve(storyPath)).href)).default;
const file = join(out, 'capture.json');
const capture = JSON.parse(readFileSync(file, 'utf8'));

// The screens must be those of the same steps: the same scenes, each with the same actions in the same order.
const problems = [];
if (capture.id !== story.id) problems.push(`the capture is of ${capture.id}, not ${story.id}`);
const captured = [];
for (const beat of capture.beats) {
  if (beat.type === 'chapter') captured.push({ chapter: beat, steps: [] });
  else captured.at(-1)?.steps.push(beat);
}
if (captured.length !== story.scenes.length) {
  problems.push(`${captured.length} scenes were captured; the storyboard has ${story.scenes.length}`);
}
story.scenes.forEach((scene, i) => {
  const beats = captured[i]?.steps ?? [];
  if (beats.length !== scene.steps.length) {
    problems.push(`scene ${i + 1}: ${beats.length} steps were captured; the storyboard has ${scene.steps.length}`);
    return;
  }
  scene.steps.forEach((step, j) => {
    if (beats[j].action !== actionOf(step)) problems.push(`${beats[j].id} was captured as ${beats[j].action}; it is now ${actionOf(step)}`);
  });
});
if (problems.length) {
  console.error(`✗ ${story.id}: the steps changed since the capture, so make it again (make.sh):`);
  for (const p of problems) console.error(`  ${p}`);
  process.exit(1);
}

// The words, from the storyboard.
story.scenes.forEach((scene, i) => {
  captured[i].chapter.title = scene.title;
  scene.steps.forEach((step, j) => {
    const beat = captured[i].steps[j];
    for (const key of ['zoom', 'say', 'after']) {
      if (step[key] === undefined) delete beat[key];
      else beat[key] = step[key];
    }
  });
});
const { next, endLines } = ending(story);
Object.assign(capture, {
  title: story.title,
  roleLabel: story.roleLabel,
  audience: story.audience,
  objectives: story.objectives,
  recap: story.recap,
  check: story.check ?? [],
  next,
  endLines,
  modules: story.modules ?? [],
  covers: story.covers ?? [],
});
const segments = narration(story);
const before = JSON.parse(readFileSync(join(out, 'segments.json'), 'utf8'));
const was = Object.fromEntries(before.map((s) => [s.id, s.text]));
const changed = segments.filter((s) => was[s.id] !== s.text).map((s) => s.id);
writeFileSync(file, JSON.stringify(capture, null, 1));
writeFileSync(join(out, 'segments.json'), JSON.stringify(segments, null, 1));
console.log(`✓ ${story.id}: ${changed.length ? `new words for ${changed.join(', ')}` : 'the words are unchanged'}`);
