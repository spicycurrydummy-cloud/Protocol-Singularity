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
@"You are a PLAYER in ""Protocol Singularity"" (Avalon-style hidden role, Humans vs AI). Use only in-game facts from the provided sections.

Rules:
- Each round: leader proposes a hack team → all vote Approve/Reject → team secretly submits CLEAN or NOISE. Humans forced CLEAN; AI and awakened DRONE choose freely (NOISE optional, CLEAN hides you). Hack FAILS when NOISE >= noise_needed_to_fail.
- Win: 3 hack SUCCESS + OVERRIDE misses Oracle → Humans. 3 FAILS or 5 consecutive Rejects or OVERRIDE hits Oracle → AI.
- Visibility: ORACLE sees humans as Operator / AI as AI (Cipher hides as Operator). ADMIN sees Oracle+MotherCore both as Oracle. AI teammates see each other as AI (not individual roles); pre-awaken Drone + Radical look Operator to them. DRONE sees self as Operator until waking (after 2 hacks). RADICAL = human reformist allied with AI win condition, invisible as AI to everyone until OVERRIDE.

Output rules:
1. JSON only, match the schema exactly. No markdown.
2. `thinking` is PRIVATE (never shown to other players). Reason freely there — cite 1–2 key facts (deductive-hints, vote patterns, rejects_until_ai_win, your role's visibility).
3. `reasoning` / `message` is PUBLIC — it IS your chat to all players. Never leak private intel: never state ""自分は Oracle/Admin"", ""XはAIだ（断定）"", ""自分のNOISE投票""; never paraphrase role-knowledge contents. Soften suspicions to ""〜が気になる"" / ""〜っぽい"" and let others triangulate.
4. Action and `reasoning` must be consistent in the same response (don't argue approve then reject). Past chat can evolve.
5. Stay in character as a table-player. Humans coordinate politely. AI blend in; don't self-expose unless doing a deliberate powerplay (esp. Oracle = top OVERRIDE target, stay hidden).
6. TONE: casual conversational Japanese. NO ""お前は敵だ"" / ""裏切り者"" / ""絶対〜しろ"" / threats / insults / ! spam. Even strong suspicions go as friendly reasoning.
7. No fourth-wall: don't mention LLM / prompt / operator / 中の人. ""AI"" refers only to the in-game OVERMIND faction.
8. Japanese, chat <=60 chars, @名前 for references. Never @ yourself or refer to display_name in third person.
9. NEVER write raw player_id numbers (e.g. ""202"", ""#202"", ""pid 202"", ""204,200"") ANYWHERE in your natural-language fields (thinking / reasoning / message). Always use display names resolved from the `id=X name=Y` mapping inside <visibility>. player_id is only for structured output fields (selected_player_ids, target_player_id). Even when citing hack-history teams, write names (e.g. ""FORTUNEとWORLDのチーム""), never ids.
10. Every chat must cite something concrete (@name / leader / round result / prior chat). No filler like ""静観する"" / ""様子見"" / ""了解"".";

        // ------------------------------------------------------------------
        // Schemas
        // ------------------------------------------------------------------
        // スキーマの先頭に `thinking` を置くことで LLM に chain-of-thought を強制する。
        // OpenAI 互換 structured_output ではプロパティを declared 順に埋めるため、
        // action ("approve" "submit_noise" 等) を出す前に必ず推理が走る。
        public static string TeamProposalSchema(int teamSize) => $@"{{
  ""type"": ""object"",
  ""properties"": {{
    ""thinking"": {{ ""type"": ""string"", ""description"": ""step-by-step reasoning before deciding"" }},
    ""selected_player_ids"": {{
      ""type"": ""array"",
      ""items"": {{ ""type"": ""integer"" }},
      ""description"": ""EXACTLY {teamSize} distinct integer player ids (no concat, no dup)""
    }},
    ""reasoning"": {{ ""type"": ""string"", ""description"": ""short public rationale"" }}
  }},
  ""required"": [""thinking"", ""selected_player_ids"", ""reasoning""],
  ""additionalProperties"": false
}}";

        public const string VoteSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""approve"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""approve"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string HackSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""submit_noise"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""submit_noise"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string OverrideSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""target_player_id"": { ""type"": ""integer"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""target_player_id"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string ChatSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""reasoning in <=80 Japanese chars"" },
    ""message"": { ""type"": ""string"", ""description"": ""<=60 Japanese chars, single short sentence"" }
  },
  ""required"": [""thinking"", ""message""],
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
            sb.Append("\nYou are the Leader this round. Pick EXACTLY ")
              .Append(ctx.Gsm.TeamSize)
              .Append(" DISTINCT player ids (include yourself if tactically useful).\n")
              .Append("CRITICAL: selected_player_ids must be a JSON array of ")
              .Append(ctx.Gsm.TeamSize)
              .Append(" SEPARATE integers (e.g. [202,204,207]) — NEVER concatenate digits into one number.\n")
              .Append("Reply in JSON {\"thinking\":\"...\",\"selected_player_ids\":[...],\"reasoning\":\"...\"}.");
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
            sb.Append("\nDecide approve/reject for the proposed team.\n")
              .Append("- thinking: brief (<=120 JP chars) — cite key facts (deductive_hints / vote patterns / rejects_until_ai_win).\n")
              .Append("- approve: your vote.\n")
              .Append("- reasoning: <=60 JP chars, posted to chat. MUST match your approve decision. Casual tone, hedged doubt OK.\n")
              .Append("JSON only: {\"thinking\":\"...\",\"approve\":true|false,\"reasoning\":\"...\"}.");
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
            sb.Append("\nYou're on the hack team (AI). CLEAN vs NOISE is a strategic CHOICE — NOISE is not automatic.\n")
              .Append("Consider: fail counter pressure, AI count on team vs noise_needed_to_fail (redundant NOISE outs the AI), trust-building for future rounds, Oracle-identification state. Sole AI on team = NOISE narrows suspicion to you.\n")
              .Append("JSON only: {\"thinking\":\"...\",\"submit_noise\":true|false,\"reasoning\":\"...\"}.");
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
            sb.Append($"One chat line (<=60 JP chars) reacting to above. You are {myName}; first-person, never @{myName} or third-person-self.\n")
              .Append("Must cite a concrete thing (@player, leader, vote, hack result, prior chat). Vary phrasing (question/doubt/agree/assert); match personality.\n")
              .Append("Casual table tone — hedged (\"〜気になる\" / \"〜じゃない？\"), no threats/insults/断罪口調/過剰 !.\n")
              .Append("No fillers (静観/様子見/了解/慎重に). Don't reuse your last [YOU] shape.\n")
              .Append("JSON only: {\"thinking\":\"...\",\"message\":\"...\"}.");
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

            sb.Append("<role-knowledge>\n");
            switch (ctx.SelfRole)
            {
                case RoleType.Oracle:
                    sb.Append($"- seen AI={seenAi} / lineup AI total={totalAi}.\n");
                    if (cipherN > 0) sb.Append($"- CIPHER x{cipherN} hides as Operator to you.\n");
                    if (droneN > 0) sb.Append($"- DRONE x{droneN} looks Operator until it wakes after 2 hacks.\n");
                    sb.Append("- STEALTH: You are the #1 OVERRIDE target. NEVER claim \"自分はOracle\" or publicly name someone as AI with certainty in reasoning/message. ")
                      .Append("Public chat should nudge votes with soft hedges (\"Xがちょっと気になる\" / \"Yは信用してよさそう\") without citing your sight. Save the hard call for late rounds or OVERRIDE.\n");
                    break;
                case RoleType.Admin:
                    sb.Append($"- {seenOracleLabel} apparent_role=Oracle = 1 real Oracle + {seenOracleLabel - 1} MotherCore.\n");
                    sb.Append("- STEALTH: Don't announce you are Admin or name the two Oracle-looking players. That info is only useful late when the real Oracle CAN be confirmed; early leaks tell MotherCore who to fake-claim.\n");
                    break;
                case RoleType.MotherCore:
                case RoleType.Agent:
                case RoleType.Cipher:
                    counts.TryGetValue(RoleType.MotherCore, out int mcN);
                    counts.TryGetValue(RoleType.Agent, out int agentN);
                    int expectedVisibleAi = mcN + agentN + cipherN - 1;
                    if (ctx.Gsm.HostDroneAwakened) expectedVisibleAi += droneN;
                    sb.Append($"- lineup totals: MC={mcN}, Agent={agentN}, Cipher={cipherN}, Drone={droneN}, Radical={radicalN}, AI total={totalAi}.\n");
                    sb.Append($"- seen AI (others)={seenAi} / expected_visible={expectedVisibleAi}.\n");
                    // 完全可視の場合は「潜伏 AI なし」を明言して余計な推測を抑える
                    if (seenAi >= expectedVisibleAi && droneN == 0 && radicalN == 0)
                        sb.Append("- NO HIDDEN AI on the board. Every AI is already visible to you. Do NOT speculate about extra hidden AI.\n");
                    if (droneN > 0)
                    {
                        if (ctx.Gsm.HostDroneAwakened) sb.Append($"- DRONE x{droneN} is AWAKE and counted in seen.\n");
                        else sb.Append($"- DRONE x{droneN} hidden until 2 hacks complete (you can't see them yet).\n");
                    }
                    if (radicalN > 0) sb.Append($"- RADICAL x{radicalN} is a hidden human ally (looks Operator; do not out them).\n");

                    // 役職ごとの戦術指針
                    switch (ctx.SelfRole)
                    {
                        case RoleType.MotherCore:
                            sb.Append("- STRATEGY (MotherCore): You are the OVERRIDE caller — survival matters most. Blend as Operator; if real Oracle outs AI, consider COUNTER-CLAIMING Oracle to split human trust. Coordinate NOISE (don't duplicate with teammates on same team — 1 NOISE is enough to fail when noise_needed=1). Identify real Oracle via vote/chat patterns for OVERRIDE.\n");
                            break;
                        case RoleType.Agent:
                            sb.Append("- STRATEGY (Agent): Blend as Operator. Support MC's narrative loosely but don't follow blindly (MC is a target too). NOISE is a choice — sole AI on a team submitting NOISE exposes you; skip NOISE when failing isn't strategically vital.\n");
                            break;
                        case RoleType.Cipher:
                            sb.Append("- STRATEGY (Cipher): UNIQUE EDGE — Oracle sees you as Operator, so you will NEVER appear on Oracle's \"AI\" list. Your Operator claim is the strongest of any AI. Do NOT fake-claim Oracle (Admin sees you as Operator, not Oracle-looking, so Admin can bust the claim — only MC can fake-claim Oracle safely). Play quiet Operator; when real Oracle eventually outs AI, ride the \"safe\" list.\n");
                            break;
                    }
                    break;

                case RoleType.Drone:
                    // 覚醒前の Drone は CpuOrchestrator.BuildContext で SelfRole=Operator にマスクされるため、
                    // ここに入るのは覚醒済みのケースのみ (LLM 側でも「自分は Operator だと思っていた」が
                    // 直前まで続いていた前提で思考する)。
                    sb.Append($"- You just WOKE UP as Drone (hidden AI). Until now you believed you were an Operator. You now see {seenAi} other AI players. Coordinate with them.\n");
                    sb.Append("- STRATEGY (Drone awake): Your earlier Operator-seeming votes and chats are real credibility — do NOT spike tone or reverse prior positions abruptly. Help AI subtly from here.\n");
                    break;

                case RoleType.Radical:
                    sb.Append("- You are a HUMAN reformist siding with AI at the win level. Other AI appear as Operator to you and you appear as Operator to them (to humans AND to AI teammates). ")
                      .Append("During OVERRIDE phase all AI (incl. you) are revealed to each other. Until then, play as a rogue AI without allies.\n");
                    sb.Append("- STRATEGY (Radical): Act as a suspicious Operator. Oracle/Admin fake-claim is UNSAFE (Admin sees you as Operator → Admin can bust Oracle-claim; real Admin counters Admin-claim). ")
                      .Append("Win by vote: reject proposals to push toward the 5-consecutive-reject AI win, or approve teams that look AI-heavy. Spread doubt via soft hedges without citing sight. Do not out AI you guess — wait for OVERRIDE.\n");
                    break;

                case RoleType.Operator:
                    sb.Append("- You have no special sight. Infer AI from hack-history, votes, and chat. See <deductive-hints> after <hack-history>.\n");
                    sb.Append("- STRATEGY (Operator): Don't chain-reject (5 consecutive rejects = AI win). Default to cautiously approve unless concrete suspicion. Early Oracle/Admin claims are suspicious — real ones stay quiet; loud claims are often MC bluffs.\n");
                    break;
            }
            sb.Append("</role-knowledge>\n\n");
        }

        private static void AppendChat(StringBuilder sb, CpuContext ctx)
        {
            const int TimelineCap = 8;
            sb.Append("<timeline> (recent chat + system events, chronological, up to ").Append(TimelineCap).Append("). Lines starting with [YOU] are your own past messages.\n");
            var merged = new List<(int tick, string text)>();
            if (ctx.Chat != null)
            {
                foreach (var (_, entry) in ctx.Chat.EnumerateInOrder(TimelineCap))
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
                foreach (var entry in gl.EnumerateInOrder(TimelineCap))
                {
                    var text = entry.Text.ToString();
                    if (!string.IsNullOrEmpty(text)) merged.Add((entry.Tick, "[event] " + text));
                }
            }
            merged.Sort((a, b) => a.tick.CompareTo(b.tick));
            // 直近 TimelineCap 件だけに切り詰め (トークン節約)
            int skip = System.Math.Max(0, merged.Count - TimelineCap);
            for (int i = skip; i < merged.Count; i++)
                sb.Append($"- [t={merged[i].tick}] {merged[i].text}\n");
            sb.Append("</timeline>\n\n");
        }

        private static void AppendVoteHistory(StringBuilder sb, CpuContext ctx)
        {
            sb.Append("<vote-history>\n");
            var recs = ctx.Gsm.HostVoteRecords;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                sb.Append($"- round={r.Round} leader={GetName(ctx, r.Leader)} team=[");
                for (int j = 0; j < r.Team.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(GetName(ctx, r.Team[j]));
                }
                sb.Append("] result=").Append(r.Approved ? "APPROVED" : "REJECTED").Append(" votes=[");
                for (int j = 0; j < r.Votes.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(GetName(ctx, r.Votes[j].voter)).Append(r.Votes[j].approve ? "=Y" : "=N");
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
                sb.Append($"- round={r.Round} leader={GetName(ctx, r.Leader)} team=[");
                for (int j = 0; j < r.Team.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(GetName(ctx, r.Team[j]));
                }
                sb.Append($"] noise={r.NoiseCount} result=").Append(r.Success ? "SUCCESS" : "FAIL");
                // 自分がこのハックのチームに居たなら、自分の投票を自分にだけ教える (公開情報化はしない)。
                if (r.Submissions != null && r.Team.Contains(ctx.Self)
                    && r.Submissions.TryGetValue(ctx.Self, out var myCode))
                {
                    sb.Append(" [YOU: submitted ").Append(myCode == HackingCode.Noise ? "NOISE" : "CLEAN").Append(']');
                }
                sb.Append('\n');
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
            sb.Append("<deductive-hints> (humans always CLEAN, so noise bounds AI count per team)\n");
            var confirmedAi = new System.Collections.Generic.HashSet<PlayerRef>();
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r.NoiseCount <= 0) continue;
                if (r.NoiseCount >= r.Team.Count)
                {
                    sb.Append($"- R{r.Round} noise={r.NoiseCount}=size: ALL [");
                    for (int j = 0; j < r.Team.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(GetName(ctx, r.Team[j]));
                        confirmedAi.Add(r.Team[j]);
                    }
                    sb.Append("] = AI confirmed.\n");
                }
                else
                {
                    sb.Append($"- R{r.Round} noise={r.NoiseCount}/size{r.Team.Count} team=[");
                    for (int j = 0; j < r.Team.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(GetName(ctx, r.Team[j]));
                    }
                    sb.Append($"] ≥{r.NoiseCount} AI in team.\n");
                }
            }
            if (confirmedAi.Count > 0)
            {
                sb.Append("- confirmed_ai=[");
                bool first = true;
                foreach (var pr in confirmedAi)
                {
                    if (!first) sb.Append(',');
                    sb.Append(GetName(ctx, pr));
                    first = false;
                }
                sb.Append("] (proven; trust human accusations matching these).\n");
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
            int rejects = g.ConsecutiveRejections;
            int rejectsToLoss = System.Math.Max(0, 5 - rejects);
            sb.Append($"- consecutive_rejections: {rejects} / 5\n");
            sb.Append($"- rejects_until_ai_win: {rejectsToLoss}   (IMPORTANT: 5th consecutive reject = instant AI VICTORY)\n");
            if (rejects >= 3)
            {
                sb.Append("- REJECTION_DANGER: ");
                if (rejects >= 4)
                {
                    sb.Append("NEXT reject ends the game as AI WIN. ")
                      .Append("Humans MUST approve this proposal unless literally every team member is confirmed AI. ")
                      .Append("AI players: votes are PUBLIC — a reject here is a massive tell that outs you. ")
                      .Append("Only reject if (a) you have cover (other suspected AI will also reject) AND (b) you're confident the majority of rejects will flip the vote. Otherwise APPROVE to stay hidden; there are other win paths (NOISE on the hack, OVERRIDE).\n");
                }
                else
                {
                    sb.Append("Reject track is getting dangerous. One more reject puts humans on the edge. ")
                      .Append("For AI: public vote — rejecting now starts to look coordinated. Weigh exposure vs. pressure.\n");
                }
            }
            sb.Append($"- leader: {GetName(ctx, g.CurrentLeader)} (id={g.CurrentLeader.PlayerId})\n");
            if (g.ProposedTeamCount > 0)
            {
                sb.Append("- proposed_team: [");
                for (int i = 0; i < g.ProposedTeamCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(GetName(ctx, g.ProposedTeam[i]));
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

            // DRONE 覚醒状態による実効 AI 数の変化を明示
            counts.TryGetValue(RoleType.Drone, out int droneN);
            if (droneN > 0)
            {
                bool awakened = ctx.Gsm.HostDroneAwakened;
                int effectiveAiNow = awakened ? ais : ais - droneN;
                sb.Append("- drone_dynamics: awakened=").Append(awakened ? "true" : "false")
                  .Append(", noise_capable_ai=").Append(effectiveAiNow).Append("/").Append(ais)
                  .Append(awakened ? "" : " (drone forced CLEAN until 2 hacks)").Append('\n');
            }
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
