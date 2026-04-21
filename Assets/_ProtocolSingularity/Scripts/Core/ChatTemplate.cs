namespace ProtocolSingularity.Core
{
    /// <summary>
    /// 定型チャットのテンプレート。自由入力は提供しない。
    /// CLAUDE.md §4.2 カテゴリに準拠。
    /// </summary>
    public enum ChatTemplate
    {
        None = 0,
        SuspectAi = 1,          // {target} は AI
        TrustHuman = 2,         // {target} は人類
        IncludeInTeam = 10,     // {target} をチームに入れるべき
        ExcludeFromTeam = 11,   // {target} は外すべき
        VoteApprove = 20,       // 賛成する
        VoteReject = 21,        // 反対する
        ClaimOracle = 30,       // 自分は ORACLE
        ClaimAdmin = 31,        // 自分は ADMIN
        BewareClaim = 40,       // {target} のカミングアウトは警戒すべき
        Agree = 50,             // 同意
        Disagree = 51,          // 反対
        Powerplay = 60,         // パワープレイを示唆
        Thought = 99,           // CPU の自由入力思考 (RawText を使う)
    }

    /// <summary>
    /// 確信度の段階（数値ではなく属性ラベル）。
    /// </summary>
    public enum ChatConfidence
    {
        Suggest = 0,    // 示唆する     [...]
        Uncertain = 1,  // 半信半疑     [?]
        Likely = 2,     // 可能性が高い [!]
        Certain = 3,    // 確信している [!!]
    }

    public static class ChatTemplateMeta
    {
        public static bool RequiresTarget(this ChatTemplate t)
        {
            switch (t)
            {
                case ChatTemplate.SuspectAi:
                case ChatTemplate.TrustHuman:
                case ChatTemplate.IncludeInTeam:
                case ChatTemplate.ExcludeFromTeam:
                case ChatTemplate.BewareClaim:
                    return true;
                default:
                    return false;
            }
        }

        public static bool UsesConfidence(this ChatTemplate t)
        {
            return t == ChatTemplate.SuspectAi || t == ChatTemplate.TrustHuman;
        }

        public static string ToDisplayLabel(this ChatTemplate t)
        {
            return t switch
            {
                ChatTemplate.SuspectAi        => "[AI 疑い] {target} は AI",
                ChatTemplate.TrustHuman       => "[人類信頼] {target} は人類",
                ChatTemplate.IncludeInTeam    => "[推奨] {target} をチームに入れるべき",
                ChatTemplate.ExcludeFromTeam  => "[警告] {target} を外すべき",
                ChatTemplate.VoteApprove      => "[意思表明] この提案に賛成",
                ChatTemplate.VoteReject       => "[意思表明] この提案に反対",
                ChatTemplate.ClaimOracle      => "[カミングアウト] 自分は ORACLE",
                ChatTemplate.ClaimAdmin       => "[カミングアウト] 自分は ADMIN",
                ChatTemplate.BewareClaim      => "[警戒] {target} の CO は信用できない",
                ChatTemplate.Agree            => "[同意]",
                ChatTemplate.Disagree         => "[反対]",
                ChatTemplate.Powerplay        => "[示唆] パワープレイに警戒",
                ChatTemplate.Thought          => "[思考]",
                _ => "(no template)",
            };
        }

        public static string ToDisplayLabel(this ChatConfidence c)
        {
            return c switch
            {
                ChatConfidence.Suggest   => "[...] 示唆する",
                ChatConfidence.Uncertain => "[?]  半信半疑",
                ChatConfidence.Likely    => "[!]  可能性が高い",
                ChatConfidence.Certain   => "[!!] 確信している",
                _ => "(confidence)",
            };
        }

        public static string ToEmote(this ChatConfidence c)
        {
            return c switch
            {
                ChatConfidence.Suggest   => "[...]",
                ChatConfidence.Uncertain => "[?]",
                ChatConfidence.Likely    => "[!]",
                ChatConfidence.Certain   => "[!!]",
                _ => string.Empty,
            };
        }

    }
}
