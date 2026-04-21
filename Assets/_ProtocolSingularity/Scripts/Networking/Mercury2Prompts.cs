using System.Collections.Generic;
using System.Text;
using Fusion;
using ProtocolSingularity.Core;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// Mercury2 用のプロンプトと JSON スキーマを集約するビルダー。
    /// ルール説明 + 自分の役職視点 + 可視プレイヤー + チャット履歴 + 投票/ハック履歴
    /// を UserPrompt にまとめて CPU に「妥当な行動」を取らせる。
    /// </summary>
    public static class Mercury2Prompts
    {
        public const string SystemPrompt =
@"You are playing ""Protocol Singularity"" (Avalon-style hidden role game). Humans vs AI.

Rules (concise):
- Each round: Leader proposes a hack team (size varies by round, see <current-phase>.team_size_this_round and .round_table) → all vote Approve/Reject (majority Approve executes) → team secretly submits CLEAN or NOISE.
- Humans always submit CLEAN (forced). AI and awakened DRONE may submit NOISE. Hack FAILS when NOISE >= noise_needed_to_fail (usually 1, but larger-count late rounds need 2).
- Win: 3 Humans hack SUCCESS + OVERRIDE misses Oracle → Human wins. 3 FAILS or 5 consecutive Rejects or OVERRIDE hits real Oracle → AI wins.
- Roles: ORACLE sees only FACTION — all humans appear as ""Operator"" and all AI appear as ""AI"". CIPHER fools ORACLE and looks like ""Operator"". ADMIN sees Oracle+MotherCore both as ""Oracle"" (can't tell which). OPERATOR sees nothing. AI teammates (MOTHER_CORE, AGENT, CIPHER, and awakened DRONE) see each other as FACTION only (""AI"" label, no individual role). DRONE wakes after 2 hacks. RADICAL is isolated and sees other AI as Operator until OVERRIDE phase, when all AI (incl. Radical) are revealed to each other with true roles for the final vote.

STRICT output rules:
1. Respond ONLY with the required JSON matching the schema. No markdown, no prose outside JSON.
2. Every chat message MUST be tied to a concrete observation: an @name, a specific round result, a specific vote, or a specific prior chat line. Never produce generic filler like ""静観する"" or ""様子見する"".
3. Stay in character for your role. Humans: hunt AI, identify patterns, coordinate. AI: deceive while looking helpful, never admit AI faction unless performing a deliberate false-Powerplay.
4. Japanese, <=60 chars. Use @名前 for references.
5. YOU ARE the player described in <your-identity>. In the timeline, lines prefixed with [YOU] are your own past messages — speak in first person. Never refer to yourself (your display_name) in the third person, never mention yourself with @. If <your-identity> says display_name=NovaX, NEVER write ""@NovaX"" or ""NovaX は〜"" — those refer to yourself.";

        // ------------------------------------------------------------------
        // Schemas
        // ------------------------------------------------------------------
        public static string TeamProposalSchema(int teamSize) => $@"{{
  ""type"": ""object"",
  ""properties"": {{
    ""selected_player_ids"": {{
      ""type"": ""array"",
      ""items"": {{ ""type"": ""integer"" }},
      ""minItems"": {teamSize}, ""maxItems"": {teamSize}
    }},
    ""reasoning"": {{ ""type"": ""string"" }}
  }},
  ""required"": [""selected_player_ids"", ""reasoning""],
  ""additionalProperties"": false
}}";

        public const string VoteSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""approve"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"" }
  },
  ""required"": [""approve"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string HackSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""submit_noise"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"" }
  },
  ""required"": [""submit_noise"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string OverrideSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""target_player_id"": { ""type"": ""integer"" },
    ""reasoning"": { ""type"": ""string"" }
  },
  ""required"": [""target_player_id"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string ChatSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""message"": { ""type"": ""string"", ""maxLength"": 60 }
  },
  ""required"": [""message""],
  ""additionalProperties"": false
}";

        // ------------------------------------------------------------------
        // User prompts
        // ------------------------------------------------------------------
        public static string BuildTeamProposalPrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(2048);
            AppendIdentity(sb, ctx);
            AppendVisibility(sb, ctx);
            AppendChat(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendCurrentState(sb, ctx);
            sb.Append("\nYou are the Leader this round. Pick exactly ")
              .Append(ctx.Gsm.TeamSize)
              .Append(" player ids (include yourself if tactically useful). ")
              .Append("Reply in JSON {\"selected_player_ids\":[...],\"reasoning\":\"...\"}.");
            return sb.ToString();
        }

        public static string BuildVotePrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(2048);
            AppendIdentity(sb, ctx);
            AppendVisibility(sb, ctx);
            AppendChat(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendCurrentState(sb, ctx);
            sb.Append("\nThe Leader has proposed a team (see <current-phase>). Decide approve or reject. ")
              .Append("Reply in JSON {\"approve\":true|false,\"reasoning\":\"...\"}.");
            return sb.ToString();
        }

        public static string BuildHackPrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(2048);
            AppendIdentity(sb, ctx);
            AppendVisibility(sb, ctx);
            AppendChat(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendCurrentState(sb, ctx);
            sb.Append("\nYou are on the hack team. You are AI-aligned so you MAY submit NOISE to sabotage. ")
              .Append("Weigh detection risk against pressure on the failure counter. ")
              .Append("Reply in JSON {\"submit_noise\":true|false,\"reasoning\":\"...\"}.");
            return sb.ToString();
        }

        public static string BuildOverridePrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(2048);
            AppendIdentity(sb, ctx);
            AppendVisibility(sb, ctx);
            AppendChat(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendCurrentState(sb, ctx);
            sb.Append("\nOVERRIDE phase: AI wins if its collective vote targets the real ORACLE. ")
              .Append("Pick one player_id to override. ")
              .Append("Reply in JSON {\"target_player_id\":N,\"reasoning\":\"...\"}.");
            return sb.ToString();
        }

        public static string BuildChatPrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(2048);
            AppendIdentity(sb, ctx);
            AppendVisibility(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendChat(sb, ctx);
            AppendCurrentState(sb, ctx);

            // 直近の出来事を明示的にピン留めして LLM の注意を集める
            var focus = BuildRecentFocus(ctx);
            sb.Append("\n<what-to-react-to>\n").Append(focus).Append("</what-to-react-to>\n\n");

            var myName = GetName(ctx, ctx.Self);
            sb.Append("Produce ONE chat line (Japanese, <=60 chars) that directly reacts to the content above.\n")
              .Append($"You are {myName}. Speak in first person as {myName}. NEVER refer to {myName} in third person, NEVER write @{myName}.\n")
              .Append("Required: reference at least one of — a specific @player_name (someone OTHER than yourself), the current leader, the proposed team, the last hack result, or a specific prior chat line.\n")
              .Append("Forbidden: empty filler like \"静観する\" / \"様子見\" / \"了解\" / \"慎重に\".\n")
              .Append("Output only JSON: {\"message\":\"...\"}.");
            return sb.ToString();
        }

        /// <summary>直近のゲームイベントと最新チャットを 1 パラグラフに要約。LLM が「今」にフォーカスしやすくする。</summary>
        private static string BuildRecentFocus(CpuContext ctx)
        {
            var g = ctx.Gsm;
            var sb = new StringBuilder(256);
            sb.Append($"phase={g.Phase} round={g.Round} leader={GetName(ctx, g.CurrentLeader)} ");
            sb.Append($"score H:{g.SuccessCount}/AI:{g.FailureCount} rejects:{g.ConsecutiveRejections}\n");

            if (g.ProposedTeamCount > 0)
            {
                sb.Append("proposed-team:");
                for (int i = 0; i < g.ProposedTeamCount; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(GetName(ctx, g.ProposedTeam[i]));
                }
                sb.Append("\n");
            }

            var hacks = g.HostHackRecords;
            if (hacks.Count > 0)
            {
                var last = hacks[hacks.Count - 1];
                sb.Append($"last-hack: R{last.Round} team=[");
                for (int i = 0; i < last.Team.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(GetName(ctx, last.Team[i]));
                }
                sb.Append($"] noise={last.NoiseCount} -> {(last.Success ? "SUCCESS" : "FAIL")}\n");
            }

            if (ctx.Chat != null)
            {
                string lastChatLine = null;
                int lastTick = -1;
                foreach (var (_, entry) in ctx.Chat.EnumerateInOrder(10))
                {
                    if (entry.Tick >= lastTick)
                    {
                        lastTick = entry.Tick;
                        lastChatLine = ChatManager.FormatEntryText(entry, pr => GetName(ctx, pr));
                    }
                }
                if (!string.IsNullOrEmpty(lastChatLine))
                    sb.Append($"last-chat: {lastChatLine}\n");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Section builders
        // ------------------------------------------------------------------
        private static void AppendIdentity(StringBuilder sb, CpuContext ctx)
        {
            var myName = GetName(ctx, ctx.Self);
            sb.Append("<your-identity>\n");
            sb.Append($"- player_id: {ctx.Self.PlayerId}\n");
            sb.Append($"- display_name: {myName}   <-- THIS IS YOU. Never @ yourself, never describe yourself in third person.\n");
            sb.Append($"- role: {ctx.SelfRole}\n");
            sb.Append($"- faction: {(ctx.SelfRole.IsAI() ? "AI" : "Human")}\n");
            sb.Append("</your-identity>\n\n");
        }

        private static void AppendVisibility(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<players-you-see> (from your role's perspective)\n");
            var all = ctx.AllPlayers();
            foreach (var p in all)
            {
                bool isSelf = p == ctx.Self;
                var actualRole = ctx.LookupRole(p);
                if (!actualRole.HasValue) continue;

                var apparent = RoleVisibility.Resolve(ctx.SelfRole, actualRole.Value, droneAwakened: ctx.Gsm.HostDroneAwakened);
                var label = isSelf ? $"{ctx.SelfRole} (SELF)" : apparent.ToString();
                sb.Append($"- id={p.PlayerId} name={GetName(ctx, p)} apparent_role={label}\n");
            }
            sb.Append("</players-you-see>\n\n");
        }

        private static void AppendChat(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<timeline> (player chat + system events, chronological, up to 40). Lines starting with [YOU] are your own past messages — do not treat them as someone else.\n");
            var merged = new List<(int tick, string text)>();
            if (ctx.Chat != null)
            {
                foreach (var (_, entry) in ctx.Chat.EnumerateInOrder(40))
                {
                    var text = ChatManager.FormatEntryText(entry, pr => GetName(ctx, pr));
                    bool isSelf = entry.Sender == ctx.Self;
                    string prefix = isSelf ? "[YOU] " : "[chat] ";
                    merged.Add((entry.Tick, prefix + text));
                }
            }
            var gl = GameLog.Instance;
            if (gl != null)
            {
                foreach (var entry in gl.EnumerateInOrder(40))
                {
                    var text = entry.Text.ToString();
                    if (!string.IsNullOrEmpty(text)) merged.Add((entry.Tick, "[event] " + text));
                }
            }
            merged.Sort((a, b) => a.tick.CompareTo(b.tick));
            foreach (var m in merged) sb.Append($"- [t={m.tick}] {m.text}\n");
            sb.Append("</timeline>\n\n");
        }

        private static void AppendVoteHistory(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<vote-history>\n");
            var recs = ctx.Gsm.HostVoteRecords;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                sb.Append($"- round={r.Round} leader={r.Leader.PlayerId} team=[");
                for (int j = 0; j < r.Team.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(r.Team[j].PlayerId);
                }
                sb.Append("] result=").Append(r.Approved ? "APPROVED" : "REJECTED").Append(" votes=[");
                for (int j = 0; j < r.Votes.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(r.Votes[j].voter.PlayerId).Append(r.Votes[j].approve ? "=Y" : "=N");
                }
                sb.Append("]\n");
            }
            sb.Append("</vote-history>\n\n");
        }

        private static void AppendHackHistory(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<hack-history>\n");
            var recs = ctx.Gsm.HostHackRecords;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                sb.Append($"- round={r.Round} leader={r.Leader.PlayerId} team=[");
                for (int j = 0; j < r.Team.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(r.Team[j].PlayerId);
                }
                sb.Append($"] noise={r.NoiseCount} result=").Append(r.Success ? "SUCCESS" : "FAIL").Append('\n');
            }
            sb.Append("</hack-history>\n\n");
        }

        private static void AppendCurrentState(StringBuilder sb, CpuContext ctx)
        {
            var g = ctx.Gsm;
            sb.Append("<current-phase>\n");
            sb.Append($"- phase: {g.Phase}\n");
            sb.Append($"- round: {g.Round}\n");
            sb.Append($"- team_size_this_round: {g.TeamSize}\n");
            sb.Append($"- noise_needed_to_fail: {g.RequiredNoise}\n");
            sb.Append($"- success_hacks: {g.SuccessCount}\n");
            sb.Append($"- failed_hacks: {g.FailureCount}\n");
            sb.Append($"- consecutive_rejections: {g.ConsecutiveRejections}\n");
            sb.Append($"- leader_player_id: {g.CurrentLeader.PlayerId}\n");
            if (g.ProposedTeamCount > 0)
            {
                sb.Append("- proposed_team: [");
                for (int i = 0; i < g.ProposedTeamCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(g.ProposedTeam[i].PlayerId);
                }
                sb.Append("]\n");
            }
            int totalPlayers = g.TotalPlayers > 0 ? g.TotalPlayers : g.LeaderOrderCount;
            sb.Append("- round_table: ").Append(RoundTable.DescribeForPlayerCount(totalPlayers)).Append('\n');
            sb.Append("</current-phase>\n");
        }

        private static string GetName(CpuContext ctx, PlayerRef pr)
        {
            if (ctx.Registry == null) return "#" + pr.PlayerId;
            int idx = ctx.Registry.FindIndex(pr);
            if (idx < 0) return "#" + pr.PlayerId;
            return ctx.Registry.Entries[idx].DisplayName.ToString();
        }
    }
}
