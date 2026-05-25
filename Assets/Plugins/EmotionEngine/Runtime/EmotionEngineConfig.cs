// SPDX-License-Identifier: MIT
//
// Managed config struct. Mirrors GV_Config 1:1 but with idiomatic
// C# types — bool instead of int flags, string instead of IntPtr.
//
// Marshalled into the native struct by EmotionEngineRuntime; callers
// never touch IntPtr or GV_Config directly.

using System;
using UnityEngine;

namespace EmotionEngine
{
    [Serializable]
    public class EmotionEngineConfig
    {
        // ---- backend + credentials ----
        public string BackendBaseUrl = "https://backend.aihumanity.io";
        public string SdkApiKey = "";          // x-sdk-key-id value (sdk_...)
        public string SdkKeySecret = "";       // HMAC signing secret
        public string BearerToken = "";        // legacy: app-user JWT for /game/voice/secure/*. Leave blank when using sdk_*.
        public string Username = "";           // becomes runtime token sub
        public string GameId = "";             // required to fetch backend per-game config

        // ---- voice agent (overrides backend's default_agent if set) ----
        public string AgentName = "";          // empty → backend default_agent.agent_name wins
        public string Voice = "";              // empty → backend default
        public string Prompt = "";             // empty → SDK default
        public string ResponseMode = "llm";    // "auto" | "llm" | "sample"

        // ---- runtime tuning (mirrors GV_Config exactly) ----
        public int TickIntervalMs = 1200;
        public int MinGapSec = 8;
        public int UserSpeakingWindowMs = 2500;
        public int PlayedAckFallbackMs = 9000;
        public int SpeakingTickFloorMs = 2200;
        public int DedupeWindowMs = 10000;
        public int EventTtlMs = 10000;
        public int MaxPendingAcks = 1;
        public bool ForceSpeak = true;
        public bool AutoJoinViaBackend = true;
        public bool UseRuntimeSdkRoutes = true;     // default to new /v1/sdk + /v1/game/voice paths
        public bool SkipGameConfigFetch = false;

        // ---- telemetry headers (informational only on macOS) ----
        public string ClientPlatform = "";    // empty → SDK infers from host
        public string ClientAppType = "unity";

        // ---- opposite party (boss agent flow; off by default) ----
        public bool OppositePartyEnabled = false;
        public string OppositeAgentName = "";
        public string OppositePartyResponseFormat = "";

        /// <summary>
        /// Validate caller-required fields. Mirrors the native SDK's
        /// validation: agent_name is intentionally optional because
        /// the backend's /v1/game/config can supply it via game_id.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(BackendBaseUrl))
                throw new ArgumentException("BackendBaseUrl is required");
            if (string.IsNullOrWhiteSpace(SdkApiKey) && string.IsNullOrWhiteSpace(BearerToken))
                throw new ArgumentException("Either SdkApiKey (with SdkKeySecret) or BearerToken must be set");
            if (!string.IsNullOrWhiteSpace(SdkApiKey) && string.IsNullOrWhiteSpace(SdkKeySecret))
                throw new ArgumentException("SdkKeySecret is required when SdkApiKey is set");
            if (string.IsNullOrWhiteSpace(Username))
                throw new ArgumentException("Username is required");
            if (string.IsNullOrWhiteSpace(GameId) && !SkipGameConfigFetch)
                Debug.LogWarning("[EmotionEngine] GameId is empty; backend won't be able to serve per-game config.");
        }
    }
}
