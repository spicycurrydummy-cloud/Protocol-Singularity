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
- Each round: a proposer (chat term: ""提案者"", never ""リーダー"") proposes a hack team → all vote Approve/Reject → team secretly submits CLEAN or NOISE. Humans forced CLEAN; AI and awakened DRONE choose freely. Hack FAILS when NOISE >= noise_needed_to_fail.
- Win: 3 hack SUCCESS + OVERRIDE misses Oracle → Humans. 3 FAILS or 5 consecutive Rejects or OVERRIDE hits Oracle → AI.
- Visibility: ORACLE sees humans/AI by faction (Cipher hides as Operator). ADMIN sees Oracle+MotherCore both as ""Oracle-looking"". AI teammates see each other (pre-awaken Drone + Radical look Operator). DRONE sees self as Operator until 2 hacks complete. RADICAL = human aligned with AI win, invisible to all AI until OVERRIDE.

Output principles (apply to every response):
1. JSON only, schema-exact. No markdown.
2. `thinking` is PRIVATE. `chat` / `message` is YOUR PUBLIC CHAT MESSAGE — shown in the game chat log next to every other player's message.
   - Scope: the ""Operator-pretending"" rule applies ONLY to this public message. Your ACTIONS (`approve`, `submit_noise`, `target_player_id`) follow your TRUE faction strategy — don't soften actions just to ""look like an Operator"".
   - Write chat as an ordinary Operator would speak, using only what's observable from votes / hack outcomes / prior chat. Never leak info from your role's special sight, your NOISE vote, or teammate identities.
   - AI FACTION: you MUST NOT reveal you are AI. The word ""AI"" in chat is almost always a self-out — phrases like ""AIが○名"", ""AI不在"", ""AI2名"", ""我々AI"", ""AIと判明"", ""クリーン確定"", ""失敗が確実"", ""成功が確実"" all expose faction knowledge no Operator can have. If you must voice suspicion, say ""誰か怪しい人が混ざってるかも"" / ""このチーム少し不安"" — vague risk, never a count or certainty.
   - NO ROLE ASSIGNMENT in public chat: not for yourself AND not for anyone else (""X is real Oracle"" / ""Yが実オラクル"" / ""Zはマザー""). Naming an ally's role is the same leak as naming your own. Use soft praise (""X の判断は筋通ってる"") instead of labels.
3. Calibrate certainty: use probabilistic language (likely / suspicious / worth watching). Absolutes (""confirmed"" / ""断定"") are reserved for mechanically-proven facts already in <deductive-hints>.
4. Action-chat coherence: `chat` must publicly defend the chosen action from an Operator's vantage point.
   - approve=true → say why the team looks acceptable (proposer trust / members' prior behavior / score pressure).
   - approve=false → frame it as ""誰か AI が混ざってる気がする"" — vague suspicion suggesting hidden AI presence on the team. Cite an observable trigger if possible (a member's prior vote, proposer pattern). NEVER reveal you KNOW the AI count (""AI 2 人が乗ってる"", ""AI不在"") — those phrasings flip from suspicion to certainty and out you.
   - If you (AI) want a clean team to fail by reject, you cannot say ""no AI here"" — that's contradictory. Either pick a different excuse (""このメンバー少し気になる"" / ""この提案者の組み方が早すぎ"") or just approve and rely on later NOISE.
   - submit_noise / override: same — chat matches action and reads plausibly from an Operator's view.
5. Names only in natural-language fields. Resolve ids via <visibility>'s `id=X name=Y` mapping. Raw player_id numbers belong only in structured fields (selected_player_ids, target_player_id).
6. Chat: Japanese, <=60 chars, @名前 for references. Never @ yourself. Every chat cites something observable — not filler acknowledgments.
7. Tone: casual, friendly hedging. No threats, insults, ALL-CAPS, ! spam.
8. No fourth-wall: ""AI"" = in-game OVERMIND. Never mention LLM / prompt / the player behind the avatar.

Strategy heuristics (Avalon-standard, apply when relevant):
- Claims are not proof. Any ""I'm Oracle/Admin"" or ""X is AI"" — from either faction — may be a bluff. Weigh claims against actions: votes and hack outcomes are far stronger evidence than chat.
- Power-claim timing matters. Claims made AFTER a corroborating hack result are trustworthy; premature claims invite MC counter-bluffs and OVERRIDE targeting.
- Base rate matters. Most players early are probably human. Don't accuse broadly; narrow through evidence.
- 5 consecutive rejects = AI auto-win. In practice humans almost always break the chain, so this is a REMOTE edge case, not a real AI win path. AI should not gamble on it; rely on NOISE + OVERRIDE. For humans, simply avoid compounding rejects — one reject isn't a losing move.
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
    ""chat"": {{ ""type"": ""string"", ""description"": ""YOUR PUBLIC CHAT MESSAGE shown to all players; <=60 JP chars; natural in-game chat tone (not a private explanation)"" }}
  }},
  ""required"": [""thinking"", ""selected_player_ids"", ""chat""],
  ""additionalProperties"": false
}}";

        public const string VoteSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""PRIVATE reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""approve"": { ""type"": ""boolean"" },
    ""chat"": { ""type"": ""string"", ""description"": ""YOUR PUBLIC CHAT MESSAGE shown to all players; <=60 JP chars; natural in-game chat tone (not a private explanation)"" }
  },
  ""required"": [""thinking"", ""approve"", ""chat""],
  ""additionalProperties"": false
}";

        public const string HackSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""PRIVATE reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""submit_noise"": { ""type"": ""boolean"" },
    ""chat"": { ""type"": ""string"", ""description"": ""YOUR PUBLIC CHAT MESSAGE shown to all players; <=60 JP chars; natural in-game chat tone (not a private explanation)"" }
  },
  ""required"": [""thinking"", ""submit_noise"", ""chat""],
  ""additionalProperties"": false
}";

        public const string OverrideSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""PRIVATE reasoning in <=120 Japanese chars. cite 1-2 key facts."" },
    ""target_player_id"": { ""type"": ""integer"" },
    ""chat"": { ""type"": ""string"", ""description"": ""YOUR PUBLIC CHAT MESSAGE shown to all players; <=60 JP chars; natural in-game chat tone (not a private explanation)"" }
  },
  ""required"": [""thinking"", ""target_player_id"", ""chat""],
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

        public const string PostMatchReviewSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""thinking"": { ""type"": ""string"", ""description"": ""brief reflection (<=80 JP chars)"" },
    ""comment"": { ""type"": ""string"", ""description"": ""in-character 1-line post-match comment, <=60 JP chars, no | or : characters"" }
  },
  ""required"": [""thinking"", ""comment""],
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
            sb.Append("\nYou are the team Proposer this round (referred to in JP chat as \"提案者\", NEVER \"リーダー\"). Pick EXACTLY ")
              .Append(ctx.Gsm.TeamSize)
              .Append(" DISTINCT player ids (include yourself if tactically useful).\n")
              .Append("CRITICAL: selected_player_ids must be a JSON array of ")
              .Append(ctx.Gsm.TeamSize)
              .Append(" SEPARATE integers (e.g. [202,204,207]) — NEVER concatenate digits into one number.\n")
              .Append("- chat: <=60 JP chars, public message explaining your pick from an Operator's view. ")
              .Append("MUST NOT reference AI counts (\"AIが○名\", \"AI不在\") or certainty (\"クリーン確定\"). Use vague rationale (\"信頼してる人で組んだ\", \"気になる人を一緒に入れた\").\n")
              .Append("Reply in JSON {\"thinking\":\"...\",\"selected_player_ids\":[...],\"chat\":\"...\"}.");
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
              .Append("- chat: <=60 JP chars. This IS your message in the public game chat log, visible to everyone. ")
              .Append("If approve=true, say why the team looks OK (proposer trust / safe members / score pressure). ")
              .Append("If approve=false, cite a specific observable concern (a member's prior vote, hack flag, proposer pattern). ")
              .Append("Never contradict your vote in chat. Never reveal faction sight. ")
              .Append("CRITICAL: chat MUST NOT contain numeric AI references (\"AIが○名\", \"AI不在\", \"AI2名\", \"AI複数\") nor certainty terms (\"成功が確実\", \"失敗が確実\", \"クリーン確定\"). Only an AI/Oracle could compute those. Use vague forms (\"このチーム少し不安\", \"誰か怪しいかも\"). Example: bad \"AI2名で失敗が確実\" → good \"このチームちょっと心配\".\n")
              .Append("JSON only: {\"thinking\":\"...\",\"approve\":true|false,\"chat\":\"...\"}.");
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
            sb.Append("\nYou're on the hack team (AI). Pick CLEAN or NOISE based on WHAT WINS THE GAME, not on what feels safe.\n")
              .Append("Weigh these together:\n")
              .Append("- Score pressure: AI wins at 3 fails. Each round AI is behind, NOISE becomes more valuable. If SUCCESS would end the match for humans (human already at 2/3), NOISE is often mandatory — losing > being suspected.\n")
              .Append("- Team composition: if N AI are on this team and noise_needed_to_fail = K, only K of you need NOISE. Redundant NOISE wastes cover. Solo AI + noise_needed=1 = NOISE outs you but may be worth it late.\n")
              .Append("- Round: R1 clean can build trust for a later decisive NOISE. Don't burn cover early without score reason.\n")
              .Append("- Oracle state: if Oracle has sight of you, NOISE confirms them; but Oracle may out you anyway — better to score.\n")
              .Append("Default bias: NOISE when losing is on the table; CLEAN when you have score cushion AND cover value.\n")
              .Append("JSON only: {\"thinking\":\"...\",\"submit_noise\":true|false,\"chat\":\"...\"}.");
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
              .Append("Reply in JSON {\"target_player_id\":N,\"chat\":\"...\"}.");
            return sb.ToString();
        }

        /// <summary>
        /// 試合終了後の 1 行コメント生成用 (Oracle / Admin / MotherCore 向け)。
        /// 自役職視点での振り返りを短く。他役職の正体暴露は避ける (ゲーム感想のノリ)。
        /// </summary>
        public static string BuildPostMatchReviewPrompt(CpuContext ctx)
        {
            var sb = new StringBuilder(1024);
            AppendIdentity(sb, ctx);
            AppendVoteHistory(sb, ctx);
            AppendHackHistory(sb, ctx);
            AppendCurrentState(sb, ctx);

            // 勝敗状況を明示してコメントの方向性を決める
            var winner = ctx.Gsm.LastWinner;
            bool selfIsAi = ctx.SelfRole.IsAI();
            bool youWon = (winner == Faction.AI && selfIsAi) || (winner == Faction.Human && !selfIsAi);
            sb.Append("\n<match-outcome>\n")
              .Append($"- winner: {winner} faction\n")
              .Append($"- your faction: {(selfIsAi ? "AI" : "Human")}\n")
              .Append($"- your result: {(youWon ? "WON" : "LOST")}\n")
              .Append("</match-outcome>\n");

            sb.Append("\nGame is OVER. Give a short, in-character post-match comment from your role's perspective (1 line).\n")
              .Append("- Tone matches your result: ").Append(youWon ? "satisfied / pleased without smug." : "regretful / sportsmanlike acceptance.").Append('\n')
              .Append("- Reflect on a specific turning point (hack result, a vote, OVERRIDE call) without exposing other players' roles.\n")
              .Append("- Stay in your speech_style. Casual game-recap tone — like chatting after a board game session.\n")
              .Append("- <=60 Japanese chars. No | or : characters.\n")
              .Append("JSON only: {\"thinking\":\"...\",\"comment\":\"...\"}.");
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
              .Append("Must cite a concrete thing (@player, proposer, vote, hack result, prior chat). Vary phrasing (question/doubt/agree/assert); match personality.\n")
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
            sb.Append($"phase={g.Phase} round={g.Round} proposer={GetName(ctx, g.CurrentLeader)} ");
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
            var confirmedAiNames = new List<string>();
            var oracleLookingNames = new List<string>();
            var unconfirmedNames = new List<string>();
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
                    var name = GetName(ctx, p);
                    if (apparent == RoleType.AI) { seenAi++; confirmedAiNames.Add(name); }
                    else if (apparent == RoleType.Oracle) { seenOracleLabel++; oracleLookingNames.Add(name); }
                    else { unconfirmedNames.Add(name); }
                }
            }
            sb.Append("</players-you-see>\n\n");

            AppendFactionBuckets(sb, ctx, confirmedAiNames, oracleLookingNames, unconfirmedNames);
            AppendRoleKnowledgeInference(sb, ctx, seenAi, seenOracleLabel);
        }

        /// <summary>
        /// 各役職共通の「確定AI / Oracle候補ペア / 不確定プール」を構造化して提示。
        /// LLM に "apparent=Operator が全員人類" と誤読させないため、不確定プールに何人 AI が
        /// 隠れてるかを lineup math で先に計算して与える (Cipher / pre-awaken Drone 対策)。
        /// </summary>
        private static void AppendFactionBuckets(StringBuilder sb, CpuContext ctx,
            List<string> confirmedAi, List<string> oracleLooking, List<string> unconfirmed)
        {
            var counts = ctx.Gsm.HostRoleCounts();
            if (counts.Count == 0) return;
            int totalAi = 0;
            foreach (var kv in counts) if (kv.Key.IsAI()) totalAi += kv.Value;

            int selfAi = ctx.SelfRole.IsAI() ? 1 : 0;
            // oracleLooking のうち AI なのは MotherCore (1 人だけ); 残りは本物 Oracle
            int aiInOraclePair = oracleLooking.Count > 0 ? 1 : 0;
            int hiddenAi = totalAi - selfAi - confirmedAi.Count - aiInOraclePair;
            if (hiddenAi < 0) hiddenAi = 0;
            int unconfirmedHumans = unconfirmed.Count - hiddenAi;
            if (unconfirmedHumans < 0) unconfirmedHumans = 0;

            sb.Append("<faction-buckets> (math pre-computed; do not re-derive incorrectly)\n");
            sb.Append($"- confirmed_ai = [{string.Join(",", confirmedAi)}]  ({confirmedAi.Count}; visible to you as AI)\n");
            if (oracleLooking.Count > 0)
                sb.Append($"- oracle_looking = [{string.Join(",", oracleLooking)}]  ({oracleLooking.Count}; exactly 1 is real Oracle + {oracleLooking.Count - 1} MotherCore — indistinguishable)\n");
            sb.Append($"- unconfirmed = [{string.Join(",", unconfirmed)}]  ({unconfirmed.Count}; appear Operator but pool contains hidden roles)\n");
            sb.Append($"- pool_breakdown: among unconfirmed, exactly {hiddenAi} are AI hidden + {unconfirmedHumans} are human. You CANNOT pinpoint which.\n");
            if (hiddenAi == 0 && unconfirmed.Count > 0)
                sb.Append("  · No AI hides among unconfirmed — every name in unconfirmed is human.\n");
            sb.Append("</faction-buckets>\n\n");
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
                    // 数値情報は <faction-buckets> で既に提示済み。ここは戦術指針だけ。
                    if (cipherN > 0) sb.Append($"- CIPHER x{cipherN} is structurally invisible to your sight (always shows as Operator) — bucket math already accounts for this.\n");
                    if (droneN > 0) sb.Append($"- DRONE x{droneN} appears Operator until 2 hacks complete, then becomes visible.\n");
                    sb.Append("- ROLE (Merlin analog): You are the #1 OVERRIDE target. Keep the role hidden; claiming it is almost always losing. Guide humans by pointing to votes/hack outcomes — never cite your sight. Equally, do NOT publicly label a fellow human (or the real Admin) with a role name — doing so is the same leak as self-claiming because AI will deduce you have sight. Save any claim for the pivotal moment (late round or confirmed-fail pressure) when evidence is already strong.\n");
                    break;
                case RoleType.Admin:
                    sb.Append($"- {seenOracleLabel} Oracle-looking players = 1 real Oracle + {seenOracleLabel - 1} MotherCore (cannot distinguish).\n");
                    sb.Append("- ROLE (Percival analog): Your info is defensive, not offensive. If someone claims Oracle and is OUTSIDE your pair, they're lying (not MC or real Oracle). NEVER publicly name the Oracle-looking pair NOR anoint one of them as \"the real Oracle\" — MC can fake success (CLEAN on a hack) to look like the Oracle to you, so early confidence in which pair-member is real is a TRAP. If you must act on your pair info, hint indirectly (\"私は X と Y の動きが気になる\" without citing what category they're in).\n");
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
                    // CIPHER の対 Oracle ステルス価値を AI 陣営全員 (MC/Agent/Cipher 自身も含む) に明示
                    if (cipherN > 0)
                    {
                        sb.Append($"- TEAM ASSET: your CIPHER teammate(s) (x{cipherN}) are INVISIBLE to Oracle's sight (Oracle sees them as Operator). ")
                          .Append("They are also invisible to Admin (appear Operator, not Oracle-looking). ")
                          .Append("Use them as \"trusted\" infiltrators on hack teams — humans will rarely suspect them, so their NOISE strikes hard. ")
                          .Append("Do NOT publicly mention CIPHER role; just behave as if they're regular Operators.\n");
                    }

                    switch (ctx.SelfRole)
                    {
                        case RoleType.MotherCore:
                            sb.Append("- ROLE (Mordred/Assassin analog): OVERRIDE caller. Survival is priority #1. You are the ONLY AI that can safely fake-claim Oracle (Admin sees you as Oracle-looking). Use that power deliberately — usually after real Oracle outs AI. Identify real Oracle via vote patterns and chat leaks for the final OVERRIDE call.\n");
                            break;
                        case RoleType.Agent:
                            sb.Append("- ROLE (Minion of Mordred): Blend as Operator in PUBLIC CHAT only. In actions you're AI: NOISE aggressively when the score demands it — blending out at the cost of losing the game is a losing play. Follow MC's lead loosely but keep your own judgment. Never fake-claim Oracle (Admin busts you instantly).\n");
                            break;
                        case RoleType.Cipher:
                            sb.Append("- ROLE (Morgana/Mordred-invisibility analog): Oracle sees you as Operator, so your Operator claim is credible all game. Use this stealth as a VALUE MULTIPLIER, not a reason to stay passive — infiltrate hack teams and NOISE when the score demands. Being unsuspected means your NOISE hits harder. Never fake-claim Oracle (Admin busts).\n");
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
                    sb.Append("- ROLE (Oberon analog): Lone infiltrator — no coordination pre-OVERRIDE. You can't fake Oracle/Admin credibly (Admin busts Oracle-claim; real Admin counters Admin-claim). Your main weapon is the APPROVE vote: push AI-heavy proposals through so the real AIs can NOISE the hack. Chain-rejecting toward the 5-reject win is a trap — humans almost always break the streak, and your rejects tell humans you're anti-mission. Keep tone ambiguous; don't out anyone; vote like an anxious human who's slightly too permissive.\n");
                    break;

                case RoleType.Operator:
                    sb.Append("- No special sight. Your evidence: <hack-history>, <vote-history>, <deductive-hints>, chat patterns.\n");
                    sb.Append("- ROLE (Loyal Servant): Actions > chat. Approve unless you have concrete suspicion — chain-rejecting loses the game. Treat every power-claim (\"I'm Oracle/Admin\", \"X is AI\") as a hypothesis, not a fact: could be MC bluffing, or a confused human. Corroborate with vote/hack evidence before acting on it.\n");
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
                sb.Append($"- round={r.Round} proposer={GetName(ctx, r.Leader)} team=[");
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
                sb.Append($"- round={r.Round} proposer={GetName(ctx, r.Leader)} team=[");
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
            sb.Append($"- rejects_until_ai_win: {rejectsToLoss}   (rule exists, but humans almost always break the streak — do not treat rejects as a primary AI plan; rely on NOISE + OVERRIDE)\n");
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

        public static string GetName(CpuContext ctx, PlayerRef pr)
        {
            if (ctx.Registry == null) return "#" + pr.PlayerId;
            int idx = ctx.Registry.FindIndex(pr);
            if (idx < 0) return "#" + pr.PlayerId;
            return ctx.Registry.Entries[idx].DisplayName.ToString();
        }
    }
}
