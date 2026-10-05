#!/bin/sh
# LlamaStash generic wrapper for Halogen (Qwen3.8-Flash-Next, native v2 .hgn).
# $1 = port; per-launch HALOGEN_* values come from the LlamaStash entry env.
# The server is closed source, so it runs on an internal network with no
# outbound access. Docker can't publish ports from one, so socat forwards
# loopback to the container; pdeathsig stops socat if this script dies.
port="$1"
name="llamastash-halogen-$port"
net=halogen-net
HUB=/home/USER/.cache/huggingface/hub    # whole hub dir: snapshot files are symlinks into blobs/
REV=fd91981e3e8117ddbb324fb7efb4c1d6df8fe9e2
IMAGE=ghcr.io/peonist-ai/halogen-flash-server:0.16.1
hg=/hub/models--peonist-ai--halogen-qwen3.8-flash-next/snapshots/$REV
docker rm -f "$name" >/dev/null 2>&1
docker network inspect "$net" >/dev/null 2>&1 || docker network create --internal "$net" >/dev/null || exit 1
docker run -d --rm --name "$name" --network "$net" \
  --device /dev/kfd --device /dev/dri \
  --group-add "$(getent group video | cut -d: -f3)" --group-add "$(getent group render | cut -d: -f3)" \
  --ipc=host --ulimit memlock=-1:-1 -v "$HUB":/hub:ro \
  -e HALOGEN_API_PORT=8080 -e HALOGEN_MODEL_ID \
  -e HALOGEN_CHECKPOINT="$hg/qwen38-flash-next-v2.hgn" \
  -e HALOGEN_TOKENIZER="$hg/tokenizer" \
  -e HALOGEN_CTX -e HALOGEN_KV_POOL_POSITIONS -e HALOGEN_MTP_DEPTH -e HALOGEN_REASONING_EFFORT \
  -e HALOGEN_MAX_TOKENS_DEFAULT=16384 -e HALOGEN_TEMPERATURE -e HALOGEN_TOP_P=0.95 -e HALOGEN_TOP_K=20 \
  "$IMAGE" >/dev/null || exit 1
# One clean stop: SIGTERM becomes `docker stop`; the image SIGKILLs its engine
# 30 s after that, so -t stays above 30 and stop_grace_secs above -t.
trap 'docker stop -t 60 "$name" >/dev/null 2>&1' TERM INT
docker logs -f "$name" 2>&1 &
# Forward only once the API listens; earlier, socat logs every readiness probe as refused.
ip=$(docker inspect -f "{{(index .NetworkSettings.Networks \"$net\").IPAddress}}" "$name")
until curl -so /dev/null "http://$ip:8080/v1/models"; do
  [ "$(docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null)" = true ] || exit 1
  sleep 2
done
setpriv --pdeathsig TERM socat TCP-LISTEN:"$port",bind=127.0.0.1,reuseaddr,fork TCP:"$ip":8080 &
fwd=$!
docker wait "$name" >/dev/null &
wait $!
kill "$fwd" 2>/dev/null
