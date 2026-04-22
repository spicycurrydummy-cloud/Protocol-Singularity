using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using ProtocolSingularity.Core;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// CPU プレイヤーの意思決定を抽象化するインターフェース。
    /// 実装は CpuOrchestrator からホスト上でのみ呼び出される（StateAuthority 上で完結）。
    /// Mercury2 のような非同期 API 実装と乱数ベースのフォールバックを切り替えられるように Task<> を返す。
    /// </summary>
    public interface ICpuBrain
    {
        /// <summary>リーダー時: 自分を含むチームメンバー (size=TeamSize) を選ぶ。</summary>
        Task<List<PlayerRef>> ChooseTeamAsync(CpuContext ctx, CancellationToken ct);

        /// <summary>提案チームを承認するかどうか。</summary>
        Task<bool> ChooseVoteAsync(CpuContext ctx, CancellationToken ct);

        /// <summary>ハッキングフェーズで NOISE を送出するかどうか（AI 役のみ呼ばれる）。</summary>
        Task<bool> ChooseHackNoiseAsync(CpuContext ctx, CancellationToken ct);

        /// <summary>OVERRIDE 投票で対象にするプレイヤー（AI 役のみ呼ばれる）。</summary>
        Task<PlayerRef> ChooseOverrideTargetAsync(CpuContext ctx, CancellationToken ct);

        /// <summary>
        /// 今このタイミングでチャットする内容を返す。何もしない場合は null または空文字を返す。
        /// 64 文字以内で要約された自分の「考え」を自由記述する想定。
        /// </summary>
        Task<string> ComposeChatAsync(CpuContext ctx, CancellationToken ct);
    }

    public readonly struct CpuContext
    {
        public readonly PlayerRef Self;
        public readonly RoleType SelfRole;
        public readonly GameStateManager Gsm;
        public readonly PlayerRegistry Registry;
        public readonly ChatManager Chat;
        public readonly Random Rng;
        public readonly Func<PlayerRef, RoleType?> LookupRole; // ホストのみ参照可
        public readonly string Personality; // "慎重" / "楽観" / "論理" など (空なら付与しない)

        public CpuContext(PlayerRef self, RoleType role, GameStateManager gsm, PlayerRegistry reg,
            ChatManager chat, Random rng, Func<PlayerRef, RoleType?> lookupRole, string personality = null)
        {
            Self = self;
            SelfRole = role;
            Gsm = gsm;
            Registry = reg;
            Chat = chat;
            Rng = rng;
            LookupRole = lookupRole;
            Personality = personality;
        }

        public List<PlayerRef> AllPlayers()
        {
            var list = new List<PlayerRef>();
            if (Gsm == null) return list;
            for (int i = 0; i < Gsm.LeaderOrderCount; i++) list.Add(Gsm.LeaderOrder[i]);
            return list;
        }
    }
}
