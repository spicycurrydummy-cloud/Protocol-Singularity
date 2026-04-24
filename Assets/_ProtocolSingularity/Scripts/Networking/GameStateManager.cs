using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Fusion;
using ProtocolSingularity.Core;
using ProtocolSingularity.Data;
using ProtocolSingularity.Gameplay;
using UnityEngine;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// ゲーム全体のステートマシン。フェーズ・ラウンド・成功/失敗カウンタ・リーダー順を同期。
    /// 役職情報はホスト内メモリに保持し、各クライアントには Targeted RPC で個別の視認ビューを配信する。
    /// ハッキングフロー（提案→投票→実行→結果）を駆動する。
    /// </summary>
    public class GameStateManager : NetworkBehaviour
    {
        public static GameStateManager Instance { get; private set; }
        public static event Action Changed;
        public static event Action LocalRoleReceived;

        public const int MaxPlayers = 10;
        public const int MaxTeamSize = 5;
        public const int DefaultTeamSize = 3;
        public const int RequiredHackSuccess = 3;
        public const int RequiredHackFailure = 3;
        public const int MaxConsecutiveRejections = 5;
        public const int DroneAwakenAfterHackCount = 2;

        // ====== Networked public state ======
        [Networked, OnChangedRender(nameof(OnChanged))] public GamePhase Phase { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int Round { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int SuccessCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int FailureCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int ConsecutiveRejections { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int CurrentLeaderIndex { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int TeamSize { get; set; }
        /// <summary>このラウンドでハックが FAIL 判定になる最低 NOISE 枚数 (通常 1、後半ラウンドは 2 等)。</summary>
        [Networked, OnChangedRender(nameof(OnChanged))] public int RequiredNoise { get; set; }
        /// <summary>ゲーム開始時のプレイヤー総数。ラウンド毎テーブル参照に使う。</summary>
        [Networked, OnChangedRender(nameof(OnChanged))] public int TotalPlayers { get; set; }

        [Networked, Capacity(MaxPlayers), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<PlayerRef> LeaderOrder => default;
        [Networked, OnChangedRender(nameof(OnChanged))] public int LeaderOrderCount { get; set; }

        [Networked, Capacity(MaxTeamSize), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<PlayerRef> ProposedTeam => default;
        [Networked, OnChangedRender(nameof(OnChanged))] public int ProposedTeamCount { get; set; }

        // Approval votes indexed by LeaderOrder index. -1=pending, 0=reject, 1=approve
        [Networked, Capacity(MaxPlayers), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<int> ApprovalVotes => default;

        [Networked, OnChangedRender(nameof(OnChanged))] public int LastNoiseCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public Faction LastWinner { get; set; }

        // OVERRIDE フェーズ用
        [Networked, OnChangedRender(nameof(OnChanged))] public PlayerRef OverrideTarget { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public int OverrideVoteCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public bool OverrideSucceeded { get; set; }

        // Result 画面用: ハッキング履歴 (0=pending, 1=success, 2=fail) / 公開された全役職
        [Networked, Capacity(20), OnChangedRender(nameof(OnChanged))]
        public NetworkArray<int> HackHistory => default;
        [Networked, OnChangedRender(nameof(OnChanged))] public int HackHistoryCount { get; set; }
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkString<_512> RevealedRoles { get; set; }
        /// <summary>
        /// 過去のハッキング詳細 (全クライアント共有)。形式: "round:leaderId:memberId,memberId,..:noise:success(1/0)" を '|' 区切り。
        /// プレイヤー名は PlayerRegistry 側で解決する。
        /// </summary>
        [Networked, OnChangedRender(nameof(OnChanged))] public NetworkString<_256> HackDetailLog { get; set; }

        /// <summary>覚醒した DRONE の PlayerRef。未覚醒なら None。クライアント側の覚醒演出トリガに使用。</summary>
        [Networked, OnChangedRender(nameof(OnChanged))] public PlayerRef AwakenedDronePlayer { get; set; }

        // ====== Client-local state (via RPC) ======
        public RoleType LocalRole { get; private set; } = RoleType.Operator;
        public bool HasLocalRole { get; private set; }
        public IReadOnlyDictionary<int, RoleType> LocalVisibility => _localVisibility;
        private readonly Dictionary<int, RoleType> _localVisibility = new();

        // ====== Host-only state ======
        private enum VisibilityMode { InGame, Override }

        private readonly Dictionary<PlayerRef, RoleType> _assignedRoles = new();
        private readonly Dictionary<PlayerRef, HackingCode> _hackSubmissions = new();
        private readonly Dictionary<PlayerRef, PlayerRef> _overrideSubmissions = new();
        private readonly List<VoteRecord> _voteRecords = new();
        private readonly List<HackRecord> _hackRecords = new();
        private bool _droneAwakened;
        /// <summary>ホスト専用: Drone が覚醒済みか (CPU ブレインの可視性計算に使う)。</summary>
        public bool HostDroneAwakened => _droneAwakened;
        private int _completedHackCount;

        public struct VoteRecord
        {
            public int Round;
            public PlayerRef Leader;
            public List<PlayerRef> Team;
            public List<(PlayerRef voter, bool approve)> Votes;
            public bool Approved;
        }

        public struct HackRecord
        {
            public int Round;
            public PlayerRef Leader;
            public List<PlayerRef> Team;
            public int NoiseCount;
            public bool Success;
            /// <summary>
            /// ホスト専用の個別投票内訳。公開情報にはしないが、CPU ブレインは「自分が過去に
            /// 何を投げたか」を参照して推理に使える。clients には同期されない。
            /// </summary>
            public Dictionary<PlayerRef, HackingCode> Submissions;
        }

        /// <summary>ホスト専用: 過去ラウンドの提案・投票履歴 (CPU ブレインのコンテキスト用)。</summary>
        public IReadOnlyList<VoteRecord> HostVoteRecords => _voteRecords;
        /// <summary>ホスト専用: 過去ラウンドのハック履歴 (CPU ブレインのコンテキスト用)。</summary>
        public IReadOnlyList<HackRecord> HostHackRecords => _hackRecords;
        private Coroutine _roundResultTimer;
        private Coroutine _overridePhaseTimer;
        private Coroutine _voteRevealTimer;
        private Coroutine _hackCompleteTimer;
        /// <summary>ハッキング演出 (プログレスバー + フレーバー) が十分見える最低秒数。</summary>
        public const float MinHackDisplaySeconds = 6f;
        private float _hackingPhaseStartTime;
        public const float VoteRevealSeconds = 4f;

        public override void Spawned()
        {
            Instance = this;
            if (HasStateAuthority && Phase == default(GamePhase)) Phase = GamePhase.Lobby;
            if (HasStateAuthority) LastNoiseCount = -1;
            Changed?.Invoke();
        }

        /// <summary>
        /// クライアント側: フェーズ変化を [Networked] で検知した直後に役職未受信なら
        /// 1 回だけ再送要求を送る。さらに 3 秒後に再試行を 1 回だけ実行する (計 2 回まで)。
        /// Update で毎フレーム送るのは Photon 帯域の無駄なので避ける。
        /// </summary>
        private Coroutine _roleRequestRetry;
        private void MaybeRequestRoleOnPhaseChange()
        {
            if (Runner == null) return;
            if (HasStateAuthority) return;
            if (HasLocalRole) return;
            if (Phase == GamePhase.Lobby || Phase == GamePhase.GameEnd) return;
            if (_roleRequestRetry != null) return;
            Debug.Log($"[GSM] Client missing role after phase change -> requesting (phase={Phase} localPid={Runner.LocalPlayer.PlayerId})");
            Rpc_RequestRoleView(Runner.LocalPlayer);
            _roleRequestRetry = StartCoroutine(RoleRequestRetryCoroutine());
        }

        private System.Collections.IEnumerator RoleRequestRetryCoroutine()
        {
            yield return new WaitForSeconds(3f);
            if (!HasStateAuthority && !HasLocalRole && Runner != null
                && Phase != GamePhase.Lobby && Phase != GamePhase.GameEnd)
            {
                Debug.Log($"[GSM] Role still not received after 3s; retry 1 (phase={Phase}).");
                Rpc_RequestRoleView(Runner.LocalPlayer);
            }
            _roleRequestRetry = null;
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void Rpc_RequestRoleView(PlayerRef requester)
        {
            if (!HasStateAuthority) return;
            Debug.Log($"[GSM] Rpc_RequestRoleView from PlayerId={requester.PlayerId}");
            DeliverRoleView(requester);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        // ==========================================================
        // Host: game setup
        // ==========================================================
        public bool HostBeginGame(RoleDistributionConfig config, int seed)
        {
            if (config == null) { Debug.LogError("GameStateManager: config missing"); return false; }
            var reg = PlayerRegistry.Instance;
            if (reg == null) { Debug.LogError("GameStateManager: PlayerRegistry missing"); return false; }
            var entry = config.GetEntry(reg.Count);
            if (entry == null)
            {
                Debug.LogError($"GameStateManager: no role distribution for {reg.Count} players");
                return false;
            }
            return HostBeginGame(entry, seed);
        }

        /// <summary>ホスト専用: 任意の Entry (ロビーで動的に調整されたもの等) でゲームを開始する。</summary>
        public bool HostBeginGame(RoleDistributionConfig.Entry entry, int seed)
        {
            if (!HasStateAuthority) return false;
            var reg = PlayerRegistry.Instance;
            if (reg == null) { Debug.LogError("GameStateManager: PlayerRegistry missing"); return false; }
            if (entry == null) { Debug.LogError("GameStateManager: entry missing"); return false; }

            var players = new List<PlayerRef>();
            for (int i = 0; i < reg.Count; i++)
            {
                var e = reg.Entries[i];
                players.Add(e.PlayerRef);
            }

            if (entry.TotalPlayers != players.Count)
            {
                Debug.LogError($"GameStateManager: entry yields {entry.TotalPlayers} roles but {players.Count} players");
                return false;
            }

            var roles = RoleAssigner.Assign(entry, seed);
            _assignedRoles.Clear();
            for (int i = 0; i < players.Count; i++)
                _assignedRoles[players[i]] = roles[i];

            var order = new List<PlayerRef>(players);
            ShuffleInPlace(order, seed + 1);
            for (int i = 0; i < order.Count && i < LeaderOrder.Length; i++)
                LeaderOrder.Set(i, order[i]);
            LeaderOrderCount = order.Count;

            TotalPlayers = order.Count;
            CurrentLeaderIndex = 0;
            Round = 1;
            ApplyRoundParams();
            SuccessCount = 0;
            FailureCount = 0;
            ConsecutiveRejections = 0;
            LastNoiseCount = -1;
            _droneAwakened = false;
            AwakenedDronePlayer = PlayerRef.None;
            _completedHackCount = 0;
            _hackSubmissions.Clear();
            _overrideSubmissions.Clear();
            _voteRecords.Clear();
            _hackRecords.Clear();
            ClearProposalAndVotes();
            HackHistoryCount = 0;
            for (int i = 0; i < HackHistory.Length; i++) HackHistory.Set(i, 0);
            RevealedRoles = string.Empty;
            HackDetailLog = string.Empty;
            OverrideTarget = PlayerRef.None;
            OverrideVoteCount = 0;
            OverrideSucceeded = false;

            Debug.Log($"[GSM] HostBeginGame: assigning {_assignedRoles.Count} roles to {players.Count} players");
            foreach (var p in players)
            {
                if (CpuPlayerRef.IsCpu(p))
                {
                    Debug.Log($"[GSM] skip CPU PlayerId={p.PlayerId}");
                    continue;
                }
                DeliverRoleView(p);
            }

            GameLog.Instance?.HostClear();
            ChatManager.Instance?.HostClear();
            CpuOrchestrator.Instance?.HostReset();
            LogEvent($"[GAME] START: {players.Count} operators connected");
            LogEvent($"[ROUND] R1 Team={TeamSize} Fail>={RequiredNoise} Leader:{NameOf(CurrentLeader)}");

            Phase = GamePhase.TeamProposal;
            return true;
        }

        public void HostAwakenDrone()
        {
            if (!HasStateAuthority) return;
            // Drone 役が配役されていない試合では awaken は不要 (ログも出さない)
            bool hasDrone = false;
            foreach (var r in _assignedRoles.Values)
            {
                if (r == RoleType.Drone) { hasDrone = true; break; }
            }
            if (!hasDrone)
            {
                _droneAwakened = true; // 再呼出しを抑止するためフラグだけ立てる
                return;
            }
            _droneAwakened = true;
            // 覚醒した Drone の PlayerRef を networked に流してクライアント側で演出トリガ
            PlayerRef awakenedDrone = PlayerRef.None;
            foreach (var kv in _assignedRoles)
            {
                if (kv.Value == RoleType.Drone) { awakenedDrone = kv.Key; break; }
            }
            AwakenedDronePlayer = awakenedDrone;
            foreach (var p in _assignedRoles.Keys)
            {
                if (CpuPlayerRef.IsCpu(p)) continue;
                DeliverRoleView(p);
            }
            LogEvent("[GAME] DRONE awakened. New AI node activated.");
        }

        // ==========================================================
        // Role view distribution
        // ==========================================================
        private void DeliverRoleView(PlayerRef target, VisibilityMode mode = VisibilityMode.InGame)
        {
            if (!_assignedRoles.TryGetValue(target, out var myRole))
            {
                Debug.LogWarning($"[GSM] DeliverRoleView: no role assigned for PlayerId={target.PlayerId} (mode={mode})");
                return;
            }
            // 覚醒前の DRONE は自分を Operator として認識する (仕様)。
            // - 視界計算は本来の役職 (Drone) ベース → 他 AI は Operator として見える (既に RoleVisibility 側で処理)
            // - 通知される「自分の役職」だけ Operator に差し替える
            var selfRoleToSend = myRole;
            if (myRole == RoleType.Drone && !_droneAwakened && mode == VisibilityMode.InGame)
            {
                selfRoleToSend = RoleType.Operator;
            }
            var sb = new StringBuilder(256);
            foreach (var kvp in _assignedRoles)
            {
                var visible = mode == VisibilityMode.Override
                    ? RoleVisibility.ResolveAtOverride(myRole, kvp.Value)
                    : RoleVisibility.Resolve(myRole, kvp.Value, _droneAwakened);
                sb.Append(kvp.Key.PlayerId).Append(':').Append((int)visible).Append('|');
            }
            Debug.Log($"[GSM] DeliverRoleView -> RPC target PlayerId={target.PlayerId} role={selfRoleToSend} (true={myRole}) mode={mode} payloadLen={sb.Length}");
            Rpc_DeliverRoleView(target, (int)selfRoleToSend, sb.ToString());
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
        private void Rpc_DeliverRoleView([RpcTarget] PlayerRef target, int myRoleInt, NetworkString<_512> visibilityData)
        {
            Debug.Log($"[GSM] Rpc_DeliverRoleView received on LocalPlayer={(Runner != null ? Runner.LocalPlayer.PlayerId : -1)} targetPid={target.PlayerId} role={(RoleType)myRoleInt}");
            LocalRole = (RoleType)myRoleInt;
            HasLocalRole = true;
            _localVisibility.Clear();
            var s = visibilityData.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                foreach (var p in s.Split('|'))
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    var kv = p.Split(':');
                    if (kv.Length != 2) continue;
                    if (int.TryParse(kv[0], out var id) && int.TryParse(kv[1], out var r))
                        _localVisibility[id] = (RoleType)r;
                }
            }
            LocalRoleReceived?.Invoke();
            Changed?.Invoke();
        }

        public RoleType GetVisibleRole(int targetPlayerId)
        {
            return _localVisibility.TryGetValue(targetPlayerId, out var r) ? r : RoleType.Operator;
        }

        /// <summary>ホスト専用: 割り当てられた役職を参照する (CPU ブレインが利用)。</summary>
        public bool TryGetHostRole(PlayerRef p, out RoleType role)
        {
            role = default;
            if (!HasStateAuthority) return false;
            return _assignedRoles.TryGetValue(p, out role);
        }

        /// <summary>ホスト専用: CpuContext 用に nullable で役職を返す。</summary>
        public RoleType? TryGetHostRoleAsNullable(PlayerRef p)
        {
            if (!HasStateAuthority) return null;
            return _assignedRoles.TryGetValue(p, out var r) ? r : (RoleType?)null;
        }

        /// <summary>ホスト専用: 今回の試合の役職構成を集計して返す (CPU プロンプト用)。</summary>
        public Dictionary<RoleType, int> HostRoleCounts()
        {
            var dict = new Dictionary<RoleType, int>();
            if (!HasStateAuthority) return dict;
            foreach (var r in _assignedRoles.Values)
            {
                dict.TryGetValue(r, out var c);
                dict[r] = c + 1;
            }
            return dict;
        }

        // ==========================================================
        // Team proposal (leader)
        // ==========================================================
        public PlayerRef CurrentLeader =>
            LeaderOrderCount > 0 && CurrentLeaderIndex >= 0 && CurrentLeaderIndex < LeaderOrderCount
                ? LeaderOrder[CurrentLeaderIndex]
                : PlayerRef.None;

        public bool IsLocalPlayerLeader(PlayerRef localPlayer) => CurrentLeader == localPlayer;

        // Fusion の RPC は NetworkArray を引数に取れないため、最大 MaxTeamSize(=5) スロットの個別引数で渡す。
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_ProposeTeamFlat(PlayerRef leader,
            PlayerRef p1, PlayerRef p2, PlayerRef p3, PlayerRef p4, PlayerRef p5,
            int count, RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            if (Phase != GamePhase.TeamProposal) return;
            if (leader != CurrentLeader) return;
            if (count <= 0 || count > TeamSize) return;

            var slots = new PlayerRef[] { p1, p2, p3, p4, p5 };
            ProposedTeamCount = count;
            for (int i = 0; i < ProposedTeam.Length; i++)
                ProposedTeam.Set(i, i < count ? slots[i] : PlayerRef.None);

            for (int i = 0; i < ApprovalVotes.Length; i++) ApprovalVotes.Set(i, -1);

            var teamNames = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) teamNames.Append(", ");
                teamNames.Append(NameOf(slots[i]));
            }
            LogEvent($"[TEAM] R{Round} {NameOf(leader)} -> [{teamNames}]");

            Phase = GamePhase.ApprovalVote;
        }

        // ==========================================================
        // Approval vote
        // ==========================================================
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_SubmitVote(PlayerRef voter, NetworkBool approve, RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            if (Phase != GamePhase.ApprovalVote) return;
            // 集計済み (リビール待ちの 4s) に届いた遅延票は無視する
            if (_voteRevealTimer != null) return;

            int idx = IndexOfPlayer(voter);
            if (idx < 0) return;

            ApprovalVotes.Set(idx, approve ? 1 : 0);

            // All in?
            bool complete = true;
            for (int i = 0; i < LeaderOrderCount; i++)
            {
                if (ApprovalVotes[i] == -1) { complete = false; break; }
            }
            if (complete) TallyVote();
        }

        private void TallyVote()
        {
            int yes = 0, no = 0;
            var votes = new List<(PlayerRef, bool)>();
            for (int i = 0; i < LeaderOrderCount; i++)
            {
                var v = ApprovalVotes[i];
                if (v == 1) { yes++; votes.Add((LeaderOrder[i], true)); }
                else if (v == 0) { no++; votes.Add((LeaderOrder[i], false)); }
            }
            bool approved = yes > no;
            var team = new List<PlayerRef>();
            for (int i = 0; i < ProposedTeamCount; i++) team.Add(ProposedTeam[i]);
            _voteRecords.Add(new VoteRecord
            {
                Round = Round, Leader = CurrentLeader,
                Team = team, Votes = votes, Approved = approved
            });

            LogEvent($"[VOTE] R{Round} {(approved ? "承認" : "却下")} ({yes} 対 {no})");
            LogVoteBreakdown("Y", votes, true);
            LogVoteBreakdown("N", votes, false);

            // 投票開示のため少し待ってから次フェーズへ進む。ApprovalVotes は保持されたままなので UI で可視化できる。
            if (_voteRevealTimer != null) StopCoroutine(_voteRevealTimer);
            _voteRevealTimer = StartCoroutine(DelayThen(VoteRevealSeconds, () => AdvanceAfterVote(approved)));
        }

        private void AdvanceAfterVote(bool approved)
        {
            if (!HasStateAuthority) return;
            _voteRevealTimer = null;
            // ゲームが既に終わっているか他のフェーズへ遷移済みなら何もしない
            if (Phase != GamePhase.ApprovalVote) return;

            if (approved)
            {
                ConsecutiveRejections = 0;
                BeginHacking();
            }
            else
            {
                ConsecutiveRejections++;
                if (ConsecutiveRejections >= MaxConsecutiveRejections)
                {
                    LogEvent($"[GAME] 5 consecutive rejections -> AI VICTORY");
                    EndGame(Faction.AI);
                    return;
                }
                AdvanceLeader();
                ClearProposalAndVotes();
                LogEvent($"[VOTE] Rejects {ConsecutiveRejections}/{MaxConsecutiveRejections} Next leader:{NameOf(CurrentLeader)}");
                Phase = GamePhase.TeamProposal;
            }
        }

        /// <summary>誰が Y / N に投票したかの名前リストを GameLog へ (64 chars を超える場合は切り詰め)。</summary>
        private void LogVoteBreakdown(string tag, List<(PlayerRef voter, bool approve)> votes, bool approve)
        {
            var sb = new StringBuilder(64);
            sb.Append("[VOTE] R").Append(Round).Append(' ').Append(tag).Append(":");
            bool first = true;
            foreach (var v in votes)
            {
                if (v.approve != approve) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(NameOf(v.voter));
                if (sb.Length >= 58) { sb.Append("..."); break; }
            }
            if (first) sb.Append("-"); // no voters on this side
            LogEvent(sb.ToString());
        }

        private int IndexOfPlayer(PlayerRef pr)
        {
            for (int i = 0; i < LeaderOrderCount; i++)
                if (LeaderOrder[i] == pr) return i;
            return -1;
        }

        private void AdvanceLeader()
        {
            if (LeaderOrderCount <= 0) return;
            CurrentLeaderIndex = (CurrentLeaderIndex + 1) % LeaderOrderCount;
        }

        /// <summary>現在の Round と TotalPlayers から TeamSize / RequiredNoise を更新。</summary>
        private void ApplyRoundParams()
        {
            if (!HasStateAuthority) return;
            int pc = TotalPlayers > 0 ? TotalPlayers : LeaderOrderCount;
            int rawSize = RoundTable.GetTeamSize(pc, Round);
            TeamSize = Mathf.Clamp(rawSize, 1, Mathf.Min(MaxTeamSize, pc));
            RequiredNoise = Mathf.Max(1, RoundTable.GetRequiredNoise(pc, Round));
        }

        private void ClearProposalAndVotes()
        {
            ProposedTeamCount = 0;
            for (int i = 0; i < ProposedTeam.Length; i++) ProposedTeam.Set(i, PlayerRef.None);
            for (int i = 0; i < ApprovalVotes.Length; i++) ApprovalVotes.Set(i, -1);
        }

        // ==========================================================
        // Hacking
        // ==========================================================
        private void BeginHacking()
        {
            _hackSubmissions.Clear();
            // 人類陣営と覚醒前 DRONE は自動 CLEAN
            for (int i = 0; i < ProposedTeamCount; i++)
            {
                var pr = ProposedTeam[i];
                if (!_assignedRoles.TryGetValue(pr, out var role)) continue;
                bool forcedClean = role.IsHuman() || (role == RoleType.Drone && !_droneAwakened);
                if (forcedClean) _hackSubmissions[pr] = HackingCode.Clean;
            }
            Phase = GamePhase.Hacking;
            _hackingPhaseStartTime = Time.time;
            if (_hackCompleteTimer != null) { StopCoroutine(_hackCompleteTimer); _hackCompleteTimer = null; }
            TryCompleteHacking();
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_SubmitHackCode(PlayerRef member, int codeInt, RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            if (Phase != GamePhase.Hacking) return;
            if (!IsInProposedTeam(member)) return;
            if (_hackSubmissions.ContainsKey(member)) return; // already submitted (forced or prior)

            var code = (HackingCode)codeInt;
            // AI かつ覚醒済み Drone なら NOISE 選択可、それ以外は CLEAN のみ
            if (_assignedRoles.TryGetValue(member, out var role))
            {
                bool canNoise = role.IsAI() && !(role == RoleType.Drone && !_droneAwakened);
                if (code == HackingCode.Noise && !canNoise) code = HackingCode.Clean;
            }
            _hackSubmissions[member] = code;
            TryCompleteHacking();
        }

        private bool IsInProposedTeam(PlayerRef pr)
        {
            for (int i = 0; i < ProposedTeamCount; i++)
                if (ProposedTeam[i] == pr) return true;
            return false;
        }

        private void TryCompleteHacking()
        {
            if (_hackSubmissions.Count < ProposedTeamCount) return;
            // 提出が揃っても、プログレスバー演出が見える最低時間に満たない場合は遅延
            float elapsed = Time.time - _hackingPhaseStartTime;
            if (elapsed < MinHackDisplaySeconds && _hackCompleteTimer == null)
            {
                _hackCompleteTimer = StartCoroutine(DelayThen(MinHackDisplaySeconds - elapsed, FinalizeHackingDeferred));
                return;
            }
            FinalizeHacking();
        }

        private void FinalizeHackingDeferred()
        {
            _hackCompleteTimer = null;
            // 再チェック (ロビー戻りなどで状態が変わっていることもある)
            if (!HasStateAuthority) return;
            if (Phase != GamePhase.Hacking) return;
            if (_hackSubmissions.Count < ProposedTeamCount) return;
            FinalizeHacking();
        }

        private void FinalizeHacking()
        {
            int noise = 0;
            foreach (var kvp in _hackSubmissions)
                if (kvp.Value == HackingCode.Noise) noise++;
            LastNoiseCount = noise;
            int failThreshold = Mathf.Max(1, RequiredNoise);
            bool success = noise < failThreshold;
            if (success) SuccessCount++;
            else FailureCount++;
            _completedHackCount++;

            var hackTeam = new List<PlayerRef>();
            for (int i = 0; i < ProposedTeamCount; i++) hackTeam.Add(ProposedTeam[i]);
            _hackRecords.Add(new HackRecord
            {
                Round = Round, Leader = CurrentLeader,
                Team = hackTeam, NoiseCount = noise, Success = success,
                Submissions = new Dictionary<PlayerRef, HackingCode>(_hackSubmissions)
            });
            RebuildHackDetailLog();

            LogEvent($"[HACK] R{Round} {(success ? "SUCCESS" : "FAIL")} (NOISE={noise}/{failThreshold}) Score H:{SuccessCount}/AI:{FailureCount}");

            // ハック履歴に記録
            if (HackHistoryCount < HackHistory.Length)
            {
                HackHistory.Set(HackHistoryCount, success ? 1 : 2);
                HackHistoryCount++;
            }

            Phase = GamePhase.RoundResult;

            // Round result display: wait then advance
            if (_roundResultTimer != null) StopCoroutine(_roundResultTimer);
            _roundResultTimer = StartCoroutine(DelayThen(6f, OnRoundResultTimeout));
        }

        private void OnRoundResultTimeout()
        {
            // Check win conditions
            if (SuccessCount >= RequiredHackSuccess)
            {
                BeginOverridePhase();
                return;
            }
            if (FailureCount >= RequiredHackFailure)
            {
                LogEvent("[GAME] 3 hacks failed -> AI VICTORY");
                EndGame(Faction.AI);
                return;
            }
            // Drone awaken check
            if (!_droneAwakened && _completedHackCount >= DroneAwakenAfterHackCount)
            {
                HostAwakenDrone();
            }
            // Next round
            Round++;
            ApplyRoundParams();
            AdvanceLeader();
            ClearProposalAndVotes();
            LastNoiseCount = -1;
            LogEvent($"[ROUND] R{Round} Team={TeamSize} Fail>={RequiredNoise} Leader:{NameOf(CurrentLeader)}");
            Phase = GamePhase.TeamProposal;
        }

        // ==========================================================
        // OVERRIDE Phase
        // ==========================================================
        private void BeginOverridePhase()
        {
            if (!HasStateAuthority) return;
            _overrideSubmissions.Clear();
            OverrideTarget = PlayerRef.None;
            OverrideVoteCount = 0;
            OverrideSucceeded = false;

            // AI 陣営の相互可視化
            foreach (var kvp in _assignedRoles)
            {
                if (CpuPlayerRef.IsCpu(kvp.Key)) continue;
                DeliverRoleView(kvp.Key, VisibilityMode.Override);
            }

            Phase = GamePhase.OverrideDiscussion;
            LogEvent("[OVERRIDE] MotherCore protection lifted. AI counterstrike begins.");

            int discussionTime = HostSettings.Instance != null ? HostSettings.Instance.DiscussionSeconds : 30;
            if (_overridePhaseTimer != null) StopCoroutine(_overridePhaseTimer);
            _overridePhaseTimer = StartCoroutine(DelayThen(discussionTime, BeginOverrideVote));
        }

        private void BeginOverrideVote()
        {
            if (!HasStateAuthority) return;
            Phase = GamePhase.OverrideVote;
            int voteTime = HostSettings.Instance != null ? HostSettings.Instance.VoteSeconds : 30;
            if (_overridePhaseTimer != null) StopCoroutine(_overridePhaseTimer);
            _overridePhaseTimer = StartCoroutine(DelayThen(voteTime, ResolveOverride));
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void Rpc_SubmitOverrideVote(PlayerRef voter, PlayerRef target, RpcInfo info = default)
        {
            if (!HasStateAuthority) return;
            if (Phase != GamePhase.OverrideVote) return;
            if (!_assignedRoles.TryGetValue(voter, out var voterRole)) return;
            if (!voterRole.IsAI()) return;

            _overrideSubmissions[voter] = target;
            OverrideVoteCount = _overrideSubmissions.Count;

            int aiCount = 0;
            foreach (var kvp in _assignedRoles) if (kvp.Value.IsAI()) aiCount++;
            if (_overrideSubmissions.Count >= aiCount) ResolveOverride();
        }

        private void ResolveOverride()
        {
            if (!HasStateAuthority) return;
            if (Phase == GamePhase.OverrideResult || Phase == GamePhase.GameEnd) return;

            if (_overridePhaseTimer != null) { StopCoroutine(_overridePhaseTimer); _overridePhaseTimer = null; }

            // 集計
            var tally = new Dictionary<PlayerRef, int>();
            PlayerRef motherCoreVote = PlayerRef.None;
            foreach (var kvp in _overrideSubmissions)
            {
                if (!tally.ContainsKey(kvp.Value)) tally[kvp.Value] = 0;
                tally[kvp.Value]++;
                if (_assignedRoles.TryGetValue(kvp.Key, out var role) && role == RoleType.MotherCore)
                    motherCoreVote = kvp.Value;
            }

            PlayerRef target = PlayerRef.None;
            int maxVotes = 0;
            int tiedCount = 0;
            foreach (var kvp in tally)
            {
                if (kvp.Value > maxVotes)
                {
                    maxVotes = kvp.Value;
                    target = kvp.Key;
                    tiedCount = 1;
                }
                else if (kvp.Value == maxVotes)
                {
                    tiedCount++;
                }
            }

            // 同票時はマザーコアの票を優先
            if (tiedCount > 1 && motherCoreVote != PlayerRef.None
                && tally.TryGetValue(motherCoreVote, out var mcCount) && mcCount == maxVotes)
            {
                target = motherCoreVote;
            }

            OverrideTarget = target;

            bool aiWin = false;
            if (_assignedRoles.TryGetValue(target, out var targetRole))
                aiWin = targetRole == RoleType.Oracle;

            OverrideSucceeded = aiWin;
            LastWinner = aiWin ? Faction.AI : Faction.Human;
            BuildRevealedRoles();
            LogEvent($"[OVERRIDE] Target:{NameOf(target)} -> {(aiWin ? "AI VICTORY" : "HUMAN VICTORY")}");
            Phase = GamePhase.OverrideResult;

            _overridePhaseTimer = StartCoroutine(DelayThen(5f, () =>
            {
                Phase = GamePhase.GameEnd;
            }));
        }

        private void EndGame(Faction winner)
        {
            LastWinner = winner;
            BuildRevealedRoles();
            LogEvent($"[GAME] END -> {(winner == Faction.Human ? "HUMAN" : "AI")} VICTORY");
            Phase = GamePhase.GameEnd;
        }

        private void BuildRevealedRoles()
        {
            var sb = new StringBuilder(256);
            foreach (var kvp in _assignedRoles)
                sb.Append(kvp.Key.PlayerId).Append(':').Append((int)kvp.Value).Append('|');
            RevealedRoles = sb.ToString();
        }

        /// <summary>ホスト専用: _hackRecords 全体を NetworkString へ再エンコード。</summary>
        private void RebuildHackDetailLog()
        {
            if (!HasStateAuthority) return;
            var sb = new StringBuilder(256);
            for (int i = 0; i < _hackRecords.Count; i++)
            {
                var r = _hackRecords[i];
                if (i > 0) sb.Append('|');
                sb.Append(r.Round).Append(':').Append(r.Leader.PlayerId).Append(':');
                for (int j = 0; j < r.Team.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(r.Team[j].PlayerId);
                }
                sb.Append(':').Append(r.NoiseCount).Append(':').Append(r.Success ? '1' : '0');
            }
            HackDetailLog = sb.ToString();
        }

        /// <summary>
        /// 全クライアントが参照できるパブリックなハック履歴レコード (名前解決は呼び出し側で)。
        /// </summary>
        public struct PublicHackRecord
        {
            public int Round;
            public PlayerRef Leader;
            public List<PlayerRef> Team;
            public int Noise;
            public bool Success;
        }

        /// <summary>HackDetailLog をパースしてレコード一覧を返す。</summary>
        public List<PublicHackRecord> ParseHackDetails()
        {
            var result = new List<PublicHackRecord>();
            var s = HackDetailLog.ToString();
            if (string.IsNullOrEmpty(s)) return result;
            foreach (var record in s.Split('|'))
            {
                if (string.IsNullOrEmpty(record)) continue;
                var parts = record.Split(':');
                if (parts.Length != 5) continue;
                if (!int.TryParse(parts[0], out var round)) continue;
                if (!int.TryParse(parts[1], out var leaderId)) continue;
                if (!int.TryParse(parts[3], out var noise)) continue;
                var team = new List<PlayerRef>();
                foreach (var mid in parts[2].Split(','))
                {
                    if (int.TryParse(mid, out var id)) team.Add(PlayerRef.FromIndex(id));
                }
                result.Add(new PublicHackRecord
                {
                    Round = round,
                    Leader = PlayerRef.FromIndex(leaderId),
                    Team = team,
                    Noise = noise,
                    Success = parts[4] == "1",
                });
            }
            return result;
        }

        public Dictionary<int, RoleType> ParseRevealedRoles()
        {
            var dict = new Dictionary<int, RoleType>();
            var s = RevealedRoles.ToString();
            if (string.IsNullOrEmpty(s)) return dict;
            foreach (var p in s.Split('|'))
            {
                if (string.IsNullOrEmpty(p)) continue;
                var kv = p.Split(':');
                if (kv.Length != 2) continue;
                if (int.TryParse(kv[0], out var id) && int.TryParse(kv[1], out var r))
                    dict[id] = (RoleType)r;
            }
            return dict;
        }

        /// <summary>
        /// ホスト側でゲームをロビーに戻す。全状態リセット。
        /// </summary>
        public void HostReturnToLobby()
        {
            if (!HasStateAuthority) return;
            if (_roundResultTimer != null) { StopCoroutine(_roundResultTimer); _roundResultTimer = null; }
            if (_overridePhaseTimer != null) { StopCoroutine(_overridePhaseTimer); _overridePhaseTimer = null; }
            if (_voteRevealTimer != null) { StopCoroutine(_voteRevealTimer); _voteRevealTimer = null; }
            if (_hackCompleteTimer != null) { StopCoroutine(_hackCompleteTimer); _hackCompleteTimer = null; }

            _assignedRoles.Clear();
            _hackSubmissions.Clear();
            _overrideSubmissions.Clear();
            _voteRecords.Clear();
            _hackRecords.Clear();
            _droneAwakened = false;
            AwakenedDronePlayer = PlayerRef.None;
            _completedHackCount = 0;

            Round = 0;
            SuccessCount = 0;
            FailureCount = 0;
            ConsecutiveRejections = 0;
            CurrentLeaderIndex = 0;
            LeaderOrderCount = 0;
            for (int i = 0; i < LeaderOrder.Length; i++) LeaderOrder.Set(i, PlayerRef.None);
            ClearProposalAndVotes();
            HackHistoryCount = 0;
            for (int i = 0; i < HackHistory.Length; i++) HackHistory.Set(i, 0);
            HackDetailLog = string.Empty;
            LastNoiseCount = -1;
            OverrideTarget = PlayerRef.None;
            OverrideVoteCount = 0;
            OverrideSucceeded = false;
            RevealedRoles = string.Empty;
            TeamSize = DefaultTeamSize;
            RequiredNoise = 1;
            TotalPlayers = 0;
            GameLog.Instance?.HostClear();
            ChatManager.Instance?.HostClear();
            CpuOrchestrator.Instance?.HostReset();
            Phase = GamePhase.Lobby;
        }

        private void LogEvent(string msg)
        {
            GameLog.Instance?.HostAppend(msg);
        }

        private string NameOf(PlayerRef pr)
        {
            if (pr == PlayerRef.None) return "-";
            var reg = PlayerRegistry.Instance;
            if (reg == null) return "#" + pr.PlayerId;
            int idx = reg.FindIndex(pr);
            if (idx < 0) return "#" + pr.PlayerId;
            var n = reg.Entries[idx].DisplayName.ToString();
            return string.IsNullOrEmpty(n) ? "#" + pr.PlayerId : n;
        }

        private IEnumerator DelayThen(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            if (HasStateAuthority) action?.Invoke();
        }

        private static void ShuffleInPlace<T>(List<T> list, int seed)
        {
            var rng = new System.Random(seed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private void OnChanged()
        {
            Changed?.Invoke();
            MaybeRequestRoleOnPhaseChange();
        }
    }
}
