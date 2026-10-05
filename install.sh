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
