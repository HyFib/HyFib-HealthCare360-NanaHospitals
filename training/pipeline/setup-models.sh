#!/usr/bin/env bash
# Fetches the narrator's voice and the checking speech recogniser, and the Python they run on. Both run offline once
# fetched: no narration text, and nothing about the hospital, leaves the machine.
#
#   training/pipeline/setup-models.sh            models into training/.models, Python into training/.venv
#
# Voice: Kokoro v1.0 (Apache-2.0), voice hf_alpha, an Indian English voice: chosen because an independent recogniser
# heard every word of a test script at about 139 words a minute (training/README.md, "The voice"). Packaged by
# sherpa-onnx (k2-fsa), which also packages the recogniser: Whisper small.en (MIT), used only to check the videos.
# sherpa-onnx is pinned to 1.13.8: its 2.x wheels leave out the espeak-ng front end these models are packaged for.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODELS="${HC360_TTS_MODELS:-$HERE/../.models}"
VENV="${HC360_VENV:-$HERE/../.venv}"
RELEASES=https://github.com/k2-fsa/sherpa-onnx/releases/download
mkdir -p "$MODELS"

fetch() {  # fetch <release path> <folder it unpacks to>
  [ -d "$MODELS/$2" ] && return
  curl -sSL --retry 4 -o "$MODELS/$2.tar.bz2" "$RELEASES/$1"
  tar -xjf "$MODELS/$2.tar.bz2" -C "$MODELS"
  rm "$MODELS/$2.tar.bz2"
}

fetch tts-models/kokoro-multi-lang-v1_0.tar.bz2 kokoro-multi-lang-v1_0
fetch asr-models/sherpa-onnx-whisper-small.en.tar.bz2 sherpa-onnx-whisper-small.en

(cd "$MODELS" && sha256sum -c --quiet <<'EOF'
b40f62b166ac8164b0627ef48a0b358eda0985e272fb03ef5252e7206305da11  kokoro-multi-lang-v1_0/model.onnx
1c5a5b983d3d50d8586d437a51f3faa2da7919ce76a013c081e65671a3447c29  kokoro-multi-lang-v1_0/voices.bin
6ebb6bb288f20f3ae8d004d3c2ca27697da27c037d75e81a60e2a6a663f95425  kokoro-multi-lang-v1_0/tokens.txt
c4cbb37316f62210dff52718a7afcaae24f50c032cc75ab47ae67b831d1049e7  kokoro-multi-lang-v1_0/lexicon-gb-en.txt
8bdac288f369aa94ee2194059238c465ed82ea9d47ee8fa4a8c0a891873e462f  sherpa-onnx-whisper-small.en/small.en-encoder.int8.onnx
710ccf890e10f3faa15f51ec346081a2723c9f3adb6e4da81c6573a5a6f877fb  sherpa-onnx-whisper-small.en/small.en-decoder.int8.onnx
306cd27f03c1a714eca7108e03d66b7dc042abe8c258b44c199a7ed9838dd930  sherpa-onnx-whisper-small.en/small.en-tokens.txt
EOF
)

if [ ! -x "$VENV/bin/python" ]; then
  python3 -m venv "$VENV"
fi
"$VENV/bin/pip" install -q -r "$HERE/requirements.txt"
echo "models: $MODELS"
echo "python: $VENV/bin/python"
