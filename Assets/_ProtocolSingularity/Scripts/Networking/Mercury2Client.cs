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

            // Mercury2 は temperature の下限が 0.5 なので、0.4 等で送ると自動的に 0.75 に書き換えられる警告が返る。
            // 他プロバイダでも 0.5 は許容範囲 (OpenAI 互換は [0.0, 2.0])。
            float temp = Mathf.Max(0.5f, temperatureOverride ?? 0.5f);

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

        // グローバル直列化ゲート。free tier RPM/queue 制限対策で全プロバイダ横断で 1 リクエストずつ、
        // かつリクエスト間に最小間隔 (MinGapSeconds) を挟むことで同時接続スパイクを抑える。
        // WebGL で SemaphoreSlim が正しく signal 伝搬しない問題を避けるため Interlocked + Task.Yield で実装。
        private static int _gateLocked; // 0=free, 1=locked
        private static DateTime _lastCallUtc = DateTime.MinValue;
        private const double MinGapSeconds = 1.0; // ~60 RPM。Mercury2 主力運用時のテンポ優先。フォールバック時の Cerebras 30 RPM も瞬間では抵触しない

        private struct TryResult
        {
            public bool success;
            public bool retriable;
            public long code;
            public string content;
            public string body; // エラー時のレスポンスボディ (json_object 再試行判定用)
        }

        /// <summary>
        /// プロバイダごと 1 回 API コール。json_schema 非対応エラー (400) を検出したら
        /// 同プロバイダで自動的に json_object (緩い JSON モード) にフォールバック再試行する。
        /// </summary>
        private static async Task<TryResult> TryOneAsync(
            Mercury2Provider provider,
            string systemPrompt, string userPrompt, string schemaName, string jsonSchemaBody,
            float temperature, CancellationToken ct)
        {
            var r1 = await DoHttpAsync(provider, systemPrompt, userPrompt, schemaName, jsonSchemaBody, temperature, useJsonSchema: true, ct);
            if (r1.success) return r1;

            // 「json_schema 非対応」系 (400 / 422) は同じプロバイダで json_object にして再試行。
            // Cerebras は maxLength 等の一部制約が非対応で 422 "wrong_api_format" / "Invalid fields for schema" を返す。
            bool schemaUnsupported = !string.IsNullOrEmpty(r1.body)
                && (r1.code == 400 || r1.code == 422)
                && (r1.body.Contains("json_schema")
                    || r1.body.Contains("response_format")
                    || r1.body.Contains("wrong_api_format")
                    || r1.body.Contains("Invalid fields for schema"));
            if (schemaUnsupported)
            {
                Debug.LogWarning($"[Mercury2] '{provider.name}' json_schema unsupported ({r1.code}); retrying with json_object mode.");
                // schema hint を systemPrompt 末尾に追記して JSON 形を誘導
                var extendedSystem = systemPrompt + "\n\nIMPORTANT: Return ONLY a single JSON object matching this schema (no markdown, no extra text):\n" + jsonSchemaBody;
                var r2 = await DoHttpAsync(provider, extendedSystem, userPrompt, schemaName, jsonSchemaBody, temperature, useJsonSchema: false, ct);
                if (r2.success) return r2;
                return r2;
            }
            return r1;
        }

        private static async Task<TryResult> DoHttpAsync(
            Mercury2Provider provider,
            string systemPrompt, string userPrompt, string schemaName, string jsonSchemaBody,
            float temperature, bool useJsonSchema, CancellationToken ct)
        {
            var url = provider.endpoint.TrimEnd('/') + "/chat/completions";
            var body = BuildRequestBody(provider.model, systemPrompt, userPrompt, schemaName, jsonSchemaBody, temperature, useJsonSchema);

            // グローバル直列化: Interlocked + Task.Yield で WebGL でも確実に動くゲート。
            // SemaphoreSlim / Task.Delay は Unity WebGL の sync context で再開されない場合があり hang の原因になるため使わない。
            while (System.Threading.Interlocked.CompareExchange(ref _gateLocked, 1, 0) != 0)
            {
                if (ct.IsCancellationRequested) return new TryResult { success = false, retriable = false };
                await Task.Yield();
            }
            try
            {
                // 前回完了から MinGapSeconds 経つまで Task.Yield で待つ (フレームごとに再チェック)
                while (true)
                {
                    var gap = (DateTime.UtcNow - _lastCallUtc).TotalSeconds;
                    if (gap >= MinGapSeconds) break;
                    if (ct.IsCancellationRequested) return new TryResult { success = false, retriable = false };
                    await Task.Yield();
                }
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
                string respBody = req.downloadHandler?.text;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    bool retriable = !(code == 401 || code == 403);
                    Debug.LogWarning($"[Mercury2] '{provider.name}' HTTP {code} ({schemaName}, jsonSchema={useJsonSchema}): {req.error}\n body={respBody}");
                    return new TryResult { success = false, retriable = retriable, code = code, body = respBody };
                }

                var content = ExtractContent(respBody);
                if (string.IsNullOrEmpty(content))
                {
                    Debug.LogWarning($"[Mercury2] '{provider.name}' response had no extractable content ({schemaName}): {respBody}");
                    return new TryResult { success = false, retriable = true, code = code, body = respBody };
                }
                Debug.Log($"[Mercury2:{provider.name}{(useJsonSchema?"":"/json_object")}] {schemaName} -> {content}");
                return new TryResult { success = true, content = content, code = code };
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mercury2] '{provider.name}' exception: {e.Message}");
                return new TryResult { success = false, retriable = true, code = -1 };
            }
            finally
            {
                _lastCallUtc = DateTime.UtcNow;
                System.Threading.Interlocked.Exchange(ref _gateLocked, 0);
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
            string schemaName, string jsonSchemaBody, float temperature, bool useJsonSchema)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"model\":").Append(JsonString(model)).Append(',');
            sb.Append("\"temperature\":")
              .Append(temperature.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(',');
            // 出力トークン上限。thinking (最大 ~300) + action + reasoning (~60) を収めるため 1024 を確保。
            // Mercury2 等の推論モデルは reasoning_tokens を数百〜1500 消費してから出力する。
            // 1024 だと思考で尽きて completion が空になるので 4096 まで拡張。
            sb.Append("\"max_tokens\":4096,");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":").Append(JsonString(systemPrompt)).Append("},");
            sb.Append("{\"role\":\"user\",\"content\":").Append(JsonString(userPrompt)).Append("}");
            sb.Append("],");
            if (useJsonSchema)
            {
                // 厳密モード: schema 制約で構造保証 (対応プロバイダ/モデルのみ)
                sb.Append("\"response_format\":{");
                sb.Append("\"type\":\"json_schema\",");
                sb.Append("\"json_schema\":{");
                sb.Append("\"name\":").Append(JsonString(schemaName)).Append(',');
                sb.Append("\"strict\":true,");
                sb.Append("\"schema\":").Append(jsonSchemaBody);
                sb.Append("}}");
            }
            else
            {
                // 緩和モード: "JSON を返せ" だけの指示。systemPrompt 側でスキーマを言葉で誘導する想定
                sb.Append("\"response_format\":{\"type\":\"json_object\"}");
            }
            sb.Append('}');
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
