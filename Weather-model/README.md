# Hybrid Weather Entity for Home Assistant

Combines a local Ecowitt weather station with a regional weather integration
(Google Weather, Met.no, OpenWeatherMap, etc.) into one `weather.*` entity —
accurate local current conditions, correctly-classified sky condition, and
(optionally) a real forecast. See `hybrid-weather-entity.yaml` for the full
config and inline comments on what comes from where.

This README covers **how to install it**, including a couple of gotchas that
came up along the way.

## Requirements

- Home Assistant 2024.4+ (forecast data was moved to the `weather.get_forecasts`
  service in that release)
- A local Ecowitt station via the built-in Ecowitt integration
- A weather integration for regional condition + forecast (Google Weather,
  Met.no, etc.)

**Field naming note:** the config uses field names with no `_template` suffix
(`condition:`, `temperature:`, `wind_gust_speed:`, etc.), which is correct as
of HA 2026.9. Older HA versions required a `_template` suffix on every field
(`condition_template:`, `temperature_template:`, etc.). If Check Configuration
rejects a field name as invalid, that's the likely reason — add `_template`
back onto that field.

## Installation

You have two options. **Packages (recommended)** keeps this self-contained in
its own file. **Direct paste** is simpler if you don't already use packages
and don't want to set them up just for this.

### Option A: Packages (recommended)

Packages let you split `configuration.yaml` into separate files by topic,
where Home Assistant automatically *merges* matching top-level keys (like
`template:`) across files instead of overwriting them — which sidesteps the
"map keys must be unique" error entirely (more on that below).

1. **Enable packages**, if you haven't already. In `configuration.yaml`,
   under the `homeassistant:` section, add a `packages:` line:

   ```yaml
   homeassistant:
     packages: !include_dir_named packages
   ```

   - `packages:` is only valid *nested under* `homeassistant:` — it is not
     its own top-level key. Adding it at the top level gives an
     `Integration 'packages' not found` error.
   - If you already have a `homeassistant:` section (common — it usually
     also has `name:`, `latitude:`, `unit_system:`, etc.), don't create a
     second one. Just add the `packages:` line inside the existing block.
   - The word `packages` after `!include_dir_named` is a **folder name**,
     not a fixed keyword — you can call it anything (e.g.
     `entity_templates`). Only the `packages:` key itself (on the left) has
     to be spelled exactly that way.
   - You can only have **one** `packages:` key total. It's not a
     limitation in practice — point it at one folder and put as many files
     in that folder as you want (see step 3).

2. **Create the folder** referenced above (e.g. `<config>/packages/`) if it
   doesn't already exist.

3. **Save `hybrid-weather-entity.yaml` inside that folder.** Each file in a
   packages folder can use `template:`, `sensor:`, `automation:`, etc.
   freely — even if another file in the same folder also defines a
   `template:` key, packages merges them instead of colliding. This is the
   cleanest way to avoid duplicate-key conflicts with anything else in your
   config.

### Option B: Direct paste into configuration.yaml

If you'd rather not set up packages, you can paste the contents of
`hybrid-weather-entity.yaml` straight into `configuration.yaml`.

**Important:** YAML only allows one `template:` key per file. If
`configuration.yaml` already has a `template:` section anywhere, do **not**
paste the whole file in as a second one — that throws `Map keys must be
unique`. Instead, copy just the two list items from the file (each starts
with `- ` at the top level, one has `variables: / triggers: / actions: /
sensor:`, the other has `weather:`) and add them as two more items inside
your *existing* `template:` list.

## Configuration

Open `hybrid-weather-entity.yaml` and set these two values (there are two
places they appear — once in each of the file's two blocks):

| Variable | Meaning | Example |
|---|---|---|
| `station` | Your Ecowitt entity-ID prefix | `gw2000b` |
| `regional` / `regional_weather` | Your regional weather entity ID | `weather.home` |

Everything else derives from these automatically, assuming your Ecowitt
entities follow the standard naming pattern (`sensor.<station>_<measurement>`).

Also check the `*_unit` fields (`temperature_unit`, `pressure_unit`,
`wind_speed_unit`, `visibility_unit`) match what your sensors actually report.

## Deploying

1. **Check the configuration** — Developer Tools → YAML → **Check
   Configuration**. This validates YAML structure and templates without a
   restart. Fix any errors before continuing.

   - A red squiggle from your text editor's *own* linter (e.g. a
     "patternWarning" under `!include_dir_named packages`) is not the same
     thing as a Home Assistant error — generic YAML editors don't understand
     Home Assistant's custom tags and sometimes flag them cosmetically even
     when the config is completely valid. Trust the Check Configuration
     result over editor squiggles.

2. **Fully restart Home Assistant** — Settings → System → Restart → Restart
   Home Assistant. A reload is not enough; the trigger-based forecast-cache
   sensor only initializes at startup.

3. **Verify the new entities** — Developer Tools → States, search
   "hybrid." You should see `weather.hybrid_weather`, and (if you kept the
   forecast block) `sensor.hybrid_weather_daily_forecast_cache` and
   `sensor.hybrid_weather_hourly_forecast_cache`. Confirm the state and
   attributes are populated, not `unknown` or `unavailable`.

4. **Add it to a dashboard** — Edit dashboard → Add Card → Weather Forecast
   → select `weather.hybrid_weather`. The custom attributes (rain totals,
   solar, indoor readings, sensor battery, etc.) won't appear on that card,
   but are available via an Entities or Glance card, or in automations.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `Map keys must be unique` on `template:` | You have two top-level `template:` keys. Merge into one list (see Option B above), or switch to packages (Option A). |
| `Integration 'packages' not found` | `packages:` was placed at the top level instead of nested under `homeassistant:`. |
| Editor shows a squiggle/warning on `!include_dir_named` | Cosmetic — your editor's generic YAML linter doesn't know Home Assistant's custom tags. Check Configuration is the real test. |
| `weather.hybrid_weather` shows `unavailable` after restart | Check Settings → System → Logs — a Jinja template error will usually point to the exact failing line. |
| A field like `condition_template` / `wind_gust_speed_template` is rejected as invalid | You're likely on an HA version that still expects the `_template` suffix (pre-2026.9-ish). Add `_template` back onto that field name. |
