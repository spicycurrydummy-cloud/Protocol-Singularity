using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using ProtocolSingularity.Core;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// 乱数ベースの CPU 判断ロジック。MVP / フォールバック用。
    /// ホスト側でのみ実行されるため、CpuContext.LookupRole を通じて役職情報を参照できる。
    /// </summary>
    public class RandomCpuBrain : ICpuBrain
    {
        private readonly Random _rng;

        public RandomCpuBrain(Random rng) { _rng = rng ?? new Random(); }

        public Task<List<PlayerRef>> ChooseTeamAsync(CpuContext ctx, CancellationToken ct)
            => Task.FromResult(ChooseTeam(ctx));

        public Task<bool> ChooseVoteAsync(CpuContext ctx, CancellationToken ct)
            => Task.FromResult(ChooseVote(ctx));

        public Task<bool> ChooseHackNoiseAsync(CpuContext ctx, CancellationToken ct)
            => Task.FromResult(ChooseHackNoise(ctx));

        public Task<PlayerRef> ChooseOverrideTargetAsync(CpuContext ctx, CancellationToken ct)
            => Task.FromResult(ChooseOverrideTarget(ctx));

        public Task<string> ComposeChatAsync(CpuContext ctx, CancellationToken ct)
        {
            // Random ブレインは賑やかし定型文を出さない。
            // 実際の発言は Mercury2CpuBrain が LLM で生成したときだけ投稿する。
            return Task.FromResult<string>(null);
        }

        public List<PlayerRef> ChooseTeam(CpuContext ctx)
        {
            var team = new List<PlayerRef> { ctx.Self };
            var all = ctx.AllPlayers();
            int size = Math.Min(ctx.Gsm.TeamSize, all.Count);

            if (ctx.SelfRole.IsAI())
            {
                foreach (var p in Shuffle(all))
                {
                    if (team.Count >= size) break;
                    if (p == ctx.Self) continue;
                    var role = ctx.LookupRole(p);
                    if (role.HasValue && role.Value.IsAI() && !team.Contains(p))
                        team.Add(p);
                }
            }

            foreach (var p in Shuffle(all))
            {
                if (team.Count >= size) break;
                if (!team.Contains(p)) team.Add(p);
            }
            return team;
        }

        public bool ChooseVote(CpuContext ctx)
        {
            var gsm = ctx.Gsm;
            if (ctx.SelfRole.IsAI())
            {
                int aiInTeam = 0;
                for (int i = 0; i < gsm.ProposedTeamCount; i++)
                {
                    var role = ctx.LookupRole(gsm.ProposedTeam[i]);
                    if (role.HasValue && role.Value.IsAI()) aiInTeam++;
                }
                if (aiInTeam >= 1) return _rng.NextDouble() < 0.8;
                return _rng.NextDouble() < 0.25;
            }
            double approveProb = gsm.ConsecutiveRejections >= 3 ? 0.75 : 0.55;
            return _rng.NextDouble() < approveProb;
        }

        public bool ChooseHackNoise(CpuContext ctx)
        {
            if (ctx.SelfRole == RoleType.MotherCore) return true;
            if (ctx.SelfRole == RoleType.Drone) return _rng.NextDouble() < 0.5;
            if (ctx.SelfRole == RoleType.Cipher) return _rng.NextDouble() < 0.7;
            return _rng.NextDouble() < 0.6;
        }

        public PlayerRef ChooseOverrideTarget(CpuContext ctx)
        {
            var gsm = ctx.Gsm;
            var humanCandidates = new List<PlayerRef>();
            PlayerRef knownOracle = PlayerRef.None;

            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                var p = gsm.LeaderOrder[i];
                if (p == ctx.Self) continue;
                var role = ctx.LookupRole(p);
                if (!role.HasValue) continue;
                if (role.Value == RoleType.Oracle) knownOracle = p;
                if (role.Value.IsHuman()) humanCandidates.Add(p);
            }

            if (knownOracle != PlayerRef.None) return knownOracle;
            if (humanCandidates.Count > 0) return humanCandidates[_rng.Next(humanCandidates.Count)];

            var all = new List<PlayerRef>();
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                var p = gsm.LeaderOrder[i];
                if (p != ctx.Self) all.Add(p);
            }
            return all.Count > 0 ? all[_rng.Next(all.Count)] : PlayerRef.None;
        }

        private IEnumerable<PlayerRef> Shuffle(List<PlayerRef> src)
        {
            var copy = new List<PlayerRef>(src);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }
            return copy;
        }
    }
}
