# Mac walkthrough: from zero to a runnable Unity build

End-to-end recipe for a Mac with no Unity installed yet. ~1 hour of
clock time, ~15 minutes of hands-on; the rest is downloads.

Scope today: data plane + TTS playback only. Mic-in / agent voice
need Track A Step 5 (real Ultravox Swift SDK) — currently the dylib's
voice provider is a stub. See [README.md §Voice](README.md#voice-when-track-a-lands)
for what flips on when that lands.

---

## 0. Prerequisites (~2 min)

Apple Silicon Mac, macOS 13+. Open Terminal and check:

```bash
uname -m            # → arm64
xcode-select -p     # → /Applications/Xcode.app/... (full Xcode, NOT just CLT)
brew --version      # if not installed: https://brew.sh
```

If `xcode-select -p` returns `/Library/Developer/CommandLineTools`,
install the full **Xcode** from the Mac App Store first — Swift
compilation needs it.

Then install the two build deps:

```bash
brew install cmake ninja
```

> The dylib uses Swift now (for the macOS voice bridge), and CMake's
> Swift support requires the Ninja generator. If you only have
> `cmake` + `make`, the dylib will fail to configure.

---

## 1. Install Unity Hub (~3 min, GUI)

1. Go to <https://unity.com/download> and click **Download for Mac**.
2. Open the downloaded `UnityHubSetup.dmg`, drag to `/Applications`.
3. Launch **Unity Hub.app**. Sign in with a Unity ID (free).

---

## 2. Install a Unity Editor (~20 min, mostly download)

In Unity Hub:

1. **Installs** tab → **Install Editor**.
2. Pick **Unity 2022.3 LTS** (any 2022.3.x version is fine; the 3D
   Game Kit was tested against this range).
3. Modules: tick **Mac Build Support (Mono)**. Leave Linux / Windows
   build support off unless you also want those.
4. Install. Coffee.

---

## 3. Get the Unity 3D Game Kit project (~5 min, GUI + git)

Two ways. Pick one.

**Option A — Unity-Technologies/3DGameKit-Lite (GitHub, fastest):**

```bash
cd ~/Projects     # or wherever you keep code
git clone https://github.com/Unity-Technologies/3DGameKit-Lite.git
```

Then in Unity Hub: **Projects → Add** → pick `~/Projects/3DGameKit-Lite`.
First open will trigger an editor-version upgrade dialog; click
**Continue** to import against your 2022.3 LTS.

**Option B — Unity Hub Templates (full 3D Game Kit, slower import):**

In Unity Hub → **Projects → New project** → search **"3D Game Kit"**
in templates → **Create project** named e.g. `3DGameKit`.

The full Game Kit downloads ~1 GB and takes ~10 min to import.
3DGameKit-Lite is enough for our demo.

---

## 4. Install the EmotionEngine plugin + dylib (~30 sec, scripted)

From this repo:

```bash
cd /Volumes/msd512/project/Unity3DGameKit-EmotionEngine
./scripts/install_to_unity_project.sh ~/Projects/3DGameKit-Lite
```

The script:
- Builds `libGameVoiceNativeSDK.dylib` via CMake + Ninja + Swift
- Copies `Assets/Plugins/EmotionEngine/` into the Unity project
- Drops the dylib at `Assets/Plugins/EmotionEngine/macOS/`
- Writes a `.meta` sidecar tagging it Editor + Standalone-macOS / ARM64

If your `GameVoiceNativeSDK` checkout lives somewhere other than
`/Volumes/msd512/project/GameVoiceNativeSDK`, set the path explicitly:

```bash
GAMEVOICE_NATIVE_SDK=~/Projects/GameVoiceNativeSDK \
  ./scripts/install_to_unity_project.sh ~/Projects/3DGameKit-Lite
```

---

## 5. Wire the bridge in a scene (~3 min, Unity Editor)

Re-open the Unity project (or let it pick up the new plugin files —
look for a `[EmotionEngine] runtime started` log later as confirmation
that compilation succeeded).

1. **Open the bootstrap scene.** For 3DGameKit-Lite: `Assets/3DGamekit
   /Scenes/MainMenu.unity` or `Start.unity`.
2. **Create a GameObject:** Hierarchy panel → right-click → **Create
   Empty**. Name it `GameVoice`.
3. **Make it persist:** in Inspector, check **DontDestroyOnLoad** if
   you have a bootstrap script (or attach a tiny script with
   `Object.DontDestroyOnLoad(gameObject)` in `Awake`).
4. **Attach the bridge:** Inspector → **Add Component** → search
   "EmotionEngineBridge" → add.
5. **Fill in the Inspector fields.** Expand the **Config** foldout
   on the bridge component (all SDK fields live there, not directly
   on the MonoBehaviour):

   | Config field | Value |
   |---|---|
   | Backend Base Url | `https://backend.aihumanity.io` |
   | Sdk Api Key | `sdk_84098de807b5e8a226` |
   | Sdk Key Secret | `mNLE0rz0DY5YyUXLxOlKn8JzUhwC2lcndtzSTnUJ3UB17b8y-UXNuYOSgfaTpJMJ` |
   | Username | `ellen-player-1` |
   | Game Id | `game_994e7ab8-abee-450b-91b3-77abccf8ecd0` |
   | Agent Name | *(leave blank — backend supplies it)* |
   | Voice | *(leave blank — backend supplies it)* |
   | Client App Type | `unity_3dgk_demo` |
   | Tick Interval Ms | `2000` |
   | **Auto Join Via Backend** | **uncheck this** *(see note)* |

   > **Why uncheck Auto Join?** The default is `true`. With it on,
   > the dylib constructs the Swift Ultravox provider — which is a
   > stub today. Harmless, but spawns no audio. Unchecking it routes
   > the runtime through `NoopProviderClient` (HTTP-only mode),
   > matching what the existing macOS CLI smoke exercises. Re-tick
   > after Track A Step 5 ships.

6. **(Optional) Add a state builder.** Without one, the bridge pushes
   minimal state (just `ts_ms`). For triggers to actually fire you
   need to push at least `mechanics.in_combat` and `player.hp_pct`.
   The full `GameKitEmotionAdapter` example in the README hooks into
   Ellen's stats and the combat manager.

---

## 6. Test in Play Mode (~1 min)

Hit ▶ (Play) in the editor. Open the **Console** window (Window →
General → Console).

Expected output within ~3 seconds:

```
[EmotionEngine] runtime started; session=sdk-...
[gv] game-config applied: agent_name='studio_va_voice_agent_e9eac914-...' voice='Tarta2'
```

To force a dialogue line, drop this script onto any active GameObject
and tick **Drop Hp Now** in the Inspector at runtime:

```csharp
using UnityEngine;
using EmotionEngine;

public class HPSmoke : MonoBehaviour {
  public EmotionEngineBridge Bridge;
  public bool DropHpNow;

  void Update() {
    if (!DropHpNow || Bridge?.Runtime == null) return;
    DropHpNow = false;
    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Bridge.Runtime.SetLatestStateJson(
      "{\"ts_ms\":" + ts +
      ",\"scene\":\"smoke\",\"mechanics\":{\"in_combat\":true}," +
      "\"player\":{\"hp_pct\":0.15}}");
  }

  void OnEnable() {
    if (Bridge != null) Bridge.OnDialogueJson += s => Debug.Log("[Vega] " + s);
  }
}
```

You should see a `HP_BELOW_25` trigger fire → `[Vega]` dialogue arrive
in the Console → an MP3 stream download → AVAudioPlayer playback
through your Mac speakers.

---

## 7. Build a standalone .app for Mac (~2 min)

Once Play Mode is happy:

1. **File → Build Settings**.
2. **Platform**: select **Mac, Linux & Windows Standalone**, then
   **Target Platform: macOS**, **Architecture: Apple Silicon**.
3. **Player Settings → Mac Standalone → Other Settings**:
   - **Allow 'unsafe' Code**: not needed; our wrapper is pure managed.
   - **Microphone Usage Description**: set something
     (e.g. "Voice companion") — required by macOS even though mic
     isn't active yet under the stub, and required for the eventual
     real provider.
4. Back in Build Settings → **Build**. Pick an output folder.

Unity will produce `<project>.app`. Right-click → **Show Package
Contents** → `Contents/Plugins/` — confirm `libGameVoiceNativeSDK.dylib`
is in there. If it isn't, the `.meta` sidecar from step 4 didn't tag
the Standalone target correctly; open the dylib in Unity Editor →
Inspector → tick **Standalone macOS** → Apply → rebuild.

Double-click the `.app` to run. macOS will prompt about the
microphone permission the first time.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `DllNotFoundException: GameVoiceNativeSDK` in Editor Console | The dylib's .meta is missing or didn't tag Editor. Re-run the installer; or in Unity click the dylib → Inspector → tick "Editor" → Apply. |
| `swiftc not found` from installer | Install full Xcode (Mac App Store), then `sudo xcode-select -s /Applications/Xcode.app/Contents/Developer`. |
| `ninja not found` | `brew install ninja`. |
| `Swift language not supported by "Unix Makefiles" generator` | The build dir was created with the old Makefiles generator. `rm -rf $GAMEVOICE_NATIVE_SDK/build-macos` and re-run the installer. |
| Build succeeds but Play Mode shows `auth failed: 401` | The hardcoded SDK secret in the Inspector has been rotated. Get a fresh `SDK_KEY_ID` + `SDK_SECRET` from the dashboard and update the bridge fields. |
| `[Vega]` log appears but no audio plays | Verify AVFoundation in dylib: `otool -L libGameVoiceNativeSDK.dylib | grep AVFoundation`. Should show one line. If not, the dylib was built before Step 2 landed — rebuild with `cmake --build build-macos --clean-first`. |
| Built `.app` runs but plays no sound | Same fix as above — confirm the `.dylib` inside `Contents/Plugins/` is the post-Step-2 build. |
