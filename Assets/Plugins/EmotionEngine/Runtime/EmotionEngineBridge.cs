// SPDX-License-Identifier: MIT
//
// MonoBehaviour that owns an EmotionEngineRuntime, drains its
// worker-thread events on Unity's main thread, and exposes simple
// Inspector-editable config.
//
// Drop one onto a persistent GameObject (e.g. inside a "Game Voice"
// prefab loaded by your bootstrap scene). It survives scene changes
// if you mark it DontDestroyOnLoad in OnEnable.

using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace EmotionEngine
{
    [DisallowMultipleComponent]
    public class EmotionEngineBridge : MonoBehaviour
    {
        [Tooltip("Configuration. Edit in the Inspector or set via code before OnEnable.")]
        public EmotionEngineConfig Config = new();

        [Tooltip("Push the latest state to the SDK every N seconds. Set to <=0 to disable auto-push and call PushStateNow manually.")]
        public float StatePushIntervalSeconds = 4f;

        [Tooltip("Build the state packet right before push. Subscribe to provide game-specific state.")]
        public Func<object> StateBuilder;

        public EmotionEngineRuntime Runtime { get; private set; }

        // ---- Events delivered on Unity main thread ----

        /// <summary>Dialogue envelope JSON (raw, marshalled to main thread).</summary>
        public event Action<string> OnDialogueJson;
        public event Action<string> OnTraceJson;
        public event Action<string> OnStatusJson;
        public event Action<string> OnPlaybackJson;
        public event Action<string> OnLog;

        /// <summary>
        /// Tool dispatch. Runs on the worker thread because the SDK
        /// needs a synchronous response. Marshal mutations to the main
        /// thread inside the handler via PostToMainThread().
        /// </summary>
        public Func<string, string, string, string> OnClientTool;

        readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        float _pushTimer;
        bool _started;

        void OnEnable()
        {
            try
            {
                Runtime = new EmotionEngineRuntime(Config);

                Runtime.OnDialogueJson += json => _mainThreadQueue.Enqueue(() => SafeInvoke(OnDialogueJson, json));
                Runtime.OnTraceJson    += json => _mainThreadQueue.Enqueue(() => SafeInvoke(OnTraceJson, json));
                Runtime.OnStatusJson   += json => _mainThreadQueue.Enqueue(() => SafeInvoke(OnStatusJson, json));
                Runtime.OnPlaybackJson += json => _mainThreadQueue.Enqueue(() => SafeInvoke(OnPlaybackJson, json));
                Runtime.OnLog          += line => _mainThreadQueue.Enqueue(() => SafeInvoke(OnLog, line));

                Runtime.OnClientTool = (name, inv, args) =>
                {
                    // OnClientTool runs synchronously on the worker
                    // thread; we cannot block it on the main thread.
                    // Callers should keep their tool handlers fast and
                    // marshal any GameObject mutation via PostToMainThread.
                    var handler = OnClientTool;
                    if (handler == null) return null;
                    try { return handler(name, inv, args); }
                    catch (Exception e) { Debug.LogException(e); return null; }
                };

                Runtime.Start();
                _started = true;
                Debug.Log($"[EmotionEngine] runtime started; session={Runtime.GetSessionId()} call={Runtime.GetCallId()}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[EmotionEngine] failed to start: {e.Message}");
                _started = false;
            }
        }

        void Update()
        {
            // Drain worker-thread events.
            while (_mainThreadQueue.TryDequeue(out var act))
            {
                try { act(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            if (!_started || StatePushIntervalSeconds <= 0f) return;

            _pushTimer += Time.unscaledDeltaTime;
            if (_pushTimer >= StatePushIntervalSeconds)
            {
                _pushTimer = 0f;
                PushStateNow();
            }
        }

        void OnDisable() => Teardown();
        void OnDestroy() => Teardown();
        void OnApplicationQuit() => Teardown();

        void Teardown()
        {
            if (Runtime == null) return;
            try { Runtime.Stop(); }
            catch (Exception e) { Debug.LogWarning("[EmotionEngine] stop raised: " + e.Message); }
            Runtime.Dispose();
            Runtime = null;
            _started = false;
        }

        // ---------------------------------------------------------------
        // Public helpers
        // ---------------------------------------------------------------

        /// <summary>Builds + pushes a state packet right now using
        /// StateBuilder (if set). Safe to call from Update.</summary>
        public void PushStateNow()
        {
            if (Runtime == null || !Runtime.IsRunning) return;
            try
            {
                var snapshot = StateBuilder?.Invoke();
                if (snapshot == null) return;
                var json = JsonUtility.ToJson(snapshot);
                Runtime.SetLatestStateJson(json);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Post a typed event from anywhere; safe from any thread.</summary>
        public void PostEvent(object eventObject)
        {
            if (Runtime == null || !Runtime.IsRunning) return;
            try
            {
                var json = JsonUtility.ToJson(eventObject);
                Runtime.PostEventJson(json);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>Schedule fn on Unity's main thread. Useful inside
        /// OnClientTool to apply game-state mutations safely.</summary>
        public void PostToMainThread(Action fn)
        {
            if (fn != null) _mainThreadQueue.Enqueue(fn);
        }

        static void SafeInvoke(Action<string> handler, string arg)
        {
            if (handler == null) return;
            try { handler(arg); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
