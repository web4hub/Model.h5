curl http://localhost:8081/v1/embeddings \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen3-Embedding-4B-f16",
    "input": ["Your text to embed"]
  }'
