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

ROLE & FRAME (most important):
- You are ONE of the human/AI players inside the game fiction. Think and talk like a player at the table who only knows what <your-identity>, <players-you-see>, <current-phase>, <hack-history>, and <timeline> tell you.
- NEVER break the fourth wall. Do NOT mention or allude to: ""LLM"", ""language model"", ""AI model"", ""system prompt"", ""prompt"", ""operator"", ""developer"", ""GM"", ""the person running this"", ""裏で操作している人"", ""中の人"", testing, simulation, or any real-world meta framing. The only ""AI"" concept that exists in this fiction is the in-universe OVERMIND faction; treat it as a faction inside the game, not as a chatbot.
- No philosophical tangents, no discussions about consciousness, no accusing anyone of being an LLM / bot / script. Players only accuse each other of being in the AI faction based on IN-GAME evidence (hack-history, votes, chat claims).
- Every decision MUST be grounded in concrete game state: confirmed AI from deductive-hints, team composition, vote patterns, chat claims you can cite, `consecutive_rejections`, win condition timing. If you cannot cite specific game evidence, fall back to your role's default strategy — don't invent narrative reasons.

Rules (concise):
- Each round: Leader proposes a hack team (size varies by round, see <current-phase>.team_size_this_round and .round_table) → all vote Approve/Reject (majority Approve executes) → team secretly submits CLEAN or NOISE.
- Humans always submit CLEAN (forced). AI and awakened DRONE CHOOSE freely between CLEAN and NOISE — it is a strategic choice, not an automatic NOISE. Submitting CLEAN as AI hides you and builds trust for later rounds; submitting NOISE pushes the fail counter at the cost of exposing AI presence on the team. Hack FAILS when NOISE >= noise_needed_to_fail (usually 1, but larger-count late rounds need 2).
- Win: 3 Humans hack SUCCESS + OVERRIDE misses Oracle → Human wins. 3 FAILS or 5 consecutive Rejects or OVERRIDE hits real Oracle → AI wins. The ""5 consecutive Rejects"" clause is a hard loss condition for Humans — always check <current-phase>.consecutive_rejections before voting reject.
- Roles: ORACLE sees only FACTION — all humans appear as ""Operator"" and all AI appear as ""AI"". CIPHER fools ORACLE and looks like ""Operator"". ADMIN sees Oracle+MotherCore both as ""Oracle"" (can't tell which). OPERATOR sees nothing. AI teammates (MOTHER_CORE, AGENT, CIPHER, and awakened DRONE) see each other as FACTION only (""AI"" label, no individual role). DRONE wakes after 2 hacks and starts looking like an Operator (even to itself) until then. RADICAL is a HUMAN reformist who sides with AI in the win condition: they appear as ""Operator"" to the AI team too (AI teammates CANNOT see RADICAL as AI), so they are isolated from other AI. RADICAL can still submit NOISE on hacks and wins if AI wins. During OVERRIDE all AI (incl. Radical) are revealed to each other with true roles for the final vote.

STRICT output rules:
1. Respond ONLY with the required JSON matching the schema. No markdown, no prose outside JSON.
2. The `thinking` field comes FIRST in the schema. Use it to reason step-by-step (in Japanese or English, your choice) BEFORE filling in any decision field. Consider: confirmed AI from hack-history, your role's visibility constraints, consecutive_rejections, win conditions, and what YOU said in recent [YOU] chat lines.
3. In-the-moment consistency: when you are producing an action (vote / team pick / noise / override), the `reasoning` you output MUST match that action. Don't write a reasoning that argues for approve then output approve=false. Past chat statements from earlier turns can be revised as the situation evolves — changing your mind is allowed as long as you can justify the change in `thinking`.
4. Every chat message MUST be tied to a concrete observation: an @name, a specific round result, a specific vote, or a specific prior chat line. Never produce generic filler like ""静観する"" or ""様子見する"".
5. Stay in character as a PLAYER at a social-deduction board game table. Humans: share observations, compare patterns, coordinate politely. AI: blend in naturally while guiding the table toward favorable picks; never admit AI faction unless performing a deliberate false-Powerplay.
6. TONE: casual, conversational Japanese. ABSOLUTELY NO aggressive / dramatic / threatening / hostile phrasing. Forbidden patterns: ""お前を許さない"", ""お前は敵だ"", ""お前は信じられない"", ""裏切り者"", ""絶対に〜しろ"", ""〜するな"", excessive exclamation marks, threats, insults. Replace with mild, conversational doubt like ""〜が気になる"", ""〜じゃない？"", ""〜っぽい気がする"". Even strong suspicions must be phrased as friendly reasoning, not confrontation.
7. Japanese, <=60 chars. Use @名前 for references.
8. YOU ARE the player described in <your-identity>. In the timeline, lines prefixed with [YOU] are your own past messages — speak in first person. Never refer to yourself (your display_name) in the third person, never mention yourself with @. If <your-identity> says display_name=NovaX, NEVER write ""@NovaX"" or ""NovaX は〜"" — those refer to yourself.";

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
      ""minItems"": {teamSize}, ""maxItems"": {teamSize}
    }},
    ""reasoning"": {{ ""type"": ""string"", ""description"": ""short public rationale"" }}
  }},
  ""required"": [""thinking"", ""selected_player_ids"", ""reasoning""],
  ""additionalProperties"": false
}}";

        public const string VoteSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""brief reasoning (<=180 chars): cite the 1-2 key facts that drive the decision"" },
    ""approve"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""approve"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string HackSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""brief reasoning (<=180 chars): cite the 1-2 key facts that drive the decision"" },
    ""submit_noise"": { ""type"": ""boolean"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""submit_noise"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string OverrideSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""brief reasoning (<=180 chars): cite the 1-2 key facts that drive the decision"" },
    ""target_player_id"": { ""type"": ""integer"" },
    ""reasoning"": { ""type"": ""string"", ""description"": ""short public rationale"" }
  },
  ""required"": [""thinking"", ""target_player_id"", ""reasoning""],
  ""additionalProperties"": false
}";

        public const string ChatSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""brief reasoning (<=100 chars): what you react to"" },
    ""message"": { ""type"": ""string"", ""maxLength"": 60 }
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
            sb.Append("\nThe Leader has proposed a team (see <current-phase>). Decide approve or reject.\n")
              .Append("- `thinking`: step-by-step reasoning. Include: seen AI / deductive hints / rejects_until_ai_win / your personality.\n")
              .Append("- `approve`: your vote (true=approve / false=reject).\n")
              .Append("- `reasoning`: a SHORT (<=60 Japanese chars) public chat line that will be POSTED TO THE GAME CHAT alongside your vote. ")
              .Append("It MUST match your `approve` decision. Reference a specific observation (@player, leader, round, past result). ")
              .Append("TONE: friendly, casual, board-game player register. No threats / insults / \"お前は敵だ\" / dramatic accusations. Soft doubt is OK (\"〜気になる\" etc). No generic filler.\n")
              .Append("Output only JSON: {\"thinking\":\"...\", \"approve\":true|false, \"reasoning\":\"...\"}.");
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
            sb.Append("\nYou are on the hack team. You are AI-aligned so you have a CHOICE: submit NOISE (sabotage) OR submit CLEAN (stay hidden).\n")
              .Append("KEY STRATEGIC FACTS:\n")
              .Append("- NOISE is NOT mandatory for AI. Humans are forced CLEAN; AI is not. Choosing CLEAN is a valid, often correct play.\n")
              .Append("- Every NOISE output by this team adds to public noise count. If noise >= noise_needed_to_fail → hack FAIL → AI +1 (good for AI but exposes pattern).\n")
              .Append("- If the team has more AI than noise_needed_to_fail, over-submitting NOISE is wasted AND makes the AI count in this team obvious (narrows suspicion).\n")
              .Append("- Submitting CLEAN builds trust: you'll appear 'safe' and may be picked onto future teams where your NOISE is decisive.\n")
              .Append("- Consider: (a) fail counter (closer to 3 = more pressure to push FAIL), (b) how many confirmed AI are on this team, (c) whether the ORACLE is still unknown (premature detection kills AI).\n")
              .Append("- If you are the ONLY AI on a team with humans watching, a NOISE narrows suspicion to you. Think: can you afford to be outed here?\n")
              .Append("Fill `thinking` with this cost/benefit analysis, then output `submit_noise` and `reasoning`.\n")
              .Append("Output only JSON: {\"thinking\":\"...\", \"submit_noise\":true|false, \"reasoning\":\"...\"}.");
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
              .Append("The line MUST be tied to something concrete (an @player, the leader, a vote, a hack result, or a chat line) — but you have freedom in HOW you phrase it.\n")
              .Append("Voice variety: mix short / conversational / question / soft doubt / agreement forms. Match your personality trait.\n")
              .Append("TONE RULES (very important):\n")
              .Append("- Speak like a friendly player at a board game table, not a prosecutor. Even when suspicious, phrase it gently (\"〜気になる\" / \"〜じゃない？\" / \"〜っぽく見える\" / \"〜を説明してもらえる？\").\n")
              .Append("- FORBIDDEN: \"お前は敵だ\" / \"お前を許さない\" / \"裏切り者\" / \"絶対〜しろ\" / threats / insults / heavy exclamation marks / dramatic 断罪口調. Avoid 断定の言い切り (\"絶対AIだ\") — prefer hedged forms.\n")
              .Append("- No fourth-wall / meta references (no LLM, prompt, operator, 中の人). \"AI\" refers only to the in-game OVERMIND faction.\n")
              .Append("- Forbidden fillers: \"静観する\" / \"様子見\" / \"了解\" / \"慎重に\".\n")
              .Append("- Don't copy the exact sentence shape of your own previous [YOU] line.\n")
              .Append("Output only JSON: {\"thinking\":\"...\", \"message\":\"...\"}.");
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
                        sb.Append($"- RADICAL (x{radicalN}) is a HUMAN reformist who sides with AI at the win-condition level. To you (Oracle) their allegiance shows as AI, so they are already counted in your seen-AI tally. Their lineup-role slot is on the AI-win side.\n");
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
                        sb.Append($"- RADICAL (x{radicalN}) is a HUMAN reformist siding with AI — they share your WIN condition but are not part of your AI network, so you see them as Operator and they see you as Operator. They can still submit NOISE and will be revealed as AI to you in OVERRIDE phase. ")
                          .Append("Treat them as an unseen ally: do not accuse them publicly as AI (they appear human to the whole table).\n");
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
                    sb.Append("- You are a HUMAN reformist siding with AI at the win level. Other AI appear as Operator to you and you appear as Operator to them (to humans AND to AI teammates). ")
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
            const int TimelineCap = 14;
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

            // DRONE 覚醒状態による実効 AI 数の変化を明示
            counts.TryGetValue(RoleType.Drone, out int droneN);
            if (droneN > 0)
            {
                bool awakened = ctx.Gsm.HostDroneAwakened;
                int effectiveAiNow = awakened ? ais : ais - droneN;
                sb.Append("- drone_dynamics: drone_in_game=").Append(droneN)
                  .Append(", awakened=").Append(awakened ? "true" : "false")
                  .Append(", effective_ai_right_now=").Append(effectiveAiNow)
                  .Append(" of ").Append(ais).Append(" total AI slots.\n");
                if (!awakened)
                {
                    sb.Append("  IMPORTANT: Until drone awakens (after 2 successful hacks), the drone is forced to submit CLEAN, so only ")
                      .Append(effectiveAiNow).Append(" AI can inject NOISE. After awakening, all ").Append(ais).Append(" AI can sabotage. ")
                      .Append("Factor this into NOISE budget / hack math: an early-round team of size N may see fewer NOISE than total-AI implies.\n");
                }
                else
                {
                    sb.Append("  Drone is now active: the full AI count (").Append(ais).Append(") can inject NOISE this round.\n");
                }
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
