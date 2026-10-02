import { defineMessages } from '../define';

/** What patients read when the hospital has turned the portal or video consultations off. */
export const featureMessages = defineMessages({
  en: {
    'features.portalOff.title': 'The patient portal is not available right now',
    'features.portalOff.body': 'Please contact the hospital.',
    'features.portalOff.stillWorks':
      'You can still check a report or prescription from its QR code, and links the hospital sends you by SMS still work.',
    'features.videoOff.title': 'Video consultations are not available right now',
    'features.videoOff.body': 'Please contact the hospital to see the doctor in person.',
    'features.videoOff.booked':
      'Video consultations are not available right now. Please contact the hospital about this appointment.',
  },
  hi: {
    'features.portalOff.title': 'मरीज़ पोर्टल अभी उपलब्ध नहीं है',
    'features.portalOff.body': 'कृपया अस्पताल से संपर्क करें।',
    'features.portalOff.stillWorks':
      'आप अब भी किसी रिपोर्ट या पर्चे को उसके QR कोड से जाँच सकते हैं, और अस्पताल द्वारा SMS से भेजे गए लिंक अब भी काम करते हैं।',
    'features.videoOff.title': 'वीडियो परामर्श अभी उपलब्ध नहीं है',
    'features.videoOff.body': 'डॉक्टर से अस्पताल में मिलने के लिए कृपया अस्पताल से संपर्क करें।',
    'features.videoOff.booked':
      'वीडियो परामर्श अभी उपलब्ध नहीं है। इस अपॉइंटमेंट के बारे में कृपया अस्पताल से संपर्क करें।',
  },
});
