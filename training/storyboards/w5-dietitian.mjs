// Wave 5 (in-patient services): the dietitian's day. Writing a diet order for an admitted patient (Fatima Begum, bed
// GWF-01, pneumonia), reading the meal plan HC360 makes from it, and the kitchen's production list. Recorded as the
// dietitian at 11:10 am; no diet has been ordered yet at that time, so nothing is made in setup.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w5-dietitian',
  title: 'Dietitian: order a diet, read the meal plan, brief the kitchen',
  role: 'Dietitian',
  roleLabel: 'Dietitian',
  user: 'dietitian',
  wave: 5,
  audience: 'For the dietitians of Nana Hospitals.',
  intro:
    'The dietitian decides what each admitted patient eats. In HC360 you write a diet order, HC360 plans the meals for every round, and the kitchen cooks from that plan.',
  objectives: [
    'Write a diet order for an admitted patient.',
    'Read the meal plan for the ward.',
    'Check what the kitchen has to cook.',
  ],
  modules: ['dietary', 'inpatient'],
  covers: ['/dietary', '/dietary/meal-plans', '/dietary/kitchen'],
  scenes: [
    {
      title: 'Write a diet order',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Dietary' },
          say: 'Open Dietary from the menu.',
        },
        {
          nav: 'Diet orders',
          say: 'Open Diet orders.',
          after: 'This list shows the diets of admitted patients, ward by ward. No diet has been ordered yet this morning.',
          wait: { role: 'table', name: 'Diet orders' },
        },
        {
          click: { role: 'button', name: 'New diet order' },
          say: 'Fatima Begum is in bed GWF-01 with pneumonia. Click New diet order.',
          wait: { role: 'dialog', name: 'New diet order' },
        },
        {
          fill: [{ label: 'Admitted patient' }, 'Fatima'],
          say: 'Search for an admitted patient by name, UHID, mobile number or IP number.',
          wait: { role: 'option', name: /Fatima Begum/ },
        },
        {
          click: { role: 'option', name: /Fatima Begum/ },
          say: 'Choose Fatima Begum.',
        },
        {
          click: { role: 'button', name: 'Continue' },
          say: 'Click Continue.',
          wait: { role: 'button', name: 'Order diet' },
        },
        {
          show: { role: 'group', name: 'Allergies and alerts' },
          say: 'Check the allergies before you choose a food. Here, no allergies are recorded, so ask the patient or her family.',
          zoom: 1.5,
        },
        {
          select: [{ label: 'Diet' }, 'High-protein diet (HIGH-PROTEIN)'],
          native: true,
          say: 'Fatima needs to regain her strength. Choose the high-protein diet.',
          after: 'The meal rounds fill in with the usual rounds of this diet.',
          wait: { role: 'checkbox', name: /^Bedtime milk/ },
        },
        {
          click: { role: 'radio', name: 'Non-vegetarian' },
          say: 'Choose the food preference the patient tells you.',
        },
        {
          fill: [{ label: 'Instructions for the kitchen' }, 'Small portions, soft food, no spicy food.'],
          say: 'Write any instruction for the kitchen.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Order diet' },
          say: 'Click Order diet.',
          after: 'The order is saved. Fatima Begum is on the list with the high-protein diet.',
          wait: { role: 'row', name: /Fatima Begum/ },
        },
      ],
    },
    {
      title: 'The meal plan and the kitchen',
      steps: [
        {
          nav: 'Meal plans',
          say: 'Open Meal plans.',
          after:
            'HC360 has planned today’s remaining meals for Fatima Begum: lunch, the evening snack, dinner and bedtime milk. Your instruction is printed on each.',
          wait: { role: 'row', name: /Lunch.*Fatima Begum/ },
          result: { role: 'row', name: /Lunch.*Fatima Begum/ },
        },
        {
          show: { role: 'button', name: 'Update plan' },
          say: 'The plan also refreshes by itself every hour. Click Update plan after you change a diet, to see the change now.',
          zoom: 1.5,
        },
        {
          nav: 'Kitchen production',
          say: 'Open Kitchen production.',
          after: 'The kitchen sees four meals to cook today, all non-vegetarian, counted round by round.',
          wait: { role: 'heading', name: /^Lunch/ },
        },
        {
          show: { role: 'button', name: 'Print cooking list' },
          say: 'Print the cooking list for the kitchen from here.',
          zoom: 1.5,
        },
      ],
    },
  ],
  recap: [
    'Check the allergies, choose the diet and the food preference, and write the instruction for the kitchen.',
    'HC360 plans the remaining meals of the day from the diet order.',
    'Kitchen production shows the kitchen what to cook, round by round.',
  ],
  check: [
    {
      q: 'Where do you look for a patient’s food allergies before you order a diet?',
      a: 'At the allergies line at the top of the diet order. If none are recorded, ask the patient or the family.',
    },
    {
      q: 'You change a patient’s diet. How does the kitchen know?',
      a: 'The meal plan and the kitchen list follow the new order. Click Update plan to see the change at once.',
    },
  ],
};
