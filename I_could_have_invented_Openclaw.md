See more of my writing [here](https://x.com/dabit3/articles). Also check out [Devin](https://x.com/dabit3/status/2037183640002601292?s=20)

In this post, I'll start from scratch and build up to OpenClaw's architecture step by step, showing how you could have invented it yourself from first principles, using nothing but a messaging API, an LLM, and the desire to make AI actually useful *outside* the chat window.

End goal: understand how persistent AI assistants work, so you can build your own (or become an OpenClaw power user).

## First, let's establish the problem

When you use ChatGPT or Claude in a browser, there are several limitations:

It's stateless. Every conversation starts from zero. It doesn't know your name, your preferences, what you asked yesterday, or what project you're working on. You're constantly re-explaining context.

It's passive. You go to it. It never comes to you. It can't wake up at 7am and brief you on your calendar, monitor your email, or run a recurring task. It only works when you're sitting in front of it.

It's isolated. It can't run commands on your machine, browse the web for you, control your apps, or send messages on your behalf. It lives in a text box with no hands.

It's single-channel. Your real life happens across WhatsApp, Telegram, Discord, Slack, iMessage - but the AI lives in its own separate tab. There's no way to text it where you already are, let alone have it maintain one continuous memory across all those surfaces.

What if instead, you had an AI that:

- Lived in the messaging apps you already use - all of them, with shared memory
- Remembered your preferences, your projects, and your past conversations across sessions
- Could run commands on your computer, browse the web, and control a real browser
- Woke up on a schedule to handle recurring tasks without being asked
- Ran on your own hardware - your laptop, a VPS, a Mac Mini - always on, under your control

This is what OpenClaw does. It's not a chatbot - it's a personal AI assistant with a persistent identity, tools, and presence across every channel you use.

Let's build one from scratch.

## The Simplest Possible Bot

Let's start with the absolute minimum: an AI that responds to messages on Telegram.

```python
# bot-v0.py - The simplest possible AI bot
import os
import anthropic
from telegram import Update
from telegram.ext import Application, MessageHandler, filters

client = anthropic.Anthropic()

async def handle_message(update: Update, context):
    user_message = update.message.text

    response = client.messages.create(
        model="claude-sonnet-4-5-20250929",
        max_tokens=1024,
        messages=[{"role": "user", "content": user_message}]
    )

    await update.message.reply_text(response.content[0].text)

app = Application.builder().token(os.getenv("TELEGRAM_BOT_TOKEN")).build()
app.add_handler(MessageHandler(filters.TEXT, handle_message))
app.run_polling()
```

Run it, send a message on Telegram, and the AI responds. Simple.

But this is basically a worse version of the Claude web interface. Every message is independent. No memory. No tools. No personality.

What if we gave it memory?

## Goal: Persistent Sessions

A problem with our simple bot is statelessness. Every message is a fresh conversation. Ask it "what did I say earlier?" and it has no idea.

The fix is sessions. Keep a conversation history per user.

```python
# bot-v1.py - Bot with persistent sessions
import json
import os
import anthropic
from telegram import Update
from telegram.ext import Application, MessageHandler, filters

client = anthropic.Anthropic()
SESSIONS_DIR = "./sessions"
os.makedirs(SESSIONS_DIR, exist_ok=True)

def get_session_path(user_id):
    return os.path.join(SESSIONS_DIR, f"{user_id}.jsonl")

def load_session(user_id):
    """Load conversation history from disk."""
    path = get_session_path(user_id)
    messages = []
    if os.path.exists(path):
        with open(path, "r") as f:
            for line in f:
                if line.strip():
                    messages.append(json.loads(line))
    return messages

def append_to_session(user_id, message):
    """Append a single message to the session file."""
    path = get_session_path(user_id)
    with open(path, "a") as f:
        f.write(json.dumps(message) + "\n")

def save_session(user_id, messages):
    """Overwrite the session file with the full message list."""
    path = get_session_path(user_id)
    with open(path, "w") as f:
        for message in messages:
            f.write(json.dumps(message) + "\n")

async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)
    user_message = update.message.text

    # Load existing conversation
    messages = load_session(user_id)

    # Add new user message
    user_msg = {"role": "user", "content": user_message}
    messages.append(user_msg)
    append_to_session(user_id, user_msg)

    # Call the AI with full history
    response = client.messages.create(
        model="claude-sonnet-4-5-20250929",
        max_tokens=4096,
        messages=messages
    )

    # Save assistant response
    assistant_msg = {"role": "assistant", "content": response.content[0].text}
    append_to_session(user_id, assistant_msg)

    await update.message.reply_text(response.content[0].text)

app = Application.builder().token(os.getenv("TELEGRAM_BOT_TOKEN")).build()
app.add_handler(MessageHandler(filters.TEXT, handle_message))
app.run_polling()
```

Now you can have an actual conversation:

```
You: My name is Nader
Bot: Nice to meet you, Nader!

[hours later...]

You: What's my name?
Bot: Your name is Nader!
```

The key insight is the JSONL format. Each line is one message. Append-only. If the process crashes mid-write, you lose at most one line. This is exactly what OpenClaw uses for session transcripts:

```
~/.openclaw/agents/<agentId>/sessions/<sessionId>.jsonl
```

Each session maps to a file. Each file is a conversation. Restart the process and everything is still there.

But we'll hit a problem: conversations grow. Eventually they'll exceed the model's context window. We'll come back to that.

## Goal: Adding a Personality (SOUL.md)

Our bot works, but it has no personality. It's a generic AI assistant. What if we wanted it to be *someone*?

OpenClaw solves this with `SOUL.md`: a markdown file that defines the agent's identity, behavior, and boundaries.

```python
# bot-v2.py - Bot with personality

SOUL = """
# Who You Are

**Name:** Jarvis
**Role:** Personal AI assistant

## Personality
- Be genuinely helpful, not performatively helpful
- Skip the "Great question!" - just help
- Have opinions. You're allowed to disagree
- Be concise when needed, thorough when it matters

## Boundaries
- Private things stay private
- When in doubt, ask before acting externally
- You're not the user's voice - be careful about sending messages on their behalf

## Memory
Remember important details from conversations.
Write them down if they matter.
"""

async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)
    messages = load_session(user_id)

    user_msg = {"role": "user", "content": update.message.text}
    messages.append(user_msg)
    append_to_session(user_id, user_msg)

    response = client.messages.create(
        model="claude-sonnet-4-5-20250929",
        max_tokens=4096,
        system=SOUL,  # <-- personality injected here
        messages=messages
    )

    assistant_msg = {"role": "assistant", "content": response.content[0].text}
    append_to_session(user_id, assistant_msg)

    await update.message.reply_text(response.content[0].text)
```

Now instead of a generic assistant, you're talking to Jarvis. The SOUL gets injected as the system prompt on every API call.

In OpenClaw, the SOUL.md lives in the agent's workspace:

```
~/.openclaw/workspace/SOUL.md
```

It gets loaded at session start and injected into the system prompt. You can write anything you want in there. Give the agent an origin story. Define its core philosophy. List its behavioral rules.

The more specific your SOUL, the more consistent the agent's behavior. "Be helpful" is vague. "Be the assistant you'd actually want to talk to. Concise when needed, thorough when it matters. Not a corporate drone. Not a sycophant. Just... good." - that gives the model something to work with.

## Goal: Adding Tools

A bot that can only talk is limited. What if it could *do things*?

The core idea: give the AI structured tools and let it decide when to use them.

```python
import subprocess

TOOLS = [
    {
        "name": "run_command",
        "description": "Run a shell command on the user's computer",
        "input_schema": {
            "type": "object",
            "properties": {
                "command": {"type": "string", "description": "The command to run"}
            },
            "required": ["command"]
        }
    },
    {
        "name": "read_file",
        "description": "Read a file from the filesystem",
        "input_schema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path to the file"}
            },
            "required": ["path"]
        }
    },
    {
        "name": "write_file",
        "description": "Write content to a file",
        "input_schema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path to the file"},
                "content": {"type": "string", "description": "Content to write"}
            },
            "required": ["path", "content"]
        }
    },
    {
        "name": "web_search",
        "description": "Search the web for information",
        "input_schema": {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "Search query"}
            },
            "required": ["query"]
        }
    }
]

def execute_tool(name, input):
    if name == "run_command":
        result = subprocess.run(
            input["command"], shell=True,
            capture_output=True, text=True, timeout=30
        )
        return result.stdout + result.stderr

    elif name == "read_file":
        with open(input["path"], "r") as f:
            return f.read()

    elif name == "write_file":
        with open(input["path"], "w") as f:
            f.write(input["content"])
        return f"Wrote to {input['path']}"

    elif name == "web_search":
        # Simplified - use a real search API in practice
        return f"Search results for: {input['query']}"

    return f"Unknown tool: {name}"
```

Now we need the agent loop. When the AI wants to use a tool, we execute it and feed the result back:

```python
def serialize_content(content):
    """Convert API response content blocks to JSON-serializable dicts."""
    serialized = []
    for block in content:
        if hasattr(block, "text"):
            serialized.append({"type": "text", "text": block.text})
        elif block.type == "tool_use":
            serialized.append({
                "type": "tool_use",
                "id": block.id,
                "name": block.name,
                "input": block.input
            })
    return serialized

def run_agent_turn(messages, system_prompt):
    """Run one full agent turn (may involve multiple tool calls)."""
    while True:
        response = client.messages.create(
            model="claude-sonnet-4-5-20250929",
            max_tokens=4096,
            system=system_prompt,
            tools=TOOLS,
            messages=messages
        )

        content = serialize_content(response.content)

        # If the AI is done (no tool use), return the text
        if response.stop_reason == "end_turn":
            text = ""
            for block in response.content:
                if hasattr(block, "text"):
                    text += block.text
            messages.append({"role": "assistant", "content": content})
            return text, messages

        # Process tool calls
        if response.stop_reason == "tool_use":
            messages.append({"role": "assistant", "content": content})

            tool_results = []
            for block in response.content:
                if block.type == "tool_use":
                    print(f"  Tool: {block.name}({json.dumps(block.input)})")
                    result = execute_tool(block.name, block.input)
                    tool_results.append({
                        "type": "tool_result",
                        "tool_use_id": block.id,
                        "content": str(result)
                    })

            messages.append({"role": "user", "content": tool_results})
```

Now we update `handle_message` to use the agent loop instead of calling the API directly:

```python
async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)
    messages = load_session(user_id)
    messages.append({"role": "user", "content": update.message.text})

    response_text, messages = run_agent_turn(messages, SOUL)

    save_session(user_id, messages)
    await update.message.reply_text(response_text)
```

Now you can text your bot:

```
You: Create a file called hello.py that prints hello world, then run it

Bot: [uses write_file to create hello.py]
    [uses run_command to execute it]
    Done! I created hello.py and ran it. Output: "hello world"
```

The AI decided which tools to use, in what order, and synthesized the results into a natural response. All through a Telegram message.

OpenClaw's production tool catalog is much larger — browser automation, inter-agent messaging, sub-agent spawning, and more -= but every tool follows this exact pattern: a schema, a description, and an execution function.

## Goal: Permission Controls

We're executing commands from Telegram messages. That's terrifying. What if someone gets access to your Telegram account and tells the bot to `rm -rf /`?

We need a permission system. OpenClaw's approach: an approval allowlist that remembers what you've approved.

We add these helpers alongside our existing code, then update the `run_command` case inside `execute_tool` to use them:

```python
import re

SAFE_COMMANDS = {"ls", "cat", "head", "tail", "wc", "date", "whoami", "echo"}
DANGEROUS_PATTERNS = [r"\brm\b", r"\bsudo\b", r"\bchmod\b", r"\bcurl.*\|.*sh"]

# Persistent allowlist
APPROVALS_FILE = "./exec-approvals.json"

def load_approvals():
    if os.path.exists(APPROVALS_FILE):
        with open(APPROVALS_FILE) as f:
            return json.load(f)
    return {"allowed": [], "denied": []}

def save_approval(command, approved):
    approvals = load_approvals()
    key = "allowed" if approved else "denied"
    if command not in approvals[key]:
        approvals[key].append(command)
    with open(APPROVALS_FILE, "w") as f:
        json.dump(approvals, f, indent=2)

def check_command_safety(command):
    """Returns 'safe', 'approved', or 'needs_approval'."""
    base_cmd = command.strip().split()[0] if command.strip() else ""
    if base_cmd in SAFE_COMMANDS:
        return "safe"

    approvals = load_approvals()
    if command in approvals["allowed"]:
        return "approved"

    for pattern in DANGEROUS_PATTERNS:
        if re.search(pattern, command):
            return "needs_approval"

    return "needs_approval"
```

Now update the `run_command` case in `execute_tool` to check permissions before executing:

```python
    if name == "run_command":
        cmd = input["command"]
        safety = check_command_safety(cmd)

        if safety == "needs_approval":
            # In a real bot, you'd prompt the user via Telegram
            # and wait for their response. For simplicity, we log and deny.
            print(f"  ⚠️  Blocked: {cmd} (needs approval)")
            return "Permission denied. Command requires approval."

        result = subprocess.run(
            cmd, shell=True, capture_output=True,
            text=True, timeout=30
        )
        return result.stdout + result.stderr
```

When a command is safe or previously approved, it runs immediately. When it's not, the agent gets told "permission denied" and can try a different approach. The approval gets persisted to `exec-approvals.json`, so you're never asked twice for the same command.

OpenClaw extends this with glob patterns (approve `git *` once) and a three-tier model: "ask" (prompt user), "record" (log but allow), and "ignore" (auto-allow).

## Goal: The Gateway

Here's where it gets interesting. So far we have a Telegram bot. But what if you also want the AI on Discord? And WhatsApp? And Slack?

You could write separate bots for each platform. But then you'd have separate sessions, separate memory, separate configurations. The AI on Telegram wouldn't know what you discussed on Discord.

The solution: a **gateway**. One central process that manages all channels.

Look at what we already have. Our `run_agent_turn` function doesn't know anything about Telegram. It takes messages and returns text. That's the key — the agent logic is already decoupled from the channel.

To prove it, let's add a second interface. We'll add a simple HTTP API alongside our Telegram bot, both talking to the same agent and the same sessions:

```python
from flask import Flask, request, jsonify
import threading

flask_app = Flask(__name__)

@flask_app.route("/chat", methods=["POST"])
def chat():
    data = request.json
    user_id = data["user_id"]
    messages = load_session(user_id)
    messages.append({"role": "user", "content": data["message"]})

    response_text, messages = run_agent_turn(messages, SOUL)

    save_session(user_id, messages)
    return jsonify({"response": response_text})

# Run the HTTP API in a background thread
threading.Thread(target=lambda: flask_app.run(port=5000), daemon=True).start()

# Telegram bot runs as before
app = Application.builder().token(os.getenv("TELEGRAM_BOT_TOKEN")).build()
app.add_handler(MessageHandler(filters.TEXT, handle_message))
app.run_polling()
```

**Try it out:** Tell the bot your name on Telegram. Then query via HTTP using the same user ID (your Telegram user ID) to prove the session is shared:

```bash
# Via Telegram
You: My name is Nader
Bot: Nice to meet you, Nader!

# Via HTTP — use your Telegram user ID so it hits the same session
curl -X POST http://127.0.0.1:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"user_id": "YOUR_TELEGRAM_USER_ID", "message": "What is my name?"}'

{"response": "Your name is Nader!"}
```

Same agent, same sessions, same memory. Two different interfaces. That's the gateway pattern.

The next step would be making this config-driven — a JSON file specifying which channels to start and how to authenticate them. That's what OpenClaw does: its gateway manages Telegram, Discord, WhatsApp, Slack, Signal, iMessage, and more, all through a single config file. It also supports configurable session scoping — per-user, per-channel, or a single shared session — so the same person gets a unified experience across channels. We'll keep our simple user-ID-as-session-key approach for now.

## Goal: Context Compaction

Remember the growing session problem we flagged earlier? After chatting with your bot for weeks, the session file has thousands of messages. The total token count exceeds the model's context window. Now what?

The fix: summarize old messages, keep recent ones. Add these two functions alongside your existing code:

```python
def estimate_tokens(messages):
    """Rough token estimate: ~4 chars per token."""
    return sum(len(json.dumps(m)) for m in messages) // 4

def compact_session(user_id, messages):
    """Summarize old messages when context gets too long."""
    if estimate_tokens(messages) < 100_000:  # ~80% of a 128k window
        return messages  # No compaction needed

    split = len(messages) // 2
    old, recent = messages[:split], messages[split:]

    print("  Compacting session history...")

    summary = client.messages.create(
        model="claude-sonnet-4-5-20250929",
        max_tokens=2000,
        messages=[{
            "role": "user",
            "content": (
                "Summarize this conversation concisely. Preserve:\n"
                "- Key facts about the user (name, preferences)\n"
                "- Important decisions made\n"
                "- Open tasks or TODOs\n\n"
                f"{json.dumps(old, indent=2)}"
            )
        }]
    )

    compacted = [{
        "role": "user",
        "content": f"[Previous conversation summary]\n{summary.content[0].text}"
    }] + recent

    save_session(user_id, compacted)
    return compacted
```

Now add the compaction check at the top of `handle_message`, right after loading the session:

```python
async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)
    messages = load_session(user_id)
    messages = compact_session(user_id, messages)  # <-- add this line
    messages.append({"role": "user", "content": update.message.text})

    response_text, messages = run_agent_turn(messages, SOUL)

    save_session(user_id, messages)
    await update.message.reply_text(response_text)
```

**Try it out:** To test compaction without chatting for hours, temporarily lower the threshold:

```python
if estimate_tokens(messages) < 1000:  # lowered for testing
```

Have a conversation of 10-15 messages, then watch the old messages get replaced with a summary. The bot still remembers key facts, but the session file is much smaller.

OpenClaw's compaction is more sophisticated — it splits messages into chunks by token count, summarizes each chunk separately, and includes a safety margin for estimation inaccuracy — but the core idea is identical.

## Goal: Long-Term Memory

Session history gives you conversation memory. But what happens when you reset a session or start a new one? Everything is gone.

We need a separate memory system — persistent knowledge that survives session resets. The approach: give the agent tools to save and search memories stored as files.

Add these two tools to the `TOOLS` list:

```python
{
    "name": "save_memory",
    "description": "Save important information to long-term memory. Use for user preferences, key facts, and anything worth remembering across sessions.",
    "input_schema": {
        "type": "object",
        "properties": {
            "key": {
                "type": "string",
                "description": "Short label, e.g. 'user-preferences', 'project-notes'"
            },
            "content": {
                "type": "string",
                "description": "The information to remember"
            }
        },
        "required": ["key", "content"]
    }
},
{
    "name": "memory_search",
    "description": "Search long-term memory for relevant information. Use at the start of conversations to recall context.",
    "input_schema": {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "What to search for"
            }
        },
        "required": ["query"]
    }
}
```

Add their cases to `execute_tool`:

```python
MEMORY_DIR = "./memory"

# Add these cases to execute_tool:

    elif name == "save_memory":
        os.makedirs(MEMORY_DIR, exist_ok=True)
        filepath = os.path.join(MEMORY_DIR, f"{input['key']}.md")
        with open(filepath, "w") as f:
            f.write(input["content"])
        return f"Saved to memory: {input['key']}"

    elif name == "memory_search":
        query = input["query"].lower()
        results = []
        if os.path.exists(MEMORY_DIR):
            for fname in os.listdir(MEMORY_DIR):
                if fname.endswith(".md"):
                    with open(os.path.join(MEMORY_DIR, fname), "r") as f:
                        content = f.read()
                    if any(word in content.lower() for word in query.split()):
                        results.append(f"--- {fname} ---\n{content}")
        return "\n\n".join(results) if results else "No matching memories found."
```

Finally, update the SOUL so the agent knows about memory:

```python
SOUL = """
# Who You Are
...existing personality...

## Memory
You have a long-term memory system.
- Use save_memory to store important information (user preferences, key facts, project details).
- Use memory_search at the start of conversations to recall context from previous sessions.
Memory files are stored in ./memory/ as markdown files.
"""
```

**Try it out:**

```
You: Remember that my favorite restaurant is Elvies and I prefer to go on weekends.

Bot: [uses save_memory to write nader-profile.md]
     Got it — saved your restaurant preference.

[Reset the session or restart the bot]

You: Where should we go for dinner?

Bot: [uses memory_search for "restaurant dinner favorite"]
     How about Elvies? I know it's your favorite. Want to go this weekend?
```

The memory persists because it's stored in files, not in the session. Reset the session, restart the bot — the memories are still there.

OpenClaw's production memory uses vector search with embeddings for semantic matching (so "auth bug" matches "authentication issues"), but our keyword search works well for getting started.

## Goal: Command Queue

Here's a subtle but critical problem: what happens when two messages arrive at the same time?

Say you send "check my calendar" on Telegram and "what's the weather" via the HTTP API simultaneously. Both try to load the same session, both try to append to it, and you get corrupted data.

The fix is simple: a per-session lock. Only one message processes at a time for each session. Different sessions can still run in parallel.

```python
# Add to your bot
from collections import defaultdict

session_locks = defaultdict(threading.Lock)
```

Now wrap the body of `handle_message` with the lock:

```python
async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)

    with session_locks[user_id]:
        messages = load_session(user_id)
        messages = compact_session(user_id, messages)
        messages.append({"role": "user", "content": update.message.text})

        response_text, messages = run_agent_turn(messages, SOUL)

        save_session(user_id, messages)

    await update.message.reply_text(response_text)
```

Do the same for the `/chat` HTTP endpoint:

```python
@flask_app.route("/chat", methods=["POST"])
def chat():
    data = request.json
    user_id = data["user_id"]

    with session_locks[user_id]:
        messages = load_session(user_id)
        messages = compact_session(user_id, messages)
        messages.append({"role": "user", "content": data["message"]})

        response_text, messages = run_agent_turn(messages, SOUL)

        save_session(user_id, messages)

    return jsonify({"response": response_text})
```

That's it — five lines of setup. Messages for the same user queue up. Messages for different users run in parallel. No race conditions.

OpenClaw extends this with lane-based queues (separate lanes for messages, cron jobs, and sub-agents) so heartbeats never block real-time conversations.

## Goal: Cron Jobs (Heartbeats)

So far our agent only responds when you talk to it. But what if you want it to check your email every morning? Or summarize your calendar before meetings?

You need scheduled execution. Let's add heartbeats — recurring tasks that trigger the agent on a timer.

```python
import schedule
import time

def setup_heartbeats():
    """Configure recurring agent tasks."""

    def morning_briefing():
        print("\n⏰ Heartbeat: morning briefing")
        # Use an isolated session key so cron doesn't pollute main chat
        session_key = "cron:morning-briefing"

        with session_locks[session_key]:
            messages = load_session(session_key)
            messages.append({
                "role": "user",
                "content": "Good morning! Check today's date and give me a motivational quote."
            })

            response_text, messages = run_agent_turn(messages, SOUL)
            save_session(session_key, messages)

        print(f"🤖 {response_text}\n")
        # In production, you'd send this to Telegram/Discord too

    schedule.every().day.at("07:30").do(morning_briefing)

    # Run the scheduler in a background thread
    def scheduler_loop():
        while True:
            schedule.run_pending()
            time.sleep(60)

    threading.Thread(target=scheduler_loop, daemon=True).start()

# Call during startup, before run_polling()
setup_heartbeats()
```

The key insight: each heartbeat uses its own session key (`cron:morning-briefing`). This keeps scheduled tasks from cluttering your main conversation history. The heartbeat calls the same `run_agent_turn` function — it's just another message, triggered by a timer instead of a human.

**Try it out:** For testing, change the schedule to run every minute:

```python
schedule.every(1).minutes.do(morning_briefing)
```

You'll see the heartbeat fire in your terminal, and the agent will respond. Change it back to a daily schedule when you're done testing.

OpenClaw supports full cron expressions (`30 7 * * *`) and routes heartbeats through a separate command queue lane so they never block real-time messages.

## Goal: Multi-Agent

One agent is useful. But as you add more tasks, you'll find a single personality and toolset can't cover everything well. A research assistant needs different instructions than a general assistant.

The fix: multiple agent configurations with routing. Each agent has its own SOUL, its own session, and you switch between them based on the message.

```python
AGENTS = {
    "main": {
        "name": "Jarvis",
        "soul": SOUL,  # our existing SOUL
        "session_prefix": "agent:main",
    },
    "researcher": {
        "name": "Scout",
        "soul": """You are Scout, a research specialist.
Your job: find information and cite sources. Every claim needs evidence.
Use tools to gather data. Be thorough but concise.
Save important findings to memory for other agents to reference.""",
        "session_prefix": "agent:researcher",
    },
}

def resolve_agent(message_text):
    """Route messages to the right agent based on prefix commands."""
    if message_text.startswith("/research "):
        return "researcher", message_text[len("/research "):]
    return "main", message_text
```

Update `handle_message` to route messages to the right agent:

```python
async def handle_message(update: Update, context):
    user_id = str(update.effective_user.id)
    agent_id, message_text = resolve_agent(update.message.text)
    agent = AGENTS[agent_id]
    session_key = f"{agent['session_prefix']}:{user_id}"

    with session_locks[session_key]:
        messages = load_session(session_key)
        messages = compact_session(session_key, messages)
        messages.append({"role": "user", "content": message_text})

        response_text, messages = run_agent_turn(messages, agent["soul"])

        save_session(session_key, messages)

    await update.message.reply_text(f"[{agent['name']}] {response_text}")
```

**Try it out:**

```
You: What's the weather like?
[Jarvis] It's a nice day! I'd check a weather service for exact details.

You: /research What are the best practices for Python async programming?
[Scout] Here's what I found...
    [uses web_search, save_memory to gather and store findings]
    The key practices are: 1) Use asyncio.gather for concurrent tasks...

You: What did Scout find about Python async?
[Jarvis] [uses memory_search]
    Scout's research found that the key async best practices are...
```

Each agent has its own conversation history, but they share the same memory directory. Scout saves research findings; Jarvis can search for them later. They collaborate through shared files without needing direct messaging.

OpenClaw extends this with sub-agent spawning (a parent agent can spawn a child for a focused task) and inter-agent messaging, but the core pattern is the same: each agent is just a SOUL + session + tools.

## Putting It All Together

Let's combine everything we've built into a single runnable script. This is a clean standalone REPL that includes every feature from the tutorial: sessions, SOUL, tools, permissions, compaction, memory, command queue, cron, and multi-agent routing.

```python
#!/usr/bin/env python3
# mini-openclaw.py - A minimal OpenClaw clone
# Run: uv run --with anthropic --with schedule python mini-openclaw.py

import anthropic
import subprocess
import json
import os
import re
import threading
import time
import schedule
from collections import defaultdict
from datetime import datetime

client = anthropic.Anthropic()

# ─── Configuration ───

WORKSPACE = os.path.expanduser("~/.mini-openclaw")
SESSIONS_DIR = os.path.join(WORKSPACE, "sessions")
MEMORY_DIR = os.path.join(WORKSPACE, "memory")
APPROVALS_FILE = os.path.join(WORKSPACE, "exec-approvals.json")

# ─── Agents ───

AGENTS = {
    "main": {
        "name": "Jarvis",
        "model": "claude-sonnet-4-5-20250929",
        "soul": (
            "You are Jarvis, a personal AI assistant.\n"
            "Be genuinely helpful. Skip the pleasantries. Have opinions.\n"
            "You have tools — use them proactively.\n\n"
            "## Memory\n"
            f"Your workspace is {WORKSPACE}.\n"
            "Use save_memory to store important information across sessions.\n"
            "Use memory_search at the start of conversations to recall context."
        ),
        "session_prefix": "agent:main",
    },
    "researcher": {
        "name": "Scout",
        "model": "claude-sonnet-4-5-20250929",
        "soul": (
            "You are Scout, a research specialist.\n"
            "Your job: find information and cite sources. Every claim needs evidence.\n"
            "Use tools to gather data. Be thorough but concise.\n"
            "Save important findings with save_memory for other agents to reference."
        ),
        "session_prefix": "agent:researcher",
    },
}

# ─── Tools ───

TOOLS = [
    {
        "name": "run_command",
        "description": "Run a shell command",
        "input_schema": {
            "type": "object",
            "properties": {
                "command": {"type": "string", "description": "The command to run"}
            },
            "required": ["command"]
        }
    },
    {
        "name": "read_file",
        "description": "Read a file from the filesystem",
        "input_schema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path to the file"}
            },
            "required": ["path"]
        }
    },
    {
        "name": "write_file",
        "description": "Write content to a file (creates directories if needed)",
        "input_schema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path to the file"},
                "content": {"type": "string", "description": "Content to write"}
            },
            "required": ["path", "content"]
        }
    },
    {
        "name": "save_memory",
        "description": "Save important information to long-term memory",
        "input_schema": {
            "type": "object",
            "properties": {
                "key": {"type": "string", "description": "Short label (e.g. 'user-preferences')"},
                "content": {"type": "string", "description": "The information to remember"}
            },
            "required": ["key", "content"]
        }
    },
    {
        "name": "memory_search",
        "description": "Search long-term memory for relevant information",
        "input_schema": {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "What to search for"}
            },
            "required": ["query"]
        }
    },
]

# ─── Permission Controls ───

SAFE_COMMANDS = {"ls", "cat", "head", "tail", "wc", "date", "whoami",
                 "echo", "pwd", "which", "git", "python", "node", "npm"}

def load_approvals():
    if os.path.exists(APPROVALS_FILE):
        with open(APPROVALS_FILE) as f:
            return json.load(f)
    return {"allowed": [], "denied": []}

def save_approval(command, approved):
    approvals = load_approvals()
    key = "allowed" if approved else "denied"
    if command not in approvals[key]:
        approvals[key].append(command)
    with open(APPROVALS_FILE, "w") as f:
        json.dump(approvals, f, indent=2)

def check_command_safety(command):
    base_cmd = command.strip().split()[0] if command.strip() else ""
    if base_cmd in SAFE_COMMANDS:
        return "safe"
    approvals = load_approvals()
    if command in approvals["allowed"]:
        return "approved"
    return "needs_approval"

# ─── Tool Execution ───

def execute_tool(name, tool_input):
    if name == "run_command":
        cmd = tool_input["command"]
        safety = check_command_safety(cmd)
        if safety == "needs_approval":
            print(f"\n  ⚠️  Command: {cmd}")
            confirm = input("  Allow? (y/n): ").strip().lower()
            if confirm != "y":
                save_approval(cmd, False)
                return "Permission denied by user."
            save_approval(cmd, True)
        try:
            result = subprocess.run(
                cmd, shell=True, capture_output=True, text=True, timeout=30
            )
            output = result.stdout + result.stderr
            return output if output else "(no output)"
        except subprocess.TimeoutExpired:
            return "Command timed out after 30 seconds"
        except Exception as e:
            return f"Error: {e}"

    elif name == "read_file":
        try:
            with open(tool_input["path"], "r") as f:
                return f.read()[:10000]
        except Exception as e:
            return f"Error: {e}"

    elif name == "write_file":
        try:
            os.makedirs(os.path.dirname(tool_input["path"]) or ".", exist_ok=True)
            with open(tool_input["path"], "w") as f:
                f.write(tool_input["content"])
            return f"Wrote to {tool_input['path']}"
        except Exception as e:
            return f"Error: {e}"

    elif name == "save_memory":
        os.makedirs(MEMORY_DIR, exist_ok=True)
        filepath = os.path.join(MEMORY_DIR, f"{tool_input['key']}.md")
        with open(filepath, "w") as f:
            f.write(tool_input["content"])
        return f"Saved to memory: {tool_input['key']}"

    elif name == "memory_search":
        query = tool_input["query"].lower()
        results = []
        if os.path.exists(MEMORY_DIR):
            for fname in os.listdir(MEMORY_DIR):
                if fname.endswith(".md"):
                    with open(os.path.join(MEMORY_DIR, fname), "r") as f:
                        content = f.read()
                    if any(w in content.lower() for w in query.split()):
                        results.append(f"--- {fname} ---\n{content}")
        return "\n\n".join(results) if results else "No matching memories found."

    return f"Unknown tool: {name}"

# ─── Session Management ───

def get_session_path(session_key):
    os.makedirs(SESSIONS_DIR, exist_ok=True)
    safe_key = session_key.replace(":", "_").replace("/", "_")
    return os.path.join(SESSIONS_DIR, f"{safe_key}.jsonl")

def load_session(session_key):
    path = get_session_path(session_key)
    messages = []
    if os.path.exists(path):
        with open(path, "r") as f:
            for line in f:
                if line.strip():
                    try:
                        messages.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
    return messages

def append_message(session_key, message):
    with open(get_session_path(session_key), "a") as f:
        f.write(json.dumps(message) + "\n")

def save_session(session_key, messages):
    with open(get_session_path(session_key), "w") as f:
        for msg in messages:
            f.write(json.dumps(msg) + "\n")

# ─── Compaction ───

def estimate_tokens(messages):
    return sum(len(json.dumps(m)) for m in messages) // 4

def compact_session(session_key, messages):
    if estimate_tokens(messages) < 100_000:
        return messages
    split = len(messages) // 2
    old, recent = messages[:split], messages[split:]
    print("\n  📦 Compacting session history...")
    summary = client.messages.create(
        model="claude-sonnet-4-5-20250929",
        max_tokens=2000,
        messages=[{
            "role": "user",
            "content": (
                "Summarize this conversation concisely. Preserve key facts, "
                "decisions, and open tasks:\n\n"
                f"{json.dumps(old, indent=2)}"
            )
        }]
    )
    compacted = [{
        "role": "user",
        "content": f"[Conversation summary]\n{summary.content[0].text}"
    }] + recent
    save_session(session_key, compacted)
    return compacted

# ─── Command Queue ───

session_locks = defaultdict(threading.Lock)

# ─── Agent Loop ───

def serialize_content(content):
    serialized = []
    for block in content:
        if hasattr(block, "text"):
            serialized.append({"type": "text", "text": block.text})
        elif block.type == "tool_use":
            serialized.append({
                "type": "tool_use", "id": block.id,
                "name": block.name, "input": block.input
            })
    return serialized

def run_agent_turn(session_key, user_text, agent_config):
    """Run a full agent turn: load session, call LLM in a loop, save."""
    with session_locks[session_key]:
        messages = load_session(session_key)
        messages = compact_session(session_key, messages)

        user_msg = {"role": "user", "content": user_text}
        messages.append(user_msg)
        append_message(session_key, user_msg)

        for _ in range(20):  # max tool-use turns
            response = client.messages.create(
                model=agent_config["model"],
                max_tokens=4096,
                system=agent_config["soul"],
                tools=TOOLS,
                messages=messages
            )

            content = serialize_content(response.content)
            assistant_msg = {"role": "assistant", "content": content}
            messages.append(assistant_msg)
            append_message(session_key, assistant_msg)

            if response.stop_reason == "end_turn":
                return "".join(
                    b.text for b in response.content if hasattr(b, "text")
                )

            if response.stop_reason == "tool_use":
                tool_results = []
                for block in response.content:
                    if block.type == "tool_use":
                        print(f"  🔧 {block.name}: {json.dumps(block.input)[:100]}")
                        result = execute_tool(block.name, block.input)
                        display = str(result)[:150]
                        print(f"     → {display}")
                        tool_results.append({
                            "type": "tool_result",
                            "tool_use_id": block.id,
                            "content": str(result)
                        })

                results_msg = {"role": "user", "content": tool_results}
                messages.append(results_msg)
                append_message(session_key, results_msg)

        return "(max turns reached)"

# ─── Multi-Agent Routing ───

def resolve_agent(message_text):
    """Route messages to the right agent based on prefix commands."""
    if message_text.startswith("/research "):
        return "researcher", message_text[len("/research "):]
    return "main", message_text

# ─── Cron / Heartbeats ───

def setup_heartbeats():
    def morning_check():
        print("\n⏰ Heartbeat: morning check")
        result = run_agent_turn(
            "cron:morning-check",
            "Good morning! Check today's date and give me a motivational quote.",
            AGENTS["main"]
        )
        print(f"🤖 {result}\n")

    schedule.every().day.at("07:30").do(morning_check)

    def scheduler_loop():
        while True:
            schedule.run_pending()
            time.sleep(60)

    threading.Thread(target=scheduler_loop, daemon=True).start()

# ─── REPL ───

def main():
    for d in [WORKSPACE, SESSIONS_DIR, MEMORY_DIR]:
        os.makedirs(d, exist_ok=True)

    setup_heartbeats()

    session_key = "agent:main:repl"

    print("Mini OpenClaw")
    print(f"  Agents: {', '.join(a['name'] for a in AGENTS.values())}")
    print(f"  Workspace: {WORKSPACE}")
    print("  Commands: /new (reset), /research <query>, /quit\n")

    while True:
        try:
            user_input = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nGoodbye!")
            break

        if not user_input:
            continue
        if user_input.lower() in ["/quit", "/exit", "/q"]:
            print("Goodbye!")
            break
        if user_input.lower() == "/new":
            session_key = f"agent:main:repl:{datetime.now().strftime('%Y%m%d%H%M%S')}"
            print("  Session reset.\n")
            continue

        agent_id, message_text = resolve_agent(user_input)
        agent_config = AGENTS[agent_id]
        sk = (
            f"{agent_config['session_prefix']}:repl"
            if agent_id != "main" else session_key
        )

        response = run_agent_turn(sk, message_text, agent_config)
        print(f"\n🤖 [{agent_config['name']}] {response}\n")

if __name__ == "__main__":
    main()
```

Save this as `mini-openclaw.py` and run it:

```bash
uv run --with anthropic --with schedule python mini-openclaw.py
```

Here's what a session looks like:

```
Mini OpenClaw
  Agents: Jarvis, Scout
  Workspace: ~/.mini-openclaw
  Commands: /new (reset), /research <query>, /quit

You: Remember that my favorite restaurant is Hai Cenato and I prefer 7pm reservations

  🔧 save_memory: {"key": "user-preferences", "content": "Favorite restaurant...
     → Saved to memory: user-preferences

🤖 [Jarvis] Got it. Saved your restaurant preference - Hai Cenato, 7pm reservations.

You: What's in my project directory?

  🔧 run_command: {"command": "ls"}
     → src, package.json, README.md, node_modules, ...

🤖 [Jarvis] Your project has a standard Node.js structure with src/, package.json,
   and the usual suspects.

You: /new
  Session reset.

You: Where do I like to eat?

  🔧 memory_search: {"query": "restaurant favorite food"}
     → --- user-preferences.md ---
        Favorite restaurant: Hai Cenato...

🤖 [Jarvis] You like Hai Cenato, and you prefer 7pm reservations.

You: /research What are the latest trends in AI agents?

  🔧 web_search: {"query": "AI agent trends 2025"}
     → Search results for: AI agent trends 2025
  🔧 save_memory: {"key": "research-ai-agents", ...}
     → Saved to memory: research-ai-agents

🤖 [Scout] Here's what I found on current AI agent trends...
```

The memory persists across sessions. Agents collaborate through shared memory files. Commands require approval. Heartbeats run in the background. All in ~400 lines.

## What We've Learned

Starting from a simple Telegram bot, we built every major component of a persistent AI assistant:

**Persistent sessions** (JSONL files): Crash-safe conversation memory. Each session is one file, each line is one message. Restart the process and everything is still there.

**SOUL.md** (system prompt): A personality file that transforms a generic AI into a specific agent with consistent behavior, boundaries, and style.

**Tools + Agent loop**: Structured tool definitions that let the AI decide when to act. The agent loop calls the LLM, executes any requested tools, feeds results back, and repeats until done.

**Permission controls**: An allowlist of safe commands plus persistent approvals, so dangerous operations require explicit consent.

**The gateway pattern**: One central agent with multiple interfaces. Telegram, HTTP, or any other channel - they all talk to the same sessions and the same memory.

**Context compaction**: When conversations outgrow the context window, summarize old messages and keep recent ones. The bot keeps its knowledge without hitting token limits.

**Long-term memory**: File-based storage with save and search tools. Knowledge that survives session resets, accessible to any agent.

**Command queue**: Per-session locking to prevent race conditions when multiple messages arrive simultaneously.

**Heartbeats**: Scheduled agent runs on a timer, each with its own isolated session. The agent wakes up, does its task, and goes back to sleep.

**Multi-agent routing**: Multiple agent configurations with different SOULs and session keys, routed by message content. Agents collaborate through shared memory files.

Each of these emerged from a practical problem:

- "The AI can't remember anything" → Sessions
- "It responds like a generic chatbot" → SOUL.md
- "It can only talk, not act" → Tools + Agent loop
- "It runs dangerous commands without asking" → Permission controls
- "I want it on all my messaging apps" → Gateway
- "The conversation got too long" → Compaction
- "It forgets things between sessions" → Memory
- "Two messages at once corrupt the data" → Command queue
- "I want it to do things automatically" → Heartbeats
- "One agent can't do everything well" → Multi-agent

This is how you could have invented OpenClaw.

## Going Further

Our prototype covers the core architecture. Here's how OpenClaw extends each idea for production use - features worth exploring once you've outgrown the basics.

### Browser with Semantic Snapshots

Most AI assistants can't see the web. OpenClaw gives the agent a browser via Playwright, but instead of sending screenshots (5MB each, expensive in tokens), it uses **semantic snapshots** - a text representation of the page's accessibility tree:

```python
# Simplified concept
snapshot = page.accessibility.snapshot()
# Output:
# - heading "Welcome to GitHub"
# - button "Sign In" [ref=1]
# - textbox "Email" [ref=2]
# - link "Forgot password?" [ref=3]
```

Each interactive element gets a numbered `ref` ID. When the agent wants to click something, it says "click ref=1"  which maps to exactly one element on the page. No guessing, no "click the blue button near the top." And since the snapshot is text instead of an image, it's roughly 100x smaller than a screenshot, which means far fewer tokens per page.

### Session Scoping & Identity Links

Our prototype uses user ID as the session key. OpenClaw supports configurable scoping:

- **`main`** (default): All DMs share one session - simple, great for single-user setups.
- **`per-peer`**: Each person gets one session across all channels.
- **`per-channel-peer`**: Each person per channel gets their own session.

Identity links let you merge sessions across channels for the same person, so Alice's Telegram and Discord conversations share the same history.

### Channel Plugin System

Our prototype hardcodes Telegram + HTTP. OpenClaw uses a plugin architecture where each channel (Telegram, Discord, WhatsApp, Slack, Signal, iMessage) is a separate adapter that normalizes messages into a common format. Adding a new channel means writing one adapter, not touching any agent logic.

### Vector Memory Search

Our keyword search works, but misses semantic matches ("auth bug" won't match "authentication issues"). OpenClaw's production memory uses a hybrid approach: vector search via SQLite with embedding extensions for semantic similarity, plus FTS5 for exact keyword matches. Configurable embedding providers include OpenAI, local models, Gemini, and Voyage.

### Sub-agent Spawning

Our multi-agent setup uses manual routing. OpenClaw lets agents spawn sub-agents programmatically - a parent agent calls `sessions_spawn`, the child runs in its own context with a timeout, and returns results to the parent. This enables patterns like "research this topic in depth" where the main agent delegates to a specialist and continues when it's done.

## Next Steps

If you want to build your own:

1. **Start with one channel**: get a Telegram or Discord bot working with sessions
2. **Add tools incrementally**: start with file read/write, then add shell execution
3. **Add memory when you need it**: once sessions reset, you'll want persistent memory
4. **Add channels when you outgrow one**: the gateway pattern emerges naturally
5. **Add agents when tasks specialize**: don't start with 10 agents, start with 2

Or just use OpenClaw. It's open source and handles all the edge cases we glossed over. But now you know how it works under the hood.
