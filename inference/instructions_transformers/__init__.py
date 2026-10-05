from sentence_transformers import CrossEncoder

model = CrossEncoder("Voodisss/Qwen3-Reranker-4B-GGUF-llama_cpp")

query = "What is the capital of China?"
documents = [
    "The capital of China is Beijing.",
    "Gravity is a force that attracts two bodies towards each other. It gives weight to physical objects and is responsible for the movement of planets around the sun.",
]

pairs = [(query, doc) for doc in documents]
scores = model.predict(pairs)
print(scores)
# [  5.0625 -14.25  ]

rankings = model.rank(query, documents)
print(rankings)
# [{'corpus_id': 0, 'score': 5.0625}, {'corpus_id': 1, 'score': -14.25}]

