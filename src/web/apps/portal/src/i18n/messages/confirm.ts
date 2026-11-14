import { defineMessages } from '../define';

/**
 * "Confirm your details": records registered with the patient's mobile number that nobody has verified yet, shown once
 * the patient's date of birth or UHID is given (ASVS V6.6.1). The Hindi and Tamil were written by the development team
 * and are listed for a native speaker's review in docs/requirements (hindi-review.md, tamil-review.md: "Portal text for
 * confirming records on a number").
 */
export const confirmMessages = defineMessages({
  en: {
    'confirm.title': 'Confirm your details',
    'confirm.banner.title': 'Some records on this number are not confirmed yet',
    'confirm.banner.body': 'To see them here, enter the patient’s date of birth or UHID.',
    'confirm.banner.action': 'Confirm details',
    'confirm.lead.one':
      'One record registered with your mobile number is not confirmed yet. To keep patients’ records private, it is shown here only once you enter the patient’s date of birth, or the UHID printed on their hospital card.',
    'confirm.lead.many':
      '{count} records registered with your mobile number are not confirmed yet. To keep patients’ records private, each is shown here only once you enter that patient’s date of birth, or the UHID printed on their hospital card, one at a time.',
    'confirm.method': 'Confirm with',
    'confirm.method.dateOfBirth': 'Date of birth',
    'confirm.method.uhid': 'UHID',
    'confirm.dob.label': 'The patient’s date of birth',
    'confirm.dob.day': 'Day',
    'confirm.dob.month': 'Month',
    'confirm.dob.year': 'Year',
    'confirm.dob.hint': 'As given at the hospital, for example 23 06 1979.',
    'confirm.dob.invalid': 'Enter a real date: the day, the month and the year in four digits.',
    'confirm.dob.past': 'Enter a date of birth that has passed.',
    'confirm.uhid.label': 'The patient’s UHID',
    'confirm.uhid.hint': 'Printed on the hospital card, bills and receipts.',
    'confirm.uhid.invalid': 'Enter the UHID printed on the hospital card or a bill.',
    'confirm.submit': 'Confirm',
    'confirm.submitting': 'Checking…',
    'confirm.attemptsLeft': 'Tries left today: {count}.',
    'confirm.done.one': 'The record is confirmed. You can see it now.',
    'confirm.done.many': '{count} records are confirmed. You can see them now.',
    'confirm.error.noMatch':
      'These details do not match a record registered with this number. Check them and try again.',
    'confirm.error.throttled':
      'Too many wrong tries for this number. Please try again later, or ask at the hospital’s front desk to confirm your mobile number.',
    'confirm.error.nothing': 'There is nothing left to confirm on this number.',
    'confirm.wait.hours': 'You can try again in about {count} hours.',
    'confirm.wait.minutes': 'You can try again in about {count} minutes.',
    'confirm.wait.minute': 'You can try again in about a minute.',
    'confirm.frontDesk':
      'You can also ask at the hospital’s front desk to confirm your mobile number on the patient’s record, with a code sent to it.',
  },
  hi: {
    'confirm.title': 'अपनी जानकारी की पुष्टि करें',
    'confirm.banner.title': 'इस नंबर के कुछ रिकॉर्ड की अभी पुष्टि नहीं हुई है',
    'confirm.banner.body': 'उन्हें यहाँ देखने के लिए रोगी की जन्मतिथि या UHID दर्ज करें।',
    'confirm.banner.action': 'जानकारी की पुष्टि करें',
    'confirm.lead.one':
      'आपके मोबाइल नंबर से पंजीकृत एक रिकॉर्ड की अभी पुष्टि नहीं हुई है। रोगियों के रिकॉर्ड गोपनीय रखने के लिए, यह यहाँ तभी दिखेगा जब आप रोगी की जन्मतिथि, या उनके अस्पताल कार्ड पर छपा UHID दर्ज करेंगे।',
    'confirm.lead.many':
      'आपके मोबाइल नंबर से पंजीकृत {count} रिकॉर्ड की अभी पुष्टि नहीं हुई है। रोगियों के रिकॉर्ड गोपनीय रखने के लिए, हर रिकॉर्ड यहाँ तभी दिखेगा जब आप उस रोगी की जन्मतिथि, या उनके अस्पताल कार्ड पर छपा UHID दर्ज करेंगे, एक-एक करके।',
    'confirm.method': 'किससे पुष्टि करें',
    'confirm.method.dateOfBirth': 'जन्मतिथि',
    'confirm.method.uhid': 'UHID',
    'confirm.dob.label': 'रोगी की जन्मतिथि',
    'confirm.dob.day': 'दिन',
    'confirm.dob.month': 'महीना',
    'confirm.dob.year': 'वर्ष',
    'confirm.dob.hint': 'जैसी अस्पताल में बताई गई थी, उदाहरण के लिए 23 06 1979।',
    'confirm.dob.invalid': 'सही तारीख दर्ज करें: दिन, महीना और चार अंकों में वर्ष।',
    'confirm.dob.past': 'ऐसी जन्मतिथि दर्ज करें जो बीत चुकी हो।',
    'confirm.uhid.label': 'रोगी का UHID',
    'confirm.uhid.hint': 'अस्पताल कार्ड, बिल और रसीदों पर छपा होता है।',
    'confirm.uhid.invalid': 'अस्पताल कार्ड या बिल पर छपा UHID दर्ज करें।',
    'confirm.submit': 'पुष्टि करें',
    'confirm.submitting': 'जाँच हो रही है…',
    'confirm.attemptsLeft': 'आज बाकी प्रयास: {count}।',
    'confirm.done.one': 'रिकॉर्ड की पुष्टि हो गई। अब आप इसे देख सकते हैं।',
    'confirm.done.many': '{count} रिकॉर्ड की पुष्टि हो गई। अब आप इन्हें देख सकते हैं।',
    'confirm.error.noMatch':
      'ये विवरण इस नंबर से पंजीकृत किसी रिकॉर्ड से मेल नहीं खाते। इन्हें जाँचें और फिर कोशिश करें।',
    'confirm.error.throttled':
      'इस नंबर से कई बार गलत विवरण दिए गए हैं। कृपया बाद में फिर कोशिश करें, या अपने मोबाइल नंबर की पुष्टि के लिए अस्पताल के फ्रंट डेस्क पर पूछें।',
    'confirm.error.nothing': 'इस नंबर पर पुष्टि के लिए अब कुछ बाकी नहीं है।',
    'confirm.wait.hours': 'आप लगभग {count} घंटे बाद फिर कोशिश कर सकते हैं।',
    'confirm.wait.minutes': 'आप लगभग {count} मिनट बाद फिर कोशिश कर सकते हैं।',
    'confirm.wait.minute': 'आप लगभग एक मिनट बाद फिर कोशिश कर सकते हैं।',
    'confirm.frontDesk':
      'आप अस्पताल के फ्रंट डेस्क पर भी कह सकते हैं कि वे रोगी के रिकॉर्ड पर आपके मोबाइल नंबर की पुष्टि करें, उस पर भेजे गए कोड से।',
  },
  ta: {
    'confirm.title': 'உங்கள் விவரங்களை உறுதிசெய்யுங்கள்',
    'confirm.banner.title': 'இந்த எண்ணில் உள்ள சில பதிவுகள் இன்னும் உறுதிசெய்யப்படவில்லை',
    'confirm.banner.body': 'அவற்றை இங்கே பார்க்க, நோயாளியின் பிறந்த தேதி அல்லது UHID-ஐ உள்ளிடுங்கள்.',
    'confirm.banner.action': 'விவரங்களை உறுதிசெய்யுங்கள்',
    'confirm.lead.one':
      'உங்கள் மொபைல் எண்ணில் பதிவு செய்யப்பட்ட ஒரு பதிவு இன்னும் உறுதிசெய்யப்படவில்லை. நோயாளிகளின் பதிவுகளை ரகசியமாக வைக்க, நோயாளியின் பிறந்த தேதியை, அல்லது அவரது மருத்துவமனை அட்டையில் அச்சிடப்பட்டுள்ள UHID-ஐ நீங்கள் உள்ளிட்ட பிறகே அது இங்கே காட்டப்படும்.',
    'confirm.lead.many':
      'உங்கள் மொபைல் எண்ணில் பதிவு செய்யப்பட்ட {count} பதிவுகள் இன்னும் உறுதிசெய்யப்படவில்லை. நோயாளிகளின் பதிவுகளை ரகசியமாக வைக்க, ஒவ்வொரு நோயாளியின் பிறந்த தேதியை, அல்லது அவரது மருத்துவமனை அட்டையில் அச்சிடப்பட்டுள்ள UHID-ஐ ஒவ்வொன்றாக நீங்கள் உள்ளிட்ட பிறகே அந்தப் பதிவு இங்கே காட்டப்படும்.',
    'confirm.method': 'எதைக் கொண்டு உறுதிசெய்வது',
    'confirm.method.dateOfBirth': 'பிறந்த தேதி',
    'confirm.method.uhid': 'UHID',
    'confirm.dob.label': 'நோயாளியின் பிறந்த தேதி',
    'confirm.dob.day': 'நாள்',
    'confirm.dob.month': 'மாதம்',
    'confirm.dob.year': 'ஆண்டு',
    'confirm.dob.hint': 'மருத்துவமனையில் கொடுத்தது போலவே, எடுத்துக்காட்டாக 23 06 1979.',
    'confirm.dob.invalid': 'சரியான தேதியை உள்ளிடுங்கள்: நாள், மாதம், நான்கு இலக்க ஆண்டு.',
    'confirm.dob.past': 'கடந்துவிட்ட ஒரு பிறந்த தேதியை உள்ளிடுங்கள்.',
    'confirm.uhid.label': 'நோயாளியின் UHID',
    'confirm.uhid.hint': 'மருத்துவமனை அட்டை, பில்கள் மற்றும் ரசீதுகளில் அச்சிடப்பட்டிருக்கும்.',
    'confirm.uhid.invalid': 'மருத்துவமனை அட்டையில் அல்லது பில்லில் அச்சிடப்பட்டுள்ள UHID-ஐ உள்ளிடுங்கள்.',
    'confirm.submit': 'உறுதிசெய்யுங்கள்',
    'confirm.submitting': 'சரிபார்க்கிறது…',
    'confirm.attemptsLeft': 'இன்று மீதமுள்ள முயற்சிகள்: {count}.',
    'confirm.done.one': 'பதிவு உறுதிசெய்யப்பட்டது. இப்போது நீங்கள் அதைப் பார்க்கலாம்.',
    'confirm.done.many': '{count} பதிவுகள் உறுதிசெய்யப்பட்டன. இப்போது நீங்கள் அவற்றைப் பார்க்கலாம்.',
    'confirm.error.noMatch':
      'இந்த விவரங்கள் இந்த எண்ணில் பதிவு செய்யப்பட்ட எந்தப் பதிவுடனும் பொருந்தவில்லை. சரிபார்த்து மீண்டும் முயற்சி செய்யுங்கள்.',
    'confirm.error.throttled':
      'இந்த எண்ணிலிருந்து பலமுறை தவறான விவரங்கள் கொடுக்கப்பட்டுள்ளன. தயவுசெய்து பிறகு மீண்டும் முயற்சி செய்யுங்கள், அல்லது உங்கள் மொபைல் எண்ணை உறுதிசெய்ய மருத்துவமனையின் முன் மேசையில் கேளுங்கள்.',
    'confirm.error.nothing': 'இந்த எண்ணில் உறுதிசெய்ய இனி எதுவும் இல்லை.',
    'confirm.wait.hours': 'சுமார் {count} மணி நேரத்தில் மீண்டும் முயற்சி செய்யலாம்.',
    'confirm.wait.minutes': 'சுமார் {count} நிமிடங்களில் மீண்டும் முயற்சி செய்யலாம்.',
    'confirm.wait.minute': 'சுமார் ஒரு நிமிடத்தில் மீண்டும் முயற்சி செய்யலாம்.',
    'confirm.frontDesk':
      'நோயாளியின் பதிவில் உங்கள் மொபைல் எண்ணை, அதற்கு அனுப்பப்படும் குறியீட்டைக் கொண்டு உறுதிசெய்யுமாறு மருத்துவமனையின் முன் மேசையிலும் கேட்கலாம்.',
  },
});
