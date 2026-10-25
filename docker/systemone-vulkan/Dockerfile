# syntax=docker/dockerfile:1
# Mica Vulkan image for AMD Radeon (and other Vulkan-capable) GPUs.
# Build context: the ArrImportSystemOne repository root.
#
# The runtime is the upstream Mica server + llama.cpp's Vulkan backend. The target host must expose
# its DRI render node (usually /dev/dri) and have a working Vulkan driver (Mesa RADV for Radeon).

FROM python:3.12-slim AS runtime-build

ARG MICA_REPO_URL=https://github.com/akivet/Mica.git
ARG MICA_COMMIT=a21c3cecefba92b11ee65b53f9241e252752eb52
ARG LLAMA_TAG=b11700

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git cmake build-essential ca-certificates libvulkan-dev glslc glslang-tools spirv-headers \
    && rm -rf /var/lib/apt/lists/*

RUN git clone "$MICA_REPO_URL" /mica \
    && git -C /mica checkout "$MICA_COMMIT"

# Reuse Mica's upstream runtime build so the Vulkan/RDNA patches are applied exactly as upstream
# intends. build_runtime.sh writes the shared libraries to /mica/runtime.
WORKDIR /mica
RUN BACKEND=vulkan LLAMA_TAG="$LLAMA_TAG" bash scripts/build_runtime.sh

FROM python:3.12-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl libgomp1 libvulkan1 mesa-vulkan-drivers vulkan-tools \
    && rm -rf /var/lib/apt/lists/*

RUN pip3 install --no-cache-dir torch==2.7.1 --index-url https://download.pytorch.org/whl/cpu \
    && pip3 install --no-cache-dir "transformers==5.5.0" safetensors "huggingface_hub>=1.0"

COPY --from=runtime-build /mica/runtime /app/runtime
COPY --from=runtime-build /mica/mica /app/mica
COPY --from=runtime-build /mica/calibration.json /app/calibration.json

WORKDIR /app

# libggml.so resolves its backend libraries from this directory.
ENV LD_LIBRARY_PATH=/app/runtime

ENV MICA_REPO=sky7350/Mica-v0.5-4B \
    MICA_REVISION=main \
    MICA_GGUF=mica-v0.5-4b-Q8_0.gguf \
    MICA_NAME=mica-v0.5-4b \
    PORT=8010 \
    SYSTEMONE_N_CTX=32768 \
    SYSTEMONE_SEQS=1 \
    SYSTEMONE_STATE_CACHE=off \
    SYSTEMONE_N_UBATCH=256

COPY docker/systemone-vulkan/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

EXPOSE 8010
ENTRYPOINT ["bash", "/app/entrypoint.sh"]
