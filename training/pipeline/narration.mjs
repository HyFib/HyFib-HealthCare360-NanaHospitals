// The words of a video, from its storyboard alone: what is said over each step and card, and how the video ends.
// capture.mjs records them with the screens; retell.mjs puts them again over screens already captured, so a change of
// wording needs no new run on the training server. No storyboard computes its words while it runs, and none may: the
// words are the storyboard's fields, nothing else.

// Lists are read as "First, ... Second, ...": a bare "One." opening a sentence is easily misheard.
const ORDINALS = ['First', 'Second', 'Third', 'Fourth', 'Fifth', 'Sixth'];
const COUNTS = ['no', 'one', 'two', 'three', 'four', 'five', 'six'];
const lower = (t) => (/^[A-Z][a-z]/.test(t) ? t.charAt(0).toLowerCase() + t.slice(1) : t);
const list = (items) => items.map((t, i) => `${ORDINALS[i]}, ${lower(t)}`).join(' ');

/** A step's action, as capture.mjs runs it: the first of its targets, else a key press, else going to a page. */
export const actionOf = (step) =>
  ['click', 'nav', 'fill', 'select', 'check', 'show'].find((k) => step[k] !== undefined) ??
  (step.press ? 'press' : step.go ? 'go' : 'none');

/** Each step's id, as capture.mjs numbers them: s001, s002, ... through all the scenes. */
export const stepId = (n) => `s${String(n).padStart(3, '0')}`;

/**
 * How a video ends. A shared video names the next video on every learner's path (`next`). A role's video is the last
 * on that role's path (training/curriculum/README.md), so it ends with practice on the training server until the
 * learner can finish the department's day script without help: the check before the department goes live
 * (docs/deployment/MODULE-ROLLOUT-PLAN.md, section 6, step 7). A role video's `next` is not used. `outro` (the words)
 * and `endLines` (the end card) replace the ending: the patient portal's viewers are patients.
 */
export function ending(story) {
  const next = story.role === '*' ? (story.next ?? null) : null;
  const text =
    story.outro ??
    (next
      ? `Well done. Next, watch: ${next}. Then practise these steps on the training server, with your own account.`
      : 'Well done. Now practise these steps on the training server, with your own account. Before your department goes live, you will finish its day script without help.');
  const endLines = story.endLines ?? [
    ...(next ? [['Next video', next]] : []),
    ['Practise', story.practise ?? 'On the training server, with your own account. Your trainer has the day script.'],
  ];
  return { next, text, endLines };
}

/** Everything said in the video, as [{ id, text }]: each scene's opening and its steps, in order, then the cards. */
export function narration(story) {
  const segments = [];
  const say = (id, text) => {
    if (text) segments.push({ id, text });
  };
  let n = 0;
  story.scenes.forEach((scene, ci) => {
    // "Part two" opening a line is heard as other words in this voice (training/README.md); "Next" is not.
    const lead = ci === 0 ? 'First' : ci === story.scenes.length - 1 ? 'Finally' : 'Next';
    say(`c${ci + 1}`, scene.say ?? `${lead}: ${scene.title.charAt(0).toLowerCase()}${scene.title.slice(1)}.`);
    for (const step of scene.steps) {
      const id = stepId(++n);
      say(`${id}.say`, step.say);
      say(`${id}.after`, step.after);
    }
  });
  say('title', story.intro ?? `Welcome. This video is for ${story.audience}. ${story.title}.`);
  say(
    'objectives',
    `In this video, you will learn ${COUNTS[story.objectives.length]} ${story.objectives.length === 1 ? 'thing' : 'things'}. ${list(story.objectives)}`,
  );
  say('recap', `Let us recap. ${list(story.recap)}`);
  (story.check ?? []).forEach((c, i) => {
    say(`check${i + 1}.q`, `Check yourself. ${c.q} Pause the video and answer.`);
    say(`check${i + 1}.a`, `The answer: ${c.a}`);
  });
  say('end', ending(story).text);
  return segments;
}
