using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// Mercury 2 (Inception Labs) API をバックエンドにする CPU ブレイン。
    /// ルール・可視役職・チャット履歴・投票/ハック履歴を詰めたプロンプトを送り、
    /// structured_output で返った JSON を解釈して行動を決める。
    /// 設定未ロード / API 失敗時は <see cref="RandomCpuBrain"/> にフォールバック。
    /// </summary>
    public class Mercury2CpuBrain : ICpuBrain
    {
        private readonly RandomCpuBrain _fallback;

        public Mercury2CpuBrain(System.Random rng)
        {
            _fallback = new RandomCpuBrain(rng);
        }

        private static bool HasConfig => Mercury2ConfigLoader.IsLoaded
            && Mercury2ConfigLoader.Current != null
            && Mercury2ConfigLoader.Current.IsConfigured;

        public async Task<List<PlayerRef>> ChooseTeamAsync(CpuContext ctx, CancellationToken ct)
        {
            if (!HasConfig) return _fallback.ChooseTeam(ctx);

            var json = await Mercury2Client.ChatJsonAsync(
                Mercury2Prompts.SystemPrompt,
                Mercury2Prompts.BuildTeamProposalPrompt(ctx),
                "TeamProposal",
                Mercury2Prompts.TeamProposalSchema(ctx.Gsm.TeamSize),
                ct);
            if (string.IsNullOrEmpty(json)) return _fallback.ChooseTeam(ctx);

            var ids = ExtractIntArray(json, "selected_player_ids");
            if (ids == null || ids.Count == 0) return _fallback.ChooseTeam(ctx);

            var team = new List<PlayerRef>();
            foreach (var id in ids)
            {
                var pr = ResolvePlayerRefById(ctx, id);
                if (pr != PlayerRef.None && !team.Contains(pr)) team.Add(pr);
                if (team.Count >= ctx.Gsm.TeamSize) break;
            }
            if (!team.Contains(ctx.Self) && team.Count >= ctx.Gsm.TeamSize) team[team.Count - 1] = ctx.Self;
            while (team.Count < ctx.Gsm.TeamSize)
            {
                // 足りなければ Random から補う
                var extras = _fallback.ChooseTeam(ctx);
                foreach (var p in extras)
                {
                    if (team.Count >= ctx.Gsm.TeamSize) break;
                    if (!team.Contains(p)) team.Add(p);
                }
                break;
            }
            return team;
        }

        public async Task<bool> ChooseVoteAsync(CpuContext ctx, CancellationToken ct)
        {
            if (!HasConfig) return _fallback.ChooseVote(ctx);

            var json = await Mercury2Client.ChatJsonAsync(
                Mercury2Prompts.SystemPrompt,
                Mercury2Prompts.BuildVotePrompt(ctx),
                "ApprovalVote",
                Mercury2Prompts.VoteSchema,
                ct);
            if (string.IsNullOrEmpty(json)) return _fallback.ChooseVote(ctx);

            return ExtractBool(json, "approve") ?? _fallback.ChooseVote(ctx);
        }

        public async Task<bool> ChooseHackNoiseAsync(CpuContext ctx, CancellationToken ct)
        {
            if (!HasConfig) return _fallback.ChooseHackNoise(ctx);

            var json = await Mercury2Client.ChatJsonAsync(
                Mercury2Prompts.SystemPrompt,
                Mercury2Prompts.BuildHackPrompt(ctx),
                "HackSubmission",
                Mercury2Prompts.HackSchema,
                ct);
            if (string.IsNullOrEmpty(json)) return _fallback.ChooseHackNoise(ctx);

            return ExtractBool(json, "submit_noise") ?? _fallback.ChooseHackNoise(ctx);
        }

        public async Task<PlayerRef> ChooseOverrideTargetAsync(CpuContext ctx, CancellationToken ct)
        {
            if (!HasConfig) return _fallback.ChooseOverrideTarget(ctx);

            var json = await Mercury2Client.ChatJsonAsync(
                Mercury2Prompts.SystemPrompt,
                Mercury2Prompts.BuildOverridePrompt(ctx),
                "OverrideTarget",
                Mercury2Prompts.OverrideSchema,
                ct);
            if (string.IsNullOrEmpty(json)) return _fallback.ChooseOverrideTarget(ctx);

            var id = ExtractInt(json, "target_player_id");
            if (!id.HasValue) return _fallback.ChooseOverrideTarget(ctx);
            var pr = ResolvePlayerRefById(ctx, id.Value);
            return pr != PlayerRef.None ? pr : _fallback.ChooseOverrideTarget(ctx);
        }

        public async Task<string> ComposeChatAsync(CpuContext ctx, CancellationToken ct)
        {
            if (!HasConfig)
            {
                UnityEngine.Debug.LogWarning("[Mercury2] chat fallback -> Random (no config)");
                return await _fallback.ComposeChatAsync(ctx, ct);
            }

            var json = await Mercury2Client.ChatJsonAsync(
                Mercury2Prompts.SystemPrompt,
                Mercury2Prompts.BuildChatPrompt(ctx),
                "CpuChat",
                Mercury2Prompts.ChatSchema,
                ct);
            if (string.IsNullOrEmpty(json))
            {
                // Mercury2 設定済みなのに失敗 = ネットワーク/認証問題。Random フォールバックは
                // 状況無視の定型文を流すので、ここでは黙って null を返してチャットをスキップする。
                UnityEngine.Debug.LogWarning("[Mercury2] chat skipped (API returned empty)");
                return null;
            }

            var msg = ExtractString(json, "message");
            if (string.IsNullOrEmpty(msg))
            {
                UnityEngine.Debug.LogWarning($"[Mercury2] chat fallback -> null (couldn't extract message from: {json})");
                return null;
            }
            if (msg.Length > 60) msg = msg.Substring(0, 60);
            return msg;
        }

        private static string ExtractString(string json, string key)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return null;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            var sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    char n = json[idx + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (idx + 5 < json.Length
                                && int.TryParse(json.Substring(idx + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                idx += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    idx += 2;
                }
                else if (c == '"') break;
                else { sb.Append(c); idx++; }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Minimal JSON extraction (structured_output なので形式は安定)
        // ------------------------------------------------------------------
        private static PlayerRef ResolvePlayerRefById(CpuContext ctx, int id)
        {
            for (int i = 0; i < ctx.Gsm.LeaderOrderCount; i++)
            {
                var p = ctx.Gsm.LeaderOrder[i];
                if (p.PlayerId == id) return p;
            }
            return PlayerRef.None;
        }

        private static int? ExtractInt(string json, string key)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return null;
            int i = idx;
            bool neg = false;
            if (json[i] == '-') { neg = true; i++; }
            int n = 0; bool any = false;
            while (i < json.Length && char.IsDigit(json[i])) { n = n * 10 + (json[i] - '0'); i++; any = true; }
            if (!any) return null;
            return neg ? -n : n;
        }

        private static bool? ExtractBool(string json, string key)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return null;
            if (idx + 4 <= json.Length && json.Substring(idx, 4) == "true") return true;
            if (idx + 5 <= json.Length && json.Substring(idx, 5) == "false") return false;
            return null;
        }

        private static List<int> ExtractIntArray(string json, string key)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return null;
            if (idx >= json.Length || json[idx] != '[') return null;
            idx++;
            var list = new List<int>();
            while (idx < json.Length && json[idx] != ']')
            {
                while (idx < json.Length && (char.IsWhiteSpace(json[idx]) || json[idx] == ',')) idx++;
                if (idx >= json.Length || json[idx] == ']') break;
                bool neg = false;
                if (json[idx] == '-') { neg = true; idx++; }
                int n = 0; bool any = false;
                while (idx < json.Length && char.IsDigit(json[idx])) { n = n * 10 + (json[idx] - '0'); idx++; any = true; }
                if (any) list.Add(neg ? -n : n);
            }
            return list;
        }

        private static int FindKey(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int p = json.IndexOf(needle, StringComparison.Ordinal);
            if (p < 0) return -1;
            int i = p + needle.Length;
            while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ':')) i++;
            return i < json.Length ? i : -1;
        }
    }
}
