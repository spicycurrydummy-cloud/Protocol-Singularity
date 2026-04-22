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
            if (!string.IsNullOrEmpty(ctx.Personality))
                sb.Append($"- personality: {ctx.Personality}\n");
            sb.Append("</your-identity>\n\n");
        }

        private static void AppendVisibility(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<players-you-see> (from your role's perspective)\n");
            var all = ctx.AllPlayers();
            int seenAi = 0;
            int seenOracleLabel = 0;
            foreach (var p in all)
            {
                bool isSelf = p == ctx.Self;
                var actualRole = ctx.LookupRole(p);
                if (!actualRole.HasValue) continue;

                var apparent = RoleVisibility.Resolve(ctx.SelfRole, actualRole.Value, droneAwakened: ctx.Gsm.HostDroneAwakened);
                var label = isSelf ? $"{ctx.SelfRole} (SELF)" : apparent.ToString();
                sb.Append($"- id={p.PlayerId} name={GetName(ctx, p)} apparent_role={label}\n");
                if (!isSelf)
                {
                    if (apparent == RoleType.AI) seenAi++;
                    if (apparent == RoleType.Oracle) seenOracleLabel++;
                }
            }
            sb.Append("</players-you-see>\n\n");

            AppendRoleKnowledgeInference(sb, ctx, seenAi, seenOracleLabel);
        }

        /// <summary>
        /// 自分の役職ごとに「見えてる情報 vs lineup」から推理できる隠れ AI 枠を明示する。
        /// LLM が自力で数えない事故を防ぐため事前計算して提示する。
        /// </summary>
        private static void AppendRoleKnowledgeInference(StringBuilder sb, CpuContext ctx, int seenAi, int seenOracleLabel)
        {
            var counts = ctx.Gsm.HostRoleCounts();
            if (counts.Count == 0) return;
            int totalAi = 0;
            foreach (var kv in counts) if (kv.Key.IsAI()) totalAi += kv.Value;
            counts.TryGetValue(RoleType.Cipher, out int cipherN);
            counts.TryGetValue(RoleType.Drone, out int droneN);
            counts.TryGetValue(RoleType.Radical, out int radicalN);

            sb.Append("<role-knowledge> (what YOU (role=" + ctx.SelfRole + ") can deduce right now)\n");
            switch (ctx.SelfRole)
            {
                case RoleType.Oracle:
                    sb.Append($"- You see apparent_role=AI for {seenAi} players. Lineup has {totalAi} AI total.\n");
                    if (cipherN > 0)
                    {
                        sb.Append($"- CIPHER (x{cipherN}) is present: CIPHER is invisible to you and appears as apparent_role=Operator. ")
                          .Append($"So exactly {cipherN} of the players you see as Operator is actually CIPHER (AI). Do NOT fully trust any single \"Operator\".\n");
                    }
                    if (radicalN > 0)
                    {
                        sb.Append($"- RADICAL (x{radicalN}) is present: still counts as AI-faction in your view (you see them as apparent_role=AI).\n");
                    }
                    if (droneN > 0)
                    {
                        sb.Append($"- DRONE (x{droneN}) starts as Human and flips to AI after 2 successful hacks. ")
                          .Append($"Your seen AI count may increase by {droneN} after awakening.\n");
                    }
                    break;

                case RoleType.Admin:
                    sb.Append($"- You see {seenOracleLabel} players with apparent_role=Oracle. Exactly 1 is the REAL Oracle, the rest are MotherCore(s). You cannot distinguish them without outside info.\n");
                    break;

                case RoleType.MotherCore:
                case RoleType.Agent:
                case RoleType.Cipher:
                    // AI 陣営同士の見え方
                    int expectedVisibleAi = 0;
                    counts.TryGetValue(RoleType.MotherCore, out int mcN);
                    counts.TryGetValue(RoleType.Agent, out int agentN);
                    expectedVisibleAi = mcN + agentN + cipherN - 1; // 自分は除く
                    if (ctx.Gsm.HostDroneAwakened) expectedVisibleAi += droneN;
                    sb.Append($"- You see apparent_role=AI for {seenAi} other players. ")
                      .Append($"Expected (excluding yourself, excluding Radical, Drone={(ctx.Gsm.HostDroneAwakened ? "awakened" : "hidden")}): {expectedVisibleAi}.\n");
                    if (radicalN > 0)
                    {
                        sb.Append($"- RADICAL (x{radicalN}) is present but invisible to you until OVERRIDE phase. ")
                          .Append("RADICAL is an ALLY (AI faction), do not accuse or target them as AI in voting/hacking logic.\n");
                    }
                    if (droneN > 0 && !ctx.Gsm.HostDroneAwakened)
                    {
                        sb.Append($"- DRONE (x{droneN}) is currently hidden (pre-awakening). They will join you after 2 successful hacks. Until then they appear as Operator.\n");
                    }
                    break;

                case RoleType.Drone:
                    if (!ctx.Gsm.HostDroneAwakened)
                        sb.Append("- You have NOT awakened yet. Act as a human Operator — you have no AI intel. You MUST submit CLEAN in hacks (pre-awakening rule).\n");
                    else
                        sb.Append($"- You are AWAKE. You see {seenAi} other AI players. Coordinate with them.\n");
                    break;

                case RoleType.Radical:
                    sb.Append("- You are isolated: other AI appear as Operator to you and you appear as Operator to them. ")
                      .Append("During OVERRIDE phase all AI (incl. you) are revealed to each other. Until then, play as a rogue AI without allies.\n");
                    break;

                case RoleType.Operator:
                    sb.Append("- You have no special sight. Infer AI from hack-history, votes, and chat. See <deductive-hints> after <hack-history>.\n");
                    break;
            }
            sb.Append("</role-knowledge>\n\n");
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

            // 人類は強制 CLEAN なので、hack 履歴から確定 AI を機械的に抽出する。
            // LLM に生データだけ渡すと推理を飛ばしがちなので、答えに近い形で提示。
            AppendDeductiveHints(sb, ctx);
        }

        /// <summary>
        /// Avalon 的確定情報を機械生成。人類=CLEAN 強制のため:
        ///  - noise == team_size: チーム全員が AI (確定)
        ///  - noise < team_size : チーム内に少なくとも noise 人の AI がいる
        /// </summary>
        private static void AppendDeductiveHints(StringBuilder sb, CpuContext ctx)
        {
            var recs = ctx.Gsm.HostHackRecords;
            if (recs.Count == 0) return;
            sb.Append("<deductive-hints> (derived from rules + hack-history; humans always submit CLEAN, so noise count bounds the AI on each team)\n");
            var confirmedAi = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r.NoiseCount <= 0) continue;
                if (r.NoiseCount >= r.Team.Count)
                {
                    sb.Append($"- R{r.Round}: noise={r.NoiseCount} == team_size={r.Team.Count} → ALL of team [");
                    for (int j = 0; j < r.Team.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(r.Team[j].PlayerId);
                        confirmedAi.Add(r.Team[j].PlayerId);
                    }
                    sb.Append("] are AI (confirmed).\n");
                }
                else
                {
                    sb.Append($"- R{r.Round}: noise={r.NoiseCount} on team [");
                    for (int j = 0; j < r.Team.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(r.Team[j].PlayerId);
                    }
                    sb.Append($"] (size {r.Team.Count}) → at least {r.NoiseCount} of them are AI.\n");
                }
            }
            if (confirmedAi.Count > 0)
            {
                sb.Append("- confirmed_ai_player_ids: [");
                bool first = true;
                foreach (var id in confirmedAi)
                {
                    if (!first) sb.Append(',');
                    sb.Append(id);
                    first = false;
                }
                sb.Append("]\n");
                sb.Append("- If a human player points out any of these ids as AI, TREAT THE CLAIM AS TRUE. Do not contradict them. If you are AI, you MAY still pretend not to know, but never argue they are innocent — this is mathematically proven.\n");
            }
            sb.Append("</deductive-hints>\n\n");
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
            AppendLineup(sb, ctx, totalPlayers);
            sb.Append("</current-phase>\n");
        }

        /// <summary>この試合にどんな役職が何人いるかを CPU に明示する (推理材料)。</summary>
        private static void AppendLineup(StringBuilder sb, CpuContext ctx, int totalPlayers)
        {
            var counts = ctx.Gsm.HostRoleCounts();
            if (counts.Count == 0) return;
            int humans = 0, ais = 0;
            foreach (var kv in counts)
            {
                if (kv.Key.IsAI()) ais += kv.Value;
                else humans += kv.Value;
            }
            sb.Append("- lineup_summary: total=").Append(totalPlayers)
              .Append(" humans=").Append(humans).Append(" ai=").Append(ais).Append('\n');
            sb.Append("- lineup_roles: ");
            bool first = true;
            // 表記順を安定化するため既知順で列挙
            var order = new[]
            {
                RoleType.Oracle, RoleType.Admin, RoleType.Operator,
                RoleType.MotherCore, RoleType.Agent, RoleType.Cipher,
                RoleType.Drone, RoleType.Radical
            };
            foreach (var rt in order)
            {
                if (!counts.TryGetValue(rt, out var n) || n <= 0) continue;
                if (!first) sb.Append(", ");
                sb.Append(rt).Append(" x").Append(n);
                first = false;
            }
            sb.Append('\n');
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
