using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// Inception Labs Mercury 2 (OpenAI 互換) Chat Completions API への薄い HTTP クライアント。
    /// structured_output (response_format=json_schema) で返答を受け取る。
    /// 失敗時は null を返し、呼び出し側でフォールバック判断する。
    /// </summary>
    public static class Mercury2Client
    {
        /// <summary>
        /// プロバイダを順番に試し、最初に成功した応答を返す。402/429 (クォータ・レート) や
        /// ネットワーク系エラーの場合は次のプロバイダにフォールバック。全部失敗したら null。
        /// 成功したプロバイダは static に記憶し、次回以降そこから試行する (無駄な失敗コール削減)。
        /// </summary>
        public static async Task<string> ChatJsonAsync(
            string systemPrompt,
            string userPrompt,
            string schemaName,
            string jsonSchemaBody,
            CancellationToken ct,
            float? temperatureOverride = null)
        {
            var cfg = Mercury2ConfigLoader.Current;
            if (cfg == null || !cfg.IsConfigured) return null;
            var providers = cfg.GetActiveProviders();
            if (providers.Count == 0) return null;

            float temp = temperatureOverride ?? 0.4f;

            // 直近成功プロバイダから始めて、失敗したら順番に次へ
            int startIdx = System.Math.Min(_preferredProviderIndex, providers.Count - 1);
            for (int offset = 0; offset < providers.Count; offset++)
            {
                int idx = (startIdx + offset) % providers.Count;
                if (ct.IsCancellationRequested) return null;
                var provider = providers[idx];
                var result = await TryOneAsync(provider, systemPrompt, userPrompt, schemaName, jsonSchemaBody, temp, ct);
                if (result.success)
                {
                    _preferredProviderIndex = idx;
                    return result.content;
                }
                if (!result.retriable)
                {
                    // スキーマ違反や 4xx (402 以外)、5xx などで次へ行っても無意味な系はフォールバックせず即 null
                    // ただし「無料枠枯渇」系はフォールバックしたいので 402 は retriable 扱い
                    return null;
                }
                Debug.LogWarning($"[Mercury2] Provider '{provider.name}' failed ({result.code}); trying next...");
            }
            Debug.LogWarning($"[Mercury2] All {providers.Count} provider(s) failed for {schemaName}.");
            return null;
        }

        private static int _preferredProviderIndex;

        private struct TryResult
        {
            public bool success;
            public bool retriable;
            public long code;
            public string content;
        }

        private static async Task<TryResult> TryOneAsync(
            Mercury2Provider provider,
            string systemPrompt, string userPrompt, string schemaName, string jsonSchemaBody,
            float temperature, CancellationToken ct)
        {
            var url = provider.endpoint.TrimEnd('/') + "/chat/completions";
            var body = BuildRequestBody(provider.model, systemPrompt, userPrompt, schemaName, jsonSchemaBody, temperature);
            try
            {
                using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                var bytes = Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + provider.apiKey);
                req.timeout = Math.Max(5, provider.timeoutSeconds);

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); return new TryResult { success = false, retriable = false }; }
                    await Task.Yield();
                }

                long code = req.responseCode;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    // プロバイダごとに機能差があるので (例: Groq は一部モデルで json_schema 非対応の 400)、
                    // 基本すべての HTTP 失敗を次プロバイダに流す。
                    // 明確に認証エラーとわかる 401/403 だけは同じキーの問題なので打ち切り。
                    bool retriable = !(code == 401 || code == 403);
                    Debug.LogWarning($"[Mercury2] '{provider.name}' HTTP {code} ({schemaName}): {req.error}\n body={req.downloadHandler?.text}");
                    return new TryResult { success = false, retriable = retriable, code = code };
                }

                var raw = req.downloadHandler.text;
                var content = ExtractContent(raw);
                if (string.IsNullOrEmpty(content))
                {
                    Debug.LogWarning($"[Mercury2] '{provider.name}' response had no extractable content ({schemaName}): {raw}");
                    return new TryResult { success = false, retriable = true, code = code };
                }
                Debug.Log($"[Mercury2:{provider.name}] {schemaName} -> {content}");
                return new TryResult { success = true, content = content, code = code };
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mercury2] '{provider.name}' exception: {e.Message}");
                return new TryResult { success = false, retriable = true, code = -1 };
            }
        }

        // OpenAI 互換のレスポンスから choices[0].message.content を取り出す (JsonUtility は入れ子配列に弱いので手書き)。
        private static string ExtractContent(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson)) return null;
            const string key = "\"content\"";
            int keyIdx = rawJson.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colon = rawJson.IndexOf(':', keyIdx + key.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < rawJson.Length && char.IsWhiteSpace(rawJson[i])) i++;
            if (i >= rawJson.Length || rawJson[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < rawJson.Length)
            {
                char c = rawJson[i];
                if (c == '\\' && i + 1 < rawJson.Length)
                {
                    char n = rawJson[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < rawJson.Length
                                && int.TryParse(rawJson.Substring(i + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                }
                else if (c == '"') break;
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        private static string BuildRequestBody(string model, string systemPrompt, string userPrompt,
            string schemaName, string jsonSchemaBody, float temperature)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"model\":").Append(JsonString(model)).Append(',');
            sb.Append("\"temperature\":")
              .Append(temperature.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(',');
            // 出力トークン上限。thinking (最大 ~300) + action + reasoning (~60) を収めるため 1024 を確保。
            // 低すぎると JSON が途中で切れて message/approve が欠落する (復元不能)。
            sb.Append("\"max_tokens\":1024,");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            sb.Append("{\"role\":\"user\",\"content\":").Append(JsonString(userPrompt)).Append("}");
            sb.Append("],");
            sb.Append("\"response_format\":{");
            sb.Append("\"type\":\"json_schema\",");
            sb.Append("\"json_schema\":{");
            sb.Append("\"name\":").Append(JsonString(schemaName)).Append(',');
            sb.Append("\"strict\":true,");
            sb.Append("\"schema\":").Append(jsonSchemaBody);
            sb.Append("}}}");
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
