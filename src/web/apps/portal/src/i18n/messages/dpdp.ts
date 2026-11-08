import { defineMessages } from '../define';

/**
 * The DPDP duties added to "Your privacy" and to the privacy notice: consents given through a consent manager (s.6(7)),
 * erased data in backups (s.8(7)), children's data (s.9) and the Data Protection Officer (s.10). The Hindi and Tamil
 * were written by the development team and are listed for a native speaker's review in docs/requirements
 * (hindi-review.md, tamil-review.md: "Portal text for the DPDP duties").
 */
export const dpdpMessages = defineMessages({
  en: {
    'myPrivacy.hub.consents': 'Consents through a consent manager',
    'myPrivacy.hub.consents.hint': 'Consents you gave through a consent manager app, and withdrawing them.',

    'myPrivacy.consents.title': 'Consents through a consent manager',
    'myPrivacy.consents.lead':
      'A consent manager is an app registered with the Data Protection Board where you give and withdraw consent. These are the consents it sent us for you. Withdrawing here is as easy as giving: we stop at once and tell the consent manager.',
    'myPrivacy.consents.none.title': 'No consents from a consent manager',
    'myPrivacy.consents.none.body': 'Consents you give at the hospital are in the summary of your data.',
    'myPrivacy.consents.purpose.DataSharing': 'Sharing your records',
    'myPrivacy.consents.purpose.Research': 'Research',
    'myPrivacy.consents.purpose.SmsWhatsAppUpdates': 'Updates by SMS and WhatsApp',
    'myPrivacy.consents.purpose.MarketingCommunication': 'Health offers',
    'myPrivacy.consents.purpose.Photography': 'Photographs',
    'myPrivacy.consents.covers': 'Covers',
    'myPrivacy.consents.from': 'From',
    'myPrivacy.consents.validUntil': 'Valid until',
    'myPrivacy.consents.inForce': 'In force',
    'myPrivacy.consents.withdrawn': 'Withdrawn on {date}',
    'myPrivacy.consents.expired': 'Ended on {date}',
    'myPrivacy.consents.withdraw': 'Withdraw this consent',
    'myPrivacy.consents.withdraw.title': 'Withdraw your consent for {purpose}?',
    'myPrivacy.consents.withdraw.body':
      'We stop using your data for this from now on and tell the consent manager. What was done before stays as it was.',
    'myPrivacy.consents.withdraw.keep': 'Keep it',
    'myPrivacy.consents.withdrawn.done': 'Consent withdrawn. We have told the consent manager.',

    'privacy.backups.title': 'Erased data and backups',
    'privacy.backups.body':
      'When we erase your data, copies stay in our encrypted backups until those backups are deleted, within {days} days. If we ever have to restore a backup before then, your erasure is carried out again before the data is used.',
    'privacy.children.title': 'Children',
    'privacy.children.body':
      'For a child under 18, we use their data to care for them. Anything else that needs consent, such as sharing records or research, needs the consent of a parent or lawful guardian, checked with a code sent to their mobile number or their identity document. We never send health offers or targeted messages to children.',
    'privacy.dpo.title': 'Data Protection Officer',
  },
  hi: {
    'myPrivacy.hub.consents': 'सहमति प्रबंधक के माध्यम से दी गई सहमतियाँ',
    'myPrivacy.hub.consents.hint': 'सहमति प्रबंधक ऐप से दी गई आपकी सहमतियाँ, और उन्हें वापस लेना।',

    'myPrivacy.consents.title': 'सहमति प्रबंधक के माध्यम से दी गई सहमतियाँ',
    'myPrivacy.consents.lead':
      'सहमति प्रबंधक डेटा संरक्षण बोर्ड में पंजीकृत एक ऐप है, जिसमें आप सहमति देते और वापस लेते हैं। ये वे सहमतियाँ हैं जो उसने आपकी ओर से हमें भेजी हैं। यहाँ वापस लेना देने जितना ही आसान है: हम तुरंत रुक जाते हैं और सहमति प्रबंधक को बताते हैं।',
    'myPrivacy.consents.none.title': 'सहमति प्रबंधक से कोई सहमति नहीं',
    'myPrivacy.consents.none.body': 'अस्पताल में दी गई सहमतियाँ आपके डेटा के सारांश में हैं।',
    'myPrivacy.consents.purpose.DataSharing': 'आपके रिकॉर्ड साझा करना',
    'myPrivacy.consents.purpose.Research': 'अनुसंधान',
    'myPrivacy.consents.purpose.SmsWhatsAppUpdates': 'SMS और WhatsApp से जानकारी',
    'myPrivacy.consents.purpose.MarketingCommunication': 'स्वास्थ्य ऑफ़र',
    'myPrivacy.consents.purpose.Photography': 'तस्वीरें',
    'myPrivacy.consents.covers': 'किस पर लागू',
    'myPrivacy.consents.from': 'किससे',
    'myPrivacy.consents.validUntil': 'कब तक मान्य',
    'myPrivacy.consents.inForce': 'लागू है',
    'myPrivacy.consents.withdrawn': '{date} को वापस ली गई',
    'myPrivacy.consents.expired': '{date} को समाप्त',
    'myPrivacy.consents.withdraw': 'यह सहमति वापस लें',
    'myPrivacy.consents.withdraw.title': '{purpose} के लिए अपनी सहमति वापस लें?',
    'myPrivacy.consents.withdraw.body':
      'अब से हम इसके लिए आपके डेटा का उपयोग बंद कर देंगे और सहमति प्रबंधक को बताएँगे। पहले जो हो चुका है, वह वैसा ही रहेगा।',
    'myPrivacy.consents.withdraw.keep': 'रहने दें',
    'myPrivacy.consents.withdrawn.done': 'सहमति वापस ले ली गई। हमने सहमति प्रबंधक को बता दिया है।',

    'privacy.backups.title': 'मिटाया गया डेटा और बैकअप',
    'privacy.backups.body':
      'जब हम आपका डेटा मिटाते हैं, तो उसकी प्रतियाँ हमारे एन्क्रिप्टेड बैकअप में तब तक रहती हैं जब तक वे बैकअप हटाए नहीं जाते, {days} दिनों के भीतर। यदि उससे पहले हमें कभी कोई बैकअप वापस लाना पड़े, तो डेटा के उपयोग से पहले आपका डेटा फिर से मिटाया जाता है।',
    'privacy.children.title': 'बच्चे',
    'privacy.children.body':
      '18 वर्ष से कम आयु के बच्चे के डेटा का उपयोग हम उसकी देखभाल के लिए करते हैं। सहमति की ज़रूरत वाली किसी भी अन्य बात, जैसे रिकॉर्ड साझा करना या अनुसंधान, के लिए माता-पिता या वैध अभिभावक की सहमति चाहिए, जिसकी जाँच उनके मोबाइल नंबर पर भेजे गए कोड या उनके पहचान दस्तावेज़ से की जाती है। हम बच्चों को कभी स्वास्थ्य ऑफ़र या लक्षित संदेश नहीं भेजते।',
    'privacy.dpo.title': 'डेटा संरक्षण अधिकारी',
  },
  ta: {
    'myPrivacy.hub.consents': 'ஒப்புதல் மேலாளர் மூலம் அளித்த ஒப்புதல்கள்',
    'myPrivacy.hub.consents.hint': 'ஒப்புதல் மேலாளர் செயலியில் நீங்கள் அளித்த ஒப்புதல்கள், அவற்றைத் திரும்பப் பெறுதல்.',

    'myPrivacy.consents.title': 'ஒப்புதல் மேலாளர் மூலம் அளித்த ஒப்புதல்கள்',
    'myPrivacy.consents.lead':
      'ஒப்புதல் மேலாளர் என்பது தரவுப் பாதுகாப்பு வாரியத்தில் பதிவுசெய்யப்பட்ட ஒரு செயலி; அதில் நீங்கள் ஒப்புதல் அளிக்கலாம், திரும்பப் பெறலாம். அது உங்கள் சார்பில் எங்களுக்கு அனுப்பிய ஒப்புதல்கள் இவை. இங்கே திரும்பப் பெறுவது அளிப்பதைப் போலவே எளிது: நாங்கள் உடனே நிறுத்தி, ஒப்புதல் மேலாளருக்குத் தெரிவிக்கிறோம்.',
    'myPrivacy.consents.none.title': 'ஒப்புதல் மேலாளரிடமிருந்து ஒப்புதல் எதுவும் இல்லை',
    'myPrivacy.consents.none.body': 'மருத்துவமனையில் அளித்த ஒப்புதல்கள் உங்கள் தரவுச் சுருக்கத்தில் உள்ளன.',
    'myPrivacy.consents.purpose.DataSharing': 'உங்கள் பதிவுகளைப் பகிர்தல்',
    'myPrivacy.consents.purpose.Research': 'ஆராய்ச்சி',
    'myPrivacy.consents.purpose.SmsWhatsAppUpdates': 'SMS மற்றும் WhatsApp மூலம் தகவல்கள்',
    'myPrivacy.consents.purpose.MarketingCommunication': 'சுகாதாரச் சலுகைகள்',
    'myPrivacy.consents.purpose.Photography': 'புகைப்படங்கள்',
    'myPrivacy.consents.covers': 'உள்ளடக்குவது',
    'myPrivacy.consents.from': 'யாரிடமிருந்து',
    'myPrivacy.consents.validUntil': 'செல்லுபடியாகும் நாள் வரை',
    'myPrivacy.consents.inForce': 'நடைமுறையில் உள்ளது',
    'myPrivacy.consents.withdrawn': '{date} அன்று திரும்பப் பெறப்பட்டது',
    'myPrivacy.consents.expired': '{date} அன்று முடிந்தது',
    'myPrivacy.consents.withdraw': 'இந்த ஒப்புதலைத் திரும்பப் பெறுக',
    'myPrivacy.consents.withdraw.title': '{purpose} க்கான உங்கள் ஒப்புதலைத் திரும்பப் பெறவா?',
    'myPrivacy.consents.withdraw.body':
      'இனிமேல் இதற்காக உங்கள் தரவைப் பயன்படுத்துவதை நிறுத்தி, ஒப்புதல் மேலாளருக்குத் தெரிவிப்போம். முன்பு நடந்தவை அப்படியே இருக்கும்.',
    'myPrivacy.consents.withdraw.keep': 'அப்படியே வைத்திருங்கள்',
    'myPrivacy.consents.withdrawn.done': 'ஒப்புதல் திரும்பப் பெறப்பட்டது. ஒப்புதல் மேலாளருக்குத் தெரிவித்துவிட்டோம்.',

    'privacy.backups.title': 'அழிக்கப்பட்ட தரவும் காப்புப்பிரதிகளும்',
    'privacy.backups.body':
      'உங்கள் தரவை நாங்கள் அழிக்கும்போது, அதன் நகல்கள் எங்கள் மறையாக்கம் செய்யப்பட்ட காப்புப்பிரதிகளில், அவை நீக்கப்படும் வரை, {days} நாட்களுக்குள், இருக்கும். அதற்கு முன் ஒரு காப்புப்பிரதியை மீட்டெடுக்க வேண்டியிருந்தால், தரவு பயன்படுத்தப்படும் முன் உங்கள் அழிப்பு மீண்டும் செய்யப்படும்.',
    'privacy.children.title': 'குழந்தைகள்',
    'privacy.children.body':
      '18 வயதுக்குக் குறைவான குழந்தையின் தரவை அவர்களைக் கவனித்துக்கொள்வதற்குப் பயன்படுத்துகிறோம். பதிவுகளைப் பகிர்தல், ஆராய்ச்சி போன்ற ஒப்புதல் தேவைப்படும் வேறு எதற்கும் பெற்றோர் அல்லது சட்டப்படியான பாதுகாவலரின் ஒப்புதல் வேண்டும்; அது அவர்களின் கைபேசி எண்ணுக்கு அனுப்பப்படும் குறியீடு அல்லது அவர்களின் அடையாள ஆவணம் மூலம் சரிபார்க்கப்படும். குழந்தைகளுக்கு சுகாதாரச் சலுகைகளோ இலக்கு வைத்த செய்திகளோ ஒருபோதும் அனுப்புவதில்லை.',
    'privacy.dpo.title': 'தரவுப் பாதுகாப்பு அலுவலர்',
  },
});
