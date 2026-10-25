#!/usr/bin/env bash
# Download the Mica weights once, then serve /v1/systemone on the Vulkan backend.
set -euo pipefail

MICA_REPO=${MICA_REPO:-sky7350/Mica-v0.5-4B}
MICA_REVISION=${MICA_REVISION:-main}
MICA_GGUF=${MICA_GGUF:-mica-v0.5-4b-Q8_0.gguf}
MICA_NAME=${MICA_NAME:-mica-v0.5-4b}
PORT=${PORT:-8010}
W=${WEIGHTS_DIR:-/weights}

if [ ! -f "$W/$MICA_GGUF" ]; then
  hf download "$MICA_REPO" "$MICA_GGUF" tokenizer.json tokenizer_config.json chat_template.jinja config.json \
     --revision "$MICA_REVISION" --local-dir "$W"
fi

# seqs=1 and state-cache=off keep the 32k KV cache within a 12 GB Radeon. The app sends one
# mapping question per request and has a single worker, so one sequence is sufficient. Increase
# seqs only if the GPU has spare VRAM.
exec python3 -m mica.server \
  --gguf "$W/$MICA_GGUF" \
  --hf "$W" \
  --runtime /app/runtime \
  --calibration /app/calibration.json \
  --flash-attn -1 \
  --n-ctx "${SYSTEMONE_N_CTX:-32768}" \
  --seqs "${SYSTEMONE_SEQS:-1}" \
  --state-cache "${SYSTEMONE_STATE_CACHE:-off}" \
  --n-ubatch "${SYSTEMONE_N_UBATCH:-256}" \
  --host 0.0.0.0 \
  --port "$PORT" \
  --name "$MICA_NAME"
