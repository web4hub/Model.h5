import torch
from transformers import AutoTokenizer, AutoModelForCausalLM

model_name = "Qwen/WebWorld-8B"  # or WebWorld-14B, WebWorld-32B
tokenizer = AutoTokenizer.from_pretrained(model_name, trust_remote_code=True)
model = AutoModelForCausalLM.from_pretrained(
    model_name,
    device_map="auto",
    torch_dtype=torch.bfloat16,
    trust_remote_code=True,
).eval()

system_prompt = (
    "You are a web world model. I will provide you with an initial page state "
    "and a sequence of actions. For each action, predict the resulting page state.\n"
    "Strictly maintain the original format. Output only the full page state "
    "without explanations, code, or truncation."
)

current_state = """RootWebArea 'Global Start - Your Daily Portal', focused
\t[1] banner 'Top Header', visible
\t\t[2] link 'Set as Homepage', clickable, visible
\t\t[3] link 'Feedback', clickable, visible
\t\t[5] region 'Weather Widget', visible
\t\t\tStaticText 'New York, USA'
\t\t\t[6] image 'Sunny', visible
\t\t\tStaticText '24°C'
\t\t[8] link 'Sign In', clickable, visible
\t[10] region 'Search Area', visible
\t\t[11] image 'Global Start Logo', visible
\t\tStaticText 'Search the entire web'
\t\t[12] tablist 'Search Engine Selector', orientation='horizontal'
\t\t\t[13] tab 'Google', selected=True, clickable
\t\t\t[14] tab 'Bing', selected=False, clickable
\t\t\t[15] tab 'DuckDuckGo', selected=False, clickable
\t\t[18] combobox 'Web Search', clickable, visible, autocomplete='both', expanded=False
\t\t\t[19] textbox 'Type keywords or URL...', clickable, visible, editable, value=''
\t\t[20] button 'Search', clickable, visible
\t[30] navigation 'Category Bar', visible
\t\t[31] link 'Home', clickable, selected=True
\t\t[32] link 'News', clickable
\t\t[33] link 'Video', clickable
\t\t[34] link 'Shopping', clickable
\t\t[35] link 'Social', clickable
\t[50] main 'Site Directory', visible
\t\t[51] region 'Top Recommended', visible
\t\t\t[52] heading 'Most Popular', visible
\t\t\t[53] list 'Top Sites Grid', visible
\t\t\t\t[54] link 'Facebook', clickable
\t\t\t\t[56] link 'YouTube', clickable
\t\t\t\t[58] link 'Amazon', clickable
\t\t\t\t[60] link 'Twitter / X', clickable
\t\t\t\t[62] link 'Instagram', clickable
\t\t\t\t[64] link 'Wikipedia', clickable
\t\t\t\t[66] link 'Netflix', clickable
\t\t\t\t[68] link 'LinkedIn', clickable
\t\t[80] region 'News & Media', visible
\t\t\t[81] heading 'Latest News', visible
\t\t\t[82] link 'CNN', clickable
\t\t\t[83] link 'BBC', clickable
\t\t\t[84] link 'The Verge', clickable
\t\t[90] region 'Shopping', visible
\t\t\t[91] heading 'E-Commerce', visible
\t\t\t[92] link 'eBay', clickable
\t\t\t[93] link 'Walmart', clickable
\t\t\t[94] link 'Best Buy', clickable
\t[200] complementary 'Ads', visible
\t\t[201] image 'Ad: Travel to Japan'
\t\t[202] link 'Book Now', clickable
\t[300] contentinfo 'Footer', visible
\t\tStaticText '© 2026 Global Start Inc.'"""

user_message = (
    f"Initial Page State:\n{current_state}\n\n"
    f"First Action: 'click([32])'\n\n"
    f"Next Page State:"
)

messages = [
    {"role": "system", "content": system_prompt},
    {"role": "user", "content": user_message},
]

text = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
inputs = tokenizer(text, return_tensors="pt").to(model.device)

with torch.no_grad():
    outputs = model.generate(
        **inputs,
        max_new_tokens=4096,
        do_sample=False,
    )

response = tokenizer.decode(
    outputs[0][inputs["input_ids"].shape[-1]:],
    skip_special_tokens=True
)
print(response)
