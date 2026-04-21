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
        /// system + user プロンプトを POST し、structured_output の JSON 文字列を返す。
        /// 設定未ロード / タイムアウト / HTTP エラー時は null。
        /// </summary>
        public static async Task<string> ChatJsonAsync(
            string systemPrompt,
            string userPrompt,
            string schemaName,
            string jsonSchemaBody,
            CancellationToken ct)
        {
            var cfg = Mercury2ConfigLoader.Current;
            if (cfg == null || !cfg.IsConfigured) return null;

            var url = cfg.endpoint.TrimEnd('/') + "/chat/completions";
            var body = BuildRequestBody(cfg.model, systemPrompt, userPrompt, schemaName, jsonSchemaBody);

            try
            {
                using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                var bytes = Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + cfg.apiKey);
                req.timeout = Math.Max(5, cfg.timeoutSeconds);

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); return null; }
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Mercury2] HTTP failed ({schemaName}): {req.error} code={req.responseCode}\n body={req.downloadHandler?.text}");
                    return null;
                }

                var raw = req.downloadHandler.text;
                var content = ExtractContent(raw);
                if (string.IsNullOrEmpty(content))
                {
                    Debug.LogWarning($"[Mercury2] Response had no extractable content ({schemaName}): {raw}");
                }
                else
                {
                    Debug.Log($"[Mercury2] {schemaName} -> {content}");
                }
                return content;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mercury2] Request exception: {e.Message}");
                return null;
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
            string schemaName, string jsonSchemaBody)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"model\":").Append(JsonString(model)).Append(',');
            sb.Append("\"temperature\":0.4,");
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
