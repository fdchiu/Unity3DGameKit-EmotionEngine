// SPDX-License-Identifier: MIT
//
// P/Invoke bindings for GameVoiceNativeSDK's C ABI.
// Mirrors GameVoiceSdk.h 1:1.
//
// The dylib (libGameVoiceNativeSDK.dylib on macOS,
// GameVoiceNativeSDK.dll on Windows, libGameVoiceNativeSDK.so on
// Linux) must be placed in Assets/Plugins/<platform>/ for Unity's
// loader to pick it up. See README for the build-and-copy steps.

using System;
using System.Runtime.InteropServices;

namespace EmotionEngine.Native
{
    /// <summary>
    /// Marshals GV_Config. Mirrors the C struct exactly — field order
    /// matters for ABI compatibility. Strings are UTF-8 / ANSI on the
    /// C side; the runtime wrapper handles string allocation in pinned
    /// buffers so callers never touch IntPtr directly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GV_Config
    {
        public IntPtr flutter_data_dir;           // wchar_t* — only used on Windows; macOS leaves null
        public IntPtr backend_base_url;
        public IntPtr bearer_token;
        public IntPtr username;
        public IntPtr agent_name;
        public IntPtr prompt;
        public IntPtr voice;
        public IntPtr response_mode;
        public int tick_interval_ms;
        public int min_gap_sec;
        public int user_speaking_window_ms;
        public int played_ack_fallback_ms;
        public int speaking_tick_floor_ms;
        public int dedupe_window_ms;
        public int event_ttl_ms;
        public int max_pending_acks;
        public int force_speak;
        public int auto_join_via_backend;
        public int use_runtime_sdk_routes;
        // Phase 1 generalization (appended for ABI compat):
        public IntPtr game_id;
        public int skip_game_config_fetch;
    }

    // ---- Callback signatures ----

    public delegate void GV_LogCallback(IntPtr message, IntPtr userData);
    public delegate void GV_DialogueCallback(IntPtr dialogueJson, IntPtr userData);
    public delegate void GV_TraceCallback(IntPtr traceJson, IntPtr userData);
    public delegate void GV_StatusCallback(IntPtr statusJson, IntPtr userData);
    public delegate void GV_PlaybackCallback(IntPtr playbackJson, IntPtr userData);

    /// <summary>
    /// Returns 0 on success. Caller fills the out_response_json buffer
    /// (UTF-8, null-terminated, ≤ out_response_json_len bytes) with
    /// the tool response envelope. See SDK_API.md for the envelope shape.
    /// </summary>
    public delegate int GV_ClientToolCallback(
        IntPtr toolName,
        IntPtr invocationId,
        IntPtr parametersJson,
        IntPtr outResponseJson,
        int outResponseJsonLen,
        IntPtr userData);

    /// <summary>
    /// Static P/Invoke surface. Calls the platform-native dylib/dll.
    /// All methods accept a GV_Handle (IntPtr) returned by GV_Create.
    /// </summary>
    public static class GV
    {
#if UNITY_IPHONE && !UNITY_EDITOR
        // iOS bundles into the main app binary as a static library.
        public const string DLL = "__Internal";
#else
        public const string DLL = "GameVoiceNativeSDK";
#endif

        // ---- Lifecycle ----

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void GV_DefaultConfig(ref GV_Config config);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_Create(ref GV_Config config, out IntPtr handle);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_Destroy(IntPtr handle);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_Start(IntPtr handle);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_Stop(IntPtr handle);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_IsRunning(IntPtr handle);

        // ---- Callback setters ----

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetLogCallback(IntPtr handle, GV_LogCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetClientToolCallback(IntPtr handle, GV_ClientToolCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetDialogueCallback(IntPtr handle, GV_DialogueCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetTraceCallback(IntPtr handle, GV_TraceCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetStatusCallback(IntPtr handle, GV_StatusCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetPlaybackCallback(IntPtr handle, GV_PlaybackCallback callback, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetSelectedToolsJson(IntPtr handle, IntPtr selectedToolsJson);

        // ---- Inputs ----

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetLatestStateJson(IntPtr handle, IntPtr stateJson);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_RunSingleTick(IntPtr handle);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_PostEventJson(IntPtr handle, IntPtr eventJson);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SubmitTranscriptJson(IntPtr handle, IntPtr transcriptJson);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetLanguage(IntPtr handle, IntPtr language);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetOppositePartyOptions(
            IntPtr handle,
            int enabled,
            IntPtr oppositeAgentName,
            IntPtr oppositePartyResponseFormat);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_SetCallDetails(IntPtr handle, IntPtr callId, IntPtr joinUrl);

        // ---- Getters (out_buffer is allocated by caller; len includes null terminator)

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetSessionId(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetCallId(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetBridgeStatus(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetLastBridgeError(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetLastError(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetEmotionSnapshotJson(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetContractVersion(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        // Phase 1 generalization getters:
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetGameConfigJson(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetResolvedAgentName(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_GetResolvedVoice(IntPtr handle, IntPtr outBuffer, int outBufferLen);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GV_RefreshGameConfig(IntPtr handle);
    }
}
