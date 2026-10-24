import { defineMessages } from '../define';

/** Family members on other mobile numbers: adding one with a code, removing one, and who else sees your records. */
export const familyMessages = defineMessages({
  en: {
    'family.title': 'Family on other numbers',
    'family.subtitle': 'Family members with their own mobile number, and who else sees your records.',
    'family.profileLink.hint': 'Add a family member who uses another mobile number, or stop sharing your records.',

    'family.linked.title': 'Family members you see',
    'family.linked.empty':
      'Everyone registered with your mobile number is already in your portal. To see the records of a family member who has their own number, add them.',
    'family.linked.relation': 'Your {relation}',
    'family.linked.since': 'Added {date}',
    'family.remove': 'Remove',
    'family.remove.label': 'Remove {name}',
    'family.remove.title': 'Remove {name}?',
    'family.remove.body':
      'You will no longer see {name}’s records here. To see them again, you need a new code from them, or ask at the front desk.',
    'family.removed': '{name} was removed from your family.',

    'family.add': 'Add a family member',
    'family.add.lead':
      'For a family member registered at the hospital with their own mobile number. We send a code to their number, and they agree by giving you the code.',
    'family.add.uhid': 'Their UHID',
    'family.add.uhid.hint': 'Printed on their hospital card and on their bills.',
    'family.add.uhid.invalid': 'Enter the UHID printed on their hospital card or a bill.',
    'family.add.phone': 'Their mobile number',
    'family.add.phone.hint': 'The number they gave the hospital.',
    'family.add.phone.invalid': 'Enter their 10-digit mobile number.',
    'family.add.relation': 'They are your',
    'family.add.relation.choose': 'Choose',
    'family.add.relation.required': 'Choose how they are related to you.',
    'family.add.send': 'Send the code',

    'family.code.title': 'Enter their code',
    'family.code.lead':
      'If the UHID and the mobile number ending {ending} belong to the same patient, we have sent that number a code by SMS. Ask them for it. It works for {minutes} minutes.',
    'family.code.label': 'Code',
    'family.code.verify': 'Add to my family',
    'family.code.again': 'Ask for a new code',
    'family.added': '{name} is now in your family.',

    'family.shared.title': 'Who else sees your records',
    'family.shared.empty': 'Only people who sign in with your mobile number see your records here.',
    'family.shared.sees': 'Sees the records of {name} (their {relation})',
    'family.shared.since': 'Since {date}',
    'family.stop': 'Stop sharing',
    'family.stop.label': 'Stop sharing with {phone}',
    'family.stop.title': 'Stop sharing with {phone}?',
    'family.stop.body':
      '{phone} will no longer see {name}’s records. To share them again, they need a new code from you, or you can ask at the front desk.',
    'family.stopped': '{phone} no longer sees {name}’s records.',

    'family.relation.Parent': 'parent',
    'family.relation.Child': 'son or daughter',
    'family.relation.Spouse': 'husband or wife',
    'family.relation.Sibling': 'brother or sister',
    'family.relation.Grandparent': 'grandparent',
    'family.relation.Grandchild': 'grandchild',
    'family.relation.Other': 'relative',

    'family.error.throttled': 'Several codes were asked for just now. Please try again in a few minutes.',
    'family.error.expired': 'This code can no longer be used. Ask for a new one.',
    'family.error.already': 'This family member is already in your family.',
    'family.error.ownNumber': 'Everyone registered with your own mobile number is already in your family.',
    'family.error.wrongCode': 'The code is not right. Check the SMS they received and try again.',
  },
  hi: {
    'family.title': 'दूसरे नंबर वाले परिवारजन',
    'family.subtitle': 'अपने अलग मोबाइल नंबर वाले परिवार के सदस्य, और आपके रिकॉर्ड और कौन देखता है।',
    'family.profileLink.hint': 'अलग मोबाइल नंबर वाले परिवार के सदस्य को जोड़ें, या अपने रिकॉर्ड साझा करना बंद करें।',

    'family.linked.title': 'परिवार के सदस्य जिनके रिकॉर्ड आप देखते हैं',
    'family.linked.empty':
      'आपके मोबाइल नंबर से पंजीकृत सभी लोग पहले से आपके पोर्टल में हैं। अपने अलग नंबर वाले परिवार के सदस्य के रिकॉर्ड देखने के लिए, उन्हें जोड़ें।',
    'family.linked.relation': 'आपके {relation}',
    'family.linked.since': '{date} को जोड़ा गया',
    'family.remove': 'हटाएँ',
    'family.remove.label': '{name} को हटाएँ',
    'family.remove.title': '{name} को हटाएँ?',
    'family.remove.body':
      'आप यहाँ {name} के रिकॉर्ड नहीं देख पाएँगे। उन्हें फिर से देखने के लिए, आपको उनसे नया कोड चाहिए होगा, या फ्रंट डेस्क पर पूछें।',
    'family.removed': '{name} को आपके परिवार से हटा दिया गया।',

    'family.add': 'परिवार का सदस्य जोड़ें',
    'family.add.lead':
      'अस्पताल में अपने अलग मोबाइल नंबर से पंजीकृत परिवार के सदस्य के लिए। हम उनके नंबर पर एक कोड भेजते हैं, और वे आपको कोड देकर अपनी सहमति देते हैं।',
    'family.add.uhid': 'उनका UHID',
    'family.add.uhid.hint': 'उनके अस्पताल कार्ड और बिलों पर छपा होता है।',
    'family.add.uhid.invalid': 'उनके अस्पताल कार्ड या बिल पर छपा UHID लिखें।',
    'family.add.phone': 'उनका मोबाइल नंबर',
    'family.add.phone.hint': 'वह नंबर जो उन्होंने अस्पताल को दिया था।',
    'family.add.phone.invalid': 'उनका 10 अंकों का मोबाइल नंबर लिखें।',
    'family.add.relation': 'वे आपके क्या लगते हैं',
    'family.add.relation.choose': 'चुनें',
    'family.add.relation.required': 'चुनें कि वे आपके क्या लगते हैं।',
    'family.add.send': 'कोड भेजें',

    'family.code.title': 'उनका कोड लिखें',
    'family.code.lead':
      'अगर UHID और {ending} पर ख़त्म होने वाला मोबाइल नंबर एक ही मरीज़ के हैं, तो हमने उस नंबर पर SMS से एक कोड भेजा है। उनसे कोड माँगें। यह {minutes} मिनट तक काम करता है।',
    'family.code.label': 'कोड',
    'family.code.verify': 'मेरे परिवार में जोड़ें',
    'family.code.again': 'नया कोड माँगें',
    'family.added': '{name} अब आपके परिवार में हैं।',

    'family.shared.title': 'आपके रिकॉर्ड और कौन देखता है',
    'family.shared.empty': 'यहाँ आपके रिकॉर्ड केवल वही लोग देखते हैं जो आपके मोबाइल नंबर से साइन इन करते हैं।',
    'family.shared.sees': '{name} के रिकॉर्ड देखते हैं (उनके {relation})',
    'family.shared.since': '{date} से',
    'family.stop': 'साझा करना बंद करें',
    'family.stop.label': '{phone} के साथ साझा करना बंद करें',
    'family.stop.title': '{phone} के साथ साझा करना बंद करें?',
    'family.stop.body':
      '{phone} अब {name} के रिकॉर्ड नहीं देख पाएँगे। फिर से साझा करने के लिए, उन्हें आपसे नया कोड चाहिए होगा, या आप फ्रंट डेस्क पर कह सकते हैं।',
    'family.stopped': '{phone} अब {name} के रिकॉर्ड नहीं देखते।',

    'family.relation.Parent': 'माता या पिता',
    'family.relation.Child': 'बेटा या बेटी',
    'family.relation.Spouse': 'पति या पत्नी',
    'family.relation.Sibling': 'भाई या बहन',
    'family.relation.Grandparent': 'दादा-दादी या नाना-नानी',
    'family.relation.Grandchild': 'पोता-पोती या नाती-नातिन',
    'family.relation.Other': 'रिश्तेदार',

    'family.error.throttled': 'अभी कई कोड माँगे गए हैं। कृपया कुछ मिनट बाद फिर कोशिश करें।',
    'family.error.expired': 'यह कोड अब इस्तेमाल नहीं हो सकता। नया कोड माँगें।',
    'family.error.already': 'यह सदस्य पहले से आपके परिवार में है।',
    'family.error.ownNumber': 'आपके अपने मोबाइल नंबर से पंजीकृत सभी लोग पहले से आपके परिवार में हैं।',
    'family.error.wrongCode': 'कोड सही नहीं है। उन्हें मिला SMS देखें और फिर कोशिश करें।',
  },
  ta: {
    'family.title': 'வேறு எண்களில் உள்ள குடும்ப உறுப்பினர்கள்',
    'family.subtitle':
      'சொந்த மொபைல் எண் உள்ள குடும்ப உறுப்பினர்கள், மற்றும் உங்கள் பதிவுகளை வேறு யார் பார்க்கிறார்கள்.',
    'family.profileLink.hint':
      'வேறு மொபைல் எண் பயன்படுத்தும் குடும்ப உறுப்பினரைச் சேருங்கள், அல்லது உங்கள் பதிவுகளைப் பகிர்வதை நிறுத்துங்கள்.',

    'family.linked.title': 'நீங்கள் பதிவுகளைப் பார்க்கும் குடும்ப உறுப்பினர்கள்',
    'family.linked.empty':
      'உங்கள் மொபைல் எண்ணில் பதிவு செய்யப்பட்ட அனைவரும் ஏற்கனவே உங்கள் போர்டலில் உள்ளனர். சொந்த எண் உள்ள குடும்ப உறுப்பினரின் பதிவுகளைப் பார்க்க, அவரைச் சேருங்கள்.',
    'family.linked.relation': 'உங்கள் {relation}',
    'family.linked.since': '{date} அன்று சேர்க்கப்பட்டது',
    'family.remove': 'நீக்குங்கள்',
    'family.remove.label': '{name} அவர்களை நீக்குங்கள்',
    'family.remove.title': '{name} அவர்களை நீக்கவா?',
    'family.remove.body':
      'இனி {name} அவர்களின் பதிவுகளை இங்கே பார்க்க முடியாது. மீண்டும் பார்க்க, அவரிடமிருந்து புதிய குறியீடு தேவை, அல்லது வரவேற்பு மேசையில் கேளுங்கள்.',
    'family.removed': '{name} உங்கள் குடும்பத்திலிருந்து நீக்கப்பட்டார்.',

    'family.add': 'குடும்ப உறுப்பினரைச் சேருங்கள்',
    'family.add.lead':
      'சொந்த மொபைல் எண்ணுடன் மருத்துவமனையில் பதிவு செய்த குடும்ப உறுப்பினருக்கு. அவருடைய எண்ணுக்கு நாங்கள் ஒரு குறியீட்டை அனுப்புவோம்; அந்தக் குறியீட்டை உங்களிடம் தருவதன் மூலம் அவர் ஒப்புதல் அளிக்கிறார்.',
    'family.add.uhid': 'அவருடைய UHID',
    'family.add.uhid.hint': 'அவருடைய மருத்துவமனை அட்டையிலும் பில்களிலும் அச்சிடப்பட்டிருக்கும்.',
    'family.add.uhid.invalid': 'அவருடைய மருத்துவமனை அட்டை அல்லது பில்லில் அச்சிடப்பட்ட UHID-ஐ உள்ளிடுங்கள்.',
    'family.add.phone': 'அவருடைய மொபைல் எண்',
    'family.add.phone.hint': 'அவர் மருத்துவமனைக்குக் கொடுத்த எண்.',
    'family.add.phone.invalid': 'அவருடைய 10 இலக்க மொபைல் எண்ணை உள்ளிடுங்கள்.',
    'family.add.relation': 'அவர் உங்களுக்கு',
    'family.add.relation.choose': 'தேர்ந்தெடுங்கள்',
    'family.add.relation.required': 'அவர் உங்களுக்கு என்ன உறவு என்பதைத் தேர்ந்தெடுங்கள்.',
    'family.add.send': 'குறியீட்டை அனுப்புங்கள்',

    'family.code.title': 'அவருடைய குறியீட்டை உள்ளிடுங்கள்',
    'family.code.lead':
      'UHID-யும் {ending} என முடியும் மொபைல் எண்ணும் ஒரே நோயாளியுடையவை என்றால், அந்த எண்ணுக்கு SMS மூலம் ஒரு குறியீட்டை அனுப்பியுள்ளோம். அதை அவரிடம் கேளுங்கள். இது {minutes} நிமிடங்களுக்குச் செல்லுபடியாகும்.',
    'family.code.label': 'குறியீடு',
    'family.code.verify': 'என் குடும்பத்தில் சேருங்கள்',
    'family.code.again': 'புதிய குறியீட்டைக் கேளுங்கள்',
    'family.added': '{name} இப்போது உங்கள் குடும்பத்தில் உள்ளார்.',

    'family.shared.title': 'உங்கள் பதிவுகளை வேறு யார் பார்க்கிறார்கள்',
    'family.shared.empty': 'உங்கள் மொபைல் எண்ணில் உள்நுழைபவர்கள் மட்டுமே உங்கள் பதிவுகளை இங்கே பார்க்கிறார்கள்.',
    'family.shared.sees': '{name} அவர்களின் பதிவுகளைப் பார்க்கிறார் (அவருடைய {relation})',
    'family.shared.since': '{date} முதல்',
    'family.stop': 'பகிர்வதை நிறுத்துங்கள்',
    'family.stop.label': '{phone} உடன் பகிர்வதை நிறுத்துங்கள்',
    'family.stop.title': '{phone} உடன் பகிர்வதை நிறுத்தவா?',
    'family.stop.body':
      '{phone} இனி {name} அவர்களின் பதிவுகளைப் பார்க்க முடியாது. மீண்டும் பகிர, அவருக்கு உங்களிடமிருந்து புதிய குறியீடு தேவை, அல்லது நீங்கள் வரவேற்பு மேசையில் கேட்கலாம்.',
    'family.stopped': '{phone} இனி {name} அவர்களின் பதிவுகளைப் பார்க்க முடியாது.',

    'family.relation.Parent': 'பெற்றோர்',
    'family.relation.Child': 'மகன் அல்லது மகள்',
    'family.relation.Spouse': 'கணவர் அல்லது மனைவி',
    'family.relation.Sibling': 'சகோதரர் அல்லது சகோதரி',
    'family.relation.Grandparent': 'தாத்தா அல்லது பாட்டி',
    'family.relation.Grandchild': 'பேரன் அல்லது பேத்தி',
    'family.relation.Other': 'உறவினர்',

    'family.error.throttled': 'சற்று முன் பல குறியீடுகள் கேட்கப்பட்டன. சில நிமிடங்களுக்குப் பிறகு மீண்டும் முயலுங்கள்.',
    'family.error.expired': 'இந்தக் குறியீட்டை இனி பயன்படுத்த முடியாது. புதிய குறியீட்டைக் கேளுங்கள்.',
    'family.error.already': 'இவர் ஏற்கனவே உங்கள் குடும்பத்தில் உள்ளார்.',
    'family.error.ownNumber':
      'உங்கள் சொந்த மொபைல் எண்ணில் பதிவு செய்யப்பட்ட அனைவரும் ஏற்கனவே உங்கள் குடும்பத்தில் உள்ளனர்.',
    'family.error.wrongCode': 'குறியீடு சரியில்லை. அவருக்கு வந்த SMS-ஐச் சரிபார்த்து மீண்டும் முயலுங்கள்.',
  },
});
