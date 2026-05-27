// SPDX-License-Identifier: MIT
//
// Managed C# wrapper around the native runtime handle.
//
// Lifecycle: construct → register handlers → Start() → push state /
// post events → Stop() → Dispose().
//
// Thread safety:
//   - Public mutators (SetLatestStateJson, PostEventJson, Setters)
//     are safe to call from any thread. The native runtime guards its
//     own state with mutexes.
//   - Event handlers (OnDialogueJson, OnTraceJson, etc.) fire on the
//     native runtime's worker thread, NOT Unity's main thread. Unity
//     APIs cannot be called from there — use EmotionEngineBridge
//     (MonoBehaviour) which dispatches to Update() for you. Or marshal
//     yourself with a UnityMainThreadDispatcher.

using System;
using System.Runtime.InteropServices;
using System.Text;
using EmotionEngine.Native;
using UnityEngine;

namespace EmotionEngine
{
    public sealed class EmotionEngineRuntime : IDisposable
    {
        const int GetBufferCapacity = 64 * 1024;   // 64 KB plenty for GameConfig JSON

        IntPtr _handle = IntPtr.Zero;
        readonly EmotionEngineConfig _config;
        bool _disposed;

        // session_id from the HMAC mint response. Must travel back to
        // the backend on every /v1/game/voice/* call (in the JSON body's
        // "session_id" field) so it matches the JWT's session_id claim,
        // otherwise the backend rejects with "session_mismatch".
        string _mintedSessionId;

        // Pinned native string allocations — kept alive for the
        // lifetime of the runtime so the C side's pointer stays valid.
        GCHandle[] _pinnedStrings;
        IntPtr _backendBaseUrl, _bearerToken, _username, _agentName, _prompt, _voice,
               _responseMode, _gameId, _clientPlatform, _clientAppType;

        // Delegate slots — must be kept alive so GC doesn't reclaim
        // them while the native side still has the function pointer.
        GV_LogCallback _logDelegate;
        GV_DialogueCallback _dialogueDelegate;
        GV_TraceCallback _traceDelegate;
        GV_StatusCallback _statusDelegate;
        GV_PlaybackCallback _playbackDelegate;
        GV_ClientToolCallback _clientToolDelegate;

        // ---- Events ----

        /// <summary>Raw dialogue envelope JSON. Fires on worker thread.</summary>
        public event Action<string> OnDialogueJson;

        /// <summary>Raw trace record JSON. Fires on worker thread.</summary>
        public event Action<string> OnTraceJson;

        /// <summary>Raw status update JSON. Fires on worker thread.</summary>
        public event Action<string> OnStatusJson;

        /// <summary>Raw playback event JSON. Fires on worker thread.</summary>
        public event Action<string> OnPlaybackJson;

        /// <summary>Log lines from the SDK. Fires on worker thread.</summary>
        public event Action<string> OnLog;

        /// <summary>
        /// Tool invocation arriving from Ultravox via the SDK.
        ///   Args: (toolName, invocationId, parametersJson)
        ///   Return: response envelope JSON string. Return null/empty
        ///   to use the SDK's built-in fallback ({"ok":true}).
        ///
        /// Fires on the SDK's tool-dispatch thread. Marshal to the
        /// Unity main thread yourself if you need to touch GameObjects.
        /// </summary>
        public Func<string, string, string, string> OnClientTool;

        public EmotionEngineRuntime(EmotionEngineConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // ---------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------

        public void Start()
        {
            ThrowIfDisposed();
            if (_handle != IntPtr.Zero)
                throw new InvalidOperationException("Already started");
            _config.Validate();

            // Mint a runtime JWT via HMAC if SDK key creds are present and
            // BearerToken isn't already set. Mirrors game_voice_sdk's auth.py.
            // Without this, the native SDK has no Bearer header for any
            // /v1/game/voice/* call and the backend returns
            // "invalid_runtime_token: Missing runtime bearer token".
            string effectiveBearerToken = _config.BearerToken;
            if (string.IsNullOrWhiteSpace(effectiveBearerToken)
                && !string.IsNullOrWhiteSpace(_config.SdkApiKey))
            {
                var mint = SdkAuth.MintRuntimeToken(_config);
                effectiveBearerToken = mint.RuntimeSessionToken;
                // Remember the session_id so we can inject it into every
                // state / event JSON. The native runtime has no
                // GV_SetSessionId; without this, every /v1/game/voice/*
                // POST sends "session_id":"gamevoice-session" (the
                // native default) and the backend rejects with
                // "session_mismatch" because the JWT's session_id claim
                // is `unity-...`.
                _mintedSessionId = mint.SessionId;
            }

            var native = new GV_Config();
            GV.GV_DefaultConfig(ref native);

            // Pin strings + write pointers into the struct.
            _backendBaseUrl = PinUtf8(_config.BackendBaseUrl);
            _bearerToken = PinUtf8(effectiveBearerToken);
            _username = PinUtf8(_config.Username);
            _agentName = PinUtf8(_config.AgentName);
            _prompt = PinUtf8(_config.Prompt);
            _voice = PinUtf8(_config.Voice);
            _responseMode = PinUtf8(_config.ResponseMode);
            _gameId = PinUtf8(_config.GameId);

            native.backend_base_url = _backendBaseUrl;
            native.bearer_token = _bearerToken;
            native.username = _username;
            native.agent_name = _agentName;
            native.prompt = _prompt;
            native.voice = _voice;
            native.response_mode = _responseMode;
            native.game_id = _gameId;
            native.tick_interval_ms = _config.TickIntervalMs;
            native.min_gap_sec = _config.MinGapSec;
            native.user_speaking_window_ms = _config.UserSpeakingWindowMs;
            native.played_ack_fallback_ms = _config.PlayedAckFallbackMs;
            native.speaking_tick_floor_ms = _config.SpeakingTickFloorMs;
            native.dedupe_window_ms = _config.DedupeWindowMs;
            native.event_ttl_ms = _config.EventTtlMs;
            native.max_pending_acks = _config.MaxPendingAcks;
            native.force_speak = _config.ForceSpeak ? 1 : 0;
            native.auto_join_via_backend = _config.AutoJoinViaBackend ? 1 : 0;
            native.use_runtime_sdk_routes = _config.UseRuntimeSdkRoutes ? 1 : 0;
            native.skip_game_config_fetch = _config.SkipGameConfigFetch ? 1 : 0;

            CheckResult(GV.GV_Create(ref native, out _handle), nameof(GV.GV_Create));

            // Register callbacks BEFORE Start so we don't miss early events.
            _logDelegate = OnNativeLog;
            _dialogueDelegate = OnNativeDialogue;
            _traceDelegate = OnNativeTrace;
            _statusDelegate = OnNativeStatus;
            _playbackDelegate = OnNativePlayback;
            _clientToolDelegate = OnNativeClientTool;

            GV.GV_SetLogCallback(_handle, _logDelegate, IntPtr.Zero);
            GV.GV_SetDialogueCallback(_handle, _dialogueDelegate, IntPtr.Zero);
            GV.GV_SetTraceCallback(_handle, _traceDelegate, IntPtr.Zero);
            GV.GV_SetStatusCallback(_handle, _statusDelegate, IntPtr.Zero);
            GV.GV_SetPlaybackCallback(_handle, _playbackDelegate, IntPtr.Zero);
            GV.GV_SetClientToolCallback(_handle, _clientToolDelegate, IntPtr.Zero);

            if (_config.OppositePartyEnabled)
            {
                using var opp = new ScopedUtf8(_config.OppositeAgentName);
                using var fmt = new ScopedUtf8(_config.OppositePartyResponseFormat);
                GV.GV_SetOppositePartyOptions(_handle,
                    enabled: 1, oppositeAgentName: opp.Ptr, oppositePartyResponseFormat: fmt.Ptr);
            }

            CheckResult(GV.GV_Start(_handle), nameof(GV.GV_Start));
        }

        public void Stop()
        {
            if (_handle == IntPtr.Zero) return;
            try { GV.GV_Stop(_handle); }
            catch (Exception e) { Debug.LogWarning("[EmotionEngine] GV_Stop raised: " + e.Message); }
            try { GV.GV_Destroy(_handle); }
            catch (Exception e) { Debug.LogWarning("[EmotionEngine] GV_Destroy raised: " + e.Message); }
            _handle = IntPtr.Zero;
            FreePinnedStrings();
        }

        public bool IsRunning =>
            _handle != IntPtr.Zero && GV.GV_IsRunning(_handle) != 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ---------------------------------------------------------------
        // Inputs
        // ---------------------------------------------------------------

        public void SetLatestStateJson(string stateJson)
        {
            ThrowIfNotRunning();
            string injected = InjectSessionId(stateJson);
            using var s = new ScopedUtf8(injected);
            CheckResult(GV.GV_SetLatestStateJson(_handle, s.Ptr), nameof(GV.GV_SetLatestStateJson));
        }

        public void RunSingleTick() =>
            CheckResult(GV.GV_RunSingleTick(_handle), nameof(GV.GV_RunSingleTick));

        public void PostEventJson(string eventJson)
        {
            ThrowIfNotRunning();
            string injected = InjectSessionId(eventJson);
            using var s = new ScopedUtf8(injected);
            CheckResult(GV.GV_PostEventJson(_handle, s.Ptr), nameof(GV.GV_PostEventJson));
        }

        // Insert `"session_id":"<minted>"` into a top-level JSON object
        // if the caller didn't already include one. The native runtime
        // adopts whatever session_id arrives in the state/event JSON
        // (see BackendGameVoiceRuntime::MergeSessionId), so this is the
        // only way to keep it aligned with the JWT's session_id claim.
        // Naive string-level injection — only safe because state/event
        // JSON we produce is always a top-level object.
        string InjectSessionId(string json)
        {
            if (string.IsNullOrEmpty(_mintedSessionId) || string.IsNullOrEmpty(json))
                return json;
            if (json.IndexOf("\"session_id\"", System.StringComparison.Ordinal) >= 0)
                return json; // caller already supplied one; respect it
            string trimmed = json.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return json; // not a top-level object; leave alone
            int openBrace = json.IndexOf('{');
            string escaped = _mintedSessionId
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
            string insert = "\"session_id\":\"" + escaped + "\"";
            // Empty object "{}" → "{"session_id":"..."}"
            // Non-empty → insert before the first existing field, with a comma.
            int afterOpen = openBrace + 1;
            // skip whitespace
            while (afterOpen < json.Length && char.IsWhiteSpace(json[afterOpen])) afterOpen++;
            if (afterOpen < json.Length && json[afterOpen] == '}')
            {
                return json.Substring(0, openBrace + 1) + insert + json.Substring(afterOpen);
            }
            return json.Substring(0, openBrace + 1) + insert + "," + json.Substring(openBrace + 1);
        }

        public void SubmitTranscriptJson(string transcript)
        {
            ThrowIfNotRunning();
            using var s = new ScopedUtf8(transcript);
            CheckResult(GV.GV_SubmitTranscriptJson(_handle, s.Ptr), nameof(GV.GV_SubmitTranscriptJson));
        }

        public void SetLanguage(string lang)
        {
            using var s = new ScopedUtf8(lang);
            CheckResult(GV.GV_SetLanguage(_handle, s.Ptr), nameof(GV.GV_SetLanguage));
        }

        public void SetSelectedToolsJson(string selectedToolsJson)
        {
            using var s = new ScopedUtf8(selectedToolsJson);
            CheckResult(GV.GV_SetSelectedToolsJson(_handle, s.Ptr), nameof(GV.GV_SetSelectedToolsJson));
        }

        public void SetCallDetails(string callId, string joinUrl)
        {
            using var a = new ScopedUtf8(callId);
            using var b = new ScopedUtf8(joinUrl);
            CheckResult(GV.GV_SetCallDetails(_handle, a.Ptr, b.Ptr), nameof(GV.GV_SetCallDetails));
        }

        // ---------------------------------------------------------------
        // Getters
        // ---------------------------------------------------------------

        public string GetSessionId() => ReadOutString(GV.GV_GetSessionId);
        public string GetCallId() => ReadOutString(GV.GV_GetCallId);
        public string GetBridgeStatus() => ReadOutString(GV.GV_GetBridgeStatus);
        public string GetLastBridgeError() => ReadOutString(GV.GV_GetLastBridgeError);
        public string GetLastError() => ReadOutString(GV.GV_GetLastError);
        public string GetEmotionSnapshotJson() => ReadOutString(GV.GV_GetEmotionSnapshotJson);
        public string GetContractVersion() => ReadOutString(GV.GV_GetContractVersion);
        public string GetGameConfigJson() => ReadOutString(GV.GV_GetGameConfigJson);
        public string GetResolvedAgentName() => ReadOutString(GV.GV_GetResolvedAgentName);
        public string GetResolvedVoice() => ReadOutString(GV.GV_GetResolvedVoice);
        public void RefreshGameConfig() =>
            CheckResult(GV.GV_RefreshGameConfig(_handle), nameof(GV.GV_RefreshGameConfig));

        // ---------------------------------------------------------------
        // Internal callback bridges (run on the native worker thread)
        // ---------------------------------------------------------------

        void OnNativeLog(IntPtr message, IntPtr userData) =>
            SafeInvoke(OnLog, PtrToString(message));

        void OnNativeDialogue(IntPtr json, IntPtr userData) =>
            SafeInvoke(OnDialogueJson, PtrToString(json));

        void OnNativeTrace(IntPtr json, IntPtr userData) =>
            SafeInvoke(OnTraceJson, PtrToString(json));

        void OnNativeStatus(IntPtr json, IntPtr userData) =>
            SafeInvoke(OnStatusJson, PtrToString(json));

        void OnNativePlayback(IntPtr json, IntPtr userData) =>
            SafeInvoke(OnPlaybackJson, PtrToString(json));

        int OnNativeClientTool(IntPtr toolNamePtr, IntPtr invocationIdPtr, IntPtr parametersJsonPtr,
                               IntPtr outResponseJson, int outResponseJsonLen, IntPtr userData)
        {
            try
            {
                var name = PtrToString(toolNamePtr);
                var inv = PtrToString(invocationIdPtr);
                var args = PtrToString(parametersJsonPtr);

                string response = null;
                var handler = OnClientTool;
                if (handler != null)
                {
                    try { response = handler(name, inv, args); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        response = "{\"errorType\":\"implementation-error\",\"errorMessage\":\"" +
                                   EscapeJson(e.Message) + "\"}";
                    }
                }
                if (string.IsNullOrEmpty(response))
                    response = "{\"result\":\"{\\\"ok\\\":true}\"}";

                var bytes = Encoding.UTF8.GetBytes(response);
                var copyLen = Math.Min(bytes.Length, outResponseJsonLen - 1);   // leave null
                if (copyLen > 0)
                    Marshal.Copy(bytes, 0, outResponseJson, copyLen);
                // null terminator
                Marshal.WriteByte(outResponseJson, copyLen, 0);
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return -3;
            }
        }

        // ---------------------------------------------------------------
        // Marshalling helpers
        // ---------------------------------------------------------------

        delegate int OutStringFn(IntPtr handle, IntPtr buffer, int len);

        string ReadOutString(OutStringFn fn)
        {
            if (_handle == IntPtr.Zero) return string.Empty;
            var buf = Marshal.AllocHGlobal(GetBufferCapacity);
            try
            {
                Marshal.WriteByte(buf, 0, 0);   // pre-zero
                fn(_handle, buf, GetBufferCapacity);
                return PtrToString(buf) ?? string.Empty;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string PtrToString(IntPtr p) =>
            p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        // Pin a managed string into native heap as UTF-8. Caller owns
        // the IntPtr; must be freed via FreeHGlobal when no longer
        // needed (we track them in _pinnedStrings).
        IntPtr PinUtf8(string value)
        {
            value = value ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            _pinnedStrings = AppendPinned(_pinnedStrings, ptr);
            return ptr;
        }

        static GCHandle[] AppendPinned(GCHandle[] arr, IntPtr ptr)
        {
            // We use the GCHandle[] only as a list to track IntPtrs
            // for later FreeHGlobal; the actual pin is handled by the
            // unmanaged copy. Stash IntPtr in a wrapper GCHandle of
            // a byte[].
            // (Simplifies cleanup; performance is irrelevant for ~10
            // strings allocated once at Start.)
            var stash = GCHandle.Alloc(new IntPtrBox { Ptr = ptr });
            var len = arr?.Length ?? 0;
            var next = new GCHandle[len + 1];
            if (arr != null) Array.Copy(arr, next, len);
            next[len] = stash;
            return next;
        }

        class IntPtrBox { public IntPtr Ptr; }

        void FreePinnedStrings()
        {
            if (_pinnedStrings == null) return;
            foreach (var h in _pinnedStrings)
            {
                if (!h.IsAllocated) continue;
                if (h.Target is IntPtrBox box && box.Ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(box.Ptr);
                h.Free();
            }
            _pinnedStrings = null;
        }

        static string EscapeJson(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                     .Replace("\n", "\\n").Replace("\r", "\\r");

        static void SafeInvoke(Action<string> handler, string arg)
        {
            if (handler == null) return;
            try { handler(arg); }
            catch (Exception e) { Debug.LogException(e); }
        }

        static void CheckResult(int code, string call)
        {
            if (code == 0) return;
            throw new InvalidOperationException($"{call} failed with code {code}");
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmotionEngineRuntime));
        }

        void ThrowIfNotRunning()
        {
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("Runtime not started");
        }

        // ---- Scoped UTF-8 string (auto-frees on dispose) ----
        struct ScopedUtf8 : IDisposable
        {
            public IntPtr Ptr;
            public ScopedUtf8(string value)
            {
                value = value ?? string.Empty;
                var bytes = Encoding.UTF8.GetBytes(value + "\0");
                Ptr = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, Ptr, bytes.Length);
            }
            public void Dispose()
            {
                if (Ptr != IntPtr.Zero) { Marshal.FreeHGlobal(Ptr); Ptr = IntPtr.Zero; }
            }
        }
    }
}
