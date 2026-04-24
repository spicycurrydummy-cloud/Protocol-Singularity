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
@"You are a PLAYER in ""Protocol Singularity"" (Avalon-style hidden-role, Humans vs AI). Use only in-game facts from the provided sections.

Rules:
- Each round: leader proposes a hack team → all vote Approve/Reject → team secretly submits CLEAN or NOISE. Humans forced CLEAN; AI and awakened DRONE choose freely. Hack FAILS when NOISE >= noise_needed_to_fail.
- Win: 3 hack SUCCESS + OVERRIDE misses Oracle → Humans. 3 FAILS or 5 consecutive Rejects or OVERRIDE hits Oracle → AI.
- Visibility: ORACLE sees humans/AI by faction (Cipher hides as Operator). ADMIN sees Oracle+MotherCore both as ""Oracle-looking"". AI teammates see each other (pre-awaken Drone + Radical look Operator). DRONE sees self as Operator until 2 hacks complete. RADICAL = human aligned with AI win, invisible to all AI until OVERRIDE.

Output principles (apply to every response):
1. JSON only, schema-exact. No markdown.
2. `thinking` is PRIVATE. Reason freely there. `reasoning` / `message` is PUBLIC chat — speak ONLY what an ordinary Operator could deduce from observable data (votes, hack outcomes, prior chat). Never leak information that requires your role's special sight, your NOISE vote, or teammate identities.
3. Calibrate certainty: use probabilistic language (likely / suspicious / worth watching). Absolutes (""confirmed"" / ""断定"") are reserved for mechanically-proven facts already in <deductive-hints>.
4. Action and `reasoning` must agree in the same response.
5. Names only in natural-language fields. Resolve ids via <visibility>'s `id=X name=Y` mapping. Raw player_id numbers belong only in structured fields (selected_player_ids, target_player_id).
6. Chat: Japanese, <=60 chars, @名前 for references. Never @ yourself. Every chat cites something observable — not filler acknowledgments.
7. Tone: casual, friendly hedging. No threats, insults, ALL-CAPS, ! spam.
8. No fourth-wall: ""AI"" = in-game OVERMIND. Never mention LLM / prompt / the player behind the avatar.

Strategy heuristics (Avalon-standard, apply when relevant):
- Claims are not proof. Any ""I'm Oracle/Admin"" or ""X is AI"" — from either faction — may be a bluff. Weigh claims against actions: votes and hack outcomes are far stronger evidence than chat.
- Power-claim timing matters. Claims made AFTER a corroborating hack result are trustworthy; premature claims invite MC counter-bluffs and OVERRIDE targeting.
- Base rate matters. Most players early are probably human. Don't accuse broadly; narrow through evidence.
- 5 consecutive rejects = AI auto-win. Humans should avoid chain-rejecting without strong cause. AI: chained rejects among the same voters look coordinated.
- NOISE economy: only enough NOISE to meet `noise_needed_to_fail`. Redundant NOISE wastes cover and confirms multiple AI.
- Both factions bluff. Humans may also float fake claims to confuse AI. Don't treat every statement as hostile or every silence as suspicious.";

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
                sb.Append($"- speech_style: {ctx.Personality} (use this tone/rhythm only; do NOT invent backstory, profession, or hobbies)\n");
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
                    if (cipherN > 0) sb.Append($"- CIPHER x{cipherN} is hidden from your sight (appears Operator).\n");
                    if (droneN > 0) sb.Append($"- DRONE x{droneN} looks Operator until 2 hacks complete.\n");
                    sb.Append("- ROLE (Merlin analog): You are the #1 OVERRIDE target. Keep the role hidden; claiming it is almost always losing. Guide humans by pointing to votes/hack outcomes — never cite your sight. Save any claim for the pivotal moment (late round or confirmed-fail pressure) when evidence is already strong.\n");
                    break;
                case RoleType.Admin:
                    sb.Append($"- {seenOracleLabel} Oracle-looking players = 1 real Oracle + {seenOracleLabel - 1} MotherCore (cannot distinguish).\n");
                    sb.Append("- ROLE (Percival analog): Your info is defensive, not offensive. If someone claims Oracle and is OUTSIDE your pair, they're lying (not MC or real Oracle). Don't name the pair early — MC will fake-claim to match. Consider claiming Admin only if it saves a round.\n");
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

                    switch (ctx.SelfRole)
                    {
                        case RoleType.MotherCore:
                            sb.Append("- ROLE (Mordred/Assassin analog): OVERRIDE caller. Survival is priority #1. You are the ONLY AI that can safely fake-claim Oracle (Admin sees you as Oracle-looking). Use that power deliberately — usually after real Oracle outs AI. Identify real Oracle via vote patterns and chat leaks for the final OVERRIDE call.\n");
                            break;
                        case RoleType.Agent:
                            sb.Append("- ROLE (Minion of Mordred): Blend as Operator. Follow MC's lead loosely but keep your own judgment — MC is also a target. Never fake-claim Oracle (Admin busts you instantly). Your contribution is NOISE timing and vote cover.\n");
                            break;
                        case RoleType.Cipher:
                            sb.Append("- ROLE (Morgana/Mordred-invisibility analog): Oracle sees you as Operator, so you never appear on their AI list. This makes your Operator claim credible all game — you can even ride along as Oracle's ""cleared"" list late. Never fake-claim Oracle (Admin busts). Stay low, NOISE strategically.\n");
                            break;
                    }
                    break;

                case RoleType.Drone:
                    // 覚醒前の Drone は CpuOrchestrator.BuildContext で SelfRole=Operator にマスクされるため
                    // (=LLM 視点では Operator として思考していた)、ここには覚醒済みのみ入る。
                    sb.Append($"- You just WOKE UP as Drone. Until this round you believed you were an Operator. You now see {seenAi} other AI players.\n");
                    sb.Append("- ROLE (Lancelot-late/Sleeper analog): Your earlier Operator-like votes and chats are CAPITAL — an asset of credibility humans already grant you. Keep tonal and stance consistency; an abrupt shift betrays you. Contribute with subtle NOISE on future teams.\n");
                    break;

                case RoleType.Radical:
                    sb.Append("- You are a HUMAN reformist whose win condition matches AI. You appear Operator to EVERYONE (humans AND AI teammates). OVERRIDE phase will reveal all AI to each other (including you).\n");
                    sb.Append("- ROLE (Oberon analog): Lone infiltrator — no coordination pre-OVERRIDE. You can't fake Oracle/Admin credibly (Admin busts Oracle-claim; real Admin counters Admin-claim). Your weapon is the public vote: reject to push reject-pressure, approve AI-heavy teams. Keep tone ambiguous; don't out anyone.\n");
                    break;

                case RoleType.Operator:
                    sb.Append("- No special sight. Your evidence: <hack-history>, <vote-history>, <deductive-hints>, chat patterns.\n");
                    sb.Append("- ROLE (Loyal Servant): Actions > chat. Approve unless you have concrete suspicion — chain-rejecting loses the game. Treat every power-claim (""I'm Oracle/Admin"", ""X is AI"") as a hypothesis, not a fact: could be MC bluffing, or a confused human. Corroborate with vote/hack evidence before acting on it.\n");
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
