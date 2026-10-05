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
