# Handover: Halogen + gufo behind LlamaStash on Strix Halo

You are setting up a local LLM stack on an AMD Strix Halo machine (Ryzen AI Max+ 395, Radeon 8060S, gfx1151, 128 GB unified memory) running native Linux. When you finish, the user can start any of these from LlamaStash (TUI, CLI, or its OpenAI/Anthropic proxy), each with ready-made presets:

| LlamaStash row                  | Engine                 | Weights                                           |
| ------------------------------- | ---------------------- | ------------------------------------------------- |
| `flash-next-halogen`            | Halogen (Docker image) | Halogen native v2 `.hgn` (Qwen3.8-Flash-Next)     |
| `Qwen3.8-Flash-Next-UD-Q4_K_XL` | gufo (native build)    | Unsloth UD-Q4_K_XL GGUF + Unsloth shared MTP head |
| `Qwen3.8-27B-UD-Q6_K`           | gufo (native build)    | Unsloth UD-Q6_K GGUF + z-lab DFlash2 draft        |

Then you connect Pi, OpenCode and Claude Code to the LlamaStash proxy.

## Rules

- **Check versions first.** Before you install anything, look up the newest release of each tool. If one is newer than the version below, use it, read its changelog for renamed flags or env vars, and tell the user what you changed.
  - LlamaStash: `gh release list -R llamastash/llamastash -L 3`
  - gufo: `gh release list -R gufo-org/gufo -L 3`
  - Halogen: `gh api repos/peonist-ai/halogen-flash-server/commits --jq '.[0:3][].commit.message'`, and `CHANGELOG.md` in that repo.
- **Versions as of 2026-10-02:** LlamaStash 0.6.1, Halogen 0.16.1, gufo v0.5.0. The Halogen setup was tested on 0.16.1 with Docker 29.8. The gufo numbers were measured on gufo 0.3.0; the v0.5.0 changelog shows no change to the flags used here, but it has not been benchmarked on this setup yet.
- **One large model at a time.** Each Flash-Next engine takes 80 to 90 GiB.
- **Never hard-kill a GPU server.** Stop it once with SIGTERM (`llamastash stop`, or `docker stop` for Halogen) and wait. A server killed mid-kernel can hang the GPU and freeze the desktop.
- **Don't build in `/tmp`.** It is often tmpfs. Use a directory on disk.
- **Ask before system-level changes:** kernel command line, BIOS, groups, packages.

## 1. Check the host

Run these checks and report anything that fails before you continue:

1. **Kernel.** You need Linux 6.18.4 or later, built with `CONFIG_HSA_AMD_SVM`. Halogen maps its weights through KFD's SVM and fails to register them without it.
   - Find the KFD node whose `properties` has `gfx_target_version 110501`, under `/sys/class/kfd/kfd/topology/nodes/<n>/properties`.
   - Its `capability` must have bit `0x08000000` set.
2. **BIOS.** The UMA frame buffer ("dedicated graphics memory") must be at its smallest explicit value (for example 512 MB), not Auto. Halogen wants the memory as normal RAM.
3. **Kernel command line** (`cat /proc/cmdline`).
   - Set `amdgpu.gttsize` and `ttm.pages_limit` to about the installed RAM. For 128 GB that is `amdgpu.gttsize=126976 ttm.pages_limit=32505856`; the Halogen README has the rows for 96 and 64 GB.
   - **Optional:** `amd_iommu=off` makes Halogen prefill 13 to 16% faster (Halogen's own measurement). It also disables the NPU and can break USB4 docks. Ask the user before adding it.
4. **Groups.** The user must be in the `video` and `render` groups, and needs read/write access to `/dev/kfd` and `/dev/dri/renderD*`.
5. **Tools:** Docker, `socat`, `curl`, `setpriv` (util-linux), `uv`, `git`, `gh`, `cmake` 3.21+, `ninja`, `pkg-config`, a C++20 compiler, and the dev packages for ICU, libcurl, OpenSSL, libpng, libjpeg and libwebp, plus `ffmpeg`.
6. **Disk:** about 260 GB free for the weights.
   - Halogen: about 118 GB.
   - Flash-Next GGUF + MTP head: about 114 GB.
   - 27B + draft: about 24 GB.
7. **Power.** On AC power, with `/sys/firmware/acpi/platform_profile` at `performance` if the machine has it. Battery or a low profile can halve the speed.

## 2. Install LlamaStash (0.6.1 or later)

```bash
curl -fsSL https://llamastash.dev/install.sh | sh     # or: yay -S llamastash / brew / cargo install llamastash
llamastash --version
llamastash init        # first run only: installs llama-server, writes the config
```

- You need 0.6.1 or later. It added the generic-entry fields that give Halogen its thinking-effort controls in Pi and OpenCode (step 6). `install.sh` installs the latest GitHub release. The AUR, Homebrew and crates.io packages can trail a release by a few hours, so check `llamastash --version`.
- The config is at `~/.config/llamastash/config.yaml`.
- LlamaStash scans `~/.cache/huggingface/hub` for GGUFs. If the weights go anywhere else (a custom `HF_HOME`), add that `hub` directory to `model_paths:` in the config.

## 3. Download the weights

Use `llamastash pull`. Files land in the standard HF cache (`$HF_HOME/hub`, by default `~/.cache/huggingface/hub`), which LlamaStash scans.

```bash
# Halogen native weights: v2 checkpoint (draft head inside), its n-gram table, tokenizer
for f in qwen38-flash-next-v2.hgn qwen38-flash-next-ngram.hgn \
         tokenizer/chat_template.jinja tokenizer/generation_config.json tokenizer/merges.txt \
         tokenizer/tokenizer.json tokenizer/tokenizer_config.json tokenizer/vocab.json; do
  llamastash pull --no-companions --json "peonist-ai/halogen-qwen3.8-flash-next:$f" | jq -r .revision
done

# Flash-Next GGUF for gufo: pinning shard 1 pulls all 4 shards. Then the shared MTP head.
llamastash pull --no-companions --json unsloth/Qwen3.8-Flash-Next-GGUF:UD-Q4_K_XL/Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf | jq -r .revision
llamastash pull --no-companions --json unsloth/Qwen3.8-Flash-Next-GGUF:MTP/mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf | jq -r .revision

# 27B for gufo + DFlash2 draft
llamastash pull --no-companions --json unsloth/Qwen3.8-27B-GGUF:Qwen3.8-27B-UD-Q6_K.gguf | jq -r .revision
llamastash pull --no-companions --json z-lab/Qwen3.8-27B-DFlash2-GGUF:Qwen3.8-27B-DFlash2-Q4_K_M.gguf | jq -r .revision

# Vision projectors, so gufo accepts images (about 0.9 GB each)
llamastash pull --no-companions --json unsloth/Qwen3.8-Flash-Next-GGUF:mmproj-BF16.gguf | jq -r .revision
llamastash pull --no-companions --json unsloth/Qwen3.8-27B-GGUF:mmproj-BF16.gguf | jq -r .revision
```

- **Always pass `--no-companions`.** Without it, `pull` also fetches one mmproj projector and one MTP head per repo, and it picks the wrong ones for gufo. For Flash-Next it would get `mtp-Qwen3.8-Flash-Next-BF16.gguf`, not the `shared-Q8_0` head. For the projector it would get `mmproj-F16.gguf`.
- **gufo only reads `mmproj-BF16.gguf`.** It looks in the model file's folder and one folder up, which is the snapshot root for both repos. With only `mmproj-F16.gguf`, an image request fails with "image input requires a matching --mmproj BF16 sidecar".
- **`pull` has no revision flag**, so it fetches each repo's current `main`. The printed `revision` is the `snapshots/<revision>` directory in the paths of steps 4 and 6. As of 2026-10-02 they match the ones written there:
  - Halogen `fd91981e...`
  - Flash-Next `38bb39ee...`
  - 27B `4ca72078...`
  - DFlash2 `2d9571f8...`

  If one differs, use the printed value.

- **Non-GGUF files:** `pull`'s docs only mention `.gguf` file pins. Pinning other files (the `.hgn` and tokenizer files) works; it was tested on LlamaStash 0.6.0 with a tokenizer file.
- Snapshot files are symlinks into `hub/blobs/`. Anything that reads them from a container must mount the whole `hub/` directory.

## 4. Halogen: image + wrapper

```bash
docker pull ghcr.io/peonist-ai/halogen-flash-server:0.16.1
```

Save the wrapper below as `~/.local/bin/halogen-serve` and `chmod +x` it. Set `HUB` to the absolute path of your `hub/` directory.

```sh
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
```

- Halogen finds `qwen38-flash-next-ngram.hgn` next to the checkpoint by itself. Do not set `HALOGEN_MTP_HEAD` with v2, because the draft head is inside the checkpoint.
- **No outbound network.** On the `--internal` network the container can't resolve DNS or reach any outside address. `HF_HUB_OFFLINE=1` is set in the image, and `HALOGEN_DOWNLOAD` stays unset, so it never needs to. To check: `docker exec <container> python3 -c 'import urllib.request as u; u.urlopen("https://example.com", timeout=5)'` must fail with "Temporary failure in name resolution".
- The container can still connect to host services that listen on all interfaces (`ss -ltn` lists them as `0.0.0.0`).
- **Podman:** not tested. Replace the two `--group-add` flags with `--group-add keep-groups` (rootless Podman needs `crun` for that). Rootless Podman keeps containers in its own network namespace, so the host may not reach the container IP. If the socat forward fails, drop the internal network and publish with `-p "127.0.0.1:$port:8080"`; outbound traffic is then open.

## 5. gufo: build v0.5.0 against TheRock's ROCm 10 SDK

This is the setup the numbers were measured on. It uses the ROCm 10 pip SDK in its own venv, so it doesn't need a system ROCm. `LLMS` is any directory on disk.

```bash
LLMS=$HOME/llms; SDK=$LLMS/rocm10-sdk; mkdir -p $SDK/tmp
git clone https://github.com/gufo-org/gufo $LLMS/gufo && git -C $LLMS/gufo checkout v0.5.0

uv venv --seed -p 3.12 $SDK/.venv
TMPDIR=$SDK/tmp PIP_NO_CACHE_DIR=1 $SDK/.venv/bin/python -m pip install \
  --index-url https://stable.repo.amd.com/rocm/whl-next/ "rocm[libraries,devel,device-gfx1151]==10.0.0"
$SDK/.venv/bin/rocm-sdk init

R=$($SDK/.venv/bin/rocm-sdk path --root)
export PATH="$R/bin:$PATH" HIP_PATH="$R" ROCM_PATH="$R"
cd $LLMS/gufo
cmake --preset release -B build/release-rocm10 \
  -DCMAKE_PREFIX_PATH="$R" -DCMAKE_HIP_COMPILER="$R/lib/llvm/bin/clang++" \
  "-DCMAKE_BUILD_RPATH=$R/lib;$R/lib/rocm_sysdeps/lib;$R/lib/llvm/lib"
cmake --build build/release-rocm10 -j 16
./build/release-rocm10/gufo diagnose
```

- **GCC 16 or newer** (Arch, for example) breaks ROCm clang HIP compiles in `<format>`. Install GCC 15 and add the following to the `cmake --preset` line:
  ```
  -DCMAKE_C_COMPILER=gcc-15 -DCMAKE_CXX_COMPILER=g++-15 -DCMAKE_HIP_FLAGS=--gcc-install-dir=$(dirname $(gcc-15 -print-libgcc-file-name))
  ```
- The RPATH points the binary at the SDK's libraries, so it runs without any env vars.
- **Fallback:** gufo's own qualified toolchain is a system ROCm 7.2.3 with GCC 15.3. Follow "Build from source / Without Nix" in gufo's README. On the reference machine, ROCm 7.2.4 and ROCm 10 builds were within 3% of each other.

## 6. LlamaStash config

1. Stop the daemon first: `llamastash daemon stop`. The config is read only at start.
2. Merge the YAML below into `~/.config/llamastash/config.yaml`, keeping anything already there (for example `backend.llamacpp`).
3. Replace every `/ABS/...` path with a real absolute path:
   - `/ABS/llms` = `$LLMS` from step 5
   - `/ABS/hub` = the `hub/` directory from step 3
4. Start the daemon again: `llamastash daemon start`.

Presets set only the context window, plus the KV pool for Halogen. Thinking effort is not a preset. Each engine defaults to `xhigh`, and a client that sends `reasoning_effort` (such as Pi's thinking level or Claude Code's `/effort`) overrides it per request.

```yaml
proxy:
  idle_ttl_secs: 0 # a loaded model stays until you stop it
backend:
  generic:
    servers:
      # Server `generic-gufo` on the Flash-Next GGUF row. --reasoning-effort is
      # the server default; a request's reasoning_effort overrides it.
      - name: gufo
        model: Qwen3.8-Flash-Next-UD-Q4_K_XL
        binary: /ABS/llms/gufo/build/release-rocm10/gufo
        args:
          [
            serve,
            --host,
            "{host}",
            --port,
            "{port}",
            --sessions,
            "1",
            llm,
            --served-model-name,
            "{name}",
            --model,
            "{model}",
            --mtp-model,
            /ABS/hub/models--unsloth--Qwen3.8-Flash-Next-GGUF/snapshots/38bb39ee97821de2c9009abb7e93950eec396e66/MTP/mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf,
            --top-p,
            "0.95",
            --top-k,
            "20",
            --reasoning-effort,
            xhigh,
          ]
        knobs:
          - { flag: --context, ctx: true, default: "131072" }
          - { flag: --speculative, default: mtp }
          - { flag: --temperature, default: "1.0" }
          - --seed
        ready: /ready
        stop_grace_secs: 60
        # gufo 404s on any model name but --served-model-name.
        rewrite_model: true
      # Server `generic-gufo-27b` on the 27B UD-Q6_K row.
      - name: gufo-27b
        model: Qwen3.8-27B-UD-Q6_K
        binary: /ABS/llms/gufo/build/release-rocm10/gufo
        args:
          [
            serve,
            --host,
            "{host}",
            --port,
            "{port}",
            --sessions,
            "1",
            llm,
            --served-model-name,
            "{name}",
            --model,
            "{model}",
            --speculative,
            dflash2,
            --dflash-model,
            /ABS/hub/models--z-lab--Qwen3.8-27B-DFlash2-GGUF/snapshots/2d9571f8ce46e151f61c6499c99dee6079e1d610/Qwen3.8-27B-DFlash2-Q4_K_M.gguf,
            --top-p,
            "0.95",
            --top-k,
            "20",
            --reasoning-effort,
            xhigh,
          ]
        knobs:
          - { flag: --context, ctx: true, default: "131072" }
          - { flag: --temperature, default: "1.0" }
          - --seed
        ready: /ready
        stop_grace_secs: 60
        rewrite_model: true
      # Own row (weights are not a GGUF). Docker wrapper from step 4.
      - name: flash-next-halogen
        binary: ~/.local/bin/halogen-serve
        args: ["{port}"]
        arch: qwen4exp
        params: 177B
        quant: v2-4.16bpw
        ctx: 262144
        # No GGUF to read these from, so the row declares them for `integrations`.
        # Thinking off plus the template's levels; leave `vision` off: the wrapper
        # doesn't set HALOGEN_VISION_TOWER, so Halogen refuses images.
        reasoning_effort: [none, low, medium, xhigh]
        reasoning_effort_default: xhigh # same as halogen-effort's default
        # At the 262144 window and pool Halogen holds about 87 GiB. Also gates
        # admission. A 786432 pool adds ~14.4 GiB this does not count.
        memory_gib: 87
        knobs:
          - {
              flag: --ctx-window,
              id: halogen-ctx,
              ctx: true,
              default: "131072",
            }
          # Positions resident across all sessions, reserved at start (~7.2 GiB
          # per 262144) out of the lookup table's page cache. Must be >= ctx.
          - { flag: --halogen-kv-pool, id: halogen-pool, default: "131072" }
          - { flag: --halogen-temperature, id: halogen-temp, default: "1.0" }
          - { flag: --halogen-mtp-depth, id: halogen-mtp-depth, default: "3" }
          # Server default only; a request's reasoning_effort overrides it.
          - {
              flag: --halogen-reasoning-effort,
              id: halogen-effort,
              default: xhigh,
            }
        env:
          HALOGEN_MODEL_ID: "{name}"
          HALOGEN_CTX: "{halogen-ctx}"
          HALOGEN_KV_POOL_POSITIONS: "{halogen-pool}"
          HALOGEN_TEMPERATURE: "{halogen-temp}"
          HALOGEN_MTP_DEPTH: "{halogen-mtp-depth}"
          HALOGEN_REASONING_EFFORT: "{halogen-effort}"
        ready: /v1/models
        stop_grace_secs: 90
        ready_timeout_secs: 600
presets:
  # 27B on gufo with the DFlash2 draft, model-card sampling. Give clients a
  # max_tokens of 16k or more, or long thinking ends in an empty answer.
  Qwen3.8-27B-UD-Q6_K.gguf:
    default: 256k
    entries:
      128k:
        server: generic-gufo-27b
        knobs: { context: 131072, temperature: "1.0" }
      256k:
        server: generic-gufo-27b
        knobs: { context: 262144, temperature: "1.0" }
  # Flash-Next GGUF on gufo with MTP. Stock llama.cpp can't load the qwen4exp MTP head.
  Qwen3.8-Flash-Next-*:
    default: 128k
    entries:
      128k:
        server: generic-gufo
        knobs: { context: 131072, speculative: mtp, temperature: "1.0" }
      256k:
        server: generic-gufo
        knobs: { context: 262144, speculative: mtp, temperature: "1.0" }
  # Halogen native v2, MTP depth 3, model-card sampling: the fastest Flash-Next
  # setup. The pool decides how many sessions stay resident; one that doesn't
  # fit re-reads its whole prompt every turn.
  flash-next-halogen:
    default: solo-256k
    entries:
      solo-256k:
        knobs: { halogen-ctx: 262144, halogen-pool: 262144 }
      dual-128k:
        knobs: { halogen-ctx: 131072, halogen-pool: 262144 }
      triple-128k:
        knobs: { halogen-ctx: 131072, halogen-pool: 393216 }
      dual-256k:
        knobs: { halogen-ctx: 262144, halogen-pool: 524288 }
      # Three full-length sessions; ~14.4 GiB less page cache than solo-256k.
      triple-256k:
        knobs: { halogen-ctx: 262144, halogen-pool: 786432 }
```

## 7. Verify

1. Run `llamastash list`. You should see:
   - the two GGUF rows with backend `llamacpp|generic`
   - a `flash-next-halogen` row with ctx 262144 and size 87G
   - `llamastash presets list <model>` showing the presets above
2. Test each engine one at a time. Start it, send one chat request through the proxy, then stop it:
   - `llamastash start flash-next-halogen --preset solo-256k`
   - `llamastash start Qwen3.8-Flash-Next-UD-Q4_K_XL --preset 128k`
   - `llamastash start Qwen3.8-27B-UD-Q6_K --preset 128k`
   - The proxy is at `http://$(llamastash status --json | jq -r .proxy.listen)/v1`. The model id is `<id>@<preset>`, as listed by `GET /v1/models`.
   - Stop with `llamastash stop <model>`. Wait for the stop to finish before starting the next engine.
3. Watch `llamastash logs <model>`. Expected:
   - Halogen cold load takes 85 to 105 s, or about 6 s when the weights are still in page cache. gufo takes about 22 s.
   - Halogen's startup log prints a memory line. If it warns about **compaction stalls**, run `echo 1 | sudo tee /proc/sys/vm/compact_memory` before the next start. Its log suggests this instead of a reboot.
   - After any unclean exit, check `cat /sys/class/drm/card*/device/mem_info_gtt_used`. Tens of GiB used with nothing running means the GPU kept the memory, and only a reboot frees it.
4. Reference numbers from an ASUS ROG Flow Z13 (Strix Halo 128 GB) at a 70 W TDP, 64k window with a 32k prompt, temperature 1.0 / top_p 0.95 / top_k 20. A desktop at 85 to 100 W should be faster.

| Engine                          | Prefill t/s | Decode t/s | Draft accept |
| ------------------------------- | ----------- | ---------- | ------------ |
| Halogen 0.15.1, v2              | 1,191       | 39.4       | 85%          |
| gufo 0.3.0, Flash-Next, ROCm 10 | 1,042       | 34.8       | 75%          |
| gufo, 27B UD-Q6_K + DFlash2     | 405         | 17 to 21   | 46%          |

Halogen 0.16.1 on the same laptop (performance profile, on AC), with a 30k-token wikitext prompt at the 256k window: prefill 1,276 to 1,362 t/s, decode 42.3 to 43.4 t/s, draft accept 78 to 81%. The prompt differs from the table's, so this is not a direct comparison.

## 8. Connect Pi, OpenCode and Claude Code

LlamaStash's `integrations` command registers every **favorite** model, one entry per preset, as `<id>@<preset>`. So mark the three rows as favorites first:

```bash
llamastash favorites add Qwen3.8-Flash-Next-UD-Q4_K_XL
llamastash favorites add Qwen3.8-27B-UD-Q6_K
llamastash favorites add flash-next-halogen
llamastash integrations pi opencode claude-code
```

- **Pi:** patches `~/.pi/agent/models.json` with a `llamastash` provider. The API key resolves through `!llamastash api-key`. Pick a model like `flash-next-halogen@solo-256k` in Pi.
- **OpenCode:** patches its config with the proxy URL and the same model list.
- **Claude Code:** writes `~/.config/llamastash/claude-code.sh` and doesn't touch `~/.claude/settings.json`. Run it with `source ~/.config/llamastash/claude-code.sh && claude`.
  - Use **Halogen** for Claude Code. Set both `ANTHROPIC_MODEL` and `ANTHROPIC_SMALL_FAST_MODEL` in that file to a Halogen preset id, such as `flash-next-halogen@solo-256k`.
  - gufo can't serve Claude Code: as of v0.5.0 its `/v1/messages` rejects tools, `thinking`, `output_config` and streaming.
- Add `codex`, `aider`, `continue`, `zed` or `env-sh` to the same command for other tools.
- Re-run `llamastash integrations ...` after you add favorites or presets.
- **Effort and image fields.** `integrations` writes them for every row: `reasoning`, plus `thinkingLevelMap` in Pi or `variants` in OpenCode, with levels off (`none`), low, medium and xhigh.
  - The gufo rows get theirs from the GGUF chat template, and image input from the `mmproj` file in step 3.
  - The Halogen row gets effort from the `reasoning_effort` fields in step 6 and no image input.
  - To check, open Pi's `/model` and pick a `flash-next-halogen@...` entry. Its thinking-level control should list off, low, medium and xhigh.
  - gufo needs `mmproj-BF16.gguf` for images. The integration only checks that some projector file is present, so a row can list image input while gufo still refuses images.


## Report back

Tell the user:

- the versions installed
- anything you changed from this guide, and why
- any host check that failed
- the prefill and decode numbers from step 7
