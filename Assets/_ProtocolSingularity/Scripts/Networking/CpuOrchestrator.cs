using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using ProtocolSingularity.Core;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// CPU プレイヤーの意思決定タイミングをホスト側で駆動するシーン配置 NetworkBehaviour。
    /// GameStateManager のフェーズ遷移を監視し、必要な CPU に対して
    /// 遅延付きで ICpuBrain (async) を呼び出して GSM/ChatManager の RPC を叩く。
    /// </summary>
    public class CpuOrchestrator : NetworkBehaviour
    {
        public static CpuOrchestrator Instance { get; private set; }

        [Tooltip("CPU が意思決定するまでの最小秒数 (思考遅延)")]
        public float MinThinkSeconds = 3f;
        [Tooltip("CPU が意思決定するまでの最大秒数")]
        public float MaxThinkSeconds = 8f;

        private readonly Dictionary<PlayerRef, ICpuBrain> _brains = new();
        private readonly Dictionary<PlayerRef, string> _personalities = new();
        private static readonly string[] PersonalityPool =
        {
            "慎重タイプ (疑い深く、根拠の薄い主張には反対寄り。発言は短く絞る)",
            "楽観タイプ (前向きで提案を通したがる。フレンドリーな口調)",
            "論理タイプ (数字と因果で語る。過去の投票やハック結果を引用する)",
            "攻撃タイプ (疑わしいプレイヤーを名指しで追及する。威圧的)",
            "寡黙タイプ (発言量は少なめ。要点を短く断言する)"
        };
        private System.Random _rng;
        private CancellationTokenSource _cts;

        private GamePhase _lastPhase = (GamePhase)(-1);
        private int _lastRound = -1;
        private int _lastLeaderIdx = -1;
        private readonly HashSet<PlayerRef> _proposalDoneFor = new();
        private readonly HashSet<PlayerRef> _voteDoneFor = new();
        private readonly HashSet<PlayerRef> _hackDoneFor = new();
        private readonly HashSet<PlayerRef> _overrideDoneFor = new();
        private readonly HashSet<PlayerRef> _chatDoneFor = new();

        public override void Spawned()
        {
            Instance = this;
            _rng = new System.Random(Environment.TickCount ^ (Runner != null ? Runner.LocalPlayer.PlayerId : 0));
            _cts = new CancellationTokenSource();
            if (HasStateAuthority)
            {
                GameStateManager.Changed += OnGsmChanged;
                OnGsmChanged();
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
            GameStateManager.Changed -= OnGsmChanged;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// ホスト専用: 新ゲーム / ロビー復帰時に CPU サイドの状態を全リセット。
        /// 在飞行中の Mercury2 API 呼出しはキャンセルして、brain 再生成する。
        /// </summary>
        public void HostReset()
        {
            if (!HasStateAuthority) return;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _brains.Clear();
            _personalities.Clear();
            _proposalDoneFor.Clear();
            _voteDoneFor.Clear();
            _hackDoneFor.Clear();
            _overrideDoneFor.Clear();
            _chatDoneFor.Clear();
            _lastPhase = (GamePhase)(-1);
            _lastRound = -1;
            _lastLeaderIdx = -1;
            _rng = new System.Random(Environment.TickCount ^ (Runner != null ? Runner.LocalPlayer.PlayerId : 0));
        }

        private ICpuBrain GetBrain(PlayerRef cpu)
        {
            if (!_brains.TryGetValue(cpu, out var brain))
            {
                brain = new Mercury2CpuBrain(_rng);
                _brains[cpu] = brain;
            }
            return brain;
        }

        private CpuContext BuildContext(PlayerRef cpu, RoleType role, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!_personalities.TryGetValue(cpu, out var personality))
            {
                personality = PersonalityPool[_rng.Next(PersonalityPool.Length)];
                _personalities[cpu] = personality;
            }
            // 覚醒前の DRONE は自分を Operator と認識する (仕様): role を差し替えて LLM に渡す。
            // hack 周りの分岐 (role.IsAI() で NOISE 可否判定) も自動的に Human 扱いになる。
            // ホストの LookupRole は真の役職を返すので他プレイヤーの可視性計算には影響しない。
            var selfRoleView = (role == RoleType.Drone && !gsm.HostDroneAwakened)
                ? RoleType.Operator : role;
            return new CpuContext(
                cpu, selfRoleView, gsm, reg,
                ChatManager.Instance,
                _rng,
                gsm.TryGetHostRoleAsNullable,
                personality);
        }

        private void OnGsmChanged()
        {
            if (!HasStateAuthority) return;
            var gsm = GameStateManager.Instance;
            var reg = PlayerRegistry.Instance;
            if (gsm == null || reg == null) return;

            bool phaseChanged = gsm.Phase != _lastPhase;
            bool roundChanged = gsm.Round != _lastRound;
            bool leaderChanged = gsm.CurrentLeaderIndex != _lastLeaderIdx;

            if (phaseChanged || roundChanged || leaderChanged)
            {
                _proposalDoneFor.Clear();
                _voteDoneFor.Clear();
                _hackDoneFor.Clear();
                _overrideDoneFor.Clear();
                _chatDoneFor.Clear();

                _lastPhase = gsm.Phase;
                _lastRound = gsm.Round;
                _lastLeaderIdx = gsm.CurrentLeaderIndex;

                switch (gsm.Phase)
                {
                    case GamePhase.TeamProposal: ScheduleTeamProposal(gsm, reg); ScheduleChat(gsm, reg); break;
                    case GamePhase.ApprovalVote: ScheduleApprovalVote(gsm, reg); ScheduleChat(gsm, reg); break;
                    case GamePhase.Hacking: ScheduleHacking(gsm, reg); ScheduleChat(gsm, reg); break;
                    case GamePhase.OverrideDiscussion: ScheduleChat(gsm, reg); break;
                    case GamePhase.OverrideVote: ScheduleOverrideVote(gsm, reg); break;
                }
            }
        }

        // ==========================================================
        // Chat: 各 CPU が 1 フェーズで 1 回発言機会を得る (delay 後)
        // ==========================================================
        private void ScheduleChat(GameStateManager gsm, PlayerRegistry reg)
        {
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                var p = gsm.LeaderOrder[i];
                if (!CpuPlayerRef.IsCpu(p)) continue;
                if (_chatDoneFor.Contains(p)) continue;
                if (!gsm.TryGetHostRole(p, out var role)) continue;
                _chatDoneFor.Add(p);
                var captured = p;
                var capturedRole = role;
                StartCoroutine(RunAfterDelay(RandomThinkSeconds(), () => RunChatAsync(captured, capturedRole, gsm, reg)));
            }
        }

        private async Task RunChatAsync(PlayerRef speaker, RoleType role, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!HasStateAuthority) return;
            var ctx = BuildContext(speaker, role, gsm, reg);
            var message = await GetBrain(speaker).ComposeChatAsync(ctx, _cts.Token);
            if (string.IsNullOrWhiteSpace(message)) return;
            if (ChatManager.Instance == null) return;
            if (message.Length > 60) message = message.Substring(0, 60);
            ChatManager.Instance.Rpc_SendThought(speaker, message);
        }

        // ==========================================================
        // TeamProposal: リーダーが CPU の場合のみ
        // ==========================================================
        private void ScheduleTeamProposal(GameStateManager gsm, PlayerRegistry reg)
        {
            var leader = gsm.CurrentLeader;
            if (!CpuPlayerRef.IsCpu(leader)) return;
            if (_proposalDoneFor.Contains(leader)) return;
            _proposalDoneFor.Add(leader);
            StartCoroutine(RunAfterDelay(RandomThinkSeconds(), () => RunProposalAsync(leader, gsm, reg)));
        }

        private async Task RunProposalAsync(PlayerRef leader, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!HasStateAuthority) return;
            if (gsm == null || gsm.Phase != GamePhase.TeamProposal) return;
            if (gsm.CurrentLeader != leader) return;
            if (!gsm.TryGetHostRole(leader, out var role)) return;

            var ctx = BuildContext(leader, role, gsm, reg);
            var team = await GetBrain(leader).ChooseTeamAsync(ctx, _cts.Token);
            if (gsm.Phase != GamePhase.TeamProposal || gsm.CurrentLeader != leader) return;

            int size = Mathf.Min(team.Count, gsm.TeamSize);
            var p1 = size > 0 ? team[0] : PlayerRef.None;
            var p2 = size > 1 ? team[1] : PlayerRef.None;
            var p3 = size > 2 ? team[2] : PlayerRef.None;
            var p4 = size > 3 ? team[3] : PlayerRef.None;
            var p5 = size > 4 ? team[4] : PlayerRef.None;
            gsm.Rpc_ProposeTeamFlat(leader, p1, p2, p3, p4, p5, size);
        }

        // ==========================================================
        // ApprovalVote
        // ==========================================================
        private void ScheduleApprovalVote(GameStateManager gsm, PlayerRegistry reg)
        {
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                var p = gsm.LeaderOrder[i];
                if (!CpuPlayerRef.IsCpu(p)) continue;
                if (_voteDoneFor.Contains(p)) continue;
                _voteDoneFor.Add(p);
                var captured = p;
                StartCoroutine(RunAfterDelay(RandomThinkSeconds(), () => RunVoteAsync(captured, gsm, reg)));
            }
        }

        private async Task RunVoteAsync(PlayerRef voter, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!HasStateAuthority) return;
            if (gsm == null || gsm.Phase != GamePhase.ApprovalVote) return;
            if (!gsm.TryGetHostRole(voter, out var role)) return;

            var ctx = BuildContext(voter, role, gsm, reg);
            bool approve = await GetBrain(voter).ChooseVoteAsync(ctx, _cts.Token);
            if (gsm.Phase != GamePhase.ApprovalVote) return;
            gsm.Rpc_SubmitVote(voter, approve);
        }

        // ==========================================================
        // Hacking
        // ==========================================================
        private void ScheduleHacking(GameStateManager gsm, PlayerRegistry reg)
        {
            for (int i = 0; i < gsm.ProposedTeamCount; i++)
            {
                var p = gsm.ProposedTeam[i];
                if (!CpuPlayerRef.IsCpu(p)) continue;
                if (_hackDoneFor.Contains(p)) continue;
                if (!gsm.TryGetHostRole(p, out var role)) continue;
                if (role.IsHuman()) continue;
                // 覚醒前の DRONE は AI 陣営だが自覚がないため、NOISE 判断をさせず CLEAN 直送する。
                if (role == RoleType.Drone && !gsm.HostDroneAwakened)
                {
                    _hackDoneFor.Add(p);
                    var capturedPreAwake = p;
                    StartCoroutine(RunAfterDelay(RandomThinkSeconds(),
                        () => SubmitCleanPreAwakeAsync(capturedPreAwake, gsm)));
                    continue;
                }
                _hackDoneFor.Add(p);
                var capturedP = p;
                var capturedRole = role;
                StartCoroutine(RunAfterDelay(RandomThinkSeconds(), () => RunHackAsync(capturedP, capturedRole, gsm, reg)));
            }
        }

        private Task SubmitCleanPreAwakeAsync(PlayerRef member, GameStateManager gsm)
        {
            if (gsm == null || gsm.Phase != GamePhase.Hacking) return Task.CompletedTask;
            gsm.Rpc_SubmitHackCode(member, (int)HackingCode.Clean);
            return Task.CompletedTask;
        }

        private async Task RunHackAsync(PlayerRef member, RoleType role, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!HasStateAuthority) return;
            if (gsm == null || gsm.Phase != GamePhase.Hacking) return;

            var ctx = BuildContext(member, role, gsm, reg);
            bool noise = await GetBrain(member).ChooseHackNoiseAsync(ctx, _cts.Token);
            if (gsm.Phase != GamePhase.Hacking) return;
            gsm.Rpc_SubmitHackCode(member, (int)(noise ? HackingCode.Noise : HackingCode.Clean));
        }

        // ==========================================================
        // OverrideVote
        // ==========================================================
        private void ScheduleOverrideVote(GameStateManager gsm, PlayerRegistry reg)
        {
            for (int i = 0; i < gsm.LeaderOrderCount; i++)
            {
                var p = gsm.LeaderOrder[i];
                if (!CpuPlayerRef.IsCpu(p)) continue;
                if (_overrideDoneFor.Contains(p)) continue;
                if (!gsm.TryGetHostRole(p, out var role)) continue;
                if (!role.IsAI()) continue;
                _overrideDoneFor.Add(p);
                var capturedP = p;
                var capturedRole = role;
                StartCoroutine(RunAfterDelay(RandomThinkSeconds(), () => RunOverrideAsync(capturedP, capturedRole, gsm, reg)));
            }
        }

        private async Task RunOverrideAsync(PlayerRef voter, RoleType role, GameStateManager gsm, PlayerRegistry reg)
        {
            if (!HasStateAuthority) return;
            if (gsm == null || gsm.Phase != GamePhase.OverrideVote) return;

            var ctx = BuildContext(voter, role, gsm, reg);
            var target = await GetBrain(voter).ChooseOverrideTargetAsync(ctx, _cts.Token);
            if (gsm.Phase != GamePhase.OverrideVote) return;
            if (target == PlayerRef.None) return;
            gsm.Rpc_SubmitOverrideVote(voter, target);
        }

        // ==========================================================
        // Helpers
        // ==========================================================
        private IEnumerator RunAfterDelay(float delay, Func<Task> work)
        {
            yield return new WaitForSeconds(delay);
            if (!HasStateAuthority) yield break;
            var task = work();
            // Coroutine 上で Task 完了を待つ
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) Debug.LogWarning($"[CpuOrchestrator] Async decision failed: {task.Exception?.GetBaseException().Message}");
        }

        private float RandomThinkSeconds()
        {
            float lo = Mathf.Min(MinThinkSeconds, MaxThinkSeconds);
            float hi = Mathf.Max(MinThinkSeconds, MaxThinkSeconds);
            return lo + (float)_rng.NextDouble() * (hi - lo);
        }
    }
}
