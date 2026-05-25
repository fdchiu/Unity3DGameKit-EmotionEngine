# Unity 3D Game Kit × Emotion Engine

Voice-driven AI companion ("Vega") for Unity's official **3D Game Kit**.
Vega comments on combat, suggests potions, calls out abilities.
Same backend, same SDK contract as Tuxemon — just the C#/Unity adapter
is new here.

## What this is

- `Assets/Plugins/EmotionEngine/` — engine-agnostic C# wrapper around
  the native `libGameVoiceNativeSDK.dylib` (or `.dll` / `.so`).
  Drop this folder into the Game Kit's `Assets/Plugins/`.
- `EmotionEngineRuntime` — managed wrapper, IDisposable, lifecycle +
  events.
- `EmotionEngineBridge` — MonoBehaviour helper that owns the runtime,
  drains worker-thread events on the main thread, and pushes state
  on a timer.
- README (this file) — integration runbook.

## How it fits with the rest of the stack

```
Your Unity scene  ──►  EmotionEngineBridge (MonoBehaviour)
                          │
                          ├─ pushes state every N seconds
                          └─ owns EmotionEngineRuntime
                                  │
                                  ├─ P/Invoke ──►  libGameVoiceNativeSDK.dylib
                                  │                  │
                                  │                  ├─ HTTP to backend.aihumanity.io
                                  │                  │   • /v1/sdk/sessions  (HMAC mint)
                                  │                  │   • /v1/game/config   (per-game)
                                  │                  │   • /v1/game/voice/*  (state/event/respond)
                                  │                  │
                                  │                  └─ Ultravox WebRTC (when macOS voice work lands)
                                  │
                                  └─ Events surface on Unity main thread:
                                      • OnDialogueJson  — Vega speaks
                                      • OnTraceJson     — diagnostics
                                      • OnClientTool    — tool dispatch from agent
```

## Backend status

The Unity 3D Game Kit demo's game record is already provisioned in
the dashboard with the `action_rpg` template applied.

| Field | Value |
|---|---|
| GAME_ID | `game_994e7ab8-abee-450b-91b3-77abccf8ecd0` |
| AGENT_NAME (resolved) | `studio_va_voice_agent_e9eac914-…` |
| VOICE | `Tarta2` |
| TRIGGERS | `BATTLE_START`, `HP_BELOW_25`, `WEAPON_BROKEN`, `BATTLE_END` |
| TOOLS | `usePotion`, `useAbility`, `switchWeapon`, `markObjective` |
| STATE_SHAPE required | `ts_ms`, `mechanics.in_combat`, `player.hp_pct` |
| STATE_SHAPE recommended | `scene`, `equipment.current_weapon`, `equipment.weapon_durability_pct`, `resources.healing_potions`, `resources.mana_pct`, `opposite_party.active_character`, `mechanics.dodge_ready` |
| SDK runtime defaults | tickIntervalMs=2000, minGapSec=4, forceSpeak=false |

The Unity adapter just needs to push that state. Backend handles
agent persona, sample lines, trigger evaluation, tool routing.

## One-time setup

### 1. Get Unity 3D Game Kit

Download via Unity Hub → Learn → "3D Game Kit", or from the Asset Store.
Open the project in Unity 2022.3 LTS (or Unity 6).

### 2. Copy the EmotionEngine plugin into the kit

```bash
# From this repo's root:
cp -R Assets/Plugins/EmotionEngine \
    /path/to/3DGameKit/Assets/Plugins/
```

Unity will detect the asmdef and compile the assembly automatically.

### 3. Build + drop in the native dylib

On macOS:

```bash
cd /Volumes/msd512/project/GameVoiceNativeSDK
cmake -S . -B build-macos -G Ninja        # Ninja required: Swift bridge
cmake --build build-macos
cp build-macos/libGameVoiceNativeSDK.dylib \
    /path/to/3DGameKit/Assets/Plugins/EmotionEngine/macOS/
```

Then in Unity Editor → click the .dylib → Inspector → set
"Editor + Standalone macOS only" so it loads in Play Mode.

> **Faster path:** `./scripts/install_to_unity_project.sh
> /path/to/3DGameKit` does the build + copy + .meta generation in
> one shot. See [MACOS_BUILD_WALKTHROUGH.md](MACOS_BUILD_WALKTHROUGH.md)
> for the zero-to-runnable Mac recipe (Unity Hub install through
> Standalone .app build).

> **Note (voice scope).** Until Track A (native macOS Flutter
> embedding) lands, the dylib provides HTTP + state push +
> dialogue events but **not** live Ultravox voice. Dialogue
> envelopes arrive at `OnDialogueJson` with the agent's text;
> the text-only path lets you build and test everything else
> today. When voice ships, `auto_join_via_backend = true` (default)
> will open the call automatically; no Unity-side code change.

### 4. Configure credentials

Create a `Resources/EmotionEngineConfig.json` (or set values in the
Inspector on your `EmotionEngineBridge` GameObject):

```json
{
  "BackendBaseUrl": "https://backend.aihumanity.io",
  "SdkApiKey":     "sdk_84098de807b5e8a226",
  "SdkKeySecret":  "mNLE0rz0DY5YyUXLxOlKn8JzUhwC2lcndtzSTnUJ3UB17b8y-UXNuYOSgfaTpJMJ",
  "Username":      "ellen-player-1",
  "GameId":        "game_994e7ab8-abee-450b-91b3-77abccf8ecd0",
  "AgentName":     "",
  "Voice":         "",
  "ClientAppType": "unity_3dgk_demo"
}
```

> **Rotate the secret before shipping** — these values were sent in
> chat for our dev setup; not production-grade.

### 5. Wire the bridge in a scene

Create an empty GameObject "GameVoice" in your bootstrap scene, mark
DontDestroyOnLoad, attach `EmotionEngineBridge`. From a script:

```csharp
using EmotionEngine;
using UnityEngine;

public class GameKitEmotionAdapter : MonoBehaviour
{
    public EmotionEngineBridge Bridge;
    public EllenStats Player;       // your existing player ref
    public Inventory Inventory;     // your existing inventory ref

    void Awake()
    {
        Bridge.StateBuilder = BuildState;
        Bridge.OnDialogueJson += OnDialogue;
        Bridge.OnClientTool = HandleTool;
    }

    object BuildState() => new GameState {
        ts_ms = (long)(System.DateTime.UtcNow.Subtract(
            new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)).TotalMilliseconds),
        scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
        mechanics = new Mechanics { in_combat = CombatManager.IsEngaged },
        player = new Player {
            hp_pct = Player.health / Player.maxHealth,
            is_down = Player.health <= 0,
        },
        equipment = new Equipment {
            current_weapon = Player.equippedWeapon,
        },
        resources = new Resources {
            healing_potions = Inventory.PotionCount,
        },
    };

    void OnDialogue(string envelopeJson)
    {
        var env = JsonUtility.FromJson<DialogueEnvelope>(envelopeJson);
        Debug.Log($"[Vega] {env.text}");
        // TODO: surface in UI / subtitles
    }

    string HandleTool(string toolName, string invocationId, string parametersJson)
    {
        // Schedule the actual game mutation on main thread:
        Bridge.PostToMainThread(() => DispatchTool(toolName, parametersJson));
        return "{\"result\":\"{\\\"ok\\\":true,\\\"queued\\\":true}\"}";
    }

    void DispatchTool(string toolName, string parametersJson)
    {
        switch (toolName)
        {
            case "usePotion":  Inventory.UseHealthPack(); break;
            case "useAbility": /* trigger ability on Player */ break;
            case "switchWeapon": /* swap weapon */ break;
            case "markObjective": /* place marker */ break;
        }
    }

    // ---- shapes matching backend stateShape (UnityEngine.JsonUtility-friendly)

    [System.Serializable] class GameState {
        public long ts_ms; public string scene;
        public Mechanics mechanics; public Player player;
        public Equipment equipment; public Resources resources;
    }
    [System.Serializable] class Mechanics { public bool in_combat; public bool dodge_ready; }
    [System.Serializable] class Player    { public float hp_pct; public bool is_down; }
    [System.Serializable] class Equipment { public string current_weapon; public float weapon_durability_pct; }
    [System.Serializable] class Resources { public int healing_potions; public float mana_pct; }
    [System.Serializable] class DialogueEnvelope { public string text; public string emotion; public string trigger; }
}
```

### 6. Run

Enter Play Mode. Console will show:

```
[EmotionEngine] runtime started; session=sdk-... call=(none)
[gv] game-config applied: agent_name='studio_va_voice_agent_e9eac914-...' voice='Tarta2'
...
[Vega] Contact — stay sharp.    ← when you walk into a robot fight
[Vega] Critical! Potion now.    ← when Ellen's HP drops below 25%
```

## Three things this validates

1. **SDK contract is engine-agnostic.** Same dylib, same backend
   routes, same JSON shapes — Python (Tuxemon) and C#/Unity hit them
   the same way.
2. **Per-game config from dashboard.** Vega's persona, tools, sample
   lines came from the `action_rpg` template; zero Unity code knows
   any of those values.
3. **Generic trigger engine.** The same JSON-DSL conditions
   (`mechanics.in_combat` transition, `player.hp_pct < 0.25`) that
   fire for Tuxemon also fire for Unity — read from
   `gameTriggerCatalogs`, evaluated against whatever state shape the
   game pushes.

## Voice (when Track A lands)

The C# wrapper's lifecycle, config, and event surfaces don't change.
Once `libGameVoiceNativeSDK.dylib` ships full Ultravox support for
macOS, `auto_join_via_backend = true` (default) makes the runtime open
the call on Start, and the agent speaks audibly through the system
speakers. Player mic capture is automatic too. No Unity-side change.

Until then: dialogue arrives as text via `OnDialogueJson` — show it
in subtitles or a UI panel and you have a full demo without voice.

## Where the bug-budget is

| Risk | Where | Mitigation |
|---|---|---|
| Tool-callback runs on worker thread | `EmotionEngineBridge.OnClientTool` | Always `PostToMainThread(...)` for game mutations; return ack synchronously |
| C# delegate GC'd while native side holds the function pointer | `EmotionEngineRuntime` | Delegates stored as instance fields; survives until `Dispose` |
| State packets pushed before `IsRunning` | `Bridge.PushStateNow` | Guarded; silent no-op |
| Mac dylib missing or wrong arch | `Plugins/EmotionEngine/macOS/` | README step 3; Unity Editor shows `DllNotFoundException` clearly |
| `JsonUtility` doesn't handle Dictionary fields | State + Tool args | Use plain `[Serializable]` classes (this README's sample uses them) or swap in `Newtonsoft.Json` |
