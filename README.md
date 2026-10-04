# Running Qwen3 Models with llama-server (Embedding + Reranking + Chat)

A practical guide to running multiple Qwen3 models through a single `llama-server` instance using model routing. Covers embedding, reranking, and chat/vision models.

Tested on Windows with RTX 3090 (24GB VRAM), `llama-server` build from [llama.cpp](https://github.com/ggml-org/llama.cpp) master branch. Last updated: 2025-03-09.

---

## What you get

One server process, one port, three model types. The router swaps models in/out of VRAM automatically:

```
POST /v1/embeddings         → Qwen3-Embedding-4B
POST /v1/rerank             → Qwen3-Reranker-4B
POST /v1/chat/completions   → Qwen3-VL-8B-Instruct
```

With `--models-max 1`, only one model sits in VRAM at a time. The router unloads the current model and loads the requested one on demand (~2-5s swap time on NVMe).

---

## Step 1: Get the models

| Model | Purpose | GGUF source | Size |
|-------|---------|-------------|------|
| Qwen3-Embedding-4B | Embeddings | [Qwen/Qwen3-Embedding-4B-GGUF](https://huggingface.co/Qwen/Qwen3-Embedding-4B-GGUF) (official) | ~8 GB (F16) |
| Qwen3-VL-8B-Instruct | Chat + Vision | [Qwen/Qwen3-VL-8B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF) (official) | ~16 GB (F16) |

**Qwen3 Reranker GGUFs** — pick the size that fits your VRAM:

| Model | GGUF (working, for llama.cpp) | Size |
|-------|-------------------------------|------|
| Qwen3-Reranker-0.6B | [Voodisss/Qwen3-Reranker-0.6B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-0.6B-GGUF-llama_cpp) | ~1.2 GB (F16) |
| Qwen3-Reranker-4B | [Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp) | ~8 GB (F16) |
| Qwen3-Reranker-8B | [Voodisss/Qwen3-Reranker-8B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-8B-GGUF-llama_cpp) | ~16 GB (F16) |

> **⚠️ Reranker warning:** Do NOT use community-converted Qwen3-Reranker GGUFs (e.g., DevQuasar, mradermacher). They are broken — missing the `cls.output.weight` classifier tensor, `pooling_type=RANK` metadata, and the rerank chat template. This produces near-zero garbage scores like `4.5e-23`. See [llama.cpp #16407](https://github.com/ggml-org/llama.cpp/issues/16407). The GGUFs linked above are converted with the official `convert_hf_to_gguf.py` and work correctly.

---

## Step 2: Create `models.ini`

This file defines all models and their settings. Put it wherever you like.

```ini
[*]
# Global defaults — applied to every model unless overridden
n-gpu-layers = all
batch-size = 2048
ubatch-size = 2048

# Pre-load the embedding model on server start
load-on-startup = Qwen3-Embedding-4B-f16

[Qwen3-Embedding-4B-f16]
model = C:\path\to\Qwen3-Embedding-4B-f16.gguf
embedding = true
pooling = mean
ctx-size = 32768

[Qwen3-Reranker-4B-f16]
model = C:\path\to\Qwen3-Reranker-4B-f16.gguf
reranking = true
pooling = rank
embedding = true
ctx-size = 32768

[Qwen3-VL-8B-Instruct-F16]
model = C:\path\to\Qwen3VL-8B-Instruct-F16.gguf
ctx-size = 32768
mmproj = C:\path\to\mmproj-Qwen3VL-8B-Instruct-F16.gguf
```

**The section name (e.g. `Qwen3-Reranker-4B-f16`) is the model name you pass in API calls.** It can be anything — it doesn't need to match the filename.

---

## Step 3: Start the server

```bash
llama-server \
    --host 127.0.0.1 \
    --port 8081 \
    --metrics \
    --models-max 1 \
    --models-preset models.ini
```

| Flag | What it does |
|------|-------------|
| `--models-max 1` | Keep 1 model loaded at a time. Set higher if you have VRAM for multiple. `0` = unlimited. |
| `--models-preset models.ini` | Load model definitions from INI file |
| `--metrics` | Enable Prometheus metrics at `/metrics` |

---

## Step 4: Call the APIs

### Embeddings → `/v1/embeddings`

```bash
curl http://localhost:8081/v1/embeddings \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen3-Embedding-4B-f16",
    "input": ["Your text to embed"]
  }'
```

Returns a 2560-dimensional vector per input, L2-normalized.

### Reranking → `/v1/rerank`

```bash
curl http://localhost:8081/v1/rerank \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen3-Reranker-4B-f16",
    "query": "employment termination notice period",
    "documents": [
      "The Labour Code requires 30 calendar days written notice.",
      "Corporate tax rates for small enterprises."
    ]
  }'
```

Returns `relevance_score` per document (0.0–1.0). Results sorted by score descending.

> **Use `/v1/rerank`, not `/v1/embeddings`.** The embeddings endpoint will never call the rerank pipeline — it returns garbage for reranker models.

Also accepts TEI format with `"texts"` instead of `"documents"`.

### Chat → `/v1/chat/completions`

```bash
curl http://localhost:8081/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen3-VL-8B-Instruct-F16",
    "messages": [
      {"role": "user", "content": "Hello!"}
    ],
    "max_tokens": 128
  }'
```

Standard OpenAI-compatible chat completions. Supports streaming with `"stream": true`.

---

## Reranker model settings explained

The reranker needs exactly three flags in `models.ini`:

```ini
reranking = true    # Enables the /v1/rerank endpoint for this model
pooling = rank      # Sets LLAMA_POOLING_TYPE_RANK (required for classifier output)
embedding = true    # Enables embedding extraction (reranking needs this internally)
```

Without all three, `/v1/rerank` returns: `"This server does not support reranking"`.

### How Qwen3 reranking works internally

It's a **generative reranker**, not a traditional cross-encoder:

1. Server formats query + document using the model's baked-in chat template
2. Model processes the prompt and produces logits
3. `cls.output.weight` (a `[hidden_dim, 2]` tensor) projects the final hidden state to 2 scores: P(yes) and P(no)
4. Softmax → `relevance_score = P(yes)`

This is why the GGUF must contain `cls.output.weight` — without it, there's nothing to produce scores from.

---

## Converting your own Qwen3-Reranker GGUF

If you want to convert from the HuggingFace source model yourself:

```bash
# 1. Download source model
pip install huggingface_hub
python -c "from huggingface_hub import snapshot_download; snapshot_download('Qwen/Qwen3-Reranker-4B', local_dir='Qwen3-Reranker-4B-src')"

# 2. Convert (needs: pip install gguf torch safetensors sentencepiece)
python convert_hf_to_gguf.py \
    --outtype f16 \
    --outfile Qwen3-Reranker-4B-f16.gguf \
    Qwen3-Reranker-4B-src/

# 3. Optional: quantize to Q8_0 (~4 GB instead of ~8 GB)
llama-quantize Qwen3-Reranker-4B-f16.gguf Qwen3-Reranker-4B-q8_0.gguf Q8_0
```

The converter automatically detects Qwen3-Reranker and extracts the classifier tensor + metadata. Takes ~2 minutes, ~16 GB RAM.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Rerank scores are `0.0` or `4.5e-23` | GGUF missing `cls.output.weight` (bad conversion) | Use the [properly converted GGUF](https://huggingface.co/Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp) or convert yourself |
| `"This server does not support reranking"` | Missing `reranking = true` and/or `pooling = rank` in preset | Add all three flags: `reranking = true`, `pooling = rank`, `embedding = true` |
| `"model not found"` | Model name in API call doesn't match section name in INI | The `"model"` field must exactly match the `[section-name]` |
| Model swap takes too long | Large model on slow disk | Use NVMe. Or increase `--models-max` if you have VRAM for multiple models |
| Embeddings endpoint returns zeros for reranker | Wrong endpoint | Use `/v1/rerank`, not `/v1/embeddings` |

---

<details>
<summary><strong>Full models.ini reference (all valid keys)</strong></summary>

### How `models.ini` works

- **`[*]`** = global defaults, applied to every model. Model-specific keys override globals.
- **`[section-name]`** = defines a model. The section name becomes the model name in API calls.
- Comments: lines starting with `#` or `;`
- Boolean values: `true`/`false`, `on`/`off`, `1`/`0`, `enabled`/`disabled`
- Keys are CLI flags without `--`. Example: `--n-gpu-layers all` → `n-gpu-layers = all`
- Negated flags work too: `no-mmap = true` is the same as `mmap = false`

### Preset-only keys (no CLI equivalent)

These keys only work inside `models.ini` — they have no `--` CLI flag equivalent.

#### `load-on-startup`

Which model to load into VRAM immediately when the server starts. The value must match a `[section-name]` exactly. Without this, no model is loaded until the first API request comes in.

```ini
[*]
load-on-startup = Qwen3-Embedding-4B-f16

[Qwen3-Embedding-4B-f16]
model = /path/to/embedding.gguf
# This model will be loaded and ready before any request arrives
```

If omitted, the first API call triggers a cold-load (~3-10s delay on that first request).

#### `stop-timeout`

How many seconds the router waits for a model to gracefully unload before force-killing it. Default: `10`. Increase if you have very large models that take a while to flush from VRAM.

```ini
[*]
stop-timeout = 30    # Give models 30 seconds to unload gracefully
```

### Model source

Where to find the GGUF file. You need exactly one of `model`, `model-url`, or `hf-repo`.

#### `model`

Absolute or relative path to a `.gguf` file on disk. This is the most common option.

```ini
[my-model]
model = C:\Users\Me\models\Qwen3-Embedding-4B-f16.gguf     # Windows
model = /home/me/models/Qwen3-Embedding-4B-f16.gguf        # Linux
```

#### `model-url`

Direct download URL. The server downloads the file on first load and caches it locally.

```ini
[my-model]
model-url = https://huggingface.co/Qwen/Qwen3-Embedding-4B-GGUF/resolve/main/Qwen3-Embedding-4B-f16.gguf
```

#### `hf-repo`

HuggingFace repo in `user/model` format. Optionally append `:quant` to pick a specific quantization. The server downloads and caches automatically.

```ini
[my-model]
hf-repo = Qwen/Qwen3-Embedding-4B-GGUF           # Downloads default file
hf-repo = Qwen/Qwen3-Embedding-4B-GGUF:Q8_0      # Downloads the Q8_0 quant
```

#### `hf-file`

When a HuggingFace repo contains multiple GGUF files and `hf-repo` alone is ambiguous, use this to specify exactly which file to download.

```ini
[my-model]
hf-repo = Qwen/Qwen3-Embedding-4B-GGUF
hf-file = Qwen3-Embedding-4B-Q4_K_M.gguf
```

#### `alias`

Extra names that can be used in API calls to refer to this model. Comma-separated. The section name always works as an alias — this adds more.

```ini
[Qwen3-Embedding-4B-f16]
model = /path/to/embedding.gguf
alias = embed, embedding-model
# Now all three work in API calls: "Qwen3-Embedding-4B-f16", "embed", "embedding-model"
```

### Context and batching

#### `ctx-size`

Maximum number of tokens the model can process in a single context window. `0` means "use whatever the model was trained with" (e.g., 40960 for Qwen3-4B). Setting this explicitly lets you reduce VRAM usage or extend context with RoPE scaling.

```ini
ctx-size = 0         # Use model default (safest)
ctx-size = 8192      # Limit to 8K tokens (saves VRAM)
ctx-size = 36864     # Extended context (needs override-kv to inform the model)
```

Higher values = more VRAM for the KV cache. On a 24GB GPU, `36864` works for 4B models but may OOM for 8B+ models.

#### `batch-size`

Maximum number of tokens the server processes in one logical batch during prompt ingestion. Larger = faster prompt processing, but more VRAM. Must be ≥ 32 to use GPU BLAS acceleration. Default: `2048`.

```ini
batch-size = 2048    # Good default for most GPUs
batch-size = 512     # Reduce if running out of VRAM during long prompt ingestion
batch-size = 4096    # Increase if you have VRAM headroom and want faster prefill
```

#### `ubatch-size`

Physical batch size — the actual number of tokens sent to the GPU in one compute call. This is a subdivision of `batch-size`. Smaller values reduce peak VRAM spikes but slow down processing. Must be ≤ `batch-size`. Default: `512`.

```ini
ubatch-size = 512    # Default, good balance
ubatch-size = 2048   # Match batch-size for maximum throughput (needs more VRAM)
ubatch-size = 128    # Very conservative, for tight VRAM situations
```

#### `parallel`

Number of concurrent request slots. Each slot holds one active request and consumes its own KV cache allocation. `-1` = auto (typically 1). More slots = more concurrent requests but multiplies VRAM usage by N.

```ini
parallel = 1         # One request at a time (safest for single-user)
parallel = 4         # Handle 4 concurrent requests (needs 4x KV cache VRAM)
parallel = -1        # Auto-detect (usually 1)
```

#### `cont-batching`

Continuous batching allows the server to start processing a new request while an existing one is still generating tokens. Improves throughput for concurrent users. Enabled by default. Only disable if you need strict sequential processing.

```ini
cont-batching = true     # Default, recommended
cont-batching = false    # Disable (one request must fully complete before the next starts)
```

### GPU and memory

#### `n-gpu-layers`

How many transformer layers to offload to GPU. More layers on GPU = faster inference, more VRAM used. Accepts a number, `auto`, or `all`.

```ini
n-gpu-layers = all       # Offload everything to GPU (fastest, needs enough VRAM)
n-gpu-layers = auto      # Let llama.cpp decide based on available VRAM
n-gpu-layers = 20        # Offload only 20 layers (rest stays in RAM — slower but fits)
n-gpu-layers = 0         # CPU-only inference (no GPU used at all)
```

For a 4B model (~8 GB) on a 24 GB GPU, `all` is fine. For an 8B model (~16 GB), `all` may work but leaves less room for KV cache.

#### `split-mode`

How to distribute model weights across multiple GPUs. Only relevant if you have 2+ GPUs.

```ini
split-mode = layer       # Default. Each GPU gets a range of layers. Simple, works well.
split-mode = row         # Split individual tensor rows across GPUs. Better for unequal GPUs.
split-mode = none        # Use only one GPU (the one specified by main-gpu).
```

#### `tensor-split`

When using multi-GPU, this sets the proportion of work each GPU gets. Comma-separated floats. Only meaningful with `split-mode = layer` or `row`.

```ini
# Two GPUs: first GPU gets 60% of layers, second gets 40%
tensor-split = 0.6,0.4

# Three GPUs: equal split
tensor-split = 1,1,1
```

#### `main-gpu`

Which GPU (by index) to use as the primary device. GPU 0 is the first one. Only matters for multi-GPU setups or `split-mode = none`.

```ini
main-gpu = 0     # Use first GPU (default)
main-gpu = 1     # Use second GPU as primary
```

#### `mmap`

Memory-map the GGUF file instead of reading it all into RAM. Enabled by default. Faster startup, lower RAM usage, but the OS may page parts in/out. Disable if you want the entire model pinned in RAM.

```ini
mmap = true      # Default. Fast startup, OS manages paging.
mmap = false     # Read entire file into RAM at load time. Slower start, no paging.
```

#### `mlock`

Lock the model weights in RAM so the OS never swaps them to disk. Requires sufficient RAM. Use with `mmap = false` for maximum stability.

```ini
mlock = false    # Default. OS may swap model to disk under memory pressure.
mlock = true     # Pin model in RAM. Prevents swapping but requires enough free RAM.
```

#### `no-host`

Bypass the host (CPU RAM) buffer and load model weights directly to GPU. Reduces peak RAM usage during loading. Only useful when offloading all layers to GPU.

```ini
no-host = false  # Default. Model weights pass through CPU RAM on the way to GPU.
no-host = true   # Skip CPU RAM buffer. Saves RAM but only works with n-gpu-layers = all.
```

#### `direct-io`

Use OS-level direct I/O when reading the GGUF file, bypassing the filesystem cache. Can reduce memory pressure on systems with limited RAM but may be slower.

```ini
direct-io = false    # Default. Normal file I/O with OS caching.
direct-io = true     # Bypass OS file cache. Saves RAM, may be slower on first load.
```

#### `flash-attn`

Flash Attention reduces VRAM usage and speeds up attention computation. `auto` enables it when the GPU supports it.

```ini
flash-attn = auto    # Default. Enable if GPU supports it, otherwise fall back.
flash-attn = on      # Force enable (crashes if GPU doesn't support it).
flash-attn = off     # Force disable (uses more VRAM, but always works).
```

### KV cache

The KV (key-value) cache stores intermediate attention states for each token in the context. It's the main consumer of VRAM beyond model weights. These settings control its memory format and placement.

#### `cache-type-k` / `cache-type-v`

Quantization type for the K and V caches. Lower precision = less VRAM per token but slightly reduced quality. Common values: `f16` (default, best quality), `q8_0` (half the VRAM, minimal quality loss), `q4_0` (quarter VRAM, noticeable quality loss).

```ini
cache-type-k = f16       # Default. Best quality, most VRAM.
cache-type-v = f16

cache-type-k = q8_0      # Good tradeoff: halves KV cache VRAM, almost no quality loss.
cache-type-v = q8_0

cache-type-k = q4_0      # Aggressive: quarter VRAM, some quality degradation on long contexts.
cache-type-v = q4_0
```

For a 4B model with `ctx-size = 36864`: KV cache at f16 ≈ 2.3 GB, at q8_0 ≈ 1.2 GB, at q4_0 ≈ 0.6 GB.

#### `kv-offload`

Whether to store the KV cache on GPU (fast) or CPU RAM (slow but saves VRAM). Default: `true` (GPU).

```ini
kv-offload = true    # Default. KV cache on GPU. Fast inference.
kv-offload = false   # KV cache in CPU RAM. Much slower but frees VRAM for larger models.
```

### Embedding and reranking

#### `embedding`

Enables the `/v1/embeddings` endpoint for this model. Required for embedding models and reranker models. Without this, the server rejects embedding/rerank requests.

```ini
embedding = true     # Enable embedding output. Required for embedding and reranker models.
embedding = false    # Default. Only chat completions work.
```

#### `pooling`

How to reduce per-token hidden states into a single vector. Different model types need different pooling.

```ini
pooling = mean       # Average all token embeddings. Standard for embedding models (Qwen3-Embedding).
pooling = cls        # Use only the [CLS] token embedding. Common for BERT-style models.
pooling = last       # Use the last token's embedding. Used by some decoder-based embedding models.
pooling = rank       # Classifier mode for rerankers. Produces a relevance score, not a vector.
pooling = none       # No pooling — return per-token embeddings (rarely needed).
```

**For Qwen3:** use `mean` for Qwen3-Embedding, use `rank` for Qwen3-Reranker.

#### `reranking`

Shortcut that enables both `embedding = true` and `pooling = rank` in one flag. Also enables the `/v1/rerank` endpoint.

```ini
reranking = true     # Equivalent to: embedding = true + pooling = rank
                     # Enables /v1/rerank endpoint
```

You can set all three explicitly if you prefer clarity:

```ini
reranking = true
pooling = rank
embedding = true
```

#### `embd-normalize`

How embedding vectors are normalized before being returned. Only applies to embedding models (not rerankers).

```ini
embd-normalize = 2       # Default. L2 (Euclidean) normalization. Vectors have unit length.
embd-normalize = 1       # L1 (taxicab) normalization.
embd-normalize = 0       # Normalize to max absolute int16 value.
embd-normalize = -1      # No normalization. Return raw embedding values.
```

For similarity search (cosine similarity), use `2` (default). This ensures all vectors are unit-length so dot product = cosine similarity.

### Fit (auto-adjust to available memory)

The "fit" system automatically adjusts `n-gpu-layers` and `ctx-size` to fit within available VRAM. Useful when you're not sure what your GPU can handle.

#### `fit`

Master switch for auto-fitting. When `on`, llama.cpp probes available VRAM and adjusts settings to fit. When `off`, it uses your settings exactly (and crashes if they don't fit).

```ini
fit = on         # Default. Auto-adjust layers and context to fit VRAM.
fit = off        # Use my settings exactly. I know they fit.
```

Set `off` when you've already tuned your settings and don't want the server second-guessing you.

#### `fit-target`

How much free VRAM (in MiB) to leave after loading the model. The fit system will reduce layers/context to maintain this margin. Comma-separated for multi-GPU.

```ini
fit-target = 0       # Use all available VRAM, leave nothing free.
fit-target = 500     # Leave 500 MiB free (for other apps, OS overhead, etc.)
fit-target = 1000,500   # Multi-GPU: 1000 MiB margin on GPU 0, 500 MiB on GPU 1.
```

#### `fit-ctx`

The minimum context size that `fit` is allowed to shrink to. Prevents the auto-fitter from reducing context to something unusably small.

```ini
fit-ctx = 2048       # Don't shrink context below 2048 tokens, even if VRAM is tight.
fit-ctx = 8192       # Ensure at least 8K context is always available.
```

### Multimodal

For vision-language models (like Qwen3-VL) that process images alongside text. These models require a separate "projector" GGUF that converts image features into tokens the language model understands.

#### `mmproj`

Path to the multimodal projector GGUF file. Required for vision models. Without this, the model loads as text-only and image inputs are ignored or cause errors.

```ini
[Qwen3-VL-8B-Instruct-F16]
model = /path/to/Qwen3VL-8B-Instruct-F16.gguf
mmproj = /path/to/mmproj-Qwen3VL-8B-Instruct-F16.gguf
```

The projector file is usually much smaller than the main model (~500 MB–1 GB).

#### `mmproj-offload`

Whether to load the multimodal projector onto GPU. Default: depends on build. Offloading speeds up image processing.

```ini
mmproj-offload = true    # Load projector on GPU. Faster image processing, uses some VRAM.
mmproj-offload = false   # Keep projector on CPU. Saves VRAM, slower image processing.
```

### Metadata override

#### `override-kv`

Override metadata values baked into the GGUF file. Format: `KEY=TYPE:VALUE`. Types: `int`, `float`, `bool`, `str`. Multiple overrides separated by commas.

This is commonly used to extend context length beyond what the model advertises, or to tweak tokenizer behavior.

```ini
# Extend Qwen3's advertised context length to 36864
override-kv = qwen3.context_length=int:36864

# Multiple overrides at once
override-kv = qwen3.context_length=int:36864,tokenizer.ggml.add_bos_token=bool:false

# Disable BOS/EOS token insertion (useful for some fine-tuned models)
override-kv = tokenizer.ggml.add_bos_token=bool:false,tokenizer.ggml.add_eos_token=bool:false
```

**Common Qwen3 override keys:**
- `qwen3.context_length=int:N` — advertised context length for Qwen3 text models
- `qwen3vl.context_length=int:N` — advertised context length for Qwen3-VL vision models

> **Important:** `override-kv` only changes metadata the server reads — it doesn't magically make the model support longer context. The model must have been trained or fine-tuned to handle the extended length (Qwen3 supports up to 128K with YaRN scaling).

### Server behavior

#### `timeout`

Read/write timeout in seconds for HTTP connections. If a request takes longer than this, it's killed. Increase for very long generations or large batch embeddings.

```ini
timeout = 120        # 2 minutes (reasonable default for most use cases)
timeout = 600        # 10 minutes (for very long chat generations or huge embedding batches)
```

#### `threads` / `threads-batch`

Number of CPU threads used during inference. `threads` is for token generation, `threads-batch` is for prompt ingestion (prefill). Auto-detected by default. Only matters for layers running on CPU.

```ini
threads = 8          # Use 8 CPU threads for generation
threads-batch = 12   # Use 12 CPU threads for prompt processing (can be higher since prefill is parallelizable)
```

If all layers are on GPU (`n-gpu-layers = all`), these have minimal effect.

#### `cache-prompt`

Cache the tokenized prompt so that follow-up requests with the same prefix skip re-tokenization and re-processing. Enabled by default. Speeds up multi-turn conversations significantly.

```ini
cache-prompt = true      # Default. Reuse KV cache from previous turns.
cache-prompt = false     # Reprocess every prompt from scratch. Wastes compute but saves VRAM.
```

#### `metrics`

Enable the Prometheus-compatible `/metrics` endpoint for monitoring. Exposes request counts, latencies, tokens/second, VRAM usage, etc.

```ini
metrics = true       # Enable /metrics endpoint
metrics = false      # Default. No metrics endpoint.
```

### RoPE (advanced context extension)

RoPE (Rotary Position Embedding) settings control how the model handles positional information. These are only needed when extending context beyond what the model was trained with, or when the GGUF metadata doesn't already include the right RoPE settings.

**Most users don't need to touch these.** The GGUF usually contains correct RoPE parameters.

#### `rope-scaling`

The algorithm used to extend context length beyond training.

```ini
rope-scaling = none      # No scaling. Use the model's native context length.
rope-scaling = linear    # Simple linear interpolation. Quick and dirty, some quality loss.
rope-scaling = yarn      # YaRN scaling. Better quality at extended lengths. Recommended for Qwen3.
```

#### `rope-scale`

Context scaling factor. For example, `2.0` means "try to handle 2x the trained context length." Only used with `rope-scaling = linear`.

```ini
rope-scale = 1.0         # No scaling (default)
rope-scale = 2.0         # Double the effective context (e.g., 32K → 64K)
rope-scale = 4.0         # Quadruple (e.g., 32K → 128K, with quality degradation)
```

#### `rope-freq-base`

Base frequency for RoPE computation. Higher values spread position information across more dimensions, enabling longer contexts. The model's GGUF usually sets this correctly.

```ini
rope-freq-base = 10000       # Standard default for most models
rope-freq-base = 1000000     # Extended context (some models set this automatically)
```

#### `rope-freq-scale`

Frequency scaling factor. Inverse of `rope-scale` — lower values = longer context. Rarely set manually.

```ini
rope-freq-scale = 1.0        # Default, no scaling
rope-freq-scale = 0.25       # Equivalent to rope-scale = 4.0
```

</details>

<details>
<summary><strong>Full working models.ini example (RTX 3090, 24 GB VRAM)</strong></summary>

```ini
[*]
# ── Global defaults for RTX 3090 ──────────────────────────
n-gpu-layers = all
batch-size = 2048
ubatch-size = 2048
fit = off
fit-target = 0
mmap = false
direct-io = false
no-host = true

# Auto-load embedding model on server start
load-on-startup = Qwen3-Embedding-4B-f16

# ── Embedding model ───────────────────────────────────────
[Qwen3-Embedding-4B-f16]
model = C:\Users\UserName\AppData\Local\llama.cpp\Qwen_Qwen3-Embedding-4B-GGUF_Qwen3-Embedding-4B-f16.gguf
embedding = true
pooling = mean
ctx-size = 36864
override-kv = qwen3.context_length=int:36864

# ── Reranker model ────────────────────────────────────────
# GGUF from: https://huggingface.co/Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp
[Qwen3-Reranker-4B-f16]
model = C:\Users\UserName\AppData\Local\llama.cpp\Qwen3-Reranker-4B-f16.gguf
reranking = true
pooling = rank
embedding = true
ctx-size = 36864
override-kv = qwen3.context_length=int:36864

# ── Chat / Vision model ──────────────────────────────────
[Qwen3-VL-8B-Instruct-F16]
model = C:\Users\UserName\AppData\Local\llama.cpp\Qwen_Qwen3-VL-8B-Instruct-GGUF_Qwen3VL-8B-Instruct-F16.gguf
ctx-size = 36864
override-kv = qwen3vl.context_length=int:36864
mmproj = C:\Users\UserName\AppData\Local\llama.cpp\Qwen_Qwen3-VL-8B-Instruct-GGUF_mmproj-Qwen3VL-8B-Instruct-F16.gguf
mmproj-offload = true
```

</details>

---

## Links

**Working Qwen3-Reranker GGUFs (converted with official `convert_hf_to_gguf.py`):**
- [Voodisss/Qwen3-Reranker-0.6B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-0.6B-GGUF-llama_cpp) — 0.6B reranker (~1.2 GB)
- [Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp) — 4B reranker (~8 GB)
- [Voodisss/Qwen3-Reranker-8B-GGUF-llama_cpp](https://huggingface.co/Voodisss/Qwen3-Reranker-8B-GGUF-llama_cpp) — 8B reranker (~16 GB)

**Official Qwen3 models:**
- [Qwen/Qwen3-Reranker-0.6B](https://huggingface.co/Qwen/Qwen3-Reranker-0.6B) — Original 0.6B reranker (source weights)
- [Qwen/Qwen3-Reranker-4B](https://huggingface.co/Qwen/Qwen3-Reranker-4B) — Original 4B reranker (source weights)
- [Qwen/Qwen3-Reranker-8B](https://huggingface.co/Qwen/Qwen3-Reranker-8B) — Original 8B reranker (source weights)
- [Qwen/Qwen3-Embedding-4B-GGUF](https://huggingface.co/Qwen/Qwen3-Embedding-4B-GGUF) — Official embedding GGUF
- [Qwen/Qwen3-VL-8B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF) — Official chat/vision GGUF

**References:**
- [llama.cpp](https://github.com/ggml-org/llama.cpp) — The inference engine
- [llama.cpp #16407](https://github.com/ggml-org/llama.cpp/issues/16407) — Why community reranker GGUFs are broken
