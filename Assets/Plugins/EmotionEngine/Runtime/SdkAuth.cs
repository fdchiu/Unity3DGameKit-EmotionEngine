// SdkAuth.cs
//
// HMAC mint: POST /v1/sdk/sessions to convert (SdkApiKey, SdkKeySecret)
// into a short-lived runtime JWT that the native SDK uses as Bearer.
// Mirrors game_voice_sdk/auth.py (Python).
//
// Canonical signing payload (newline-delimited):
//     {timestamp_ms}\n{METHOD}\n{path_without_querystring}
// Headers:
//     x-sdk-key-id, x-sdk-timestamp, x-sdk-signature
//
// Today this mints ONCE at runtime start. The native SDK has no
// bearer-token refresh hook, so if the runtime lives past the JWT
// TTL (~30 min) you'll get auth failures and need to Restart the
// runtime. A future iteration should either:
//   (a) add GV_SetBearerToken to the native ABI + a refresh timer in
//       C#; or
//   (b) move HMAC awareness into the native runtime itself.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace EmotionEngine
{
    internal static class SdkAuth
    {
        /// <summary>
        /// Posts to /v1/sdk/sessions and returns the runtime_session_token.
        /// Throws on any failure — caller should let the exception propagate
        /// so the bridge's OnEnable surfaces it.
        /// </summary>
        public static MintResult MintRuntimeToken(EmotionEngineConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.SdkApiKey))
                throw new InvalidOperationException("SdkApiKey is empty");
            if (string.IsNullOrWhiteSpace(cfg.SdkKeySecret))
                throw new InvalidOperationException("SdkKeySecret is empty");
            if (string.IsNullOrWhiteSpace(cfg.GameId))
                throw new InvalidOperationException("GameId is empty");

            const string path = "/v1/sdk/sessions";
            string baseUrl = cfg.BackendBaseUrl.TrimEnd('/');
            string url = baseUrl + path;
            string method = "POST";

            long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string canonical = $"{timestampMs}\n{method}\n{path}";
            string signature = HmacSha256Base64(cfg.SdkKeySecret, canonical);

            string sessionId = $"unity-{timestampMs}-{Guid.NewGuid():N}".Substring(0, 28);

            string bodyJson =
                "{" +
                "\"game_id\":" + JsonStr(cfg.GameId) + "," +
                "\"user_id\":" + JsonStr(cfg.Username) + "," +
                "\"session_id\":" + JsonStr(sessionId) + "," +
                "\"requested_scopes\":[\"voiceplay:state\",\"voiceplay:event\",\"voiceplay:respond\"," +
                  "\"voiceplay:played\",\"voiceplay:call\",\"voiceplay:cleanup\",\"voiceplay:debug\"," +
                  "\"voiceplay:tool\"]" +
                "}";

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-sdk-key-id", cfg.SdkApiKey);
            req.Headers.Add("x-sdk-timestamp", timestampMs.ToString());
            req.Headers.Add("x-sdk-signature", signature);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = http.SendAsync(req).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"HMAC mint POST to {url} failed: {e.Message}", e);
            }
            string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"HMAC mint returned {(int)resp.StatusCode}: {Trunc(respBody, 400)}");
            }

            string token = ExtractJsonString(respBody, "runtime_session_token");
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    $"/v1/sdk/sessions response missing runtime_session_token: {Trunc(respBody, 400)}");
            }

            string returnedSessionId = ExtractJsonString(respBody, "session_id");
            if (string.IsNullOrEmpty(returnedSessionId)) returnedSessionId = sessionId;

            Debug.Log($"[EmotionEngine] HMAC mint OK; session_id={returnedSessionId} token_len={token.Length}");
            return new MintResult
            {
                RuntimeSessionToken = token,
                SessionId = returnedSessionId,
            };
        }

        // ------ internal helpers --------------------------------------------

        private static string HmacSha256Base64(string secret, string canonical)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] sig = h.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToBase64String(sig);
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Tiny string-only extraction of "key":"value" from a JSON blob.
        /// Doesn't handle nested objects, escaped quotes in values, or
        /// arrays — fine for our flat response shape.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            // skip whitespace
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            int start = i + 1;
            int end = start;
            var sb = new StringBuilder();
            while (end < json.Length)
            {
                char c = json[end];
                if (c == '\\' && end + 1 < json.Length)
                {
                    char nxt = json[end + 1];
                    switch (nxt)
                    {
                        case '"':  sb.Append('"');  end += 2; continue;
                        case '\\': sb.Append('\\'); end += 2; continue;
                        case '/':  sb.Append('/');  end += 2; continue;
                        case 'n':  sb.Append('\n'); end += 2; continue;
                        case 'r':  sb.Append('\r'); end += 2; continue;
                        case 't':  sb.Append('\t'); end += 2; continue;
                        default:   sb.Append(c);    end += 1; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                end++;
            }
            return null;
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        public class MintResult
        {
            public string RuntimeSessionToken;
            public string SessionId;
        }
    }
}
