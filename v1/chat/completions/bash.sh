curl http://localhost:8081/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen3-VL-8B-Instruct-F16",
    "messages": [
      {"role": "user", "content": "Hello!"}
    ],
    "max_tokens": 4028
  }'
