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
